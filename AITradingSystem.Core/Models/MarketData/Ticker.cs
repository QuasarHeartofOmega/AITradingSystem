namespace AITradingSystem.Core.Models.MarketData;

/// <summary>
/// 24-часовой тикер (FR-1.1).
/// </summary>
public sealed class Ticker
{
    public string Symbol { get; set; } = string.Empty;
    public decimal LastPrice { get; set; }
    public decimal PriceChange { get; set; }
    public decimal PriceChangePercent { get; set; }
    public decimal HighPrice { get; set; }
    public decimal LowPrice { get; set; }
    public decimal Volume { get; set; }
    public decimal QuoteVolume { get; set; }
    public DateTime Timestamp { get; set; }
}