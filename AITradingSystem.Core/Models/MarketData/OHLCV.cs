namespace AITradingSystem.Core.Models.MarketData;

/// <summary>
/// Свеча OHLCV (FR-1.2).
/// </summary>
public sealed class OHLCV
{
    public string Symbol { get; set; } = string.Empty;
    public DateTime OpenTime { get; set; }
    public DateTime CloseTime { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public decimal Volume { get; set; }
    public decimal QuoteVolume { get; set; }
    public int TradesCount { get; set; }

    /// <summary>
    /// Является ли свеча завершенной (закрытой).
    /// Незавершенные свечи не должны использоваться для расчета индикаторов.
    /// </summary>
    public bool IsClosed => DateTime.UtcNow >= CloseTime;

    /// <summary>
    /// True Range — необходим для ATR и Supertrend.
    /// </summary>
    public decimal TrueRange(decimal? previousClose)
    {
        if (previousClose is null)
            return High - Low;

        var pc = previousClose.Value;
        return Math.Max(High - Low,
            Math.Max(Math.Abs(High - pc), Math.Abs(Low - pc)));
    }

    /// <summary>
    /// Типичная цена — необходима для VWAP и других индикаторов.
    /// </summary>
    public decimal TypicalPrice => (High + Low + Close) / 3m;
}