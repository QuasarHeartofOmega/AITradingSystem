using AITradingSystem.Common.Configuration;
using AITradingSystem.Core.Models.MarketData;
using AITradingSystem.Core.Models.Trading;

namespace AITradingSystem.Core.RiskManagement;

/// <summary>
/// P1.7: портфельный риск поверх поштучного RiskManager.
/// Чистый расчётный класс: не ходит в сеть/файлы; историю сделок и закрытые свечи
/// передаёт вызывающий (TradingEngine). Состояние дневного лимита/серии убытков
/// восстанавливается из полной истории сделок, поэтому рестарт не «прощает» паузу.
/// </summary>
public sealed class PortfolioRiskManager
{
    private readonly TradingConfig _config;
    private Func<DateTime> _exchangeUtcNow;
    private readonly object _lock = new();

    private DateTime _currentDay;
    private readonly Dictionary<string, decimal> _symbolDailyPnL = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seenTradeIds = new(StringComparer.OrdinalIgnoreCase);
    private int _consecutiveLosses;
    private DateTime? _pauseUntilUtc;
    private DateTime? _lastLossClosedAtUtc;

    public PortfolioRiskManager(TradingConfig config, Func<DateTime>? exchangeUtcNow = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _exchangeUtcNow = exchangeUtcNow ?? (() => DateTime.UtcNow);
        _currentDay = _exchangeUtcNow().Date;
    }

    public void SetExchangeClock(Func<DateTime> utcNow)
    {
        ArgumentNullException.ThrowIfNull(utcNow);
        lock (_lock)
        {
            _exchangeUtcNow = utcNow;
            RollDayLocked(_exchangeUtcNow());
        }
    }

    public int ConsecutiveLosses { get { lock (_lock) return _consecutiveLosses; } }
    public DateTime? PauseUntilUtc { get { lock (_lock) return _pauseUntilUtc; } }

    public decimal GetSymbolDailyPnL(string symbol)
    {
        lock (_lock)
        {
            RollDayLocked(_exchangeUtcNow());
            return _symbolDailyPnL.TryGetValue(symbol, out var pnl) ? pnl : 0m;
        }
    }

    /// <summary>Пересобрать дневной PnL/серию убытков из полной истории (старые первыми).</summary>
    public void RebuildFromHistory(IEnumerable<TradeRecord> trades)
    {
        ArgumentNullException.ThrowIfNull(trades);
        lock (_lock)
        {
            _symbolDailyPnL.Clear();
            _seenTradeIds.Clear();
            _consecutiveLosses = 0;
            _pauseUntilUtc = null;
            _lastLossClosedAtUtc = null;
            _currentDay = _exchangeUtcNow().Date;

            foreach (var trade in trades.OrderBy(t => t.ClosedAt))
                ApplyTradeLocked(trade);
        }
    }

    /// <summary>Учесть только что сохранённую закрытую сделку (дедуп по Id).</summary>
    public void RecordClosedTrade(TradeRecord trade)
    {
        ArgumentNullException.ThrowIfNull(trade);
        lock (_lock)
        {
            RollDayLocked(_exchangeUtcNow());
            ApplyTradeLocked(trade);
        }
    }

    private void ApplyTradeLocked(TradeRecord trade)
    {
        if (string.IsNullOrWhiteSpace(trade.Id) || !_seenTradeIds.Add(trade.Id))
            return;

        var closedDay = trade.ClosedAt.Date;
        if (closedDay == _currentDay)
        {
            _symbolDailyPnL.TryGetValue(trade.Symbol, out var pnl);
            _symbolDailyPnL[trade.Symbol] = pnl + trade.NetPnL;
        }

        if (trade.NetPnL < 0m)
        {
            _consecutiveLosses++;
            _lastLossClosedAtUtc = trade.ClosedAt;
            if (_config.PortfolioRiskEnabled &&
                _config.LossStreakPauseThreshold > 0 &&
                _consecutiveLosses >= _config.LossStreakPauseThreshold)
            {
                _pauseUntilUtc = trade.ClosedAt.AddMinutes(_config.LossStreakPauseMinutes);
            }
        }
        else
        {
            // Прибыль или безубыток прерывают серию убытков.
            _consecutiveLosses = 0;
            _lastLossClosedAtUtc = null;
        }
    }

