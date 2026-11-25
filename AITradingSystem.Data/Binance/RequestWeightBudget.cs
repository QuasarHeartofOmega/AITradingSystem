namespace AITradingSystem.Data.Binance;

/// <summary>
/// P1.6: приёмник метрик rate-limit/весов REST-клиента.
/// Интерфейс живёт в Data (Data не ссылается на Services); реализация —
/// <c>MetricsCollector</c> в Services, регистрация в DI — App.xaml.cs.
/// </summary>
public interface IRestRateLimitMetrics
{
    /// <summary>Снимок израсходованного веса из заголовка X-MBX-USED-WEIGHT-1M.</summary>
    void RecordRestWeight(int usedWeight, int limitPerMinute);

    /// <summary>Ответ 429 / -1003 / 418: биржа ограничила запросы, назначен backoff.</summary>
    void RecordRateLimitHit(int httpStatus, int? apiCode, double backoffMs);

    /// <summary>Запрос приторможен бюджетом весов до отправки (soft-limit или бан).</summary>
    void RecordWeightThrottle(double delayMs, bool critical);
}

/// <summary>
/// P1.6: бюджет весов REST-запросов Binance (лимит веса в минуту) + учёт банов 429/-1003/418.
///
/// Модель окна: биржа считает вес в фиксированной серверной минуте и сообщает израсходованный
/// вес заголовком X-MBX-USED-WEIGHT-1M. Локально окно аппроксимируется минутой от первого
/// наблюдения (сэмпл заголовка или локальная оценка веса запроса). Локальное окно начинается
/// не раньше серверного, поэтому его конец — консервативная (безопасная) граница сброса.
///
/// Между ответами вес оценивается локально по таблице весов эндпоинтов (<see cref="EstimateWeight"/>);
/// свежий заголовок биржи всегда важнее локальной оценки (оценка обнуляется при новом сэмпле).
///
/// Два уровня притормаживания:
///  - <b>бан</b> (429/-1003/418 c Retry-After): действуют для ВСЕХ запросов, включая критичные —
///    отправка во время бана ведёт к IP-бану 418;
///  - <b>soft-limit</b> (вес ≥ порога, напр. 80% лимита): задерживаются только НЕкритичные запросы
///    (polling/рыночные данные) до конца окна. Критичные — постановка/снятие ордеров и OCO
///    (POST/DELETE /api/v3/order*) — проходят без задержки: защита и закрытие не должны стоять
///    за исчерпанным бюджетом polling'а (очередь исполнения P0.3 приоритизирует их отдельно).
///
/// Класс чистый и потокобезопасный; время передаётся параметром — покрыт тестами без сети.
/// </summary>
public sealed class RequestWeightBudget
{
    private readonly object _lock = new();
    private readonly bool _softThrottleEnabled;

    private DateTime _windowStartUtc = DateTime.MinValue;
    private int _reportedWeight;   // из заголовка биржи (текущее окно)
    private int _estimatedWeight;  // локальные оценки запросов после последнего сэмпла
    private DateTime _banUntilUtc = DateTime.MinValue;

    /// <summary>Лимит веса в минуту (spot: 6000).</summary>
    public int LimitPerMinute { get; }

    /// <summary>Порог soft-limit в процентах лимита [1; 100].</summary>
    public int SoftLimitPercent { get; }

    /// <summary>Абсолютный порог soft-limit в весе.</summary>
    public int SoftLimitWeight => (int)((long)LimitPerMinute * SoftLimitPercent / 100);

    public RequestWeightBudget(int limitPerMinute = 6000, int softLimitPercent = 80, bool softThrottleEnabled = true)
    {
        if (limitPerMinute < 1) throw new ArgumentOutOfRangeException(nameof(limitPerMinute));
        if (softLimitPercent is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(softLimitPercent));

        LimitPerMinute = limitPerMinute;
        SoftLimitPercent = softLimitPercent;
        _softThrottleEnabled = softThrottleEnabled;
    }

    /// <summary>Текущий израсходованный вес окна: заголовок биржи + локальные оценки.</summary>
    public int GetUsedWeight(DateTime nowUtc)
    {
        lock (_lock)
        {
            RollWindow(nowUtc);
            return _reportedWeight + _estimatedWeight;
        }
    }

    /// <summary>Активен ли бан (429/-1003/418) — блокируются все запросы.</summary>
    public bool IsBanned(DateTime nowUtc)
    {
        lock (_lock) return nowUtc < _banUntilUtc;
    }

    /// <summary>Остаток бана (Zero, если бана нет).</summary>
    public TimeSpan GetBanRemaining(DateTime nowUtc)
    {
        lock (_lock) return nowUtc < _banUntilUtc ? _banUntilUtc - nowUtc : TimeSpan.Zero;
    }

