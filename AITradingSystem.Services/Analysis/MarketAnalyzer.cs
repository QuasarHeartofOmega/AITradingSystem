using System.Collections.Concurrent;
using AITradingSystem.Core.Indicators;
using AITradingSystem.Core.Models.MarketData;
using Microsoft.Extensions.Logging;

namespace AITradingSystem.Services.Analysis;

/// <summary>
/// Анализатор рынка — агрегирует индикаторы и рыночные данные.
/// P1.4: хранит посимвольную целостность/свежесть данных и выполняет атомарный повторный прогрев из REST-истории.
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
            state.LastKlineUtc = receivedAtUtc;
        }
    }

    /// <summary>P1.4: отметить получение свежего стакана.</summary>
    public void NoteOrderBook(string symbol, DateTime receivedAtUtc)
    {
        var state = GetState(symbol);
        lock (state.Gate)
        {
            state.LastOrderBookUtc = receivedAtUtc;
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
            state.LastKlineUtc = receivedAtUtc;

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
        out string reason)
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

            state.LastKlineUtc = nowUtc;
            state.RecoveryRequired = false;
            state.RecoveryReason = null;

            _logger.LogInformation(
                "DATA-RECOVERED {Symbol}: REST-история непрерывна, закрытых свечей {Count}, индикаторы повторно прогреты",
                symbol, closed.Count);
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
            if (state.RecoveryRequired)
            {
                reason = state.RecoveryReason ?? "recovery-required";
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

            var snapshot = state.Calculator.GetNormalizedSnapshot(currentCandle);
            return snapshot.ToDictionary();
        }
    }
}
