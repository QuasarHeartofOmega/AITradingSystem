using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITradingSystem.Common.Configuration;
using AITradingSystem.Common.Logging;
using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.MarketData;
using AITradingSystem.Core.Models.Trading;
using AITradingSystem.Data.Binance;
using AITradingSystem.Data.Storage;
using AITradingSystem.Services.Analysis;
using Microsoft.Extensions.Logging;

namespace AITradingSystem.Services.Trading;

/// <summary>
/// E1 runtime producer: decision -> durable entry intent -> exchange/simulated fill deltas
/// -> protection identity/resolution -> one lifecycle outcome. Fail-closed on ledger errors.
/// This is evidence storage, not the P0.3 execution queue and not a human log.
/// </summary>
public sealed class E1Runtime
{
    private readonly AppConfig _config;
    private readonly IBinanceRestClient _rest;
    private readonly IE1Ledger _ledger;
    private readonly IExecutionJournal _journal;
    private readonly PositionManager _positions;
    private readonly ILogger<E1Runtime> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _configHash = "unknown";

    public string RunId { get; } = Guid.NewGuid().ToString("N");
    public LedgerSource Source { get; }
    public string Environment { get; }
    public string AccountFingerprint { get; }

    public static string Fingerprint(string environment, string accountId) => Hash(environment + "|" + accountId);

    public E1Runtime(
        AppConfig config,
        IBinanceRestClient rest,
        IE1Ledger ledger,
        IExecutionJournal journal,
        PositionManager positions,
        ILogger<E1Runtime> logger)
    {
        _config = config;
        _rest = rest;
        _ledger = ledger;
        _journal = journal;
        _positions = positions;
        _logger = logger;
        Environment = rest.EnvironmentName;
        Source = config.Trading.PaperTradingMode
            ? LedgerSource.Paper
            : string.Equals(Environment, "Live", StringComparison.OrdinalIgnoreCase)
                ? LedgerSource.Live
                : LedgerSource.Demo;
        AccountFingerprint = Fingerprint(Environment, rest.AccountId);
    }

    public async Task InitializeAsync(string configHash, CancellationToken ct = default)
    {
        _configHash = string.IsNullOrWhiteSpace(configHash) ? "unknown" : configHash;
        // Touch all partitions first: corrupt/torn ledger must fail closed before new entries.
        await _ledger.LoadDecisionsAsync(ct);
        await _ledger.LoadFillsAsync(ct);
        await _ledger.LoadOutcomesAsync(ct);
        await ReconcileAsync(ct);
    }

    public DecisionLedgerRecord NewDecision(string symbol) => new()
    {
        RunId = RunId,
        ConfigHash = _configHash,
        BuildHash = "unknown",
        Environment = Environment,
        AccountFingerprint = AccountFingerprint,
        QuoteAsset = QuoteOf(symbol),
        Source = Source,
        Symbol = symbol,
        DecisionId = Guid.NewGuid().ToString("N"),
        GateStage = "pending",
        ReasonCode = "pending",
        Outcome = DecisionOutcome.Error,
        GateVersions = GateVersions()
    };

    public async Task RecordDecisionAsync(DecisionLedgerRecord record, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { await _ledger.RecordDecisionAsync(record, ct); }
        finally { _gate.Release(); }
    }

    /// <summary>Write-ahead entry identity before any network submit. The clientOrderId is the intent id.</summary>
    public async Task<ExecutionIntent> PrepareEntryAsync(
        string decisionId, TradingSignal signal, decimal quantity, OrderSide side, CancellationToken ct = default)
    {
        var intentId = Guid.NewGuid().ToString("N");
        var intent = new ExecutionIntent
        {
            ClientOrderId = StableId("e1", decisionId, intentId),
            Symbol = signal.Symbol,
            Operation = nameof(ExecutionPriority.Entry),
            Status = ExecutionIntentStatus.Pending,
            DecisionId = decisionId,
            EntryIntentId = intentId,
            Side = side,
            CreatedAtUtc = DateTime.UtcNow
        };
        await _journal.RecordAsync(intent, ct);
        await RecordOutcomeAsync(new OutcomeLedgerRecord
        {
            RunId = RunId, ConfigHash = _configHash, BuildHash = "unknown",
            Environment = Environment, AccountFingerprint = AccountFingerprint,
            QuoteAsset = QuoteOf(signal.Symbol), Source = Source, Symbol = signal.Symbol,
            LifecycleId = intentId, EntryIntentId = intentId, DecisionId = decisionId,
            Status = OutcomeStatus.Unresolved, Side = side,
            RequestedQuantity = quantity,
            InitialStopLoss = signal.StopLoss,
            UnresolvedReason = "entry-submission-pending",
            EntryBasis = signal.ConfidenceSource ?? "engine"
        }, ct);
        return intent;
    }

