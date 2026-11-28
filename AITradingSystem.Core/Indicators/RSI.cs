using AITradingSystem.Core.Models.MarketData;

namespace AITradingSystem.Core.Indicators;

/// <summary>
/// Relative Strength Index (FR-3.1).
/// Инкрементальный расчет через Wilder's smoothing.
/// </summary>
public sealed class RSI : BaseIndicator
{
    private readonly int _period;
    private decimal _avgGain;
    private decimal _avgLoss;
    private decimal _lastClose;
    private decimal _value;
    private bool _firstUpdate = true;

    public override string Name => $"RSI_{_period}";
    public override int WarmupPeriod => _period + 1;

    public RSI(int period = 14)
    {
        if (period < 1)
            throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
    }

    public override void Update(OHLCV candle)
    {
        if (_firstUpdate)
        {
            _lastClose = candle.Close;
            _firstUpdate = false;
            ProcessedCount++;
            return;
        }

        var change = candle.Close - _lastClose;
        var gain = change > 0 ? change : 0m;
        var loss = change < 0 ? -change : 0m;

        if (ProcessedCount <= _period)
        {
            // Начальный период: простое среднее
            _avgGain = (_avgGain * (ProcessedCount - 1) + gain) / ProcessedCount;
            _avgLoss = (_avgLoss * (ProcessedCount - 1) + loss) / ProcessedCount;
        }
        else
        {
            // Wilder's smoothing
            _avgGain = (_avgGain * (_period - 1) + gain) / _period;
            _avgLoss = (_avgLoss * (_period - 1) + loss) / _period;
        }

        _value = CalculateRSI();
        _lastClose = candle.Close;
        ProcessedCount++;
    }

    public override decimal UpdateUnfinished(OHLCV candle)
    {
        if (_firstUpdate || ProcessedCount < 2)
            return 50m;

        var change = candle.Close - _lastClose;
        var gain = change > 0 ? change : 0m;
        var loss = change < 0 ? -change : 0m;

        decimal avgGain, avgLoss;
        if (ProcessedCount <= _period)
        {
            avgGain = (_avgGain * (ProcessedCount - 1) + gain) / ProcessedCount;
            avgLoss = (_avgLoss * (ProcessedCount - 1) + loss) / ProcessedCount;
        }
        else
        {
            avgGain = (_avgGain * (_period - 1) + gain) / _period;
            avgLoss = (_avgLoss * (_period - 1) + loss) / _period;
        }

        if (avgLoss == 0) return avgGain == 0 ? 50m : 100m; // 1.6: плоский рынок → 50, а не 100
        var rs = avgGain / avgLoss;
        return 100m - (100m / (1m + rs));
    }

    public override decimal GetValue() => _value;

    private decimal CalculateRSI()
    {
        if (_avgLoss == 0) return _avgGain == 0 ? 50m : 100m; // 1.6: плоский рынок → 50, а не 100
        var rs = _avgGain / _avgLoss;
        return 100m - (100m / (1m + rs));
    }

    public override void Reset()
    {
        _avgGain = 0;
        _avgLoss = 0;
        _lastClose = 0;
        _value = 0;
        _firstUpdate = true;
        ProcessedCount = 0;
    }
}