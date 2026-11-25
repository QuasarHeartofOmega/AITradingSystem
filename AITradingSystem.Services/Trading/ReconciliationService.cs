using AITradingSystem.Common.Configuration;
using AITradingSystem.Common.Logging;
using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.Trading;
using AITradingSystem.Data.Binance;
using AITradingSystem.Data.Storage;
using AITradingSystem.Services.Monitoring;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace AITradingSystem.Services.Trading;

/// <summary>
/// P0.5: реконсиляция с биржей + user-data stream.
///  - listenKey: создание, keepalive, пересоздание при обрыве/истечении, health-check;
///  - стартовая и пост-реконнектная инвентаризация: открытые ордера/OCO против positions.json;
///  - применение филлов защитных ног к позициям/истории/балансу без повторного MARKET;
///  - подхват сиротских позиций бота из журнала намерений и исполненных entry-ордеров;
///  - снятие сиротских ордеров/OCO бота (внешние ордера не трогаем);
///  - учёт частичных филлов через кумулятив ордера и durable-леджер Position.AppliedExchangeFills.
/// </summary>
public sealed class ReconciliationService : IAsyncDisposable
{
    private readonly TradingConfig _config;
    private readonly IBinanceRestClient _rest;
    private readonly IBinanceWebSocketClient _ws;
    private readonly PositionManager _positions;
    private readonly ExecutionQueue _executionQueue;
    private readonly IExecutionJournal _journal;
    private readonly MetricsCollector _metrics;
    private readonly ILogger<ReconciliationService> _logger;

    private CancellationTokenSource? _cts;
    private Task? _keepAliveLoop;
    private Task? _healthLoop;
    private string? _listenKey;
    private readonly SemaphoreSlim _applyLock = new(1, 1);
    private readonly SemaphoreSlim _listenKeyLock = new(1, 1);

    /// <summary>Колбэки движка: применить биржевой филл к позиции и подхватить сиротскую позицию.</summary>
    private Func<Position, ExecutionReport, string, CancellationToken, Task<bool>>? _applyExchangeFill;
    private Func<Position, CancellationToken, Task>? _adoptOrphanPosition;

    public ReconciliationReport? LastReport { get; private set; }
    public bool IsRunning => _cts is { IsCancellationRequested: false };

    /// <summary>Префиксы детерминированных clientOrderId бота (P0.3/P0.1).</summary>
    private static readonly string[] BotClientIdPrefixes = { "mkt-", "ent-", "cls-", "oco-", "sl-", "tp-" };
    private static readonly string[] EntryPrefixes = { "mkt-", "ent-" };

    public sealed record ReconciliationReport(
        DateTime AtUtc,
        int PositionsChecked,
        int OrphanOcoLists,
        int OrphanOrders,
        int ExternalOrders,
        int ProtectionMissing,
        IReadOnlyList<string> Findings)
    {
        public int TotalIssues => OrphanOcoLists + OrphanOrders + ProtectionMissing;
    }