    public async Task RecordEntryResultAsync(ExecutionIntent intent, Order? order, OrderBook? book, CancellationToken ct = default)
    {
        if (order != null && order.ExecutedQuantity > 0)
        {
            await RecordOrderAsync(order, FillRole.Entry, intent.DecisionId, intent.EntryIntentId,
                intent.EntryIntentId, null, book, ct);
            return;
        }

        var stored = (await _journal.LoadAsync(ct)).FirstOrDefault(i =>
            i.ClientOrderId == intent.ClientOrderId);
        if (order == null && stored?.Status != ExecutionIntentStatus.Confirmed)
        {
            await RecordOutcomeAsync(new OutcomeLedgerRecord
            {
                RunId = RunId, ConfigHash = _configHash, BuildHash = "unknown",
                Environment = Environment, AccountFingerprint = AccountFingerprint,
                QuoteAsset = QuoteOf(intent.Symbol), Source = Source, Symbol = intent.Symbol,
                LifecycleId = intent.EntryIntentId!, EntryIntentId = intent.EntryIntentId!,
                DecisionId = intent.DecisionId, Status = OutcomeStatus.NoFill,
                Side = intent.Side ?? OrderSide.Buy, UnresolvedReason = string.Empty,
                ExitReason = stored?.Error ?? "entry-not-filled"
            }, ct);
        }
    }

