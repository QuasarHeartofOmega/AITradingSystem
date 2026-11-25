using AITradingSystem.Core.Models.MarketData;

namespace AITradingSystem.Core.Indicators;

/// <summary>
/// Bollinger Bands (FR-3.1).
/// Параметры: период (20), множитель стандартного отклонения (2).
/// </summary>
public sealed class BollingerBands : BaseIndicator
{
    private readonly int _period;
    private readonly decimal _multiplier;
    private readonly Queue<decimal> _buffer = new();
    private decimal _sma;
    private decimal _upper;
    private decimal _lower;

    public override string Name => $"BB_{_period}_{_multiplier}";
    public override int WarmupPeriod => _period;

    public decimal Middle => _sma;
    public decimal Upper => _upper;
    public decimal Lower => _lower;
    public decimal Width => _sma > 0 ? (_upper - _lower) / _sma : 0;

    public BollingerBands(int period = 20, decimal multiplier = 2.0m)
    {
        if (period < 2)
            throw new ArgumentOutOfRangeException(nameof(period));
        if (multiplier <= 0)
            throw new ArgumentOutOfRangeException(nameof(multiplier));

        _period = period;
        _multiplier = multiplier;
    }

    public override void Update(OHLCV candle)
    {
        _buffer.Enqueue(candle.Close);
        if (_buffer.Count > _period)
            _buffer.Dequeue();

        if (_buffer.Count == _period)
        {
            Calculate();
        }

        ProcessedCount++;
    }

    public override decimal UpdateUnfinished(OHLCV candle)
    {
        if (_buffer.Count < _period - 1)
            return candle.Close;

        // Включаем текущую цену в расчет
        var tempBuffer = new List<decimal>(_buffer) { candle.Close };
        if (tempBuffer.Count > _period)
            tempBuffer.RemoveAt(0);

        var avg = tempBuffer.Average();
        var variance = tempBuffer.Sum(x => (x - avg) * (x - avg)) / tempBuffer.Count;
        var stdDev = (decimal)Math.Sqrt((double)variance);

        return avg; // возвращаем middle для совместимости
    }

    public override decimal GetValue() => _sma;

    private void Calculate()
    {
        _sma = _buffer.Average();
        var variance = _buffer.Sum(x => (x - _sma) * (x - _sma)) / _buffer.Count;
        var stdDev = (decimal)Math.Sqrt((double)variance);

        _upper = _sma + _multiplier * stdDev;
        _lower = _sma - _multiplier * stdDev;
    }

    public override void Reset()
    {
        _buffer.Clear();
        _sma = 0;
        _upper = 0;
        _lower = 0;
        ProcessedCount = 0;
    }
}