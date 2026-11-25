using AITradingSystem.Core.Models.MarketData;

namespace AITradingSystem.Core.Indicators;

/// <summary>
/// Average True Range (FR-3.1).
/// Используется для SL/TP и position sizing (FR-5.1, FR-5.3).
/// </summary>
public sealed class ATR : BaseIndicator
{
    private readonly int _period;
    private decimal _value;
    private decimal _lastClose;
    private bool _hasLastClose;

    public override string Name => $"ATR_{_period}";
    public override int WarmupPeriod => _period + 1;

    public ATR(int period = 14)
    {
        if (period < 1)
            throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
    }

    public override void Update(OHLCV candle)
    {
        var tr = CalculateTR(candle);

        if (!_hasLastClose)
        {
            _lastClose = candle.Close;
            _hasLastClose = true;
            ProcessedCount++;
            return;
        }

        if (ProcessedCount <= _period)
        {
            // Начальный период: SMA
            _value = (_value * (ProcessedCount - 1) + tr) / ProcessedCount;
        }
        else
        {
            // Wilder's smoothing
            _value = (_value * (_period - 1) + tr) / _period;
        }

        _lastClose = candle.Close;
        ProcessedCount++;
    }

    public override decimal UpdateUnfinished(OHLCV candle)
    {
        if (!_hasLastClose || ProcessedCount < 2)
            return 0;

        var tr = candle.TrueRange(_lastClose);

        if (ProcessedCount <= _period)
            return (_value * (ProcessedCount - 1) + tr) / ProcessedCount;

        return (_value * (_period - 1) + tr) / _period;
    }

    public override decimal GetValue() => _value;

    private decimal CalculateTR(OHLCV candle)
    {
        if (!_hasLastClose)
            return candle.High - candle.Low;

        return candle.TrueRange(_lastClose);
    }

    public override void Reset()
    {
        _value = 0;
        _lastClose = 0;
        _hasLastClose = false;
        ProcessedCount = 0;
    }
}