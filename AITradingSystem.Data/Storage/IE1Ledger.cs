using AITradingSystem.Core.Models.Trading;

namespace AITradingSystem.Data.Storage;

/// <summary>
/// E1 machine-readable ledger. One writer per source/environment/account partition.
/// Returns true only when a new record was durably appended; false means idempotent replay.
/// Storage failures and conflicting identities throw: callers must not silently treat an
/// unacknowledged write as success or count it in statistics.
/// </summary>
public interface IE1Ledger
{
    Task<bool> RecordDecisionAsync(DecisionLedgerRecord record, CancellationToken ct = default);
    Task<bool> RecordFillAsync(FillLedgerRecord record, CancellationToken ct = default);
    /// <summary>Unresolved may be finalized once. Repeating a final outcome is a no-op.</summary>
    Task<bool> RecordOutcomeAsync(OutcomeLedgerRecord record, CancellationToken ct = default);
    Task<IReadOnlyList<DecisionLedgerRecord>> LoadDecisionsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<FillLedgerRecord>> LoadFillsAsync(CancellationToken ct = default);
    /// <summary>Latest record per lifecycle; filter IsStatisticallyUsable before computing edge.</summary>
    Task<IReadOnlyList<OutcomeLedgerRecord>> LoadOutcomesAsync(CancellationToken ct = default);
}

/// <summary>A different payload was supplied for an immutable ledger identity.</summary>
public sealed class LedgerConflictException : InvalidOperationException
{
    public LedgerConflictException(string message) : base(message) { }
}