    public ReconciliationService(
        TradingConfig config,
        IBinanceRestClient rest,
        IBinanceWebSocketClient ws,
        PositionManager positions,
        ExecutionQueue executionQueue,
        IExecutionJournal journal,
        MetricsCollector metrics,
        ILogger<ReconciliationService> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _rest = rest ?? throw new ArgumentNullException(nameof(rest));
        _ws = ws ?? throw new ArgumentNullException(nameof(ws));
        _positions = positions ?? throw new ArgumentNullException(nameof(positions));
        _executionQueue = executionQueue ?? throw new ArgumentNullException(nameof(executionQueue));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Подключить колбэки движка до StartAsync.</summary>
    public void Configure(
        Func<Position, ExecutionReport, string, CancellationToken, Task<bool>> applyExchangeFill,
        Func<Position, CancellationToken, Task> adoptOrphanPosition)
    {
        _applyExchangeFill = applyExchangeFill ?? throw new ArgumentNullException(nameof(applyExchangeFill));
        _adoptOrphanPosition = adoptOrphanPosition ?? throw new ArgumentNullException(nameof(adoptOrphanPosition));
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (IsRunning) return;

        if (_config.PaperTradingMode || !_rest.HasApiKeys || !_config.UserDataStreamEnabled)
        {
            _logger.LogInformation(
                "Реконсиляция/user-data stream пропущены: paper={Paper}, ключи={Keys}, включено={Enabled}",
                _config.PaperTradingMode, _rest.HasApiKeys, _config.UserDataStreamEnabled);
            // Инвентаризация всё равно полезна в live без stream; в paper — нет.
            if (!_config.PaperTradingMode && _rest.HasApiKeys)
                await ReconcileAsync(ct);
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            await ConnectUserDataAsync(_cts.Token);
            _keepAliveLoop = Task.Run(() => KeepAliveLoopAsync(_cts.Token), _cts.Token);
            _healthLoop = Task.Run(() => HealthLoopAsync(_cts.Token), _cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "USER-DATA-FAIL: user-data stream не поднят — остаётся polling/периодическая реконсиляция");
        }

        await ReconcileAsync(ct);
    }

    private async Task ConnectUserDataAsync(CancellationToken ct)
    {
        _listenKey = await _rest.CreateListenKeyAsync(ct);
        _ws.OnExecutionReport -= OnExecutionReport;
        _ws.OnExecutionReport += OnExecutionReport;
        await _ws.ConnectUserDataStreamAsync(_listenKey, ct);
        _logger.LogInformation("USER-DATA-START: listenKey получен, keepalive каждые {Minutes} мин, health-check каждые {Seconds} с",
            _config.UserDataKeepAliveMinutes, _config.UserDataStreamHealthCheckSeconds);
    }

    private async Task RecreateListenKeyAsync(CancellationToken ct)
    {
        await _listenKeyLock.WaitAsync(ct);
        try
        {
            if (!IsRunning || ct.IsCancellationRequested) return;
            _logger.LogWarning("USER-DATA-RECREATE: пересоздаю listenKey и переподключаю user-data stream");
            try { await _ws.DisconnectUserDataStreamAsync(); } catch { /* best effort */ }
            if (_listenKey != null)
            {
                try { await _rest.CloseListenKeyAsync(_listenKey); } catch { /* истечёт сам */ }
            }
            await ConnectUserDataAsync(ct);
            _metrics.RecordListenKeyRecreation();
            await ReconcileAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "USER-DATA-RECREATE-FAIL: не удалось пересоздать listenKey");
        }
        finally
        {
            _listenKeyLock.Release();
        }
    }

    public async Task<ReconciliationReport?> ReconcileAsync(CancellationToken ct = default)
    {
        if (_config.PaperTradingMode || !_rest.HasApiKeys) return null;

        try
        {
            var openOrders = await _rest.GetOpenOrdersAsync(ct: ct);
            var openLists = await _rest.GetOpenOrderListsAsync(ct: ct);
            var positions = _positions.GetOpenPositions();

            var report = Classify(positions, openOrders, openLists);
            LastReport = report;
            _metrics.RecordReconciliation(report.TotalIssues);

            foreach (var finding in report.Findings)
                _logger.LogWarning("RECON: {Finding}", finding);

            await RemediateAsync(positions, openOrders, openLists, ct);

            _logger.LogInformation(
                "RECON-SUMMARY: позиций {Positions}, сиротских OCO {OrphanOco}, сиротских ордеров {OrphanOrders}, " +
                "внешних ордеров {External}, позиций без защиты {Missing}",
                report.PositionsChecked, report.OrphanOcoLists, report.OrphanOrders,
                report.ExternalOrders, report.ProtectionMissing);

            return report;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RECON-FAIL: реконсиляция не выполнена");
            return null;
        }
    }

