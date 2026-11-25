using AITradingSystem.Core.Models.MarketData;

namespace AITradingSystem.Core.Indicators;

/// <summary>
/// On-Balance Volume (FR-3.1).
/// Кумулятивный индикатор объема.
/// </summary>
public sealed class OBV : BaseIndicator
{
    private decimal _value;
    private decimal _prevClose;
    private bool _hasPrev;
    private readonly EMA _trendEma = new(20);

    public override string Name => "OBV";
    public override int WarmupPeriod => 1;

    /// <summary>
    /// Нормализованный тренд OBV [-1; 1]: отклонение OBV от его EMA(20).
    /// </summary>
    public decimal Trend
    {
        get
        {
            var ema = _trendEma.GetValue();
            var denom = Math.Abs(ema) + 1m;
            return Math.Clamp((_value - ema) / denom * 10m, -1m, 1m);
        }
    }

    public override void Update(OHLCV candle)
    {
        ProcessedCount++;

        if (!_hasPrev)
        {
            _value = candle.Volume;
            _prevClose = candle.Close;
            _hasPrev = true;
            _trendEma.Update(new OHLCV { Close = _value });
            return;
        }

        if (candle.Close > _prevClose)
            _value += candle.Volume;
        else if (candle.Close < _prevClose)
            _value -= candle.Volume;

        _prevClose = candle.Close;
        _trendEma.Update(new OHLCV { Close = _value });
    }

    public override decimal UpdateUnfinished(OHLCV candle)
    {
        if (!_hasPrev)
            return candle.Volume;

        var obv = _value;
        if (candle.Close > _prevClose)
            obv += candle.Volume;
        else if (candle.Close < _prevClose)
            obv -= candle.Volume;

        return obv;
    }

    public override decimal GetValue() => _value;

    public override void Reset()
    {
        _value = 0;
        _prevClose = 0;
        _hasPrev = false;
        _trendEma.Reset();
        ProcessedCount = 0;
    }
}