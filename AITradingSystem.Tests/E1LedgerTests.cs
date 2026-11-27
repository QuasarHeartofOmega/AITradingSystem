using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.Trading;
using AITradingSystem.Data.Storage;

namespace AITradingSystem.Tests;

/// <summary>
/// E1 step 1 storage contracts. Added but NOT executed: the current pass forbids builds/tests.
/// Runtime producers and Demo acceptance remain separate E1 steps.
/// </summary>
public sealed class E1LedgerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "e1-ledger-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private JsonE1Ledger NewLedger(LedgerSource source = LedgerSource.Demo) =>
        new(_root, "Demo", "account-hash", source);

    private static DecisionLedgerRecord Decision(string id = "decision-1") => new()
    {
        RunId = "run-1", ConfigHash = "config-1", BuildHash = "unknown",
        Environment = "Demo", AccountFingerprint = "account-hash", QuoteAsset = "USDT",
        Source = LedgerSource.Demo, Symbol = "DOGEUSDT", RecordedAtUtc = Now,
        DecisionId = id, GateStage = "risk", ReasonCode = "ALLOW", Outcome = DecisionOutcome.Allow,
        CandleCloseUtc = Now.AddMinutes(-1), Features = new() { ["score"] = 0.25m },
        GateVersions = new() { ["risk"] = "v1" }
    };

    private static FillLedgerRecord Fill(string id, FillRole role, decimal qty, decimal feeQuote,
        string? decisionId = "decision-1") => new()
    {
        RunId = "run-1", ConfigHash = "config-1", BuildHash = "unknown",
        Environment = "Demo", AccountFingerprint = "account-hash", QuoteAsset = "USDT",
        Source = LedgerSource.Demo, Symbol = "DOGEUSDT", RecordedAtUtc = Now,
        FillId = id, DecisionId = decisionId, EntryIntentId = "entry-1", PositionId = "position-1",
        ClientOrderId = role == FillRole.Entry ? "entry-client" : "exit-client",
        ExchangeOrderId = "123", ExchangeTradeId = "trade-" + id,
        Role = role, Evidence = FillEvidence.ExchangeTrade, Side = role == FillRole.Entry ? OrderSide.Buy : OrderSide.Sell,
        Quantity = qty, CumulativeFilledQuantity = qty, AverageFillPrice = role == FillRole.Entry ? 100m : 110m,
        Fee = feeQuote, FeeAsset = "USDT", FeeQuote = feeQuote,
        FeeValuation = feeQuote == 0 ? FeeValuation.NotApplicable : FeeValuation.ReportedQuote,
        ExchangeTimeUtc = Now
    };

    private static OutcomeLedgerRecord ClosedOutcome(decimal fees = 0.3m) => new()
    {
        RunId = "run-1", ConfigHash = "config-1", BuildHash = "unknown",
        Environment = "Demo", AccountFingerprint = "account-hash", QuoteAsset = "USDT",
        Source = LedgerSource.Demo, Symbol = "DOGEUSDT", RecordedAtUtc = Now,
        LifecycleId = "position-1", EntryIntentId = "entry-1", PositionId = "position-1", DecisionId = "decision-1",
        Status = OutcomeStatus.Closed, Side = OrderSide.Buy,
        EntryQuantity = 1m, ExitQuantity = 1m, DustQuantity = 0m,
        AverageEntryPrice = 100m, AverageExitPrice = 110m,
        InitialStopLoss = 95m, InitialRiskQuote = 5m,
        RealizedGrossQuote = 10m, RealizedFeesQuote = fees, RealizedNetQuote = 10m - fees, RealizedNetR = (10m - fees) / 5m,
        ExitReason = "TP", ExitTimeUtc = Now.AddMinutes(5), HoldingSeconds = 300m,
        ProtectionIdentity = "oco-1", ProtectionResolution = "TP_FILLED", ProtectionConfirmed = true,
        EntryFillIds = new() { "fill-entry" }, ExitFillIds = new() { "fill-exit" }
    };

    [Fact]
    public async Task Restart_Replay_Is_Idempotent_For_Decision_Fill_And_Outcome()
    {
        var ledger = NewLedger();
        Assert.True(await ledger.RecordDecisionAsync(Decision()));
        Assert.True(await ledger.RecordFillAsync(Fill("fill-entry", FillRole.Entry, 1m, 0.1m)));
        Assert.True(await ledger.RecordFillAsync(Fill("fill-exit", FillRole.Exit, 1m, 0.2m)));
        Assert.True(await ledger.RecordOutcomeAsync(ClosedOutcome()));

        var restarted = NewLedger();
        Assert.False(await restarted.RecordDecisionAsync(Decision()));
        Assert.False(await restarted.RecordFillAsync(Fill("fill-entry", FillRole.Entry, 1m, 0.1m)));
        Assert.False(await restarted.RecordOutcomeAsync(ClosedOutcome()));
        Assert.Single(await restarted.LoadDecisionsAsync());
        Assert.Equal(2, (await restarted.LoadFillsAsync()).Count);
        Assert.Single(await restarted.LoadOutcomesAsync());
    }

    [Fact]
    public async Task Conflicting_Immutable_Identity_Throws()
    {
        var ledger = NewLedger();
        await ledger.RecordDecisionAsync(Decision());
        var changed = Decision() with { ReasonCode = "OTHER" };
        await Assert.ThrowsAsync<LedgerConflictException>(() => ledger.RecordDecisionAsync(changed));
    }

    [Fact]
    public async Task Wrong_Partition_Source_Or_Account_Is_Rejected()
    {
        var ledger = NewLedger();
        var wrong = Decision() with { AccountFingerprint = "other-account" };
        await Assert.ThrowsAsync<LedgerConflictException>(() => ledger.RecordDecisionAsync(wrong));

        var paper = NewLedger(LedgerSource.Paper);
        await Assert.ThrowsAsync<LedgerConflictException>(() => paper.RecordDecisionAsync(Decision()));
    }

    [Fact]
    public async Task Shadow_Has_No_Fills_Or_Outcomes()
    {
        var shadow = NewLedger(LedgerSource.Shadow);
        await Assert.ThrowsAsync<InvalidOperationException>(() => shadow.RecordFillAsync(Fill("f", FillRole.Entry, 1m, 0m)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => shadow.RecordOutcomeAsync(ClosedOutcome()));
    }

    [Fact]
    public async Task Corrupt_Line_Fails_Closed_On_Load_And_Append()
    {
        var ledger = NewLedger();
        await ledger.RecordDecisionAsync(Decision());
        var path = Path.Combine(_root, "ledger", "demo-demo", "account-hash", "decisions.jsonl");
        await File.AppendAllTextAsync(path, "{torn-line\n");

        var restarted = NewLedger();
        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.LoadDecisionsAsync());
        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.RecordDecisionAsync(Decision("decision-2")));
    }

    [Fact]
    public async Task Unvalued_Base_Fee_Excludes_Outcome_From_Statistics()
    {
        var ledger = NewLedger();
        await ledger.RecordDecisionAsync(Decision());
        var entry = Fill("fill-entry", FillRole.Entry, 1m, 0m);
        var exit = Fill("fill-exit", FillRole.Exit, 1m, 0m) with
        {
            Fee = 0.001m, FeeAsset = "BNB", FeeQuote = null, FeeValuation = FeeValuation.Unvalued
        };
        await ledger.RecordFillAsync(entry);
        await ledger.RecordFillAsync(exit);
        await ledger.RecordOutcomeAsync(ClosedOutcome(fees: 0m) with { RealizedFeesQuote = null, RealizedNetQuote = null, RealizedNetR = null });
        var outcome = Assert.Single(await ledger.LoadOutcomesAsync());
        Assert.False(outcome.IsStatisticallyUsable);
    }

    [Fact]
    public async Task Converted_Fee_Requires_Evidence_And_Matches_Rate()
    {
        var ledger = NewLedger();
        var bad = Fill("fill-entry", FillRole.Entry, 1m, 0m) with
        {
            Fee = 0.001m, FeeAsset = "BNB", FeeQuote = 0.5m,
            FeeValuation = FeeValuation.ConvertedQuote, FeeConversionRate = 600m, FeeConversionTimeUtc = Now
        };
        await Assert.ThrowsAsync<LedgerConflictException>(() => ledger.RecordFillAsync(bad));

        var good = bad with { FeeQuote = 0.6m };
        Assert.True(await ledger.RecordFillAsync(good));
    }

    [Fact]
    public async Task NoFill_Is_Not_A_Trade_And_Requires_Decision()
    {
        var ledger = NewLedger();
        await ledger.RecordDecisionAsync(Decision());
        var noFill = ClosedOutcome() with
        {
            Status = OutcomeStatus.NoFill, LifecycleId = "entry-1", PositionId = null,
            EntryQuantity = 0, ExitQuantity = 0, DustQuantity = 0,
            EntryFillIds = new(), ExitFillIds = new(),
            RealizedGrossQuote = null, RealizedFeesQuote = null, RealizedNetQuote = null, RealizedNetR = null
        };
        Assert.True(await ledger.RecordOutcomeAsync(noFill));
        Assert.False(Assert.Single(await ledger.LoadOutcomesAsync()).IsStatisticallyUsable);

        var orphan = noFill with { LifecycleId = "entry-2", DecisionId = null };
        await Assert.ThrowsAsync<LedgerConflictException>(() => ledger.RecordOutcomeAsync(orphan));
    }

    [Fact]
    public async Task Unresolved_Outcome_Can_Finalize_Once_Then_Is_Immutable()
    {
        var ledger = NewLedger();
        await ledger.RecordDecisionAsync(Decision());
        await ledger.RecordFillAsync(Fill("fill-entry", FillRole.Entry, 1m, 0.1m));
        await ledger.RecordFillAsync(Fill("fill-exit", FillRole.Exit, 1m, 0.2m));
        var unresolved = ClosedOutcome() with
        {
            Status = OutcomeStatus.Unresolved, UnresolvedReason = "history unavailable",
            RealizedFeesQuote = null, RealizedNetQuote = null, RealizedNetR = null
        };
        Assert.True(await ledger.RecordOutcomeAsync(unresolved));
        Assert.True(await ledger.RecordOutcomeAsync(ClosedOutcome()));
        Assert.False(await ledger.RecordOutcomeAsync(ClosedOutcome()));
        await Assert.ThrowsAsync<LedgerConflictException>(() => ledger.RecordOutcomeAsync(ClosedOutcome(fees: 0.4m)));
        Assert.True(Assert.Single(await ledger.LoadOutcomesAsync()).IsStatisticallyUsable);
    }

    [Fact]
    public async Task Outcome_Fee_Total_Must_Match_Linked_Fills()
    {
        var ledger = NewLedger();
        await ledger.RecordDecisionAsync(Decision());
        await ledger.RecordFillAsync(Fill("fill-entry", FillRole.Entry, 1m, 0.1m));
        await ledger.RecordFillAsync(Fill("fill-exit", FillRole.Exit, 1m, 0.2m));
        await Assert.ThrowsAsync<LedgerConflictException>(() => ledger.RecordOutcomeAsync(ClosedOutcome(fees: 0.5m)));
    }

    [Fact]
    public async Task Simulated_Fill_Evidence_Cannot_Enter_Exchange_Partition()
    {
        var ledger = NewLedger();
        var simulated = Fill("fill-entry", FillRole.Entry, 1m, 0m) with { Evidence = FillEvidence.PaperSimulation };
        await Assert.ThrowsAsync<LedgerConflictException>(() => ledger.RecordFillAsync(simulated));
    }

    [Fact]
    public async Task Closed_Outcome_Must_Account_For_Exit_And_Dust()
    {
        var ledger = NewLedger();
        await ledger.RecordDecisionAsync(Decision());
        await ledger.RecordFillAsync(Fill("fill-entry", FillRole.Entry, 1m, 0.1m));
        await ledger.RecordFillAsync(Fill("fill-exit", FillRole.Exit, 0.9m, 0.2m));
        var missingDust = ClosedOutcome() with { ExitQuantity = 0.9m, DustQuantity = 0m,
            RealizedGrossQuote = 9m, RealizedNetQuote = 8.7m, RealizedNetR = 1.74m };
        await Assert.ThrowsAsync<LedgerConflictException>(() => ledger.RecordOutcomeAsync(missingDust));
    }
}
