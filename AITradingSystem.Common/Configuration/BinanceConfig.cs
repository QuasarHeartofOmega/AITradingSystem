namespace AITradingSystem.Common.Configuration;

/// <summary>
/// Окружение Binance.
/// Live    — реальная биржа (api.binance.com).
/// Testnet — спот-тестнет (testnet.binance.vision), свои цены и стакан.
/// Demo    — демо-торговля demo.binance.com (demo-api.binance.com): виртуальные средства,
///           но цены/стакан/фильтры как на реальной бирже.
/// </summary>
public enum BinanceEnvironment
{
    Live = 0,
    Testnet = 1,
    Demo = 2
}

/// <summary>
/// Конфигурация подключения к Binance (FR-6, NFR-3).
/// Поддерживаются Live, Spot Testnet и Demo Mode (demo.binance.com).
/// </summary>
public sealed class BinanceConfig
{
    /// <summary>Зашифрованный API Key (NFR-3.1).</summary>
    public string EncryptedApiKey { get; set; } = string.Empty;

    /// <summary>Зашифрованный API Secret (NFR-3.1).</summary>
    public string EncryptedApiSecret { get; set; } = string.Empty;

    /// <summary>
    /// P0.7: отдельные ключи для Live-окружения. Если заданы — в Live используются ТОЛЬКО они,
    /// общие ключи Demo/Testnet не могут случайно торговать реальными деньгами.
    /// Пустые — обратная совместимость: Live берёт общие ключи.
    /// </summary>
    public string EncryptedLiveApiKey { get; set; } = string.Empty;

    /// <summary>P0.7: отдельный API Secret для Live (см. EncryptedLiveApiKey).</summary>
    public string EncryptedLiveApiSecret { get; set; } = string.Empty;

    /// <summary>P0.7: фактический ключ для выбранного окружения.</summary>
    public string EffectiveEncryptedApiKey =>
        ResolvedEnvironment == BinanceEnvironment.Live && !string.IsNullOrWhiteSpace(EncryptedLiveApiKey)
            ? EncryptedLiveApiKey
            : EncryptedApiKey;

    /// <summary>P0.7: фактический секрет для выбранного окружения.</summary>
    public string EffectiveEncryptedApiSecret =>
        ResolvedEnvironment == BinanceEnvironment.Live && !string.IsNullOrWhiteSpace(EncryptedLiveApiSecret)
            ? EncryptedLiveApiSecret
            : EncryptedApiSecret;

    /// <summary>
    /// Окружение: "Live" | "Testnet" | "Demo".
    /// Пустое значение = автоопределение по устаревшему флагу UseTestnet.
    /// </summary>
    public string Environment { get; set; } = "Demo";

    /// <summary>
    /// Устаревший флаг тестнета. Используется только если <see cref="Environment"/> пуст.
    /// Оставлен для совместимости со старыми appsettings.json.
    /// </summary>
    public bool UseTestnet { get; set; } = true;

    /// <summary>
    /// Брать публичные рыночные данные (WebSocket) с mainnet, а не с выбранного окружения.
    /// Для Testnet полезно (там тонкий стакан), для Demo по умолчанию false —
    /// demo-стакан практически совпадает с реальным.
    /// </summary>
    public bool? MarketDataFromMainnet { get; set; }

