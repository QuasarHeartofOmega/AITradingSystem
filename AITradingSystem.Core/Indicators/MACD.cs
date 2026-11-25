using AITradingSystem.Core.Models.MarketData;

namespace AITradingSystem.Core.Indicators;

/// <summary>
/// MACD (Moving Average Convergence Divergence) (FR-3.1).
/// Параметры по умолчанию: 12, 26, 9.
/// </summary>
public sealed class MACD : BaseIndicator
{
    private readonly EMA _fast;
    private readonly EMA _slow;
    private readonly EMA _signal;
    private decimal _macdLine;
    private decimal _signalLine;
    private decimal _histogram;

    public int FastPeriod { get; }
    public int SlowPeriod { get; }
    public int SignalPeriod { get; }

    public override string Name => $"MACD_{FastPeriod}_{SlowPeriod}_{SignalPeriod}";
    public override int WarmupPeriod => SlowPeriod + SignalPeriod;

    public decimal MacdLine => _macdLine;
    public decimal SignalLine => _signalLine;
    public decimal Histogram => _histogram;

    public MACD(int fastPeriod = 12, int slowPeriod = 26, int signalPeriod = 9)
    {
        if (fastPeriod >= slowPeriod)
            throw new ArgumentException("fastPeriod must be < slowPeriod");

        FastPeriod = fastPeriod;
        SlowPeriod = slowPeriod;
        SignalPeriod = signalPeriod;

        _fast = new EMA(fastPeriod);
        _slow = new EMA(slowPeriod);
        _signal = new EMA(signalPeriod);
    }

    public override void Update(OHLCV candle)
    {
        _fast.Update(candle);
        _slow.Update(candle);

        if (_fast.IsReady && _slow.IsReady)
        {
            _macdLine = _fast.GetValue() - _slow.GetValue();

            // Создаем искусственную свечу для сигнальной EMA
            var signalCandle = new OHLCV
            {
                Close = _macdLine,
                Open = _macdLine,
                High = _macdLine,
                Low = _macdLine,
                OpenTime = candle.OpenTime,
                CloseTime = candle.CloseTime
            };
            _signal.Update(signalCandle);

            if (_signal.IsReady)
            {
                _signalLine = _signal.GetValue();
                _histogram = _macdLine - _signalLine;
            }
        }

        ProcessedCount++;
    }

    public override decimal UpdateUnfinished(OHLCV candle)
    {
        var fastUnfinished = _fast.UpdateUnfinished(candle);
        var slowUnfinished = _slow.UpdateUnfinished(candle);

        if (!_fast.IsReady || !_slow.IsReady)
            return 0;

        return fastUnfinished - slowUnfinished;
    }

    public override decimal GetValue() => _macdLine;

    public override void Reset()
    {
        _fast.Reset();
        _slow.Reset();
        _signal.Reset();
        _macdLine = 0;
        _signalLine = 0;
        _histogram = 0;
        ProcessedCount = 0;
    }
}