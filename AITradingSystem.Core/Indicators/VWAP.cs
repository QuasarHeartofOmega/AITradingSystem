using AITradingSystem.Core.Models.MarketData;

namespace AITradingSystem.Core.Indicators;

/// <summary>
/// Volume Weighted Average Price (FR-3.1).
/// Сбрасывается ежедневно (session-based).
/// </summary>
public sealed class VWAP : BaseIndicator
{
    private decimal _cumulativeTPV;
    private decimal _cumulativeVolume;
    private DateTime _currentDay;

    public override string Name => "VWAP";
    public override int WarmupPeriod => 1;

    public override void Update(OHLCV candle)
    {
        ProcessedCount++;

        var day = candle.OpenTime.Date;
        if (day != _currentDay)
        {
            // Новый день — сброс
            _cumulativeTPV = 0;
            _cumulativeVolume = 0;
            _currentDay = day;
        }

        var tp = candle.TypicalPrice;
        _cumulativeTPV += tp * candle.Volume;
        _cumulativeVolume += candle.Volume;
    }

    public override decimal UpdateUnfinished(OHLCV candle)
    {
        var tp = candle.TypicalPrice;
        var totalTPV = _cumulativeTPV + tp * candle.Volume;
        var totalVolume = _cumulativeVolume + candle.Volume;

        return totalVolume > 0 ? totalTPV / totalVolume : candle.Close;
    }

    public override decimal GetValue()
    {
        return _cumulativeVolume > 0 ? _cumulativeTPV / _cumulativeVolume : 0;
    }

    public override void Reset()
    {
        _cumulativeTPV = 0;
        _cumulativeVolume = 0;
        _currentDay = DateTime.MinValue;
        ProcessedCount = 0;
    }
}