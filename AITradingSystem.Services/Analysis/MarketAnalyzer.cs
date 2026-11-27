using System.Collections.Concurrent;
using AITradingSystem.Core.Indicators;
using AITradingSystem.Core.Models.MarketData;
using Microsoft.Extensions.Logging;

namespace AITradingSystem.Services.Analysis;

/// <summary>
/// Анализатор рынка — агрегирует индикаторы и рыночные данные.
/// P1.4: хранит посимвольную целостность/свежесть данных и выполняет атомарный повторный прогрев из REST-истории.
/// P0.3: history_reloaded (REST) отделён от stream_live (WS): прогрев не разрешает входы, пока не пришёл живой тик;
/// молчащий символ проходит resubscribe → reconnect → карантин вместо бесконечного цикла recovered → stale.
/// Потокобезопасен: WebSocket-поток, основной цикл и контур восстановления работают с одним состоянием символа.
/// </summary>
public sealed class MarketAnalyzer
{
    private readonly ILogger<MarketAnalyzer> _logger;
    private readonly ConcurrentDictionary<string, SymbolState> _symbols = new(StringComparer.OrdinalIgnoreCase);

    private sealed class SymbolState
    {
        public readonly object Gate = new();
        public readonly IndicatorCalculator Calculator;
        public DateTime LastKlineUtc;
        public DateTime LastOrderBookUtc;
        public bool RecoveryRequired;
        public string? RecoveryReason;

        // P0.3: history_reloaded (REST) и stream_live (WS) — разные факты.
        public DateTime LastRestReloadUtc;
        public bool LiveTickPending;
        public DateTime LiveWaitStartedUtc;
        public DateTime LiveTickDeadlineUtc;
        public bool LiveConfirmationUnreported;
        public double LastTimeToLiveSeconds;
        public int ResubscribeAttempts;
        public DateTime QuarantinedSinceUtc;
        public DateTime QuarantinedUntilUtc;
        public string? QuarantineReason;
        public int QuarantineStreak;
        public double LastQuarantineServedSeconds;
        public bool QuarantineLiftUnreported;
        public string? QuarantineLiftReason;

        public SymbolState(string symbol)
        {
            Calculator = new IndicatorCalculator(symbol);
        }
    }

