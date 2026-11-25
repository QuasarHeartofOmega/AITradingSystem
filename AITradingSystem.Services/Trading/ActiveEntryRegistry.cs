using System.Collections.Concurrent;
using AITradingSystem.Common.Configuration;
using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.Trading;

namespace AITradingSystem.Services.Trading;

/// <summary>P0.4a: in-memory ownership of an entry; never restored as ownership after a restart.</summary>
public sealed record ActiveEntryIntent(
    string ClientOrderId, string Symbol, Guid OwnerId, DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc, DateTime DeadlineUtc, ExecutionIntentStatus Status,
    OrderStatus? OrderStatus, decimal ExecutedQuantity, bool LifecycleCompleted)
{
    public string Operation => nameof(ExecutionPriority.Entry);
    public bool ProtectionRequired => ExecutedQuantity > 0;
    public bool IsTerminal => OrderStatus is Core.Models.Enums.OrderStatus.Filled
        or Core.Models.Enums.OrderStatus.Canceled or Core.Models.Enums.OrderStatus.Rejected
        or Core.Models.Enums.OrderStatus.Expired;
}

public enum EntryReconciliationAction { SkipActive, CancelExpired, CancelOrphan, IgnoreTerminal, ResolveUnresolved }

public sealed class ActiveEntryRegistry
{
    private readonly ConcurrentDictionary<string, ActiveEntryIntent> _entries = new(StringComparer.OrdinalIgnoreCase);
    public Guid OwnerId { get; } = Guid.NewGuid();

    public ActiveEntryIntent Track(string symbol, string clientOrderId, TradingConfig config, DateTime? createdAt = null)
    {
        var now = DateTime.UtcNow;
        var created = createdAt ?? now;
        return _entries.AddOrUpdate(clientOrderId,
            _ => New(symbol, clientOrderId, created, now, config),
            (_, old) => old with { UpdatedAtUtc = now });
    }
    private ActiveEntryIntent New(string symbol, string id, DateTime created, DateTime now, TradingConfig config) =>
        new(id, symbol, OwnerId, created, now,
            created.AddSeconds(config.OrderFillTimeoutSeconds + config.ReconciliationEntryGraceSeconds),
            ExecutionIntentStatus.Pending, null, 0, false);

    public void Update(string id, ExecutionIntentStatus status) => Mutate(id, x => x with { Status = status });
    public void Observe(string id, Order order) => Mutate(id, x => x with
    {
        Status = ExecutionIntentStatus.Confirmed, OrderStatus = order.Status,
        ExecutedQuantity = Math.Max(x.ExecutedQuantity, order.ExecutedQuantity)
    });
    public void ObserveReport(ExecutionReport report)
    {
        if (string.IsNullOrWhiteSpace(report.ClientOrderId)) return;
        var status = report.OrderStatus.ToUpperInvariant() switch
        {
            "FILLED" => OrderStatus.Filled, "PARTIALLY_FILLED" => OrderStatus.PartiallyFilled,
            "CANCELED" => OrderStatus.Canceled, "REJECTED" => OrderStatus.Rejected,
            "EXPIRED" => OrderStatus.Expired, _ => OrderStatus.New
        };
        Mutate(report.ClientOrderId, x => x with { Status = ExecutionIntentStatus.Confirmed,
            OrderStatus = status, ExecutedQuantity = Math.Max(x.ExecutedQuantity, report.CumulativeQuantity) });
    }
    public void Complete(string id) => Mutate(id, x => x with { LifecycleCompleted = true });
    private void Mutate(string id, Func<ActiveEntryIntent, ActiveEntryIntent> update)
    {
        // External/restored orders are not owned by this process; never invent ownership from a report.
        while (_entries.TryGetValue(id, out var old))
        {
            var next = update(old with { UpdatedAtUtc = DateTime.UtcNow });
            if (_entries.TryUpdate(id, next, old)) return;
        }
    }

    public IReadOnlyList<ActiveEntryIntent> Snapshot() => _entries.Values.ToArray();
    public bool TryGet(string id, out ActiveEntryIntent? entry) => _entries.TryGetValue(id, out entry);
    public void Remove(string id) => _entries.TryRemove(id, out _);

    public static EntryReconciliationAction Evaluate(ActiveEntryIntent? entry, Order order, DateTime nowUtc)
    {
        if (entry is null) return EntryReconciliationAction.CancelOrphan;
        if (entry.IsTerminal || entry.LifecycleCompleted) return EntryReconciliationAction.IgnoreTerminal;
        if (entry.Status == ExecutionIntentStatus.Failed) return EntryReconciliationAction.ResolveUnresolved;
        if (nowUtc >= entry.DeadlineUtc) return EntryReconciliationAction.CancelExpired;
        return entry.Status == ExecutionIntentStatus.Pending && order.BinanceOrderId <= 0
            ? EntryReconciliationAction.ResolveUnresolved : EntryReconciliationAction.SkipActive;
    }
}
