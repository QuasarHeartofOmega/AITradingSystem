namespace AITradingSystem.Core.Models.Trading;

/// <summary>
/// P0.1: единый снимок капитала. Equity — mark-to-market всех активов;
/// free/locked cash — отдельные биржевые остатки quote-актива; notional позиций
/// не вычитается из equity повторно. IsValuationComplete=false означает,
/// что часть активов не удалось оценить и риск-контур должен fail-closed.
/// </summary>
public sealed record AccountSnapshot
{
    public decimal EquityQuote { get; init; }
    public decimal FreeCashQuote { get; init; }
    public decimal LockedCashQuote { get; init; }
    public decimal PositionNotionalQuote { get; init; }
    public decimal UnrealizedPnlQuote { get; init; }
    public string AccountId { get; init; } = string.Empty;
    public string Environment { get; init; } = string.Empty;
    public string QuoteAsset { get; init; } = "USDT";
    public DateTime AsOfUtc { get; init; }
    public string Source { get; init; } = string.Empty;
    public bool IsValuationComplete { get; init; } = true;
}