    /// <summary>
    /// Чистая классификация: известные защиты — по id связки/ног из Position.Protection;
    /// ордера бота — по префиксам clientOrderId; всё прочее — внешние (не трогаем).
    /// </summary>
    public static ReconciliationReport Classify(
        IReadOnlyList<Position> positions,
        IReadOnlyList<Order> openOrders,
        IReadOnlyList<ProtectiveOrderList> openLists)
    {
        var findings = new List<string>();
        var knownListIds = new HashSet<long>();
        var knownOrderIds = new HashSet<long>();
        var knownClientIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pos in positions)
        {
            if (pos.Protection is not { } prot || !prot.IsLive) continue;
            knownListIds.Add(prot.OrderListId);
            knownOrderIds.Add(prot.StopOrderId);
            knownOrderIds.Add(prot.TakeProfitOrderId);
            foreach (var id in prot.OrderIds) knownOrderIds.Add(id);
            if (prot.ListClientOrderId.Length > 0) knownClientIds.Add(prot.ListClientOrderId);
            if (prot.StopClientOrderId.Length > 0) knownClientIds.Add(prot.StopClientOrderId);
            if (prot.TakeProfitClientOrderId.Length > 0) knownClientIds.Add(prot.TakeProfitClientOrderId);
        }

        var openListIds = openLists.Select(l => l.OrderListId).ToHashSet();

        var orphanOco = 0;
        foreach (var list in openLists)
        {
            if (knownListIds.Contains(list.OrderListId)) continue;
            orphanOco++;
            findings.Add($"ORPHAN-OCO: связка {list.OrderListId} ({list.Symbol}) на бирже не принадлежит ни одной позиции бота");
        }

        var orphanOrders = 0;
        var externalOrders = 0;
        foreach (var order in openOrders)
        {
            if (knownOrderIds.Contains(order.BinanceOrderId) || knownClientIds.Contains(order.ClientOrderId))
                continue;

            if (IsBotOrder(order.ClientOrderId))
            {
                orphanOrders++;
                findings.Add($"ORPHAN-ORDER: ордер бота {order.ClientOrderId} ({order.Symbol}) открыт без позиции/защиты в снапшоте");
            }
            else
            {
                externalOrders++;
                findings.Add($"EXTERNAL-ORDER: ордер {order.ClientOrderId} ({order.Symbol}) поставлен не ботом — не трогаем");
            }
        }

        var protectionMissing = 0;
        foreach (var pos in positions)
        {
            if (pos.Protection is not { } prot || !prot.IsLive) continue;
            if (openListIds.Contains(prot.OrderListId)) continue;
            protectionMissing++;
            findings.Add($"PROTECTION-MISSING: у позиции {pos.Symbol} ({pos.Id}) связка {prot.OrderListId} исчезла с биржи — " +
                         "возможно, стоп/тейк сработал пока процесс был мёртв");
        }