    public async Task AttachPositionAsync(Position position, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(position.E1EntryIntentId)) return;
        var risk = position.InitialStopLoss > 0 && position.EntryPrice > 0
            ? Math.Abs(position.EntryPrice - position.InitialStopLoss) * position.Quantity
            : 0m;
        position.E1InitialRiskQuote = risk;
        await UpsertPositionOutcomeAsync(position, "position-open", ct);
    }

    public async Task RecordProtectionAsync(Position position, string resolution, bool confirmed, CancellationToken ct = default)
    {
        position.E1ProtectionConfirmed |= confirmed;
        position.E1ProtectionResolution = resolution;
        if (string.IsNullOrWhiteSpace(position.E1EntryIntentId)) return;
        await UpsertPositionOutcomeAsync(position, resolution, ct);
    }

    public async Task RecordOrderAsync(
        Order order, FillRole role, string? decisionId, string? entryIntentId, string? positionId,
        string? protectionIdentity, OrderBook? arrivalBook, CancellationToken ct = default,
        bool cumulativeFee = true)
    {
        if (order.ExecutedQuantity <= 0) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (Source == LedgerSource.Paper)
            {
                await RecordAggregateFillAsync(order, role, decisionId, entryIntentId, positionId,
                    protectionIdentity, arrivalBook, FillEvidence.PaperSimulation, ct, cumulativeFee);
                return;
            }

            List<BinanceMyTrade> trades;
            try
            {
                trades = await _rest.GetMyTradesAsync(order.Symbol, order.BinanceOrderId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "E1 myTrades unavailable for {ClientOrderId}; recording cumulative delta only", order.ClientOrderId);
                await RecordAggregateFillAsync(order, role, decisionId, entryIntentId, positionId,
                    protectionIdentity, arrivalBook, FillEvidence.OrderCumulativeDelta, ct, cumulativeFee);
                return;
            }

            var ordered = trades.Where(t => t.Id > 0).OrderBy(t => t.Time).ThenBy(t => t.Id).ToList();
            if (ordered.Count == 0)
            {
                await RecordAggregateFillAsync(order, role, decisionId, entryIntentId, positionId,
                    protectionIdentity, arrivalBook, FillEvidence.OrderCumulativeDelta, ct, cumulativeFee);
                return;
            }

            var existing = await _ledger.LoadFillsAsync(ct);
            decimal cumulative = 0m;
            foreach (var t in ordered)
            {
                var qty = Dec(t.Quantity);
                var price = Dec(t.Price);
                if (qty <= 0 || price <= 0) continue;
                cumulative += qty;
                var fee = Dec(t.Commission);
                var feeAsset = t.CommissionAsset ?? string.Empty;
                var quote = QuoteOf(order.Symbol);
                var valuation = FeeValuation.Unvalued;
                decimal? feeQuote = null;
                decimal? rate = null;
                DateTime? rateTime = null;
                if (fee == 0)
                {
                    valuation = FeeValuation.NotApplicable;
                    feeQuote = 0m;
                }
                else if (string.Equals(feeAsset, quote, StringComparison.OrdinalIgnoreCase))
                {
                    valuation = FeeValuation.ReportedQuote;
                    feeQuote = fee;
                }
                else if (SymbolNaming.TrySplit(order.Symbol, _config.Trading.QuoteAssets, out var baseAsset, out _)
                         && string.Equals(feeAsset, baseAsset, StringComparison.OrdinalIgnoreCase))
                {
                    // The exchange trade itself is the conversion evidence: base fee valued at its own fill price.
                    valuation = FeeValuation.ConvertedQuote;
                    rate = price;
                    feeQuote = fee * price;
                    rateTime = DateTimeOffset.FromUnixTimeMilliseconds(t.Time).UtcDateTime;
                }

                await _ledger.RecordFillAsync(new FillLedgerRecord
                {
                    RunId = RunId, ConfigHash = _configHash, BuildHash = "unknown",
                    Environment = Environment, AccountFingerprint = AccountFingerprint,
                    QuoteAsset = quote, Source = Source, Symbol = order.Symbol,
                    FillId = $"exchange:{order.BinanceOrderId}:{t.Id}",
                    DecisionId = decisionId, EntryIntentId = entryIntentId, PositionId = positionId,
                    ProtectionIdentity = protectionIdentity,
                    ClientOrderId = order.ClientOrderId,
                    ExchangeOrderId = order.BinanceOrderId.ToString(CultureInfo.InvariantCulture),
                    ExchangeTradeId = t.Id.ToString(CultureInfo.InvariantCulture),
                    Role = role, Evidence = FillEvidence.ExchangeTrade, Side = order.Side,
                    OrderType = order.Type.ToString(), TimeInForce = order.IsPostOnly ? "GTX" : string.Empty,
                    IsPostOnly = order.IsPostOnly,
                    RequestedQuantity = order.OriginalQuantity, RoundedQuantity = order.OriginalQuantity,
                    Quantity = qty, CumulativeFilledQuantity = cumulative, AverageFillPrice = price,
                    Fee = fee, FeeAsset = feeAsset, FeeQuote = feeQuote,
                    FeeConversionRate = rate, FeeConversionTimeUtc = rateTime, FeeValuation = valuation,
                    ExchangeTimeUtc = DateTimeOffset.FromUnixTimeMilliseconds(t.Time).UtcDateTime,
                    LimitPrice = order.Price > 0 ? order.Price : null,
                    ArrivalMid = arrivalBook?.MidPrice, BestBid = arrivalBook?.BestBid, BestAsk = arrivalBook?.BestAsk
                }, ct);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task RecordReportAsync(Position? position, ExecutionReport report, FillRole role,
        string? protectionIdentity, CancellationToken ct = default)
    {
        if (!report.IsFill) return;
        var decisionId = position?.E1DecisionId;
        var entryIntentId = position?.E1EntryIntentId;
        var positionId = position?.Id;
        if (string.IsNullOrWhiteSpace(entryIntentId))
        {
            var intent = (await _journal.LoadAsync(ct)).FirstOrDefault(i =>
                i.ClientOrderId == report.ClientOrderId || i.PositionId == position?.Id);
            decisionId ??= intent?.DecisionId;
            entryIntentId ??= intent?.EntryIntentId;
            positionId ??= intent?.PositionId;
        }

        if (report.TradeId > 0 && Source is LedgerSource.Demo or LedgerSource.Live)
        {
            var fee = report.Commission;
            var quote = QuoteOf(report.Symbol);
            var valuation = fee == 0 ? FeeValuation.NotApplicable
                : string.Equals(report.CommissionAsset, quote, StringComparison.OrdinalIgnoreCase)
                    ? FeeValuation.ReportedQuote : FeeValuation.Unvalued;
            decimal? feeQuote = valuation == FeeValuation.NotApplicable ? 0m
                : valuation == FeeValuation.ReportedQuote ? fee : null;
            decimal? rate = null;
            DateTime? rateTime = null;
            if (valuation == FeeValuation.Unvalued && report.LastPrice > 0
                && SymbolNaming.TrySplit(report.Symbol, _config.Trading.QuoteAssets, out var baseAsset, out _)
                && string.Equals(report.CommissionAsset, baseAsset, StringComparison.OrdinalIgnoreCase))
            {
                valuation = FeeValuation.ConvertedQuote;
                rate = report.LastPrice;
                feeQuote = fee * report.LastPrice;
                rateTime = report.TransactionTimeUtc;
            }
            await _gate.WaitAsync(ct);
            try
            {
                await _ledger.RecordFillAsync(new FillLedgerRecord
                {
                    RunId = RunId, ConfigHash = _configHash, BuildHash = "unknown",
                    Environment = Environment, AccountFingerprint = AccountFingerprint,
                    QuoteAsset = quote, Source = Source, Symbol = report.Symbol,
                    FillId = $"exchange:{report.OrderId}:{report.TradeId}",
                    DecisionId = decisionId, EntryIntentId = entryIntentId, PositionId = positionId,
                    ProtectionIdentity = protectionIdentity, ClientOrderId = report.ClientOrderId,
                    ExchangeOrderId = report.OrderId.ToString(CultureInfo.InvariantCulture),
                    ExchangeTradeId = report.TradeId.ToString(CultureInfo.InvariantCulture),
                    Role = role, Evidence = FillEvidence.ExchangeTrade, Side = report.Side,
                    Quantity = report.LastQuantity, CumulativeFilledQuantity = report.CumulativeQuantity,
                    AverageFillPrice = report.LastPrice, Fee = fee, FeeAsset = report.CommissionAsset,
                    FeeQuote = feeQuote, FeeConversionRate = rate, FeeConversionTimeUtc = rateTime,
                    FeeValuation = valuation, ExchangeTimeUtc = report.TransactionTimeUtc
                }, ct);
            }
            finally { _gate.Release(); }
            return;
        }

        var order = new Order
        {
            Id = report.OrderId.ToString(CultureInfo.InvariantCulture),
            ClientOrderId = report.ClientOrderId,
            Symbol = report.Symbol, Side = report.Side,
            Status = OrderStatus.PartiallyFilled,
            BinanceOrderId = report.OrderId,
            OriginalQuantity = report.CumulativeQuantity,
            ExecutedQuantity = report.CumulativeQuantity,
            FilledQuoteQuantity = report.CumulativeQuoteQuantity,
            Commission = report.Commission,
            CommissionAsset = report.CommissionAsset,
            UpdatedAt = report.TransactionTimeUtc
        };
        if (feeAssetKnown(report.CommissionAsset))
            order.CommissionByAsset[report.CommissionAsset] = report.Commission;
        await RecordOrderAsync(order, role, decisionId, entryIntentId, positionId, protectionIdentity, null, ct, cumulativeFee: false);
    }

    public async Task RefreshOutcomeAsync(string positionId, string exitReason, decimal dustQuantity = 0m, CancellationToken ct = default)
    {
        var position = _positions.GetPositionById(positionId);
        if (position != null)
        {
            await UpsertPositionOutcomeAsync(position, exitReason, ct);
            return;
        }
        await FinalizeFromFillsAsync(positionId, exitReason, dustQuantity, ct);
    }

    /// <summary>Restart/polling recovery: replay exchange trades for known intents/positions; never invent fills.</summary>
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        var intents = await _journal.LoadAsync(ct);
        var positions = _positions.GetOpenPositions();
        var outcomes = await _ledger.LoadOutcomesAsync(ct);

        foreach (var intent in intents)
        {
            if (string.IsNullOrWhiteSpace(intent.EntryIntentId) && string.IsNullOrWhiteSpace(intent.PositionId))
                continue;
            if (Source == LedgerSource.Paper) continue;
            try
            {
                var order = await _rest.GetOrderAsync(intent.Symbol, intent.ClientOrderId, ct);
                var role = intent.Operation == nameof(ExecutionPriority.Entry) ? FillRole.Entry : FillRole.Exit;
                await RecordOrderAsync(order, role, intent.DecisionId, intent.EntryIntentId,
                    intent.PositionId ?? intent.EntryIntentId, intent.ProtectionIdentity, null, ct);
            }
            catch (BinanceApiException ex) when (ex.IsUnknownOrderOrList)
            {
                if (intent.Operation == nameof(ExecutionPriority.Entry) && intent.Status == ExecutionIntentStatus.Failed
                    && !string.IsNullOrWhiteSpace(intent.EntryIntentId))
                {
                    await RecordOutcomeAsync(new OutcomeLedgerRecord
                    {
                        RunId = RunId, ConfigHash = _configHash, BuildHash = "unknown",
                        Environment = Environment, AccountFingerprint = AccountFingerprint,
                        QuoteAsset = QuoteOf(intent.Symbol), Source = Source, Symbol = intent.Symbol,
                        LifecycleId = intent.EntryIntentId, EntryIntentId = intent.EntryIntentId,
                        DecisionId = intent.DecisionId, Status = OutcomeStatus.NoFill,
                        Side = intent.Side ?? OrderSide.Buy, ExitReason = "entry-unknown-or-rejected"
                    }, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "E1 reconcile could not resolve intent {ClientOrderId}", intent.ClientOrderId);
            }
        }

        foreach (var p in positions)
        {
            if (string.IsNullOrWhiteSpace(p.E1EntryIntentId)) continue;
            if (Source != LedgerSource.Paper)
            foreach (var prot in p.E1ProtectionHistory.Concat(p.Protection != null ? new[] { p.Protection } : Array.Empty<ProtectiveOrderList>()))
            {
                foreach (var orderId in new[] { prot.StopOrderId, prot.TakeProfitOrderId }.Where(x => x > 0))
                {
                    try
                    {
                        var order = await _rest.GetOrderAsync(p.Symbol, orderId, ct);
                        if (order.ExecutedQuantity > 0)
                            await RecordOrderAsync(order, FillRole.ProtectiveExit, p.E1DecisionId, p.E1EntryIntentId,
                                p.Id, ProtectionIdentity(prot), null, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "E1 could not recover protection order {OrderId}", orderId);
                    }
                }
            }
            await UpsertPositionOutcomeAsync(p, "reconcile-open", ct);
        }

        foreach (var o in outcomes.Where(x => x.Status == OutcomeStatus.Unresolved))
        {
            if (_positions.GetPositionById(o.LifecycleId) == null)
                await FinalizeFromFillsAsync(o.LifecycleId, o.ExitReason.Length > 0 ? o.ExitReason : "reconcile", o.DustQuantity, ct);
        }
    }

    private async Task RecordAggregateFillAsync(Order order, FillRole role, string? decisionId,
        string? entryIntentId, string? positionId, string? protectionIdentity, OrderBook? book,
        FillEvidence evidence, CancellationToken ct, bool cumulativeFee = true)
    {
        var fills = await _ledger.LoadFillsAsync(ct);
        var previous = fills.Where(f => f.ClientOrderId == order.ClientOrderId && f.Evidence == evidence).ToList();
        var prevQty = previous.Count == 0 ? 0m : previous.Max(f => f.CumulativeFilledQuantity);
        var delta = order.ExecutedQuantity - prevQty;
        if (delta <= 0) return;

        var quote = QuoteOf(order.Symbol);
        var feeAsset = order.CommissionAsset ?? string.Empty;
        var totalFee = order.CommissionByAsset.Count > 0 ? order.CommissionByAsset.Values.Sum() : order.Commission;
        var prevFee = previous.Where(f => string.Equals(f.FeeAsset, feeAsset, StringComparison.OrdinalIgnoreCase)).Sum(f => f.Fee);
        var fee = cumulativeFee ? Math.Max(0m, totalFee - prevFee) : totalFee;
        var valuation = fee == 0 ? FeeValuation.NotApplicable
            : string.Equals(feeAsset, quote, StringComparison.OrdinalIgnoreCase) ? FeeValuation.ReportedQuote
            : FeeValuation.Unvalued;
        var price = order.AverageFillPrice ?? order.Price;
        if (price <= 0) return;

        await _ledger.RecordFillAsync(new FillLedgerRecord
        {
            RunId = RunId, ConfigHash = _configHash, BuildHash = "unknown",
            Environment = Environment, AccountFingerprint = AccountFingerprint,
            QuoteAsset = quote, Source = Source, Symbol = order.Symbol,
            FillId = $"{evidence}:{order.ClientOrderId}:{order.ExecutedQuantity.ToString(CultureInfo.InvariantCulture)}",
            DecisionId = decisionId, EntryIntentId = entryIntentId, PositionId = positionId,
            ProtectionIdentity = protectionIdentity, ClientOrderId = order.ClientOrderId,
            ExchangeOrderId = order.BinanceOrderId > 0 ? order.BinanceOrderId.ToString(CultureInfo.InvariantCulture) : null,
            Role = role, Evidence = evidence, Side = order.Side,
            OrderType = order.Type.ToString(), IsPostOnly = order.IsPostOnly,
            RequestedQuantity = order.OriginalQuantity, RoundedQuantity = order.OriginalQuantity,
            Quantity = delta, CumulativeFilledQuantity = order.ExecutedQuantity, AverageFillPrice = price,
            Fee = fee, FeeAsset = feeAsset,
            FeeQuote = valuation == FeeValuation.NotApplicable ? 0m : valuation == FeeValuation.ReportedQuote ? fee : null,
            FeeValuation = valuation,
            ExchangeTimeUtc = order.UpdatedAt ?? order.CreatedAt,
            LimitPrice = order.Price > 0 ? order.Price : null,
            ArrivalMid = book?.MidPrice, BestBid = book?.BestBid, BestAsk = book?.BestAsk
        }, ct);
    }

    private async Task UpsertPositionOutcomeAsync(Position p, string reason, CancellationToken ct)
    {
        var fills = await _ledger.LoadFillsAsync(ct);
        var linked = SelectFills(fills, p.E1EntryIntentId, p.Id);
        var entry = linked.Where(f => f.Role == FillRole.Entry).ToList();
        var exit = linked.Where(f => f.Role != FillRole.Entry).ToList();
        var entryQty = entry.Sum(f => f.Quantity);
        var exitQty = exit.Sum(f => f.Quantity);
        var avgEntry = WeightedPrice(entry);
        var avgExit = WeightedPrice(exit);
        var feesKnown = linked.All(f => f.FeeValuation != FeeValuation.Unvalued);
        var fees = feesKnown ? linked.Sum(f => f.FeeQuote ?? 0m) : (decimal?)null;
        var gross = avgEntry.HasValue && avgExit.HasValue && exitQty > 0
            ? (p.Side == OrderSide.Buy ? avgExit.Value - avgEntry.Value : avgEntry.Value - avgExit.Value) * exitQty
            : (decimal?)null;
        var net = gross.HasValue && fees.HasValue ? gross.Value - fees.Value : (decimal?)null;
        var risk = p.E1InitialRiskQuote > 0 ? p.E1InitialRiskQuote
            : avgEntry.HasValue && p.InitialStopLoss > 0 ? Math.Abs(avgEntry.Value - p.InitialStopLoss) * entryQty : 0m;
        var closed = entryQty > 0 && exitQty + p.DustExchangeQuantity == entryQty;
        var unresolved = closed
            ? (feesKnown && p.E1ProtectionConfirmed && !string.IsNullOrWhiteSpace(p.E1ProtectionResolution) ? string.Empty : "missing-fee-protection-or-dust-evidence")
            : "position-open-or-partial";

        await RecordOutcomeAsync(new OutcomeLedgerRecord
        {
            RunId = RunId, ConfigHash = _configHash, BuildHash = "unknown",
            Environment = Environment, AccountFingerprint = AccountFingerprint,
            QuoteAsset = QuoteOf(p.Symbol), Source = Source, Symbol = p.Symbol,
            LifecycleId = p.Id, EntryIntentId = p.E1EntryIntentId, PositionId = p.Id,
            DecisionId = p.E1DecisionId,
            Status = closed ? OutcomeStatus.Closed : OutcomeStatus.Unresolved,
            Side = p.Side, EntryBasis = p.ConfidenceSource,
            RequestedQuantity = entryQty, RoundedQuantity = entryQty,
            EntryQuantity = entryQty, ExitQuantity = exitQty, DustQuantity = p.DustExchangeQuantity,
            AverageEntryPrice = avgEntry, AverageExitPrice = avgExit,
            InitialStopLoss = p.InitialStopLoss, InitialRiskQuote = risk > 0 ? risk : null,
            RealizedGrossQuote = gross, RealizedFeesQuote = fees, RealizedNetQuote = net,
            RealizedNetR = net.HasValue && risk > 0 ? net.Value / risk : null,
            ExitReason = reason, ExitTimeUtc = exit.Count > 0 ? exit.Max(f => f.ExchangeTimeUtc) : null,
            HoldingSeconds = exit.Count > 0 && entry.Count > 0
                ? (decimal)(exit.Max(f => f.ExchangeTimeUtc) - entry.Min(f => f.ExchangeTimeUtc)).TotalSeconds : null,
            ProtectionIdentity = p.Protection != null ? ProtectionIdentity(p.Protection)
                : p.E1ProtectionHistory.Count > 0 ? ProtectionIdentity(p.E1ProtectionHistory[^1]) : null,
            ProtectionResolution = p.E1ProtectionResolution,
            ProtectionConfirmed = p.E1ProtectionConfirmed,
            UnresolvedReason = unresolved,
            EntryFillIds = entry.Select(f => f.FillId).ToList(),
            ExitFillIds = exit.Select(f => f.FillId).ToList()
        }, ct);
    }

    private async Task FinalizeFromFillsAsync(string lifecycleId, string reason, decimal dust, CancellationToken ct)
    {
        var outcomes = await _ledger.LoadOutcomesAsync(ct);
        var current = outcomes.FirstOrDefault(o => o.LifecycleId == lifecycleId);
        if (current == null || current.Status != OutcomeStatus.Unresolved) return;
        var fills = await _ledger.LoadFillsAsync(ct);
        var linked = SelectFills(fills, current.EntryIntentId, lifecycleId);
        var entry = linked.Where(f => f.Role == FillRole.Entry).ToList();
        var exit = linked.Where(f => f.Role != FillRole.Entry).ToList();
        var entryQty = entry.Sum(f => f.Quantity);
        var exitQty = exit.Sum(f => f.Quantity);
        if (entryQty <= 0)
        {
            var intent = (await _journal.LoadAsync(ct)).FirstOrDefault(i => i.EntryIntentId == current.EntryIntentId);
            if (intent?.Status == ExecutionIntentStatus.Failed)
            {
                await RecordOutcomeAsync(current with
                {
                    Status = OutcomeStatus.NoFill, UnresolvedReason = string.Empty,
                    ExitReason = reason.Length > 0 ? reason : "no-fill", DustQuantity = 0
                }, ct);
            }
            return;
        }
        if (exitQty + dust < entryQty) return; // still open / dust unresolved; do not finalize a partial as closed.

        var avgEntry = WeightedPrice(entry);
        var avgExit = WeightedPrice(exit);
        var feesKnown = linked.All(f => f.FeeValuation != FeeValuation.Unvalued);
        var fees = feesKnown ? linked.Sum(f => f.FeeQuote ?? 0m) : (decimal?)null;
        var gross = avgEntry.HasValue && avgExit.HasValue
            ? (current.Side == OrderSide.Buy ? avgExit.Value - avgEntry.Value : avgEntry.Value - avgExit.Value) * exitQty
            : (decimal?)null;
        var net = gross.HasValue && fees.HasValue ? gross.Value - fees.Value : (decimal?)null;
        var risk = current.InitialRiskQuote ?? 0m;
        var unresolved = feesKnown && current.ProtectionConfirmed && !string.IsNullOrWhiteSpace(current.ProtectionResolution)
            ? string.Empty : "missing-fee-protection-or-dust-evidence";
        await RecordOutcomeAsync(current with
        {
            Status = OutcomeStatus.Closed,
            EntryQuantity = entryQty, ExitQuantity = exitQty, DustQuantity = dust,
            AverageEntryPrice = avgEntry, AverageExitPrice = avgExit,
            RealizedGrossQuote = gross, RealizedFeesQuote = fees, RealizedNetQuote = net,
            RealizedNetR = net.HasValue && risk > 0 ? net.Value / risk : null,
            ExitReason = reason, ExitTimeUtc = exit.Count > 0 ? exit.Max(f => f.ExchangeTimeUtc) : DateTime.UtcNow,
            HoldingSeconds = exit.Count > 0 && entry.Count > 0
                ? (decimal)(exit.Max(f => f.ExchangeTimeUtc) - entry.Min(f => f.ExchangeTimeUtc)).TotalSeconds : null,
            UnresolvedReason = unresolved,
            EntryFillIds = entry.Select(f => f.FillId).ToList(),
            ExitFillIds = exit.Select(f => f.FillId).ToList()
        }, ct);
    }

    private async Task RecordOutcomeAsync(OutcomeLedgerRecord record, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var existing = (await _ledger.LoadOutcomesAsync(ct))
                .FirstOrDefault(o => o.LifecycleId == record.LifecycleId);
            if (existing != null && existing.Status != OutcomeStatus.Unresolved)
            {
                // Final evidence is immutable. Repeated reconcile/protection passes are idempotent no-ops,
                // not conflicts: a rewrite here used to abort reconciliation and block all entries.
                _logger.LogDebug("E1 outcome {LifecycleId} already resolved as {Status}; skipping {Requested} rewrite",
                    record.LifecycleId, existing.Status, record.Status);
                return;
            }
            await _ledger.RecordOutcomeAsync(record, ct);
        }
        finally { _gate.Release(); }
    }

    private static List<FillLedgerRecord> SelectFills(IReadOnlyList<FillLedgerRecord> fills, string? entryIntentId, string? positionId)
    {
        var linked = fills.Where(f =>
            (entryIntentId != null && f.EntryIntentId == entryIntentId) ||
            (positionId != null && f.PositionId == positionId)).ToList();
        // Prefer authoritative exchange trades; ignore cumulative fallback for the same order once trades exist.
        var byOrder = linked.GroupBy(f => f.ClientOrderId, StringComparer.OrdinalIgnoreCase);
        var result = new List<FillLedgerRecord>();
        foreach (var g in byOrder)
        {
            var exchange = g.Where(f => f.Evidence == FillEvidence.ExchangeTrade).ToList();
            result.AddRange(exchange.Count > 0 ? exchange : g.ToList());
        }
        return result;
    }

    private static decimal? WeightedPrice(List<FillLedgerRecord> fills)
    {
        var qty = fills.Sum(f => f.Quantity);
        return qty > 0 ? fills.Sum(f => f.Quantity * f.AverageFillPrice) / qty : null;
    }

    private static string ProtectionIdentity(ProtectiveOrderList p) =>
        p.OrderListId > 0 ? $"oco:{p.OrderListId}" : $"oco-client:{p.ListClientOrderId}";

    private static Dictionary<string, string> GateVersions() => new()
    {
        ["e1"] = "1", ["risk"] = "1", ["liquidity"] = "1", ["portfolio"] = "1", ["symbol-policy"] = "1"
    };

    private string QuoteOf(string symbol) =>
        SymbolNaming.TrySplit(symbol, _config.Trading.QuoteAssets, out _, out var quote) ? quote : "USDT";

    private static bool feeAssetKnown(string asset) => !string.IsNullOrWhiteSpace(asset);

    private static decimal Dec(string? s) =>
        decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0m;

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..32].ToLowerInvariant();

    private static string StableId(string prefix, params string[] parts) =>
        prefix + "-" + Hash(string.Join('|', parts))[..32];
}

