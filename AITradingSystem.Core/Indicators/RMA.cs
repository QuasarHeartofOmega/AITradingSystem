namespace AITradingSystem.Core.Indicators;

/// <summary>
/// Wilder Moving Average (RMA): α = 1/p, затравка — SMA первых p значений.
/// Именно так TA-Lib/TradingView сглаживают +DM/−DM/TR/DX в ADX (Phase 1.6).
/// Отличие от EMA (α = 2/(p+1)): RMA «длиннее» по памяти; прежняя реализация ADX
/// на EMA давала расхождение с эталоном ~9.5 пт.
/// </summary>
public sealed class RMA
{
    private readonly int _period;
    private readonly decimal _alpha;
    private decimal[]? _seedBuffer;
    private decimal _seedSum;
    private int _seedCount;
    private decimal _value;

    public RMA(int period)
    {
        if (period < 1)
            throw new ArgumentOutOfRangeException(nameof(period), "Period must be >= 1");
        _period = period;
        _alpha = 1m / period;
        _seedBuffer = new decimal[period];
    }

    public int Period => _period;

    /// <summary>Накоплено p значений и посчитана SMA-затравка.</summary>
    public bool IsReady { get; private set; }

    public decimal Value => _value;

    public void Update(decimal value)
    {
        if (!IsReady)
        {
            _seedBuffer![_seedCount] = value;
            _seedSum += value;
            _seedCount++;
            if (_seedCount == _period)
            {
                _value = _seedSum / _period;
                IsReady = true;
                _seedBuffer = null;
                _seedSum = 0m;
            }
            return;
        }

        // Wilder: value/p + prev·(p−1)/p
        _value = _alpha * value + (1m - _alpha) * _value;
    }

    public void Reset()
    {
        _seedBuffer = new decimal[_period];
        _seedSum = 0m;
        _seedCount = 0;
        _value = 0m;
        IsReady = false;
    }
}