    /// <summary>
    /// Сэмпл заголовка X-MBX-USED-WEIGHT-1M. Значение биржи замещает локальные оценки
    /// (они уже учтены биржей либо придут следующим сэмплом — лёгкий недоучёт в полёте
    /// компенсируется запасом soft-limit). Берётся максимум в окне: сэмплы могут
    /// приходить не по порядку (параллельные ответы).
    /// </summary>
    public void ObserveUsedWeight(int usedWeight, DateTime nowUtc)
    {
        if (usedWeight < 0) return;
        lock (_lock)
        {
            RollWindow(nowUtc);
            if (usedWeight > _reportedWeight) _reportedWeight = usedWeight;
            _estimatedWeight = 0;
        }
    }

    /// <summary>Локальная оценка веса запроса перед отправкой (между сэмплами заголовка).</summary>
    public void AddEstimatedWeight(int weight, DateTime nowUtc)
    {
        if (weight <= 0) return;
        lock (_lock)
        {
            RollWindow(nowUtc);
            _estimatedWeight += weight;
        }
    }

    /// <summary>
    /// Зарегистрировать бан после 429/-1003/418. Продлевает существующий (максимум, не сумма).
    /// </summary>
    public void RegisterBan(TimeSpan duration, DateTime nowUtc)
    {
        if (duration <= TimeSpan.Zero) return;
        lock (_lock)
        {
            var until = nowUtc + duration;
            if (until > _banUntilUtc) _banUntilUtc = until;
        }
    }

    /// <summary>
    /// Задержка перед отправкой запроса. Бан — для всех; soft-limit — только для некритичных.
    /// </summary>
    public TimeSpan GetThrottleDelay(DateTime nowUtc, bool critical)
    {
        lock (_lock)
        {
            // Бан жёсткий: отправка во время бана грозит 418 (IP-бан) — ждём даже закрытие.
            if (nowUtc < _banUntilUtc)
                return _banUntilUtc - nowUtc;

            if (!_softThrottleEnabled || critical)
                return TimeSpan.Zero;

            RollWindow(nowUtc);
            if (_reportedWeight + _estimatedWeight < SoftLimitWeight)
                return TimeSpan.Zero;

            // Ждём конца окна + небольшой guard, чтобы не попасть в старую минуту на границе.
            var delay = _windowStartUtc.AddSeconds(60.25) - nowUtc;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }
    }

    private void RollWindow(DateTime nowUtc)
    {
        var windowEnd = _windowStartUtc.AddSeconds(60);
        if (nowUtc < windowEnd) return;

        _windowStartUtc = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, nowUtc.Minute, 0, DateTimeKind.Utc);
        _reportedWeight = 0;
        _estimatedWeight = 0;
    }

    /// <summary>
    /// Критичная операция — постановка/снятие ордера или OCO-связки (POST/DELETE /api/v3/order*).
    /// Только они обходят soft-limit; GET-опросы (/order, /openOrders, свечи, баланс) — нет.
    /// </summary>
    public static bool IsCriticalOperation(HttpMethod method, string endpoint)
    {
        if (method != HttpMethod.Post && method != HttpMethod.Delete) return false;
        var path = PathOf(endpoint);
        return path.StartsWith("/api/v3/order", StringComparison.Ordinal);
    }

    /// <summary>
    /// Оценка веса запроса по документации Binance spot (используется между сэмплами заголовка).
    /// Неизвестный эндпоинт — консервативный минимум 1.
    /// </summary>
    public static int EstimateWeight(HttpMethod method, string endpoint)
    {
        var path = PathOf(endpoint);
        var hasSymbol = endpoint.Contains("symbol=", StringComparison.OrdinalIgnoreCase);

        if (method == HttpMethod.Get)
        {
            return path switch
            {
                "/api/v3/ping" or "/api/v3/time" => 1,
                "/api/v3/exchangeInfo" => 20,
                "/api/v3/klines" => 2,
                "/api/v3/ticker/price" => hasSymbol ? 1 : 2,
                "/api/v3/depth" => 5,
                "/api/v3/account" => 20,
                "/api/v3/order" => 4,
                "/api/v3/openOrders" => hasSymbol ? 6 : 80,
                "/api/v3/openOrderList" => 6,
                _ => 1
            };
        }

        // POST/DELETE /order, /orderList* — 1; userDataStream — 2.
        if (path.StartsWith("/api/v3/order", StringComparison.Ordinal)) return 1;
        if (path == "/api/v3/userDataStream") return 2;
        return 1;
    }

    private static string PathOf(string endpoint)
    {
        var cut = endpoint.IndexOf('?');
        return cut >= 0 ? endpoint[..cut] : endpoint;
    }
}
