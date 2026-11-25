using AITradingSystem.Core.Models.MarketData;

namespace AITradingSystem.Core.Indicators;

/// <summary>
/// Агрегатор всех индикаторов для одной торговой пары.
/// </summary>
public sealed class IndicatorCalculator
{
    public string Symbol { get; }

    // Тренд
    public EMA EMA20 { get; }
    public EMA EMA50 { get; }
    public EMA EMA200 { get; }
    public Supertrend Supertrend { get; }
    public ADX ADX { get; }

    // Импульс
    public RSI RSI { get; }
    public MACD MACD { get; }
    public StochasticRsi StochasticRsi { get; }

    // Волатильность
    public ATR ATR { get; }
    public BollingerBands BollingerBands { get; }

    // Объем
    public OBV OBV { get; }
    public VWAP VWAP { get; }

    /// <summary>Окно объёмов для volume_ratio (B10): последняя закрытая свеча / SMA20 объёма.</summary>
    private readonly Queue<decimal> _volumeWindow = new();
    private const int VolumeWindow = 20;
    private decimal _volumeSum;

    /// <summary>P1.7: окно закрытых свечей для корреляции портфельного риска.</summary>
    private readonly Queue<OHLCV> _closedCandles = new();
    private const int MaxClosedCandleHistory = 1000;

    /// <summary>Сколько свечей прошло через калькулятор (для лога прогрева N/Required).</summary>
    public int ClosedCandles { get; private set; }

    /// <summary>P1.4: OpenTime последней принятой закрытой свечи; null — истории ещё не было.</summary>
    public DateTime? LastClosedOpenTime { get; private set; }

    /// <summary>SMA20 объёма закрытых свечей (0 — ещё не прогрето).</summary>
    public decimal VolumeSma20 => _volumeWindow.Count > 0 ? _volumeSum / _volumeWindow.Count : 0m;

    /// <summary>Объём последней свечи, принятой калькулятором как закрытая. Тики её не меняют.</summary>
    public decimal LastClosedVolume { get; private set; }

    /// <summary>Объём последней закрытой свечи / SMA20 закрытых свечей; 0 — нет данных.</summary>
    public decimal VolumeRatio => GetVolumeRatio(LastClosedVolume);

    /// <summary>Отношение объёма свечи к SMA20 объёма (1 = средний объём, 0 — нет данных).</summary>
    public decimal GetVolumeRatio(decimal volume)
    {
        var sma = VolumeSma20;
        return sma > 0m && volume > 0m ? volume / sma : 0m;
    }

    public IndicatorCalculator(string symbol)
    {
        Symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));

        // Тренд
        EMA20 = new EMA(20);
        EMA50 = new EMA(50);
        EMA200 = new EMA(200);
        Supertrend = new Supertrend(10, 3.0m);
        ADX = new ADX(14);

        // Импульс
        RSI = new RSI(14);
        MACD = new MACD(12, 26, 9);
        StochasticRsi = new StochasticRsi(14, 14, 3, 3);

        // Волатильность
        ATR = new ATR(14);
        BollingerBands = new BollingerBands(20, 2.0m);

        // Объем
        OBV = new OBV();
        VWAP = new VWAP();
    }

    /// <summary>
    /// P1.4: добавить закрытую свечу с проверкой сетки интервала.
    /// Разрыв не маскируется: свеча не попадает в индикаторы, вызывающий обязан дозагрузить историю через REST.
    /// </summary>
    public CandleAppendResult TryAppendClosed(OHLCV candle, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));

        if (LastClosedOpenTime is { } last)
        {
            if (candle.OpenTime == last)
                return CandleAppendResult.Duplicate;
            if (candle.OpenTime < last)
                return CandleAppendResult.OutOfOrder;
            if (candle.OpenTime - last != interval)
                return CandleAppendResult.Gap;
        }

        Update(candle);
        return CandleAppendResult.Accepted;
    }

    /// <summary>
    /// Обновить все индикаторы новой свечой.
    /// </summary>
    public void Update(OHLCV candle)
    {
        EMA20.Update(candle);
        EMA50.Update(candle);
        EMA200.Update(candle);
        Supertrend.Update(candle);
        ADX.Update(candle);

        RSI.Update(candle);
        MACD.Update(candle);
        StochasticRsi.Update(candle);

        ATR.Update(candle);
        BollingerBands.Update(candle);

        OBV.Update(candle);
        VWAP.Update(candle);

        ClosedCandles++;
        LastClosedOpenTime = candle.OpenTime;

        // Окно объёмов (B10): Update вызывается только для закрытых свечей.
        // Числитель volume_ratio фиксируем здесь, а не берём из текущей WS-свечи.
        LastClosedVolume = candle.Volume;
        _volumeWindow.Enqueue(candle.Volume);
        _volumeSum += candle.Volume;
        while (_volumeWindow.Count > VolumeWindow)
            _volumeSum -= _volumeWindow.Dequeue();

        _closedCandles.Enqueue(candle);
        while (_closedCandles.Count > MaxClosedCandleHistory)
            _closedCandles.Dequeue();
    }

    /// <summary>P1.7: последние закрытые свечи (старые первыми) для расчёта доходностей.</summary>
    public IReadOnlyList<OHLCV> GetClosedCandles(int count) =>
        _closedCandles.TakeLast(Math.Max(0, count)).ToList();

    /// <summary>
    /// Все индикаторы прошли прогрев.
    /// </summary>
    public bool AllReady =>
        EMA20.IsReady && EMA50.IsReady && EMA200.IsReady &&
        Supertrend.IsReady && ADX.IsReady &&
        RSI.IsReady && MACD.IsReady && StochasticRsi.IsReady &&
        ATR.IsReady && BollingerBands.IsReady &&
        OBV.IsReady && VWAP.IsReady;

    /// <summary>
    /// Максимальный период прогрева среди всех индикаторов.
    /// </summary>
    public int MaxWarmupPeriod => 250; // EMA200 + буфер

    /// <summary>
    /// Получить нормализованный снимок для AI-модели (FR-3.3).
    /// </summary>
    public NormalizedIndicatorSnapshot GetNormalizedSnapshot(OHLCV currentCandle)
    {
        return new NormalizedIndicatorSnapshot(Symbol, currentCandle, this);
    }

    public void Reset()
    {
        EMA20.Reset();
        EMA50.Reset();
        EMA200.Reset();
        Supertrend.Reset();
        ADX.Reset();
        RSI.Reset();
        MACD.Reset();
        StochasticRsi.Reset();
        ATR.Reset();
        BollingerBands.Reset();
        OBV.Reset();
        VWAP.Reset();

        _volumeWindow.Clear();
        _volumeSum = 0m;
        LastClosedVolume = 0m;
        ClosedCandles = 0;
        LastClosedOpenTime = null;
        _closedCandles.Clear();
    }
}