        return new ReconciliationReport(DateTime.UtcNow, positions.Count,
            orphanOco, orphanOrders, externalOrders, protectionMissing, findings);
    }

    private static bool IsBotOrder(string clientOrderId) =>
        BotClientIdPrefixes.Any(p => clientOrderId.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Применение филлов, подхват сирот, снятие сиротских ордеров бота.</summary>
    private async Task RemediateAsync(
        IReadOnlyList<Position> positions,
        IReadOnlyList<Order> openOrders,
        IReadOnlyList<ProtectiveOrderList> openLists,
        CancellationToken ct)
    {
        if (_applyExchangeFill is null || _adoptOrphanPosition is null)
        {
            _logger.LogWarning("RECON: колбэки движка не настроены — только детект, без применения филлов/подхвата");
            return;
        }

        var openListIds = openLists.Select(l => l.OrderListId).ToHashSet();

        // 1. Пропавшая защита: спрашиваем ноги по clientOrderId и применяем исполненный объём.
        foreach (var pos in positions)
        {
            if (pos.Protection is not { IsLive: true } prot) continue;
            if (openListIds.Contains(prot.OrderListId)) continue;

            var appliedAny = false;
            foreach (var (clientId, reason) in new[]
                     {
                         (prot.StopClientOrderId, "ExchangeStopLoss"),
                         (prot.TakeProfitClientOrderId, "ExchangeTakeProfit")
                     })
            {
                if (string.IsNullOrWhiteSpace(clientId)) continue;
                try
                {
                    var order = await _rest.GetOrderAsync(pos.Symbol, clientId, ct);
                    if (order.ExecutedQuantity > 0)
                    {
                        appliedAny |= await _applyExchangeFill(pos, ToReport(order, reason), reason, ct);
                    }
                }
                catch (BinanceApiException ex) when (ex.IsUnknownOrderOrList)
                {
                    // Нога неизвестна бирже — связки нет, филла нет.
                }
            }

            // Если филлов нет, связка просто исчезла/отменена — сбрасываем локальную защиту,
            // чтобы тик-контур поставил новую OCO (или fail-closed закрыл остаток).
            var live = _positions.GetPositionById(pos.Id);
            if (live != null && !appliedAny && live.Protection?.OrderListId == prot.OrderListId)
            {
                live.SetProtection(null);
                await _positions.SavePositionsAsync(ct);
                _logger.LogWarning("RECON: защита {Symbol} OCO#{ListId} сброшена локально — будет переставлена контуром сопровождения",
                    live.Symbol, prot.OrderListId);
            }
        }

        // 2. Журнал намерений: филлы, случившиеся пока процесс был мёртв.
        var intents = await _journal.LoadAsync(ct);
        foreach (var intent in intents)
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(intent.ClientOrderId) || string.IsNullOrWhiteSpace(intent.Symbol)) continue;

            if (intent.Operation is nameof(ExecutionPriority.Entry))
            {
                if (!_config.ReconciliationAdoptOrphanPositions) continue;
                if (_positions.GetPositionBySymbol(intent.Symbol) != null) continue;
                try
                {
                    var order = await _rest.GetOrderAsync(intent.Symbol, intent.ClientOrderId, ct);
                    if (order.ExecutedQuantity <= 0) continue;
                    var orphan = new Position
                    {
                        Symbol = order.Symbol,
                        Side = order.Side,
                        Quantity = order.ExecutedQuantity,
                        EntryPrice = order.AverageFillPrice ?? order.Price,
                        CurrentPrice = order.AverageFillPrice ?? order.Price,
                        OpenedAt = order.CreatedAt,
                        OpeningOrderId = order.ClientOrderId,
                        EntryCommission = order.Commission
                    };
                    await _adoptOrphanPosition(orphan, ct);
                    _metrics.RecordOrphanPositionAdopted();
                    _logger.LogWarning("RECON-ADOPT: подхвачена сиротская позиция {Symbol} {Qty}@{Price} из ордера {ClientId}",
                        orphan.Symbol, orphan.Quantity, orphan.EntryPrice, order.ClientOrderId);
                }
                catch (BinanceApiException ex) when (ex.IsUnknownOrderOrList) { }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "RECON-ADOPT-FAIL: не удалось подхватить {Symbol} по {ClientId}", intent.Symbol, intent.ClientOrderId);
                }
            }
            else if (intent.Operation is nameof(ExecutionPriority.Close))
            {
                var pos = _positions.GetPositionBySymbol(intent.Symbol);
                if (pos is null) continue;
                try
                {
                    var order = await _rest.GetOrderAsync(intent.Symbol, intent.ClientOrderId, ct);
                    if (order.ExecutedQuantity > 0)
                        await _applyExchangeFill(pos, ToReport(order, "ExchangeClose"), "ExchangeClose", ct);
                }
                catch (BinanceApiException ex) when (ex.IsUnknownOrderOrList) { }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "RECON-CLOSE-FAIL: не удалось применить close-филл {Symbol} {ClientId}", intent.Symbol, intent.ClientOrderId);
                }
            }
        }

        // 3. Сиротские ордера/OCO бота: снимаем; внешние не трогаем.
        if (_config.ReconciliationCancelOrphanBotOrders)
        {
            var knownOrderIds = new HashSet<long>();
            var knownClientIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pos in _positions.GetOpenPositions())
            {
                if (pos.Protection is not { IsLive: true } prot) continue;
                knownOrderIds.Add(prot.StopOrderId);
                knownOrderIds.Add(prot.TakeProfitOrderId);
                foreach (var id in prot.OrderIds) knownOrderIds.Add(id);
                if (prot.StopClientOrderId.Length > 0) knownClientIds.Add(prot.StopClientOrderId);
                if (prot.TakeProfitClientOrderId.Length > 0) knownClientIds.Add(prot.TakeProfitClientOrderId);
            }

            foreach (var order in openOrders)
            {
                if (knownOrderIds.Contains(order.BinanceOrderId) || knownClientIds.Contains(order.ClientOrderId)) continue;
                if (!IsBotOrder(order.ClientOrderId)) continue;

                // Сиротский entry с исполненным объёмом уже подхвачен выше из журнала;
                // если позиции всё ещё нет — подхватываем прямо из открытого ордера.
                if (_config.ReconciliationAdoptOrphanPositions &&
                    EntryPrefixes.Any(p => order.ClientOrderId.StartsWith(p, StringComparison.OrdinalIgnoreCase)) &&
                    order.ExecutedQuantity > 0 &&
                    _positions.GetPositionBySymbol(order.Symbol) is null)
                {
                    var orphan = new Position
                    {
                        Symbol = order.Symbol,
                        Side = order.Side,
                        Quantity = order.ExecutedQuantity,
                        EntryPrice = order.AverageFillPrice ?? order.Price,
                        CurrentPrice = order.AverageFillPrice ?? order.Price,
                        OpenedAt = order.CreatedAt,
                        OpeningOrderId = order.ClientOrderId,
                        EntryCommission = order.Commission
                    };
                    await _adoptOrphanPosition(orphan, ct);
                    _metrics.RecordOrphanPositionAdopted();
                }

                if (!order.IsActive) continue;
                try
                {
                    await _executionQueue.SubmitAsync<object?>(
                        order.Symbol, ExecutionPriority.Close, null,
                        async token =>
                        {
                            await _rest.CancelOrderAsync(order.Symbol, order.BinanceOrderId, token);
                            return null;
                        }, ct);
                    _metrics.RecordOrphanOrderCanceled();
                    _logger.LogWarning("RECON-CANCEL: снят сиротский ордер бота {Symbol} {ClientId}", order.Symbol, order.ClientOrderId);
                }
                catch (BinanceApiException ex) when (ex.IsUnknownOrderOrList) { }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "RECON-CANCEL-FAIL: не удалось снять сиротский ордер {Symbol} {ClientId}", order.Symbol, order.ClientOrderId);
                }
            }

            var knownListIds = _positions.GetOpenPositions()
                .Where(p => p.Protection is { IsLive: true })
                .Select(p => p.Protection!.OrderListId)
                .ToHashSet();
            foreach (var list in openLists)
            {
                if (knownListIds.Contains(list.OrderListId)) continue;
                try
                {
                    await _executionQueue.SubmitAsync<object?>(
                        list.Symbol, ExecutionPriority.Protection, null,
                        async token =>
                        {
                            await _rest.CancelOcoAsync(list.Symbol, list.OrderListId, list.ListClientOrderId, token);
                            return null;
                        }, ct);
                    _metrics.RecordOrphanOrderCanceled();
                    _logger.LogWarning("RECON-CANCEL: снята сиротская OCO#{ListId} {Symbol}", list.OrderListId, list.Symbol);
                }
                catch (BinanceApiException ex) when (ex.IsUnknownOrderOrList) { }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "RECON-CANCEL-FAIL: не удалось снять сиротскую OCO#{ListId} {Symbol}", list.OrderListId, list.Symbol);
                }
            }
        }
    }

    private void OnExecutionReport(ExecutionReport report)
    {
        _metrics.RecordExecutionReport();
        _logger.LogInformation(
            "EXEC-REPORT: {Symbol} {Side} {Execution}/{Status} last={LastQty}@{LastPrice} cum={CumQty} id={ClientId}",
            report.Symbol, report.Side, report.ExecutionType, report.OrderStatus,
            report.LastQuantity, report.LastPrice, report.CumulativeQuantity, report.ClientOrderId);

        if (!report.IsFill || _applyExchangeFill is null || _cts is null) return;

        var match = _positions.GetOpenPositions().FirstOrDefault(p =>
            p.Protection is { } prot &&
            (string.Equals(prot.StopClientOrderId, report.ClientOrderId, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(prot.TakeProfitClientOrderId, report.ClientOrderId, StringComparison.OrdinalIgnoreCase)));

        if (match is null) return;

        var reason = string.Equals(match.Protection!.StopClientOrderId, report.ClientOrderId, StringComparison.OrdinalIgnoreCase)
            ? "ExchangeStopLoss"
            : "ExchangeTakeProfit";

        _ = Task.Run(async () =>
        {
            try
            {
                await _applyLock.WaitAsync(_cts.Token);
                try
                {
                    var live = _positions.GetPositionById(match.Id);
                    if (live is null) return;
                    var applied = await _applyExchangeFill(live, report, reason, _cts.Token);
                    if (applied)
                        _logger.LogWarning("PROTECTION-FILLED-APPLIED: {Symbol} {Reason} cum={CumQty} применён к позиции без повторного MARKET",
                            live.Symbol, reason, report.CumulativeQuantity);
                }
                finally
                {
                    _applyLock.Release();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PROTECTION-FILL-APPLY-FAIL: {Symbol} {ClientId}", report.Symbol, report.ClientOrderId);
            }
        }, _cts.Token);
    }

    /// <summary>REST-ордер → факт исполнения для применения к позиции (кумулятив, не последний тик).</summary>
    public static ExecutionReport ToReport(Order order, string reason)
    {
        var status = order.Status switch
        {
            OrderStatus.Filled => "FILLED",
            OrderStatus.PartiallyFilled => "PARTIALLY_FILLED",
            OrderStatus.Canceled => "CANCELED",
            OrderStatus.Rejected => "REJECTED",
            OrderStatus.Expired => "EXPIRED",
            _ => "NEW"
        };
        return new ExecutionReport
        {
            Symbol = order.Symbol,
            Side = order.Side,
            OrderId = order.BinanceOrderId,
            ClientOrderId = order.ClientOrderId,
            ExecutionType = order.ExecutedQuantity > 0 ? "TRADE" : "NEW",
            OrderStatus = status,
            LastQuantity = order.ExecutedQuantity,
            LastPrice = order.AverageFillPrice ?? order.Price,
            CumulativeQuantity = order.ExecutedQuantity,
            CumulativeQuoteQuantity = order.FilledQuoteQuantity,
            Commission = order.Commission,
            TransactionTimeUtc = order.UpdatedAt ?? order.CreatedAt,
            RejectReason = reason
        };
    }

    private async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_config.UserDataKeepAliveMinutes));
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (_listenKey is null) continue;
                try
                {
                    await _rest.KeepAliveListenKeyAsync(_listenKey, ct);
                    _logger.LogDebug("listenKey продлён");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "USER-DATA-KEEPALIVE-FAIL: продление не удалось — пересоздаю listenKey");
                    await RecreateListenKeyAsync(ct);
                }
            }
        }
        catch (OperationCanceledException) { /* остановка */ }
    }

    private async Task HealthLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_config.UserDataStreamHealthCheckSeconds));
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (_ws.IsUserDataStreamConnected) continue;
                _logger.LogWarning("USER-DATA-DOWN: socket не подключён — пересоздаю listenKey и реконсилирую");
                await RecreateListenKeyAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* остановка */ }
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();

        foreach (var loop in new[] { _keepAliveLoop, _healthLoop })
        {
            if (loop is null) continue;
            try { await loop; }
            catch (OperationCanceledException) { /* ожидаемо */ }
        }
        _keepAliveLoop = null;
        _healthLoop = null;

        _ws.OnExecutionReport -= OnExecutionReport;

        try { await _ws.DisconnectUserDataStreamAsync(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Ошибка отключения user-data stream"); }

        if (_listenKey != null)
        {
            try { await _rest.CloseListenKeyAsync(_listenKey); }
            catch (Exception ex) { _logger.LogDebug(ex, "Не удалось закрыть listenKey (истечёт сам)"); }
            _listenKey = null;
        }

        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _applyLock.Dispose();
        _listenKeyLock.Dispose();
    }
}
