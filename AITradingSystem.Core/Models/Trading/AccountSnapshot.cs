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

    /// <summary>Phase 1.7: сырой биржевой баланс снимка (для external balance-delta recon).</summary>
    public AccountBalance? Balance { get; init; }

    /// <summary>Phase 1.7: цены, использованные снимком (REST+WS), для external-детекта.</summary>
    public IReadOnlyDictionary<string, decimal> Prices { get; init; } = new Dictionary<string, decimal>();

    /// <summary>Phase 1.7: кандидаты external-остатков, ещё не подхваченные ботом.</summary>
    public IReadOnlyList<ExternalPositionCandidate> ExternalCandidates { get; init; } = Array.Empty<ExternalPositionCandidate>();

    /// <summary>Phase 1.7: нотариал external-остатков, не входящих в positions.json (риск-учёт).</summary>
    public decimal UnattributedExternalNotionalQuote { get; init; }
}