/// <summary>Mutable per-pass decision context. One final decision is recorded for every gate pass.</summary>
public sealed class E1DecisionContext
{
    private readonly E1Runtime _runtime;
    private readonly DecisionLedgerRecord _record;
    private bool _recorded;
    public Dictionary<string, decimal> Features { get; } = new();
    public Dictionary<string, string> EventFlags { get; } = new();
    public string DecisionId => _record.DecisionId;
    public EngineScore? PreScore { get; set; }
    public TradingSignal? Signal { get; set; }
    public AiDisposition AiDisposition { get; set; } = AiDisposition.NotInvoked;
    public string LlmRole { get; set; } = string.Empty;
    public DateTime? CandleCloseUtc { get; set; }
    public string DataSnapshotHash { get; set; } = string.Empty;

    public E1DecisionContext(E1Runtime runtime, string symbol)
    {
        _runtime = runtime;
        _record = runtime.NewDecision(symbol);
    }

    public async Task RejectAsync(string stage, string reasonCode, string text, CancellationToken ct = default) =>
        await RecordAsync(stage, reasonCode, DecisionOutcome.Reject, text, ct);

    public async Task HoldAsync(string stage, string reasonCode, string text, CancellationToken ct = default) =>
        await RecordAsync(stage, reasonCode, DecisionOutcome.Hold, text, ct);

