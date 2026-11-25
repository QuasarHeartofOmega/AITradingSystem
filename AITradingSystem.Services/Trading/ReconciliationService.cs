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
/// P0.4: явная проверка поддержки user-data stream в окружении, режим POLLING-FALLBACK с интервалом,
/// fail-closed по неизвестному состоянию ордеров/позиций (IsExecutionStateTrusted) и журнал RECON-*.
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
    private Task? _pollingLoop;
    private string? _listenKey;

    /// <summary>P0.4: результат явной проверки поддержки user-data stream в текущем окружении.</summary>
    public bool UserDataStreamProbeCompleted { get; private set; }
    public bool IsUserDataStreamSupported { get; private set; }
    private string _executionMode = "UNKNOWN";
    private int _unresolvedStateIssues;

    /// <summary>P0.4: момент последней успешной реконсиляции — основа доверия к состоянию исполнения.</summary>
    public DateTime LastSuccessfulReconcileUtc { get; private set; }

    /// <summary>P0.4: STREAM | POLLING-FALLBACK | UNAVAILABLE | PAPER | DISABLED.</summary>
    public string ExecutionMode => _executionMode;

    /// <summary>
    /// P0.4: можно ли доверять состоянию ордеров/позиций. Ложь = freeze entries, не угадывать.
    /// </summary>
    public bool IsExecutionStateTrusted =>
        EvaluateExecutionTrust(
            _config.PaperTradingMode, _rest.HasApiKeys, _config.UserDataStreamEnabled,
            _ws.IsUserDataStreamConnected, _config.UserDataPollingFallbackEnabled,
            LastSuccessfulReconcileUtc, DateTime.UtcNow, _config.ExecutionStateMaxStalenessSeconds,
            Volatile.Read(ref _unresolvedStateIssues), out _);

    /// <summary>P0.4: причина недоверия к состоянию исполнения (для журнала отказов).</summary>
    public string ExecutionStateBlockReason =>
        EvaluateExecutionTrust(
            _config.PaperTradingMode, _rest.HasApiKeys, _config.UserDataStreamEnabled,
            _ws.IsUserDataStreamConnected, _config.UserDataPollingFallbackEnabled,
            LastSuccessfulReconcileUtc, DateTime.UtcNow, _config.ExecutionStateMaxStalenessSeconds,
            Volatile.Read(ref _unresolvedStateIssues), out var reason)
            ? string.Empty
            : reason;

    /// <summary>
    /// P0.4: чистая политика fail-closed. Отправленный REST-запрос сам по себе не доказывает исполнение:
    /// нужна либо живая лента executionReport, либо свежая успешная реконсиляция в polling-режиме.
    /// </summary>
    public static bool EvaluateExecutionTrust(
        bool paperMode, bool hasApiKeys, bool streamEnabled, bool streamConnected,
        bool pollingFallbackEnabled, DateTime lastSuccessfulReconcileUtc, DateTime nowUtc,
        int maxStalenessSeconds, int unresolvedIssues, out string reason)
    {
        if (unresolvedIssues > 0)
        {
            reason = $"unknown-order-or-position-state:{unresolvedIssues}";
            return false;
        }

        // Paper/без ключей/контур выключен — реального биржевого состояния нет.
        if (paperMode || !hasApiKeys || !streamEnabled)
        {
            reason = string.Empty;
            return true;
        }

        if (lastSuccessfulReconcileUtc == default)
        {
            reason = "no-successful-reconciliation";
            return false;
        }

        var age = (nowUtc - lastSuccessfulReconcileUtc).TotalSeconds;
        if (streamConnected)
        {
            // Стрим жив, но состояние всё равно должно быть сверено хотя бы раз и не быть древним.
            if (age > maxStalenessSeconds * 4)
            {
                reason = $"execution-state-stale:{age:F0}s";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        if (!pollingFallbackEnabled)
        {
            reason = "user-data-stream-down-without-polling-fallback";
            return false;
        }

        if (age > maxStalenessSeconds)
        {
            reason = $"execution-state-stale:{age:F0}s";
            return false;
        }

        reason = string.Empty;
        return true;
    }
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
            _executionMode = _config.PaperTradingMode ? "PAPER" : "DISABLED";
            _logger.LogInformation(
                "Реконсиляция/user-data stream пропущены: paper={Paper}, ключи={Keys}, включено={Enabled}",
                _config.PaperTradingMode, _rest.HasApiKeys, _config.UserDataStreamEnabled);
            TradeLog.ExecutionMode(_executionMode, "paper-or-no-keys-or-disabled", _config.UserDataPollingIntervalSeconds);
            // Инвентаризация всё равно полезна в live без stream; в paper — нет.
            if (!_config.PaperTradingMode && _rest.HasApiKeys)
                await ReconcileAsync(ct, "startup-no-stream");
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            await ConnectUserDataAsync(_cts.Token);
            _keepAliveLoop = Task.Run(() => KeepAliveLoopAsync(_cts.Token), _cts.Token);
            _healthLoop = Task.Run(() => HealthLoopAsync(_cts.Token), _cts.Token);
            SetExecutionMode("STREAM", "user-data-stream-connected");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // P0.4: недоступность stream — не «тихое продолжение», а явный режим с политикой fail-closed.
            _logger.LogWarning(ex, "USER-DATA-FAIL: user-data stream не поднят");
            EnterDegradedMode($"start-failed:{ex.GetType().Name}", _cts.Token);
        }

        await ReconcileAsync(ct, "startup");
    }

    /// <summary>P0.4: явный режим исполнения и его причина в trade-log.</summary>
    private void SetExecutionMode(string mode, string reason)
    {
        _executionMode = mode;
        TradeLog.ExecutionMode(mode, reason, _config.UserDataPollingIntervalSeconds);
        _logger.LogWarning("EXECUTION-MODE={Mode}: {Reason}", mode, reason);
    }

    /// <summary>
    /// P0.4: stream недоступен. Если polling разрешён — переходим на периодическую REST-реконсиляцию,
    /// иначе состояние исполнения считается неизвестным и входы запрещаются.
    /// </summary>
    private void EnterDegradedMode(string reason, CancellationToken ct)
    {
        if (_config.UserDataPollingFallbackEnabled)
        {
            SetExecutionMode("POLLING-FALLBACK", reason);
            if (_pollingLoop is null && _cts is { IsCancellationRequested: false })
                _pollingLoop = Task.Run(() => PollingLoopAsync(ct), ct);
            return;
        }

        SetExecutionMode("UNAVAILABLE", reason);
        if (_config.UserDataStreamRequiredForEntries)
            TradeLog.ExecutionStateBlocked("user-data-stream-down-without-polling-fallback");
    }

    /// <summary>P0.4: polling-fallback — периодическая реконсиляция вместо executionReport.</summary>
    private async Task PollingLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_config.UserDataPollingIntervalSeconds));
            while (await timer.WaitForNextTickAsync(ct))
            {
                if (_ws.IsUserDataStreamConnected)
                {
                    SetExecutionMode("STREAM", "user-data-stream-restored");
                    return;
                }

                _metrics.RecordPollingFallbackReconcile();
                await ReconcileAsync(ct, "polling-fallback");
            }
        }
        catch (OperationCanceledException) { /* остановка */ }
    }

    private async Task ConnectUserDataAsync(CancellationToken ct)
    {
        // P0.4: явная проверка поддержки user-data stream в Demo/Testnet/Live (410 = окружение не поддерживает).
        try
        {
            _listenKey = await _rest.CreateListenKeyAsync(ct);
        }
        catch (BinanceApiException ex)
        {
            UserDataStreamProbeCompleted = true;
            IsUserDataStreamSupported = false;
            _metrics.RecordUserDataStreamProbe(supported: false);
            TradeLog.UserDataProbe(_rest.EnvironmentName, false, ex.HttpStatus, ex.ApiCode, ex.ApiMessage);
            _logger.LogWarning("USER-DATA-PROBE: окружение {Env} не поддержало listenKey (http {Http}, code {Code})",
                _rest.EnvironmentName, ex.HttpStatus, ex.ApiCode);
            throw;
        }

        _ws.OnExecutionReport -= OnExecutionReport;
        _ws.OnExecutionReport += OnExecutionReport;
        await _ws.ConnectUserDataStreamAsync(_listenKey, ct);

        UserDataStreamProbeCompleted = true;
        IsUserDataStreamSupported = true;
        _metrics.RecordUserDataStreamProbe(supported: true);
        TradeLog.UserDataProbe(_rest.EnvironmentName, true, null, null, "listenKey+socket ok");
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
            SetExecutionMode("STREAM", "listenKey-recreated");
            await ReconcileAsync(ct, "listenKey-recreated");
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

    public async Task<ReconciliationReport?> ReconcileAsync(CancellationToken ct = default) =>
        await ReconcileAsync(ct, "manual");

    public async Task<ReconciliationReport?> ReconcileAsync(CancellationToken ct, string trigger)
    {
        if (_config.PaperTradingMode || !_rest.HasApiKeys) return null;

        var positionsSnapshot = _positions.GetOpenPositions();
        TradeLog.ReconStart(trigger, positionsSnapshot.Count);
        Interlocked.Exchange(ref _unresolvedStateIssues, 0);

        try
        {
            var openOrders = await _rest.GetOpenOrdersAsync(ct: ct);
            var openLists = await _rest.GetOpenOrderListsAsync(ct: ct);

            var report = Classify(positionsSnapshot, openOrders, openLists);
            LastReport = report;
            _metrics.RecordReconciliation(report.TotalIssues);

            foreach (var finding in report.Findings)
                _logger.LogWarning("RECON: {Finding}", finding);

            await RemediateAsync(positionsSnapshot, openOrders, openLists, ct);

            // P0.4: успех фиксируется только если не осталось неразрешённых состояний.
            var unresolved = Volatile.Read(ref _unresolvedStateIssues);
            if (unresolved == 0)
                LastSuccessfulReconcileUtc = DateTime.UtcNow;
            else
                TradeLog.ExecutionStateBlocked($"unknown-order-or-position-state:{unresolved}");

            TradeLog.ReconSummary(report.PositionsChecked, report.OrphanOcoLists, report.OrphanOrders,
                report.ExternalOrders, report.ProtectionMissing, unresolved, IsExecutionStateTrusted);

            _logger.LogInformation(
                "RECON-SUMMARY: позиций {Positions}, сиротских OCO {OrphanOco}, сиротских ордеров {OrphanOrders}, " +
                "внешних ордеров {External}, позиций без защиты {Missing}, неразрешённых {Unresolved}",
                report.PositionsChecked, report.OrphanOcoLists, report.OrphanOrders,
                report.ExternalOrders, report.ProtectionMissing, unresolved);

            return report;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // P0.4: неизвестное состояние — freeze entries, а не оптимистичное продолжение.
            Interlocked.Increment(ref _unresolvedStateIssues);
            _metrics.RecordReconciliationFailure();
            TradeLog.ReconFail(trigger, ex.Message);
            TradeLog.ExecutionStateBlocked("reconciliation-failed");
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
                    TradeLog.ReconAdopt(orphan.Symbol, orphan.Quantity, orphan.EntryPrice, order.ClientOrderId);
                    _logger.LogWarning("RECON-ADOPT: подхвачена сиротская позиция {Symbol} {Qty}@{Price} из ордера {ClientId}",
                        orphan.Symbol, orphan.Quantity, orphan.EntryPrice, order.ClientOrderId);
                }
                catch (BinanceApiException ex) when (ex.IsUnknownOrderOrList) { }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _unresolvedStateIssues);
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
                    Interlocked.Increment(ref _unresolvedStateIssues);
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
                    TradeLog.ReconAdopt(orphan.Symbol, orphan.Quantity, orphan.EntryPrice, order.ClientOrderId);
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
                    TradeLog.ReconCancel(order.Symbol, "order", order.ClientOrderId);
                    _logger.LogWarning("RECON-CANCEL: снят сиротский ордер бота {Symbol} {ClientId}", order.Symbol, order.ClientOrderId);
                }
                catch (BinanceApiException ex) when (ex.IsUnknownOrderOrList) { }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _unresolvedStateIssues);
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
                    TradeLog.ReconCancel(list.Symbol, "oco", list.OrderListId.ToString());
                    _logger.LogWarning("RECON-CANCEL: снята сиротская OCO#{ListId} {Symbol}", list.OrderListId, list.Symbol);
                }
                catch (BinanceApiException ex) when (ex.IsUnknownOrderOrList) { }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _unresolvedStateIssues);
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
                    {
                        TradeLog.ProtectionFillApplied(live.Symbol, reason, report.CumulativeQuantity, "user-data-stream");
                        _logger.LogWarning("PROTECTION-FILLED-APPLIED: {Symbol} {Reason} cum={CumQty} применён к позиции без повторного MARKET",
                            live.Symbol, reason, report.CumulativeQuantity);
                    }
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
                EnterDegradedMode("health-check-socket-down", ct);
                await RecreateListenKeyAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* остановка */ }
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();

        foreach (var loop in new[] { _keepAliveLoop, _healthLoop, _pollingLoop })
        {
            if (loop is null) continue;
            try { await loop; }
            catch (OperationCanceledException) { /* ожидаемо */ }
        }
        _keepAliveLoop = null;
        _healthLoop = null;
        _pollingLoop = null;
        _executionMode = "STOPPED";

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
