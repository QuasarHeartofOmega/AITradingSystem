using AITradingSystem.Core.Models.MarketData;

namespace AITradingSystem.Core.Indicators;

/// <summary>
/// Exponential Moving Average (FR-3.1).
/// Инкрементальный расчет: O(1) на свечу после прогрева.
///
/// Phase 1.6 — конвенция затравки TA-Lib: первые p значений накапливаются,
/// EMA стартует с SMA(p), далее рекуррента α = 2/(p+1). Прежняя конвенция
/// (seed = первая цена) для EMA200 давала вес затравки ≈14% на 200 барах
/// и ≈5% на 300 — расхождение с эталонами TA-Lib/TradingView.
/// MACD использует те же EMA: сигнальная линия получает значения MACD только
/// после готовности fast/slow, поэтому SMA-seed согласован и там.
/// </summary>
public sealed class EMA : BaseIndicator
{
    private readonly int _period;
    private readonly decimal _multiplier;
    private decimal[]? _seedBuffer;
    private decimal _seedSum;
    private decimal _value;

    public override string Name => $"EMA_{_period}";
    public override int WarmupPeriod => _period;

    public EMA(int period)
    {
        if (period < 1)
            throw new ArgumentOutOfRangeException(nameof(period), "Period must be >= 1");
        _period = period;
        _multiplier = 2m / (period + 1m);
        _seedBuffer = new decimal[period];
    }

    public override void Update(OHLCV candle)
    {
        if (ProcessedCount < _period)
        {
            _seedBuffer![ProcessedCount] = candle.Close;
            _seedSum += candle.Close;
            ProcessedCount++;
            if (ProcessedCount == _period)
            {
                // SMA-затравка (TA-Lib): EMA готова ровно на p-й свече.
                _value = _seedSum / _period;
                _seedBuffer = null;
            }
            return;
        }

        _value = (candle.Close - _value) * _multiplier + _value;
        ProcessedCount++;
    }

    public override decimal UpdateUnfinished(OHLCV candle)
    {
        if (ProcessedCount == 0)
            return candle.Close;

        if (ProcessedCount < _period)
        {
            // Прогрев ещё не завершён — частичная SMA с текущей свечой.
            return (_seedSum + candle.Close) / (ProcessedCount + 1);
        }

        return (candle.Close - _value) * _multiplier + _value;
    }

    public override decimal GetValue() => _value;

    public override void Reset()
    {
        _seedBuffer = new decimal[_period];
        _seedSum = 0;
        _value = 0;
        ProcessedCount = 0;
    }
}
