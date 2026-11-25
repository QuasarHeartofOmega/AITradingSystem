using AITradingSystem.Core.Models.MarketData;

namespace AITradingSystem.Core.Indicators;

/// <summary>
/// Stochastic RSI (FR-3.1).
/// Комбинация RSI и Stochastic Oscillator.
/// </summary>
public sealed class StochasticRsi : BaseIndicator
{
    private readonly RSI _rsi;
    private readonly int _stochPeriod;
    private readonly int _kSmooth;
    private readonly int _dSmooth;
    private readonly Queue<decimal> _rsiBuffer = new();
    private readonly Queue<decimal> _kBuffer = new();
    private readonly Queue<decimal> _dBuffer = new();
    private decimal _k;
    private decimal _d;

    public override string Name => $"StochRSI_{_rsi.WarmupPeriod - 1}_{_stochPeriod}_{_kSmooth}_{_dSmooth}";
    public override int WarmupPeriod => _rsi.WarmupPeriod + _stochPeriod + _dSmooth;

    public decimal K => _k;
    public decimal D => _d;

    public StochasticRsi(int rsiPeriod = 14, int stochPeriod = 14, int kSmooth = 3, int dSmooth = 3)
    {
        _rsi = new RSI(rsiPeriod);
        _stochPeriod = stochPeriod;
        _kSmooth = kSmooth;
        _dSmooth = dSmooth;
    }

    public override void Update(OHLCV candle)
    {
        _rsi.Update(candle);
        ProcessedCount++;

        if (!_rsi.IsReady)
            return;

        var rsiValue = _rsi.GetValue();
        _rsiBuffer.Enqueue(rsiValue);
        if (_rsiBuffer.Count > _stochPeriod)
            _rsiBuffer.Dequeue();

        if (_rsiBuffer.Count == _stochPeriod)
        {
            var min = _rsiBuffer.Min();
            var max = _rsiBuffer.Max();
            var range = max - min;

            var rawK = range > 0 ? (rsiValue - min) / range * 100m : 50m;
            _kBuffer.Enqueue(rawK);
            if (_kBuffer.Count > _kSmooth)
                _kBuffer.Dequeue();

            if (_kBuffer.Count == _kSmooth)
            {
                _k = _kBuffer.Average();

                // D — SMA от K за _dSmooth периодов
                _dBuffer.Enqueue(_k);
                if (_dBuffer.Count > _dSmooth)
                    _dBuffer.Dequeue();

                if (_dBuffer.Count == _dSmooth)
                    _d = _dBuffer.Average();
            }
        }
    }

    public override decimal UpdateUnfinished(OHLCV candle)
    {
        return _k;
    }

    public override decimal GetValue() => _k;

    public override void Reset()
    {
        _rsi.Reset();
        _rsiBuffer.Clear();
        _kBuffer.Clear();
        _dBuffer.Clear();
        _k = 0;
        _d = 0;
        ProcessedCount = 0;
    }
}