    public async Task AllowAsync(string stage, string reasonCode, string text, CancellationToken ct = default) =>
        await RecordAsync(stage, reasonCode, DecisionOutcome.Allow, text, ct);

    public async Task ErrorAsync(string stage, string reasonCode, string text, CancellationToken ct = default) =>
        await RecordAsync(stage, reasonCode, DecisionOutcome.Error, text, ct);

    private async Task RecordAsync(string stage, string code, DecisionOutcome outcome, string text, CancellationToken ct)
    {
        if (_recorded) return;
        _recorded = true;
        var signal = Signal;
        var final = _record with
        {
            GateStage = stage,
            ReasonCode = code,
            Outcome = outcome,
            ReasonText = text,
            CandleCloseUtc = CandleCloseUtc,
            EngineDirection = PreScore?.DirectionName ?? string.Empty,
            EnginePrior = PreScore?.PriorConfidence,
            ModelAction = signal?.Action.ToString(),
            ModelConfidence = signal?.ModelConfidence,
            FinalConfidence = signal?.Confidence,
            AiDisposition = AiDisposition,
            LlmRole = LlmRole,
            DataSnapshotHash = DataSnapshotHash,
            Features = new Dictionary<string, decimal>(Features),
            EventFlags = new Dictionary<string, string>(EventFlags),
            EstimatorVersion = "e1-runtime-1",
            BucketKey = signal?.Action.ToString() ?? "none",
            BucketLevel = "decision"
        };
        if (signal != null)
        {
            final.Features["final_confidence"] = signal.Confidence;
            final.Features["model_confidence"] = signal.ModelConfidence;
            if (signal.EngineConfidence.HasValue) final.Features["engine_confidence"] = signal.EngineConfidence.Value;
            if (signal.EntryPrice.HasValue) final.Features["signal_entry_price"] = signal.EntryPrice.Value;
            if (signal.StopLoss.HasValue) final.Features["signal_stop_loss"] = signal.StopLoss.Value;
            if (signal.TakeProfit.HasValue) final.Features["signal_take_profit"] = signal.TakeProfit.Value;
        }
        await _runtime.RecordDecisionAsync(final, ct);
    }
}
