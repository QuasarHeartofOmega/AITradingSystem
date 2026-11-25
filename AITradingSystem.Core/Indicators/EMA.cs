using AITradingSystem.Core.Models.MarketData;

namespace AITradingSystem.Core.Indicators;

/// <summary>
/// Exponential Moving Average (FR-3.1).
/// Инкрементальный расчет: O(1) на свечу.
/// </summary>
public sealed class EMA : BaseIndicator
{
    private readonly int _period;
    private readonly decimal _multiplier;
    private decimal _value;

    public override string Name => $"EMA_{_period}";
    public override int WarmupPeriod => _period;

    public EMA(int period)
    {
        if (period < 1)
            throw new ArgumentOutOfRangeException(nameof(period), "Period must be >= 1");
        _period = period;
        _multiplier = 2m / (period + 1m);
    }

    public override void Update(OHLCV candle)
    {
        if (ProcessedCount == 0)
        {
            _value = candle.Close;
        }
        else
        {
            _value = (candle.Close - _value) * _multiplier + _value;
        }
        ProcessedCount++;
    }

    public override decimal UpdateUnfinished(OHLCV candle)
    {
        if (ProcessedCount == 0)
            return candle.Close;

        return (candle.Close - _value) * _multiplier + _value;
    }

    public override decimal GetValue() => _value;

    public override void Reset()
    {
        _value = 0;
        ProcessedCount = 0;
    }
}