    private void RollDayLocked(DateTime nowUtc)
    {
        var day = nowUtc.Date;
        if (day == _currentDay) return;
        _currentDay = day;
        _symbolDailyPnL.Clear();
    }

    /// <summary>Дешёвый гейт до AI: пауза после серии убытков и дневной лимит символа.</summary>
    public bool IsEntryBlocked(string symbol, decimal dayStartBalance, out string reason)
    {
        lock (_lock)
        {
            var now = _exchangeUtcNow();
            RollDayLocked(now);

            if (_config.PortfolioRiskEnabled && _pauseUntilUtc is { } until && now < until)
            {
                reason = $"loss-streak pause until {until:O} ({_consecutiveLosses} consecutive losses)";
                return true;
            }

            if (_config.PortfolioRiskEnabled && _config.SymbolDailyLossLimitPercent > 0m && dayStartBalance > 0m)
            {
                var pnl = _symbolDailyPnL.TryGetValue(symbol, out var v) ? v : 0m;
                var limit = dayStartBalance * _config.SymbolDailyLossLimitPercent / 100m;
                if (pnl <= -limit)
                {
                    reason = $"symbol daily loss limit hit: {symbol} {pnl:F2} <= -{limit:F2}";
                    return true;
                }
            }

            reason = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Полный гейт входа: суммарная экспозиция, корреляционный кластер и дневной/серийный блокировки.
    /// Возвращает также допустимый дополнительный нотариал — движок может урезать объём, а не только отказать.
    /// </summary>
    public PortfolioEntryDecision EvaluateEntry(
        string symbol,
        decimal proposedNotional,
        IReadOnlyList<Position> openPositions,
        decimal equity,
        decimal dayStartBalance,
        Func<string, IReadOnlyList<OHLCV>>? closedCandles = null)
    {
        ArgumentNullException.ThrowIfNull(openPositions);
        if (proposedNotional < 0m) proposedNotional = 0m;

        lock (_lock)
        {
            var now = _exchangeUtcNow();
            RollDayLocked(now);

            if (IsEntryBlockedCore(symbol, dayStartBalance, now, out var blockedReason))
                return PortfolioEntryDecision.Reject(blockedReason);

            if (!_config.PortfolioRiskEnabled)
                return PortfolioEntryDecision.Allow(proposedNotional, 0m, 0m, 0m);

            if (equity <= 0m)
                return PortfolioEntryDecision.Reject("portfolio equity is not positive");

            var existingExposure = openPositions.Sum(MarkedNotional);
            var portfolioCap = equity * _config.MaxPortfolioExposurePercent / 100m;
            var portfolioHeadroom = Math.Max(0m, portfolioCap - existingExposure);
            var maxAdditional = Math.Min(proposedNotional, portfolioHeadroom);

            var correlatedExposure = 0m;
            var maxCorrelation = 0m;
            var correlationReason = string.Empty;

            if (_config.CorrelationRiskEnabled)
            {
                if (closedCandles is null)
                    return PortfolioEntryDecision.Reject("correlation data provider missing (fail-closed)");

                var benchmark = _config.CorrelationBenchmarkSymbol;
                var benchmarkCandles = closedCandles(benchmark);
                var benchmarkReturns = LogReturns(benchmarkCandles, _config.CorrelationLookbackCandles);
                if (benchmarkReturns.Count < _config.MinCorrelationSamples)
                    return PortfolioEntryDecision.Reject(
                        $"correlation benchmark unavailable/insufficient: {benchmark} ({benchmarkReturns.Count}/{_config.MinCorrelationSamples})");

                var candidateCandles = closedCandles(symbol);
                var candidateReturns = LogReturns(candidateCandles, _config.CorrelationLookbackCandles);
                if (candidateReturns.Count < _config.MinCorrelationSamples)
                    return PortfolioEntryDecision.Reject(
                        $"correlation history insufficient: {symbol} ({candidateReturns.Count}/{_config.MinCorrelationSamples})");

                var candidateBtc = Pearson(candidateReturns, benchmarkReturns);
                if (!candidateBtc.HasValue)
                    return PortfolioEntryDecision.Reject($"correlation undefined: {symbol}/{benchmark}");

                maxCorrelation = Math.Abs(candidateBtc.Value);
                var candidateInBtcCluster = candidateBtc.Value >= _config.MaxPairwiseCorrelation;

                // Кластер = кандидат + открытые позиции, сильно коррелированные с кандидатом
                // или входящие в тот же BTC-кластер (12 альтов ≈ ставка на BTC).
                var clusterNotional = proposedNotional;
                var clusterHasCorrelation = candidateInBtcCluster;
                foreach (var pos in openPositions)
                {
                    var posCandles = closedCandles(pos.Symbol);
                    var posReturns = LogReturns(posCandles, _config.CorrelationLookbackCandles);
                    if (posReturns.Count < _config.MinCorrelationSamples)
                        return PortfolioEntryDecision.Reject(
                            $"correlation history insufficient for open position: {pos.Symbol}");

                    var posBtc = Pearson(posReturns, benchmarkReturns);
                    if (!posBtc.HasValue)
                        return PortfolioEntryDecision.Reject($"correlation undefined: {pos.Symbol}/{benchmark}");

                    var pairwise = Pearson(candidateReturns, posReturns);
                    var pairwiseHigh = pairwise.HasValue && pairwise.Value >= _config.MaxPairwiseCorrelation;
                    var sameBtcCluster = candidateInBtcCluster && posBtc.Value >= _config.MaxPairwiseCorrelation;
                    var isBenchmark = pos.Symbol.Equals(benchmark, StringComparison.OrdinalIgnoreCase);

                    if (pairwiseHigh || sameBtcCluster || (candidateInBtcCluster && isBenchmark))
                    {
                        clusterHasCorrelation = true;
                        clusterNotional += MarkedNotional(pos);
                        if (pairwise.HasValue)
                            maxCorrelation = Math.Max(maxCorrelation, Math.Abs(pairwise.Value));
                    }
                }

                if (clusterHasCorrelation)
                {
                    correlatedExposure = clusterNotional;
                    var clusterCap = equity * _config.MaxCorrelatedExposurePercent / 100m;
                    var clusterHeadroom = Math.Max(0m, clusterCap - (clusterNotional - proposedNotional));
                    maxAdditional = Math.Min(maxAdditional, clusterHeadroom);

                    if (maxAdditional <= 0m)
                        correlationReason = $"correlated exposure cap reached ({clusterNotional - proposedNotional:F2}/{clusterCap:F2})";
                }
            }

            if (maxAdditional <= 0m)
            {
                var reason = !string.IsNullOrEmpty(correlationReason)
                    ? correlationReason
                    : $"portfolio exposure cap reached ({existingExposure:F2}/{portfolioCap:F2})";
                return new PortfolioEntryDecision
                {
                    Allowed = false,
                    Reason = reason,
                    MaxAdditionalNotional = 0m,
                    PortfolioExposure = existingExposure,
                    CorrelatedExposure = correlatedExposure,
                    MaxCorrelation = maxCorrelation
                };
            }

            return new PortfolioEntryDecision
            {
                Allowed = true,
                Reason = string.Empty,
                MaxAdditionalNotional = maxAdditional,
                PortfolioExposure = existingExposure + maxAdditional,
                CorrelatedExposure = correlatedExposure > 0m ? correlatedExposure - proposedNotional + maxAdditional : 0m,
                MaxCorrelation = maxCorrelation
            };
        }
    }

    private bool IsEntryBlockedCore(string symbol, decimal dayStartBalance, DateTime now, out string reason)
    {
        if (_config.PortfolioRiskEnabled && _pauseUntilUtc is { } until)
        {
            if (now < until)
            {
                reason = $"loss-streak pause until {until:O} ({_consecutiveLosses} consecutive losses)";
                return true;
            }

            // Пауза истекла — серия больше не должна мгновенно взводить новую паузу на следующем убытке.
            _pauseUntilUtc = null;
            _consecutiveLosses = 0;
            _lastLossClosedAtUtc = null;
        }

        if (_config.PortfolioRiskEnabled && _config.SymbolDailyLossLimitPercent > 0m && dayStartBalance > 0m)
        {
            var pnl = _symbolDailyPnL.TryGetValue(symbol, out var v) ? v : 0m;
            var limit = dayStartBalance * _config.SymbolDailyLossLimitPercent / 100m;
            if (pnl <= -limit)
            {
                reason = $"symbol daily loss limit hit: {symbol} {pnl:F2} <= -{limit:F2}";
                return true;
            }
        }

        reason = string.Empty;
        return false;
    }

    private static decimal MarkedNotional(Position p) =>
        p.Quantity * (p.CurrentPrice > 0m ? p.CurrentPrice : p.EntryPrice);

    /// <summary>Лог-доходности по закрытым свечам, выровненные по OpenTime.</summary>
    public static Dictionary<DateTime, double> LogReturns(IReadOnlyList<OHLCV> candles, int lookback)
    {
        var result = new Dictionary<DateTime, double>();
        if (candles.Count < 2) return result;

        var ordered = candles
            .Where(c => c.Close > 0m)
            .OrderBy(c => c.OpenTime)
            .TakeLast(Math.Max(2, lookback + 1))
            .ToList();

        for (var i = 1; i < ordered.Count; i++)
        {
            var prev = ordered[i - 1];
            var cur = ordered[i];
            if (prev.Close <= 0m || cur.Close <= 0m) continue;
            result[cur.OpenTime] = Math.Log((double)(cur.Close / prev.Close));
        }

        return result;
    }

    /// <summary>Pearson по пересечению timestamps; null если пересечение мало или дисперсия нулевая.</summary>
    public static decimal? Pearson(Dictionary<DateTime, double> a, Dictionary<DateTime, double> b)
    {
        var keys = a.Keys.Intersect(b.Keys).OrderBy(k => k).ToList();
        if (keys.Count < 2) return null;

        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
        foreach (var k in keys)
        {
            var x = a[k];
            var y = b[k];
            sx += x; sy += y;
            sxx += x * x; syy += y * y;
            sxy += x * y;
        }

        var n = keys.Count;
        var cov = sxy - sx * sy / n;
        var vx = sxx - sx * sx / n;
        var vy = syy - sy * sy / n;
        if (vx <= 1e-18 || vy <= 1e-18) return null;

        var r = cov / Math.Sqrt(vx * vy);
        if (double.IsNaN(r) || double.IsInfinity(r)) return null;
        return (decimal)Math.Clamp(r, -1.0, 1.0);
    }
}

public sealed record PortfolioEntryDecision
{
    public bool Allowed { get; init; }
    public string Reason { get; init; } = string.Empty;
    public decimal MaxAdditionalNotional { get; init; }
    public decimal PortfolioExposure { get; init; }
    public decimal CorrelatedExposure { get; init; }
    public decimal MaxCorrelation { get; init; }

    public static PortfolioEntryDecision Allow(decimal notional, decimal exposure, decimal correlated, decimal corr) => new()
    {
        Allowed = true,
        MaxAdditionalNotional = notional,
        PortfolioExposure = exposure,
        CorrelatedExposure = correlated,
        MaxCorrelation = corr
    };

    public static PortfolioEntryDecision Reject(string reason) => new()
    {
        Allowed = false,
        Reason = reason
    };
}