    public MarketAnalyzer(ILogger<MarketAnalyzer> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Получить или создать калькулятор индикаторов для символа.
    /// </summary>
    public IndicatorCalculator GetOrCreateCalculator(string symbol) => GetState(symbol).Calculator;

    private SymbolState GetState(string symbol) =>
        _symbols.GetOrAdd(symbol, s =>
        {
            _logger.LogInformation("Создан калькулятор индикаторов для {Symbol}", s);
            return new SymbolState(s);
        });

    /// <summary>P1.4: отметить получение kline-тика (в т.ч. незакрытой свечи) для контроля протухания.</summary>
    public void NoteKline(string symbol, DateTime receivedAtUtc)
    {
        var state = GetState(symbol);
        lock (state.Gate)
        {
            NoteStreamMessageLocked(state, receivedAtUtc, isKline: true);
        }
    }

    /// <summary>
    /// P0.3: единственный источник факта «стрим жив» — реальные WS-сообщения.
    /// REST-прогрев этот метод не вызывает, поэтому не может сам себе подтвердить live.
    /// P0.3a шаг 2: live-подтверждение и досрочное снятие карантина требуют ОБА типа сообщений
    /// (kline и стакан), observado после начала ожидания/постановки карантина: вход требует оба,
    /// один тип не объявляет весь символ живым. Молчание второго типа продолжает эскалацию recovery.
    /// </summary>
    private void NoteStreamMessageLocked(SymbolState state, DateTime receivedAtUtc, bool isKline)
    {
        if (isKline)
            state.LastKlineUtc = receivedAtUtc;
        else
            state.LastOrderBookUtc = receivedAtUtc;

        if (state.LiveTickPending
            && state.LastKlineUtc != default && state.LastKlineUtc >= state.LiveWaitStartedUtc
            && state.LastOrderBookUtc != default && state.LastOrderBookUtc >= state.LiveWaitStartedUtc)
        {
            state.LiveTickPending = false;
            state.LiveConfirmationUnreported = true;
            var confirmedAtUtc = state.LastKlineUtc > state.LastOrderBookUtc ? state.LastKlineUtc : state.LastOrderBookUtc;
            state.LastTimeToLiveSeconds = Math.Max(0d, (confirmedAtUtc - state.LiveWaitStartedUtc).TotalSeconds);
            state.ResubscribeAttempts = 0;
            // P0.3a шаг 3: серия карантинов сбрасывается только реальным live-подтверждением обоих стримов.
            state.QuarantineStreak = 0;
        }

        if (state.QuarantinedUntilUtc != default
            && state.LastKlineUtc > state.QuarantinedSinceUtc
            && state.LastOrderBookUtc > state.QuarantinedSinceUtc)
        {
            state.LastQuarantineServedSeconds = Math.Max(0d, (receivedAtUtc - state.QuarantinedSinceUtc).TotalSeconds);
            state.QuarantinedUntilUtc = default;
            state.QuarantineLiftUnreported = true;
            state.QuarantineLiftReason = state.QuarantineReason ?? "live-tick";
            state.QuarantineReason = null;
        }
    }

    /// <summary>P0.3: забрать однократное подтверждение живого WS-тика после REST-прогрева (для журнала/метрик).</summary>
    public bool TryConsumeLiveConfirmation(string symbol, out double secondsToLive)
    {
        var state = GetState(symbol);
        lock (state.Gate)
        {
            secondsToLive = state.LastTimeToLiveSeconds;
            if (!state.LiveConfirmationUnreported)
                return false;
            state.LiveConfirmationUnreported = false;
            return true;
        }
    }

    /// <summary>P0.3: жив ли стрим по символу — ожидается ли ещё первый WS-тик после REST-прогрева.</summary>
    public bool IsAwaitingLiveTick(string symbol, out DateTime deadlineUtc)
    {
        var state = GetState(symbol);
        lock (state.Gate)
        {
            deadlineUtc = state.LiveTickDeadlineUtc;
            return state.LiveTickPending;
        }
    }

    /// <summary>P0.3: окно ожидания живого тика истекло — пора resubscribe/reconnect/quarantine.</summary>
    public bool IsLiveTickOverdue(string symbol, DateTime nowUtc)
    {
        var state = GetState(symbol);
        lock (state.Gate)
        {
            return state.LiveTickPending && state.LiveTickDeadlineUtc != default && nowUtc > state.LiveTickDeadlineUtc;
        }
    }

    /// <summary>P0.3: зарегистрировать попытку resubscribe и продлить окно ожидания живого тика.</summary>
    public int NoteResubscribeAttempt(string symbol, DateTime nowUtc, TimeSpan liveConfirmWindow)
    {
        var state = GetState(symbol);
        lock (state.Gate)
        {
            state.ResubscribeAttempts++;
            state.LiveTickPending = true;
            if (state.LiveWaitStartedUtc == default)
                state.LiveWaitStartedUtc = nowUtc;
            state.LiveTickDeadlineUtc = nowUtc + liveConfirmWindow;
            return state.ResubscribeAttempts;
        }
    }

    public int GetResubscribeAttempts(string symbol)
    {
        var state = GetState(symbol);
        lock (state.Gate)
        {
            return state.ResubscribeAttempts;
        }
    }

    /// <summary>
    /// P0.3: явный карантин символа — входы запрещены, цикл recovery больше не крутится по нему.
    /// P0.3a шаг 3: возвращает длину серии повторных карантинов без live-подтверждения (для backoff/алерта).
    /// </summary>
    public int Quarantine(string symbol, string reason, DateTime nowUtc, TimeSpan duration)
    {
        var state = GetState(symbol);
        lock (state.Gate)
        {
            state.QuarantinedSinceUtc = nowUtc;
            state.QuarantinedUntilUtc = nowUtc + duration;
            state.QuarantineReason = reason;
            state.LiveTickPending = false;
            state.LiveConfirmationUnreported = false;
            state.ResubscribeAttempts = 0;
            state.QuarantineStreak++;
            _logger.LogWarning(
                "DATA-QUARANTINE {Symbol}: {Reason}; входы запрещены до {Until:O}",
                symbol, reason, state.QuarantinedUntilUtc);
            return state.QuarantineStreak;
        }
    }

    /// <summary>P0.3a шаг 3: сколько карантинов подряд symbol получил без live-подтверждения обоих стримов.</summary>
    public int GetQuarantineStreak(string symbol)
    {
        var state = GetState(symbol);
        lock (state.Gate)
        {
            return state.QuarantineStreak;
        }
    }

    public bool IsQuarantined(string symbol, DateTime nowUtc, out string reason)
    {
        var state = GetState(symbol);
        lock (state.Gate)
        {
            if (state.QuarantinedUntilUtc != default && nowUtc < state.QuarantinedUntilUtc)
            {
                reason = state.QuarantineReason ?? "quarantine";
                return true;
            }
            reason = string.Empty;
            return false;
        }
    }

    /// <summary>P0.3: карантин истёк — символ возвращается в контур восстановления, но не сразу в торговлю.</summary>
    public bool TryLiftExpiredQuarantine(string symbol, DateTime nowUtc)
    {
        var state = GetState(symbol);
        lock (state.Gate)
        {
            if (state.QuarantinedUntilUtc == default || nowUtc < state.QuarantinedUntilUtc)
                return false;

            state.LastQuarantineServedSeconds = Math.Max(0d, (nowUtc - state.QuarantinedSinceUtc).TotalSeconds);
            state.QuarantineLiftUnreported = true;
            state.QuarantineLiftReason = state.QuarantineReason ?? "quarantine";
            state.QuarantinedUntilUtc = default;
            state.QuarantineReason = null;
            state.RecoveryRequired = true;
            state.RecoveryReason = "quarantine-expired";
            return true;
        }
    }

    /// <summary>P0.3/P0.3a: забрать однократное событие снятия карантина для журнала и фактические секунды карантина.</summary>
    public bool TryConsumeQuarantineLift(string symbol, out string reason, out double servedSeconds)
    {
        var state = GetState(symbol);
        lock (state.Gate)
        {
            reason = state.QuarantineLiftReason ?? string.Empty;
            servedSeconds = state.LastQuarantineServedSeconds;
            if (!state.QuarantineLiftUnreported)
                return false;
            state.QuarantineLiftUnreported = false;
            state.QuarantineLiftReason = null;
            return true;
        }
    }

    /// <summary>
    /// P0.3a шаг 3: общий WS-реконнект не должен стирать прогресс здоровых пар.
    /// Если закрытая сетка свечей непрерывна (последняя закрытая свеча совпадает с расписанием —
    /// пропущенных закрытых свечей нет), REST-дозагрузка не нужна: индикаторы сохраняются,
    /// но входы блокируются до свежего kline И стакана, полученных ПОСЛЕ реконнекта.
    /// Возвращает false, если нужен полный REST-путь (разрыв сетки, недогрев, карантин, чужое recovery).
    /// </summary>
    public bool TryResumeAfterReconnect(string symbol, DateTime nowUtc, TimeSpan interval, TimeSpan liveConfirmWindow)
    {
        var state = GetState(symbol);
        lock (state.Gate)
        {
            if (state.QuarantinedUntilUtc != default && nowUtc < state.QuarantinedUntilUtc)
                return false;

            // Recovery по реальной проблеме данных (candle-gap и т.п.) лечится только REST-перезагрузкой.
            if (state.RecoveryRequired
                && state.RecoveryReason is not ("ws-disconnected" or "ws-reconnected"))
                return false;

            if (!state.Calculator.AllReady)
                return false;

            if (interval <= TimeSpan.Zero)
                return false;

            var expectedLastClosed = CandleSchedule.GetLastClosedOpenTime(nowUtc, interval);
            if (state.Calculator.LastClosedOpenTime != expectedLastClosed)
                return false;

            state.RecoveryRequired = false;
            state.RecoveryReason = null;
            state.LiveTickPending = true;
            state.LiveWaitStartedUtc = nowUtc;
            state.LiveTickDeadlineUtc = nowUtc + (liveConfirmWindow > TimeSpan.Zero ? liveConfirmWindow : TimeSpan.FromSeconds(20));
            state.LiveConfirmationUnreported = false;
            return true;
        }
    }

    /// <summary>P1.4/P0.3a: отметить получение свежего стакана; участвует в live-подтверждении наравне с kline.</summary>
    public void NoteOrderBook(string symbol, DateTime receivedAtUtc)
    {
        var state = GetState(symbol);
        lock (state.Gate)
        {
            NoteStreamMessageLocked(state, receivedAtUtc, isKline: false);
        }
    }

    /// <summary>
    /// Обновить индикаторы закрытой свечой с детектом разрыва сетки интервала.
    /// При разрыве свеча не попадает в индикаторы: требуется REST-дозагрузка и повторный прогрев.
    /// </summary>
    public CandleAppendResult UpdateIndicators(string symbol, OHLCV candle, TimeSpan interval, DateTime receivedAtUtc)
    {
        var state = GetState(symbol);
        lock (state.Gate)
        {
            NoteStreamMessageLocked(state, receivedAtUtc, isKline: true);

            if (!candle.IsClosed)
                return CandleAppendResult.Accepted;

            if (state.RecoveryRequired)
                return CandleAppendResult.Gap;

            var result = state.Calculator.TryAppendClosed(candle, interval);
            if (result == CandleAppendResult.Gap)
            {
                state.RecoveryRequired = true;
                state.RecoveryReason = "candle-gap";
                _logger.LogWarning(
                    "DATA-GAP {Symbol}: ожидалась свеча после {Last:O}, получена {Open:O}; вход будет заблокирован до REST-восстановления",
                    symbol, state.Calculator.LastClosedOpenTime, candle.OpenTime);
            }

            return result;
        }
    }

    /// <summary>P1.4: принудительно отправить символ на REST-восстановление (реконнект/разрыв/протухание).</summary>
    public void MarkRecoveryRequired(string symbol, string reason)
    {
        var state = GetState(symbol);
        lock (state.Gate)
        {
            state.RecoveryRequired = true;
            state.RecoveryReason = reason;
            // P0.3: история снова недостоверна — сначала REST, потом ожидание живого WS-тика.
            state.LiveTickPending = false;
            state.LiveTickDeadlineUtc = default;
        }
    }

    public bool IsRecoveryRequired(string symbol)
    {
        var state = GetState(symbol);
        lock (state.Gate)
        {
            return state.RecoveryRequired;
        }
    }

    /// <summary>
    /// P1.4: атомарно заменить историю символа закрытыми REST-свечами и повторно прогреть индикаторы.
    /// История должна быть непрерывной, без дублей/разрывов, достаточной для прогрева и не древнее одной свечи.
    /// </summary>
    public bool TryReloadHistory(
        string symbol,
        IReadOnlyList<OHLCV> history,
        TimeSpan interval,
        int requiredClosedCandles,
        DateTime nowUtc,
        out string reason,
        TimeSpan liveConfirmWindow = default)
    {
        var state = GetState(symbol);
        lock (state.Gate)
        {
            reason = string.Empty;
            if (interval <= TimeSpan.Zero)
            {
                reason = "invalid-interval";
                return false;
            }

            var closed = history
                .Where(k => k.IsClosed)
                .OrderBy(k => k.OpenTime)
                .ToList();

            if (closed.Count < requiredClosedCandles)
            {
                reason = $"insufficient-history:{closed.Count}/{requiredClosedCandles}";
                return false;
            }

            for (var i = 1; i < closed.Count; i++)
            {
                var delta = closed[i].OpenTime - closed[i - 1].OpenTime;
                if (delta == TimeSpan.Zero)
                {
                    reason = "duplicate-candle";
                    return false;
                }
                if (delta < TimeSpan.Zero)
                {
                    reason = "out-of-order-candle";
                    return false;
                }
                if (delta != interval)
                {
                    reason = "history-gap";
                    return false;
                }
            }

            var expectedLastClosed = CandleSchedule.GetLastClosedOpenTime(nowUtc, interval);
            var lastClosed = closed[^1].OpenTime;
            if (lastClosed < expectedLastClosed - interval || lastClosed > expectedLastClosed)
            {
                reason = "stale-history";
                return false;
            }

            var rebuilt = new IndicatorCalculator(symbol);
            foreach (var candle in closed)
            {
                if (rebuilt.TryAppendClosed(candle, interval) != CandleAppendResult.Accepted)
                {
                    reason = "rebuild-gap";
                    return false;
                }
            }

            if (!rebuilt.AllReady)
            {
                reason = "warmup-incomplete";
                return false;
            }

            state.Calculator.Reset();
            foreach (var candle in closed)
                state.Calculator.Update(candle);

            // P0.3: REST-прогрев — это history_reloaded, а НЕ stream_live.
            // LastKlineUtc намеренно не обновляется: свежесть подтверждает только WS-тик.
            var window = liveConfirmWindow > TimeSpan.Zero ? liveConfirmWindow : TimeSpan.FromSeconds(20);
            state.LastRestReloadUtc = nowUtc;
            state.RecoveryRequired = false;
            state.RecoveryReason = null;
            state.LiveTickPending = true;
            state.LiveWaitStartedUtc = nowUtc;
            state.LiveTickDeadlineUtc = nowUtc + window;
            state.LiveConfirmationUnreported = false;

            _logger.LogInformation(
                "DATA-REST-RELOAD {Symbol}: REST-история непрерывна, закрытых свечей {Count}, индикаторы повторно прогреты; ожидается живой WS-тик до {Deadline:O}",
                symbol, closed.Count, state.LiveTickDeadlineUtc);
            return true;
        }
    }

    /// <summary>
    /// P1.4: пригодны ли данные символа для входа: нет ожидающего восстановления, индикаторы прогреты,
    /// kline и стакан не старше maxAge. Стакан после реконнекта должен прийти заново из WS.
    /// </summary>
    public bool IsDataFresh(string symbol, DateTime nowUtc, TimeSpan maxAge, out string reason)
    {
        var state = GetState(symbol);
        lock (state.Gate)
        {
            if (state.QuarantinedUntilUtc != default && nowUtc < state.QuarantinedUntilUtc)
            {
                reason = "quarantined:" + (state.QuarantineReason ?? "unknown");
                return false;
            }

            if (state.RecoveryRequired)
            {
                reason = state.RecoveryReason ?? "recovery-required";
                return false;
            }

            // P0.3: REST-история есть, но живой WS-тик по символу ещё не подтверждён.
            // P0.3a шаг 2: суффикс указывает, какого типа сообщений не хватает после начала ожидания.
            if (state.LiveTickPending)
            {
                var klineSeen = state.LastKlineUtc != default && state.LastKlineUtc >= state.LiveWaitStartedUtc;
                var bookSeen = state.LastOrderBookUtc != default && state.LastOrderBookUtc >= state.LiveWaitStartedUtc;
                reason = (klineSeen, bookSeen) switch
                {
                    (false, false) => "awaiting-live-tick:both",
                    (false, true) => "awaiting-live-tick:kline",
                    (true, false) => "awaiting-live-tick:book",
                    _ => "awaiting-live-tick"
                };
                return false;
            }

            if (!state.Calculator.AllReady)
            {
                reason = "warmup-incomplete";
                return false;
            }

            if (state.LastKlineUtc == default)
            {
                reason = "no-kline";
                return false;
            }

            if (nowUtc - state.LastKlineUtc > maxAge)
            {
                reason = "stale-kline";
                return false;
            }

            if (state.LastOrderBookUtc == default)
            {
                reason = "no-book";
                return false;
            }

            if (nowUtc - state.LastOrderBookUtc > maxAge)
            {
                reason = "stale-book";
                return false;
            }

            reason = string.Empty;
            return true;
        }
    }

    /// <summary>
    /// Статус прогрева индикаторов символа (B11): чтобы движок писал в лог
    /// «прогрев N/M свечей» вместо молчаливого return.
    /// </summary>
    public (bool Ready, int Candles, int Required) GetWarmupInfo(string symbol)
    {
        var state = GetState(symbol);
        lock (state.Gate)
        {
            return (state.Calculator.AllReady, state.Calculator.ClosedCandles, state.Calculator.MaxWarmupPeriod);
        }
    }

    /// <summary>P1.4: SMA20 объёма закрытых свечей под посимвольным локом.</summary>
    public decimal GetVolumeSma20(string symbol)
    {
        var state = GetState(symbol);
        lock (state.Gate)
        {
            return state.Calculator.VolumeSma20;
        }
    }

    /// <summary>P1.7: закрытые свечи символа под посимвольным локом (для корреляции).</summary>
    public IReadOnlyList<OHLCV> GetClosedCandles(string symbol, int count)
    {
        var state = GetState(symbol);
        lock (state.Gate)
        {
            return state.Calculator.GetClosedCandles(count);
        }
    }

    /// <summary>
    /// Получить нормализованный снимок индикаторов для AI.
    /// </summary>
    public Dictionary<string, decimal>? GetIndicatorSnapshot(string symbol, OHLCV currentCandle)
    {
        var state = GetState(symbol);
        lock (state.Gate)
        {
            if (state.RecoveryRequired || !state.Calculator.AllReady)
                return null;

            if (state.QuarantinedUntilUtc != default && DateTime.UtcNow < state.QuarantinedUntilUtc)
                return null;

            var snapshot = state.Calculator.GetNormalizedSnapshot(currentCandle);
            return snapshot.ToDictionary();
        }
    }
}
