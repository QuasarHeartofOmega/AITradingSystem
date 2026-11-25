namespace AITradingSystem.Core.Indicators;

/// <summary>
/// Базовый класс для инкрементальных индикаторов.
/// </summary>
public abstract class BaseIndicator : IIncrementalIndicator
{
    public abstract string Name { get; }
    public abstract int WarmupPeriod { get; }
    public bool IsReady => ProcessedCount >= WarmupPeriod;

    /// <summary>
    /// Количество обработанных свечей (публичный геттер для внешнего доступа).
    /// </summary>
    public int ProcessedCandlesCount => ProcessedCount;

    protected int ProcessedCount { get; set; }

    public abstract void Reset();
    public abstract void Update(Models.MarketData.OHLCV candle);
    public abstract decimal UpdateUnfinished(Models.MarketData.OHLCV candle);
    public abstract decimal GetValue();
}