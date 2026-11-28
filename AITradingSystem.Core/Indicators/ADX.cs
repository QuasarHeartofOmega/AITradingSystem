using AITradingSystem.Core.Models.MarketData;

namespace AITradingSystem.Core.Indicators;

/// <summary>
/// Average Directional Index (FR-3.1).
/// Измеряет силу тренда (не направление).
///
/// Phase 1.6 — формула Уайлдера:
///  - сглаживание +DM/−DM/TR/DX через RMA (α=1/p, SMA-затравка), а не EMA;
///  - при +DM == −DM (в т.ч. оба &gt; 0) обнуляются ОБА значения — прежний код
///    мутировал plusDM до вычисления minusDM, из-за чего на равенстве оставался −DM;
///  - первый ADX = SMA первых p значений DX (затравка RMA), как в TA-Lib.
/// </summary>
public sealed class ADX : BaseIndicator
{
    private readonly int _period;
    private readonly RMA _rmaPlusDM;
    private readonly RMA _rmaMinusDM;
    private readonly RMA _rmaTR;
    private readonly RMA _rmaDX;
    private decimal _prevHigh;
    private decimal _prevLow;
    private decimal _prevClose;
    private bool _hasPrev;

    public override string Name => $"ADX_{_period}";

    /// <summary>
    /// Свеча 1 — только prev; свечи 2..p+1 — SMA-затравка RMA(+DM/−DM/TR);
    /// с свечи p+1 начинается DX, ещё p значений DX дают затравку RMA(DX) → 2·p.
    /// </summary>
    public override int WarmupPeriod => _period * 2;

    public decimal Value { get; private set; }
    public decimal PlusDI { get; private set; }
    public decimal MinusDI { get; private set; }

    public ADX(int period = 14)
    {
        if (period < 1)
            throw new ArgumentOutOfRangeException(nameof(period));
        _period = period;
        _rmaPlusDM = new RMA(period);
        _rmaMinusDM = new RMA(period);
        _rmaTR = new RMA(period);
        _rmaDX = new RMA(period);
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
        var upMove = candle.High - _prevHigh;
        var downMove = _prevLow - candle.Low;

        // Уайлдер: сравнение с НЕИЗМЕНЁННЫМИ upMove/downMove;
        // равенство (в т.ч. оба > 0) → оба DM равны 0.
        var plusDM = (upMove > downMove && upMove > 0) ? upMove : 0m;
        var minusDM = (downMove > upMove && downMove > 0) ? downMove : 0m;

        _rmaPlusDM.Update(plusDM);
        _rmaMinusDM.Update(minusDM);
        _rmaTR.Update(tr);

        if (_rmaTR.IsReady && _rmaTR.Value > 0m)
        {
            PlusDI = _rmaPlusDM.Value / _rmaTR.Value * 100m;
            MinusDI = _rmaMinusDM.Value / _rmaTR.Value * 100m;

            var diSum = PlusDI + MinusDI;
            var dx = diSum > 0m ? Math.Abs(PlusDI - MinusDI) / diSum * 100m : 0m;

            _rmaDX.Update(dx);
            if (_rmaDX.IsReady)
                Value = _rmaDX.Value;
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
        _rmaPlusDM.Reset();
        _rmaMinusDM.Reset();
        _rmaTR.Reset();
        _rmaDX.Reset();
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