    /// <summary>Итоговое окружение с учётом обратной совместимости.</summary>
    public BinanceEnvironment ResolvedEnvironment
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Environment) &&
                Enum.TryParse<BinanceEnvironment>(Environment.Trim(), true, out var parsed))
                return parsed;

            return UseTestnet ? BinanceEnvironment.Testnet : BinanceEnvironment.Live;
        }
    }

    /// <summary>Base URL для REST API.</summary>
    public string RestBaseUrl => ResolvedEnvironment switch
    {
        BinanceEnvironment.Demo => "https://demo-api.binance.com",
        BinanceEnvironment.Testnet => "https://testnet.binance.vision",
        _ => "https://api.binance.com"
    };

    /// <summary>Base URL для одиночных WebSocket-стримов (/ws).</summary>
    public string WebSocketBaseUrl => ResolvedEnvironment switch
    {
        BinanceEnvironment.Demo => "wss://demo-stream.binance.com:9443/ws",
        BinanceEnvironment.Testnet => "wss://testnet.binance.vision/ws",
        _ => "wss://stream.binance.com:9443/ws"
    };

    /// <summary>Base URL для комбинированных WebSocket-стримов (/stream?streams=...).</summary>
    public string CombinedStreamBaseUrl => ResolvedEnvironment switch
    {
        BinanceEnvironment.Demo => "wss://demo-stream.binance.com:9443/stream",
        BinanceEnvironment.Testnet => "wss://testnet.binance.vision/stream",
        _ => "wss://stream.binance.com:9443/stream"
    };

    /// <summary>URL рыночных данных с учётом MarketDataFromMainnet.</summary>
    public string MarketStreamBaseUrl => UseMainnetMarketData
        ? "wss://stream.binance.com:9443/stream"
        : CombinedStreamBaseUrl;

    /// <summary>REST-база для публичных рыночных данных (свечи, exchangeInfo).</summary>
    public string MarketRestBaseUrl => UseMainnetMarketData
        ? "https://api.binance.com"
        : RestBaseUrl;

    /// <summary>Фактическое значение признака «рыночные данные с mainnet».</summary>
    public bool UseMainnetMarketData =>
        MarketDataFromMainnet ?? ResolvedEnvironment == BinanceEnvironment.Testnet;

    /// <summary>Таймаут HTTP-запросов в миллисекундах.</summary>
    public int HttpTimeoutMs { get; set; } = 10_000;

    /// <summary>Максимальное количество повторных попыток при ошибке (FR-6.4).</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Начальная задержка между retry в мс (экспоненциальный backoff).</summary>
    public int RetryBaseDelayMs { get; set; } = 500;

    /// <summary>
    /// recvWindow для подписанных запросов, мс (максимум 60000 по документации Binance).
    /// </summary>
    public int RecvWindowMs { get; set; } = 10_000;

    /// <summary>
    /// Синхронизировать время с сервером (/api/v3/time) перед подписанными запросами.
    /// Убирает ошибку -1021 (Timestamp for this request is outside of the recvWindow).
    /// </summary>
    public bool SyncServerTime { get; set; } = true;

    /// <summary>
    /// P1.6: лимит веса REST-запросов в минуту (spot: 6000). Бюджет читает факт
    /// из заголовка X-MBX-USED-WEIGHT-1M и оценивает вес запросов между ответами.
    /// </summary>
    public int RequestWeightLimitPerMinute { get; set; } = 6000;

    /// <summary>
    /// P1.6: порог soft-limit в процентах лимита [1; 100]. При достижении порога
    /// некритичные запросы (polling/данные) ждут конца минутного окна;
    /// постановка/снятие ордеров и OCO проходит без задержки.
    /// </summary>
    public int RequestWeightSoftLimitPercent { get; set; } = 80;

    /// <summary>
    /// P1.6: притормаживание запросов при приближении к лимиту веса.
    /// false — только учёт весов/метрик и жёсткие баны 429/-1003/418 (баны действуют всегда).
    /// </summary>
    public bool WeightThrottleEnabled { get; set; } = true;

    public void Validate()
    {
        if (HttpTimeoutMs < 1000 || HttpTimeoutMs > 60_000)
            throw new InvalidOperationException("HttpTimeoutMs должен быть в диапазоне [1000; 60000]");

        if (MaxRetries < 0 || MaxRetries > 10)
            throw new InvalidOperationException("MaxRetries должен быть в диапазоне [0; 10]");

        if (RecvWindowMs < 1000 || RecvWindowMs > 60_000)
            throw new InvalidOperationException("RecvWindowMs должен быть в диапазоне [1000; 60000]");

        if (RequestWeightLimitPerMinute < 100 || RequestWeightLimitPerMinute > 1_000_000)
            throw new InvalidOperationException("RequestWeightLimitPerMinute должен быть в диапазоне [100; 1000000]");

        if (RequestWeightSoftLimitPercent < 1 || RequestWeightSoftLimitPercent > 100)
            throw new InvalidOperationException("RequestWeightSoftLimitPercent должен быть в диапазоне [1; 100]");

        if (!string.IsNullOrWhiteSpace(Environment) &&
            !Enum.TryParse<BinanceEnvironment>(Environment.Trim(), true, out _))
            throw new InvalidOperationException(
                $"Binance:Environment должен быть Live, Testnet или Demo. Текущее: {Environment}");

        // Для Live ключи обязательны. Для Demo/Testnet допускаем пустые ключи:
        // приложение сможет читать данные, а торговля выдаст понятную ошибку.
        // P0.7: проверяются фактические ключи окружения (Live может иметь отдельные).
        if (ResolvedEnvironment == BinanceEnvironment.Live &&
            (string.IsNullOrWhiteSpace(EffectiveEncryptedApiKey) || string.IsNullOrWhiteSpace(EffectiveEncryptedApiSecret)))
        {
            throw new InvalidOperationException(
                "API ключи не настроены. Используйте UI для ввода ключей в режиме Live.");
        }
    }
}
