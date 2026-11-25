using AITradingSystem.Core.Models.MarketData;

namespace AITradingSystem.Core.Indicators;

/// <summary>
/// Average Directional Index (FR-3.1).
/// Измеряет силу тренда (не направление).
/// </summary>
public sealed class ADX : BaseIndicator
{
    private readonly int _period;
    private readonly EMA _smoothedPlusDM;
    private readonly EMA _smoothedMinusDM;
    private readonly EMA _smoothedTR;
    private readonly EMA _smoothedDX;
    private decimal _prevHigh;
    private decimal _prevLow;
    private decimal _prevClose;
    private bool _hasPrev;

    public override string Name => $"ADX_{_period}";
    public override int WarmupPeriod => _period * 2 + 1; // prev + сглаживание DM/TR + сглаживание DX

    public decimal Value { get; private set; }
    public decimal PlusDI { get; private set; }
    public decimal MinusDI { get; private set; }

    public ADX(int period = 14)
    {
        if (period < 1)
            throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
        _smoothedPlusDM = new EMA(period);
        _smoothedMinusDM = new EMA(period);
        _smoothedTR = new EMA(period);
        _smoothedDX = new EMA(period);
    }

    public override void Update(OHLCV candle)
    {
        ProcessedCount++;

        if (!_hasPrev)
        {
            _prevHigh = candle.High;
            _prevLow = candle.Low;
            _prevClose = candle.Close;
            _hasPrev = true;
            return;
        }

        var tr = candle.TrueRange(_prevClose);
        var plusDM = candle.High - _prevHigh;
        var minusDM = _prevLow - candle.Low;

        plusDM = (plusDM > minusDM && plusDM > 0) ? plusDM : 0;
        minusDM = (minusDM > plusDM && minusDM > 0) ? minusDM : 0;

        // Создаем свечи для EMA
        var plusDMCandle = new OHLCV { Close = plusDM, OpenTime = candle.OpenTime, CloseTime = candle.CloseTime };
        var minusDMCandle = new OHLCV { Close = minusDM, OpenTime = candle.OpenTime, CloseTime = candle.CloseTime };
        var trCandle = new OHLCV { Close = tr, OpenTime = candle.OpenTime, CloseTime = candle.CloseTime };

        _smoothedPlusDM.Update(plusDMCandle);
        _smoothedMinusDM.Update(minusDMCandle);
        _smoothedTR.Update(trCandle);

        if (_smoothedPlusDM.IsReady && _smoothedTR.IsReady && _smoothedTR.GetValue() > 0)
        {
            var plusDI = _smoothedPlusDM.GetValue() / _smoothedTR.GetValue() * 100m;
            var minusDI = _smoothedMinusDM.GetValue() / _smoothedTR.GetValue() * 100m;

            var diSum = plusDI + minusDI;
            var dx = diSum > 0 ? Math.Abs(plusDI - minusDI) / diSum * 100m : 0;

            var dxCandle = new OHLCV { Close = dx, OpenTime = candle.OpenTime, CloseTime = candle.CloseTime };
            _smoothedDX.Update(dxCandle);

            if (_smoothedDX.IsReady)
            {
                Value = _smoothedDX.GetValue();
                PlusDI = plusDI;
                MinusDI = minusDI;
            }
        }

        _prevHigh = candle.High;
        _prevLow = candle.Low;
        _prevClose = candle.Close;
    }

    public override decimal UpdateUnfinished(OHLCV candle)
    {
        return Value;
    }

    public override decimal GetValue() => Value;

    public override void Reset()
    {
        _smoothedPlusDM.Reset();
        _smoothedMinusDM.Reset();
        _smoothedTR.Reset();
        _smoothedDX.Reset();
        _prevHigh = 0;
        _prevLow = 0;
        _prevClose = 0;
        _hasPrev = false;
        Value = 0;
        PlusDI = 0;
        MinusDI = 0;
        ProcessedCount = 0;
    }
}