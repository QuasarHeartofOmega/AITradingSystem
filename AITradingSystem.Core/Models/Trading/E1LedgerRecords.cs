namespace AITradingSystem.Core.Models.Trading;

/// <summary>E1: execution provenance. Never infer this from the AI role or from a balance change.</summary>
public enum LedgerSource { Replay, Shadow, Paper, Demo, Live }

public enum DecisionOutcome { Allow, Reject, Hold, Error }
public enum AiDisposition { NotInvoked, Completed, Failed, TimedOut }
public enum FillRole { Entry, Exit, ProtectiveExit }
public enum FillEvidence { ExchangeTrade, OrderCumulativeDelta, PaperSimulation, ReplaySimulation }
public enum FeeValuation { NotApplicable, ReportedQuote, ConvertedQuote, Unvalued }
public enum OutcomeStatus { Unresolved, NoFill, Closed }

/// <summary>Point-in-time identity shared by every E1 record. AccountFingerprint must already be hashed.</summary>
public abstract record LedgerRecord
{
    public required string RunId { get; init; }
    public required string ConfigHash { get; init; }
    /// <summary>Use the explicit literal "unknown" when the binary hash was not captured.</summary>
    public required string BuildHash { get; init; }
    public required string Environment { get; init; }
    public required string AccountFingerprint { get; init; }
    public required string QuoteAsset { get; init; }
    public required LedgerSource Source { get; init; }
    public required string Symbol { get; init; }
    public DateTime RecordedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>One final gate decision, not a human-log line and not a trade.</summary>
public sealed record DecisionLedgerRecord : LedgerRecord
{
    public required string DecisionId { get; init; }
    public DateTime? CandleCloseUtc { get; init; }
    public required string GateStage { get; init; }
    public required string ReasonCode { get; init; }
    public required DecisionOutcome Outcome { get; init; }
    public string ReasonText { get; init; } = string.Empty;
    public string EngineDirection { get; init; } = string.Empty;
    public decimal? EnginePrior { get; init; }
    public string? ModelAction { get; init; }
    public decimal? ModelConfidence { get; init; }
    public decimal? FinalConfidence { get; init; }
    public AiDisposition AiDisposition { get; init; } = AiDisposition.NotInvoked;
    public string LlmRole { get; init; } = string.Empty;
    public string BucketKey { get; init; } = string.Empty;
    public string BucketLevel { get; init; } = string.Empty;
    public string EstimatorVersion { get; init; } = string.Empty;
    public string DataSnapshotHash { get; init; } = string.Empty;
    public Dictionary<string, string> GateVersions { get; init; } = new();
    public Dictionary<string, decimal> Features { get; init; } = new();
    public Dictionary<string, string> EventFlags { get; init; } = new();
}

/// <summary>
/// An idempotent fill delta. ExchangeTrade is a myTrades fact; OrderCumulativeDelta is only
/// an aggregate observation and must not be relabelled as an exchange trade.
/// FeeQuote is null until an actual conversion/valuation exists; base-asset and BNB fees are not USDT.
/// </summary>
public sealed record FillLedgerRecord : LedgerRecord
{
    public required string FillId { get; init; }
    public string? DecisionId { get; init; }
    public string? EntryIntentId { get; init; }
    public string? PositionId { get; init; }
    public string? ProtectionIdentity { get; init; }
    public required string ClientOrderId { get; init; }
    public string? ExchangeOrderId { get; init; }
    public string? ListId { get; init; }
    public string? ExchangeTradeId { get; init; }
    public required FillRole Role { get; init; }
    public required FillEvidence Evidence { get; init; }
    public required Enums.OrderSide Side { get; init; }
    public string OrderType { get; init; } = string.Empty;
    public string TimeInForce { get; init; } = string.Empty;
    public bool IsPostOnly { get; init; }
    public decimal RequestedQuantity { get; init; }
    public decimal RoundedQuantity { get; init; }
    /// <summary>New quantity in this record, never the cumulative order quantity again.</summary>
    public required decimal Quantity { get; init; }
    public required decimal CumulativeFilledQuantity { get; init; }
    public required decimal AverageFillPrice { get; init; }
    public required decimal Fee { get; init; }
    public string FeeAsset { get; init; } = string.Empty;
    public decimal? FeeQuote { get; init; }
    public decimal? FeeConversionRate { get; init; }
    public DateTime? FeeConversionTimeUtc { get; init; }
    public required FeeValuation FeeValuation { get; init; }
    public required DateTime ExchangeTimeUtc { get; init; }
    public decimal? LimitPrice { get; init; }
    public decimal? ArrivalMid { get; init; }
    public decimal? BestBid { get; init; }
    public decimal? BestAsk { get; init; }
}

/// <summary>
/// One final outcome per position lifecycle (or NO_FILL per entry intent). An Unresolved
/// outcome may be finalized once; a final outcome is immutable. NO_FILL is never a trade.
/// Gross uses actual fill prices; slippage is diagnostic and is not subtracted a second time.
/// </summary>
public sealed record OutcomeLedgerRecord : LedgerRecord
{
    /// <summary>Stable Position.Id; for NoFill use the entry intent id. Never generate a new id on restart.</summary>
    public required string LifecycleId { get; init; }
    public required string EntryIntentId { get; init; }
    public string? PositionId { get; init; }
    public string? DecisionId { get; init; }
    public required OutcomeStatus Status { get; init; }
    public required Enums.OrderSide Side { get; init; }
    public string EntryBasis { get; init; } = string.Empty;
    public decimal RequestedQuantity { get; init; }
    public decimal RoundedQuantity { get; init; }
    public decimal EntryQuantity { get; init; }
    public decimal ExitQuantity { get; init; }
    public decimal DustQuantity { get; init; }
    public decimal? AverageEntryPrice { get; init; }
    public decimal? AverageExitPrice { get; init; }
    public decimal? InitialStopLoss { get; init; }
    public decimal? InitialRiskQuote { get; init; }
    public decimal? RealizedGrossQuote { get; init; }
    public decimal? RealizedFeesQuote { get; init; }
    public decimal? RealizedSlippageQuote { get; init; }
    public decimal? RealizedNetQuote { get; init; }
    public decimal? RealizedNetR { get; init; }
    public string ExitReason { get; init; } = string.Empty;
    public DateTime? ExitTimeUtc { get; init; }
    public decimal? HoldingSeconds { get; init; }
    public string? ProtectionIdentity { get; init; }
    public string ProtectionResolution { get; init; } = string.Empty;
    public bool ProtectionConfirmed { get; init; }
    public decimal? UnprotectedSeconds { get; init; }
    public string UnresolvedReason { get; init; } = string.Empty;
    public List<string> EntryFillIds { get; init; } = new();
    public List<string> ExitFillIds { get; init; } = new();
    public string BucketKey { get; init; } = string.Empty;
    public string BucketLevel { get; init; } = string.Empty;
    public string EstimatorVersion { get; init; } = string.Empty;
    public string DataSnapshotHash { get; init; } = string.Empty;

    /// <summary>Conservative E2 eligibility; closed does not by itself mean statistically usable.</summary>
    public bool IsStatisticallyUsable => Status == OutcomeStatus.Closed
        && string.IsNullOrWhiteSpace(UnresolvedReason)
        && !string.IsNullOrWhiteSpace(DecisionId)
        && ExitQuantity + DustQuantity == EntryQuantity
        && EntryFillIds.Count > 0 && ExitFillIds.Count > 0
        && EntryQuantity > 0 && ExitQuantity > 0 && DustQuantity >= 0
        && AverageEntryPrice > 0 && AverageExitPrice > 0
        && InitialRiskQuote > 0 && RealizedFeesQuote.HasValue
        && RealizedNetQuote.HasValue && RealizedNetR.HasValue
        && ProtectionConfirmed && !string.IsNullOrWhiteSpace(ProtectionResolution)
        && Source is LedgerSource.Paper or LedgerSource.Demo or LedgerSource.Live;
}
