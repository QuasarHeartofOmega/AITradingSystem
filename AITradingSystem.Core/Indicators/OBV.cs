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
    private readonly EMA _volumeEma = new(20);

    /// <summary>1.6: насыщение ±1 достигается при отклонении OBV от EMA(20)
    /// в N средних объёмов бара. Константа — явный кандидат на валидацию в P2.6.</summary>
    private const decimal TrendSaturationBars = 3m;

    public override string Name => "OBV";
    public override int WarmupPeriod => 1;

    /// <summary>
    /// Нормализованный тренд OBV [-1; 1]: отклонение OBV от его EMA(20).
    /// 1.6: scale-free метрика — отклонение в единицах среднего объёма бара (EMA20 объёма).
    /// Инвариантна к масштабу объёма и не зависит от неограниченно растущего абсолютного
    /// уровня OBV (прежняя формула ×10/(|EMA|+1) зависела от обоих).
    /// </summary>
    public decimal Trend
    {
        get
        {
            if (!_trendEma.IsReady || !_volumeEma.IsReady)
                return 0m;

            var scale = _volumeEma.GetValue();
            if (scale <= 0m)
                return 0m;

            return Math.Clamp((_value - _trendEma.GetValue()) / (scale * TrendSaturationBars), -1m, 1m);
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
            _volumeEma.Update(new OHLCV { Close = candle.Volume });
            return;
        }

        if (candle.Close > _prevClose)
            _value += candle.Volume;
        else if (candle.Close < _prevClose)
            _value -= candle.Volume;

        _prevClose = candle.Close;
        _trendEma.Update(new OHLCV { Close = _value });
        _volumeEma.Update(new OHLCV { Close = candle.Volume });
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
        _volumeEma.Reset();
        ProcessedCount = 0;
    }
}