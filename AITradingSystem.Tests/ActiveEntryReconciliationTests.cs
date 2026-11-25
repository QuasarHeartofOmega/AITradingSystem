using AITradingSystem.Common.Configuration;
using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.Trading;
using AITradingSystem.Services.Trading;

namespace AITradingSystem.Tests;

// Added without running tests/builds, per audit constraint.
public class ActiveEntryReconciliationTests
{
    private static readonly DateTime Now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);
    private static Order Entry(decimal filled = 0) => new() { Symbol = "DASHUSDT", ClientOrderId = "ent-test", BinanceOrderId = 42, Status = filled > 0 ? OrderStatus.PartiallyFilled : OrderStatus.New, ExecutedQuantity = filled };
    private static ActiveEntryIntent Intent(DateTime? deadline = null) => new("ent-test", "DASHUSDT", Guid.NewGuid(), Now, Now, deadline ?? Now.AddSeconds(90), ExecutionIntentStatus.Confirmed, OrderStatus.New, 0, false);
    private static ReconciliationService.ReconciliationReport Classify(Order order, ActiveEntryIntent? intent) => ReconciliationService.Classify(Array.Empty<Position>(), new[] { order }, Array.Empty<ProtectiveOrderList>(), intent is null ? Array.Empty<ActiveEntryIntent>() : new[] { intent }, Now);

    [Fact] public void Active_Zero_Fill_Is_Not_Orphan()
    {
        var report = Classify(Entry(), Intent());
        Assert.Equal(0, report.OrphanOrders); Assert.Equal(1, report.ActiveEntries); Assert.Equal(1, report.SkippedActiveEntryCancels);
        Assert.Equal(EntryReconciliationAction.SkipActive, ActiveEntryRegistry.Evaluate(Intent(), Entry(), Now));
    }
    [Fact] public void Expired_Entry_Is_Cancelled_For_Deadline() => Assert.Equal(EntryReconciliationAction.CancelExpired, ActiveEntryRegistry.Evaluate(Intent(Now.AddSeconds(-1)), Entry(), Now));
    [Fact] public void Partial_Fill_Is_Active_And_Requires_Protection()
    {
        var intent = Intent() with { ExecutedQuantity = 0.5m };
        Assert.True(intent.ProtectionRequired); Assert.Equal(0, Classify(Entry(0.5m), intent).OrphanOrders);
    }
    [Fact] public void Lost_Owner_Is_Orphan() => Assert.Equal(EntryReconciliationAction.CancelOrphan, ActiveEntryRegistry.Evaluate(null, Entry(), Now));
    [Fact] public void External_Order_Is_Never_Cancelled()
    {
        var order = Entry(); order.ClientOrderId = "manual-order";
        var report = Classify(order, null); Assert.Equal(1, report.ExternalOrders); Assert.Equal(0, report.OrphanOrders);
    }
    [Fact] public void Unknown_Submission_Is_Unresolved()
    {
        var order = Entry(); order.BinanceOrderId = 0;
        var intent = Intent() with { Status = ExecutionIntentStatus.Pending };
        Assert.Equal(1, Classify(order, intent).UnresolvedEntries);
    }
    [Fact] public void Grace_Is_Validated()
    {
        var config = new TradingConfig { ReconciliationEntryGraceSeconds = 4 };
        Assert.Throws<InvalidOperationException>(() => config.Validate());
    }
    [Fact] public void Registry_Tracks_Current_Process_Deadline()
    {
        var registry = new ActiveEntryRegistry();
        var entry = registry.Track("DASHUSDT", "ent-test", new TradingConfig());
        Assert.Equal(registry.OwnerId, entry.OwnerId); Assert.Equal(nameof(ExecutionPriority.Entry), entry.Operation);
        Assert.Equal(entry.CreatedAtUtc.AddSeconds(90), entry.DeadlineUtc);
    }
}

public class ActiveEntryOwnershipTests
{
    [Fact] public void External_Report_Does_Not_Create_Owned_Intent()
    {
        var registry = new ActiveEntryRegistry();
        registry.ObserveReport(new ExecutionReport { ClientOrderId = "external", OrderStatus = "NEW", CumulativeQuantity = 0 });
        Assert.Empty(registry.Snapshot());
    }
}
