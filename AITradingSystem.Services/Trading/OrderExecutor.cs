using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AITradingSystem.Common.Configuration;
using AITradingSystem.Common.Extensions;
using AITradingSystem.Common.Logging;
using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.MarketData;
using AITradingSystem.Core.Models.Trading;
using AITradingSystem.Data.Binance;
using AITradingSystem.Services.Monitoring;
using Microsoft.Extensions.Logging;

namespace AITradingSystem.Services.Trading;

/// <summary>
/// Исполнитель ордеров (FR-6).
///
/// Ключевые исправления:
///  - ордер на вход считается исполненным ТОЛЬКО после подтверждения биржей
///    (опрос статуса с таймаутом, отмена неисполненного остатка) — иначе позиция
///    создавалась по неисполненному лимитному ордеру и «висела» в воздухе;
///  - частичное исполнение: позиция открывается на фактически исполненный объём;
///  - округление до stepSize/tickSize и проверка minQty/minNotional (фильтры Binance
///    приходят строками — парсер исправлен в BinanceModels);
///  - спот: SELL без открытой позиции не открывает шорт;
///  - paper-режим учитывает комиссию.
///
/// P0.1: сразу после филла входа ставится биржевая защита — OCO
/// (STOP_LOSS_LIMIT + TAKE_PROFIT_LIMIT). Трейлинг — cancel/replace связки,
/// локальный SL в TradingEngine остаётся только дублёром.
/// </summary>
public sealed class OrderExecutor : IOrderExecutor, IDisposable
{
    private readonly IBinanceRestClient _binanceClient;
    private readonly TradingConfig _tradingConfig;
    private readonly ExecutionQueue _executionQueue;
    private readonly IBinanceWebSocketClient _ws;
    private readonly ILogger<OrderExecutor> _logger;

    /// <summary>P1.5: метрики post-only-отказов (опционально — старые вызовы совместимы).</summary>
    private readonly MetricsCollector? _metrics;

    // P0.5: ожидание филлов идёт через executionReport, а не polling.
    private readonly ConcurrentDictionary<string, ExecutionReport> _latestReports = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ExecutionReport>> _fillWaiters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (BinanceSymbolInfo Info, DateTime CachedAt)> _symbolInfoCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan SymbolInfoTtl = TimeSpan.FromHours(1);

    /// <summary>Когда последний раз делали cancel/replace защитной связки позиции (троттлинг трейлинга).</summary>
    private readonly Dictionary<string, DateTime> _lastProtectionReplace = new(StringComparer.Ordinal);
    private readonly object _protectionLock = new();

    public OrderExecutor(
        IBinanceRestClient binanceClient,
        TradingConfig tradingConfig,
        ExecutionQueue executionQueue,
        IBinanceWebSocketClient ws,
        ILogger<OrderExecutor> logger,
        MetricsCollector? metrics = null)
    {
        _binanceClient = binanceClient ?? throw new ArgumentNullException(nameof(binanceClient));
        _tradingConfig = tradingConfig ?? throw new ArgumentNullException(nameof(tradingConfig));
        _executionQueue = executionQueue ?? throw new ArgumentNullException(nameof(executionQueue));
        _ws = ws ?? throw new ArgumentNullException(nameof(ws));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metrics = metrics;

        _ws.OnExecutionReport += OnExecutionReportForFill;

        // P0.3: восстановленные из журнала намерения сверяются с биржей здесь —
        // только исполнитель знает, как забрать ордер/связку по clientOrderId.
        _executionQueue.SetIntentResolver(ResolveRestoredIntentAsync);
    }

    /// <summary>
    /// P0.3: сверка намерения, пережившего рестарт процесса.
    /// Ордер (entry/close) забирается по <c>origClientOrderId</c>; связку OCO целиком
    /// восстановить нельзя (Binance не отдаёт ноги в openOrderList), поэтому возможный
    /// «висяк» снимается и защита ставится заново — тем же детерминированным ключом.
    /// Возврат null = биржа об ордере не знает, ключ можно снять.
    /// </summary>
    private async Task<object?> ResolveRestoredIntentAsync(ExecutionIntent intent, CancellationToken ct)
    {
        if (intent.Operation is nameof(ExecutionPriority.Protection) or nameof(ExecutionPriority.Trailing))
        {
            try
            {
                await _binanceClient.CancelOcoAsync(intent.Symbol, 0, intent.ClientOrderId, ct);
                TradeLog.Warn("RESTART-PROTECTION-CANCELED", intent.Symbol,
                    $"снята незавершённая связка {intent.ClientOrderId} — защита будет поставлена заново");
            }
            catch (BinanceApiException ex) when (ex.IsUnknownOrderOrList)
            {
                // Связки нет: сработала нога или постановка не долетела. Разбор филлов — P0.5.
            }

            return null;
        }

        try
        {
            return await _binanceClient.GetOrderAsync(intent.Symbol, intent.ClientOrderId, ct);
        }
        catch (BinanceApiException ex) when (ex.IsUnknownOrderOrList)
        {
            return null;
        }
    }

    /// <summary>
    /// P0.3: отправка ордера через очередь с приоритетом и ключом идемпотентности.
    /// «Duplicate order sent» — не отказ: ордер уже принят, берём его состояние по clientOrderId.
    /// </summary>
    private async Task<Order> PlaceOrderIdempotentAsync(
        string symbol,
        ExecutionPriority priority,
        string clientOrderId,
        Func<string, CancellationToken, Task<Order>> place,
        CancellationToken ct)
    {
        try
        {
            return await _executionQueue.SubmitAsync(
                symbol, priority, clientOrderId, token => place(clientOrderId, token), ct);
        }
        catch (BinanceApiException ex) when (ex.IsDuplicateOrder)
        {
            TradeLog.Decision(symbol, "IDEMPOTENT",
                $"{clientOrderId}: ордер уже на бирже, берём его состояние вместо повторной отправки");

            return await _binanceClient.GetOrderAsync(symbol, clientOrderId, ct);
        }
    }

    /// <summary>
    /// P0.3: детерминированный clientOrderId (префикс + 32 hex от параметров операции).
    /// В отличие от GUID он одинаков после рестарта: повторное закрытие той же позиции
    /// или постановка той же защиты попадает в журнал намерений и не создаёт второй ордер.
    /// </summary>
    private static string StableClientOrderId(string prefix, params object[] parts)
    {
        var payload = string.Join('|', parts.Select(p => p switch
        {
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            DateTime dt => dt.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => p?.ToString() ?? string.Empty
        }));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return $"{prefix}-{Convert.ToHexString(hash, 0, 16).ToLowerInvariant()}";
    }

    private static IEnumerable<string> ReportKeys(Order order)
    {
        if (!string.IsNullOrWhiteSpace(order.ClientOrderId)) yield return order.ClientOrderId;
        if (order.BinanceOrderId > 0) yield return "id:" + order.BinanceOrderId;
    }

    private static IEnumerable<string> ReportKeys(ExecutionReport report)
    {
        if (!string.IsNullOrWhiteSpace(report.ClientOrderId)) yield return report.ClientOrderId;
        if (report.OrderId > 0) yield return "id:" + report.OrderId;
    }

    private void OnExecutionReportForFill(ExecutionReport report)
    {
        _executionQueue.ActiveEntries.ObserveReport(report);
        foreach (var key in ReportKeys(report))
        {
            _latestReports[key] = report;
            if (_fillWaiters.TryRemove(key, out var waiter))
                waiter.TrySetResult(report);
        }
    }

    private static bool IsTerminalStatus(OrderStatus status) =>
        status is OrderStatus.Filled or OrderStatus.Canceled or OrderStatus.Rejected or OrderStatus.Expired;

    private static OrderStatus MapReportStatus(string status) => status switch
    {
        "FILLED" => OrderStatus.Filled,
        "PARTIALLY_FILLED" => OrderStatus.PartiallyFilled,
        "CANCELED" => OrderStatus.Canceled,
        "REJECTED" => OrderStatus.Rejected,
        "EXPIRED" => OrderStatus.Expired,
        _ => OrderStatus.New
    };

    private static Order ApplyReport(Order order, ExecutionReport report)
    {
        order.Status = MapReportStatus(report.OrderStatus);
        order.ExecutedQuantity = report.CumulativeQuantity;
        order.FilledQuoteQuantity = report.CumulativeQuoteQuantity;
        order.UpdatedAt = report.TransactionTimeUtc;
        return order;
    }

    public void Dispose()
    {
        _ws.OnExecutionReport -= OnExecutionReportForFill;
        foreach (var waiter in _fillWaiters.Values) waiter.TrySetCanceled();
        _fillWaiters.Clear();
    }

    public async Task<Order?> ExecuteSignalAsync(
        TradingSignal signal,
        decimal quantity,
        OrderBook? currentOrderBook,
        CancellationToken ct = default)
    {
        if (signal.Action is SignalAction.Hold or SignalAction.Close)
        {
            _logger.LogDebug("Сигнал {Action} для {Symbol} не требует ордера на вход", signal.Action, signal.Symbol);
            return null;
        }

        if (!signal.EntryPrice.HasValue || quantity <= 0)
        {
            TradeLog.Rejected(signal.Symbol, "entry", "нет EntryPrice или quantity <= 0");
            return null;
        }

        var side = signal.Action == SignalAction.Buy ? OrderSide.Buy : OrderSide.Sell;

        if (side == OrderSide.Sell && !_tradingConfig.AllowShort)
        {
            TradeLog.Rejected(signal.Symbol, "entry",
                "SELL на споте без открытой позиции — шорт невозможен (Trading:AllowShort=false)");
            return null;
        }

        var executionPrice = signal.EntryPrice.Value;

        // FR-6.2: Post-Only (Maker). Buy: цена <= Best Bid; Sell: цена >= Best Ask.
        if (currentOrderBook != null && !_tradingConfig.UseMarketEntry)
        {
            if (side == OrderSide.Buy && currentOrderBook.BestBid.HasValue)
                executionPrice = Math.Min(executionPrice, currentOrderBook.BestBid.Value);
            else if (side == OrderSide.Sell && currentOrderBook.BestAsk.HasValue)
                executionPrice = Math.Max(executionPrice, currentOrderBook.BestAsk.Value);
        }

        // === Фильтры символа: округление до stepSize/tickSize, minQty/minNotional ===
        // Применяем и в paper-режиме (best effort): иначе «бумажные» сделки проходят там,
        // где реальная биржа ордер отклонит, и статистика paper-режима врёт.
        var info = await GetSymbolInfoCachedAsync(signal.Symbol, ct);
        if (info != null)
        {
            if (!_tradingConfig.UseMarketEntry && info.TickSize > 0)
                executionPrice = RoundPriceToTick(executionPrice, info.TickSize, side);

            var notionalPrice = _tradingConfig.UseMarketEntry
                ? currentOrderBook?.MidPrice ?? executionPrice
                : executionPrice;

            // MARKET живёт по MARKET_LOT_SIZE, LIMIT — по LOT_SIZE; NOTIONAL для MARKET —
            // только при applyMinToMarket. Минимум проверяем с буфером на проскальзывание.
            // bumpMaxPercent = 0: исполнитель не имеет права увеличивать объём против риск-сайзинга,
            // подъём до минимума решает TradingEngine (MIN-NOTIONAL-BUMP).
            var entryPlan = EntryFilterPolicy.PlanEntry(
                quantity, notionalPrice, _tradingConfig.UseMarketEntry,
                info.StepSize, info.MinQty, info.MarketStepSize, info.MarketMinQty,
                info.MinNotional, info.ApplyMinNotionalToMarket,
                _tradingConfig.MinNotionalBufferPercent,
                bumpMaxPercent: 0m, quantityCeiling: quantity);

            if (entryPlan.Route == EntryFilterRoute.Reject)
            {
                TradeLog.Rejected(signal.Symbol, "filters", entryPlan.Reason ?? "объём ниже фильтров символа");
                return null;
            }

            quantity = entryPlan.Quantity;
        }
        else if (!_tradingConfig.PaperTradingMode)
        {
            TradeLog.Warn("FILTERS-UNAVAILABLE", signal.Symbol, "ордер отправляется без округления",
                gateKey: "filters-unavailable:" + signal.Symbol);
        }

        // === Paper Trading: P1.1 taker-филл по стакану + комиссия; post-only — maker по лимитной цене ===
        if (_tradingConfig.PaperTradingMode)
        {
            Order? paperOrder;
            if (_tradingConfig.UseMarketEntry)
            {
                if (_tradingConfig.PaperSlippageEnabled)
                {
                    if (currentOrderBook == null)
                    {
                        TradeLog.Rejected(signal.Symbol, "paper-entry", "нет стакана для симуляции taker-филла");
                        return null;
                    }

                    var fill = currentOrderBook.SimulateMarketFill(
                        side, quantity, maxLevels: _tradingConfig.PaperSlippageMaxLevels);
                    if (fill.FilledQuantity <= 0m)
                    {
                        TradeLog.Rejected(signal.Symbol, "paper-entry", "стакан не исполнил paper market-вход");
                        return null;
                    }

                    paperOrder = CreatePaperOrder(signal.Symbol, side, quantity, fill.FilledQuantity,
                        fill.AveragePrice, postOnly: false, _tradingConfig.PaperCommissionRate);
                    TradeLog.Decision(signal.Symbol, "PAPER-SLIPPAGE",
                        $"entry {side}: filled={fill.FilledQuantity}/{quantity}, avg={fill.AveragePrice:F8}, " +
                        $"slippage={fill.SlippagePercent:F4}%");
                }
                else
                {
                    paperOrder = CreatePaperOrder(signal.Symbol, side, quantity, quantity,
                        executionPrice, postOnly: false, _tradingConfig.PaperCommissionRate);
                }
            }
            else
            {
                // Post-only maker не пересекает спред; в paper считаем филл по лимитной цене.
                paperOrder = CreatePaperOrder(signal.Symbol, side, quantity, quantity,
                    executionPrice, postOnly: true, _tradingConfig.PaperCommissionRate);
            }

            var openedPrice = paperOrder.AverageFillPrice ?? paperOrder.Price;
            TradeLog.Opened(signal.Symbol, side.ToString(), paperOrder.ExecutedQuantity, openedPrice,
                signal.StopLoss ?? 0m, signal.TakeProfit ?? 0m, "PAPER", paperOrder.Id);
            return paperOrder;
        }

        try
        {
            if (_tradingConfig.UseMarketEntry)
            {
                var marketOrder = await PlaceOrderIdempotentAsync(
                    signal.Symbol, ExecutionPriority.Entry,
                    StableClientOrderId("mkt", signal.Symbol, side, quantity, executionPrice, signal.Timestamp),
                    (id, token) => _binanceClient.PlaceMarketOrderAsync(signal.Symbol, side, quantity, id, token), ct);
                _executionQueue.ActiveEntries.Complete(marketOrder.ClientOrderId);
                return ValidateFilled(marketOrder, signal.Symbol, "market-entry");
            }

            var order = await PlaceOrderIdempotentAsync(
                signal.Symbol, ExecutionPriority.Entry,
                StableClientOrderId("ent", signal.Symbol, side, quantity, executionPrice, signal.Timestamp),
                (id, token) => _binanceClient.PlaceOrderAsync(
                    signal.Symbol, side, quantity, executionPrice, postOnly: true, clientOrderId: id, ct: token), ct);

            if (order.Status is OrderStatus.Rejected or OrderStatus.Expired or OrderStatus.Canceled)
            {
                _executionQueue.ActiveEntries.Complete(order.ClientOrderId);
                TradeLog.Rejected(signal.Symbol, "exchange", $"ордер отклонён биржей: {order.Status}");
                return null;
            }

            // Ждём реального исполнения: лимитный Post-Only ордер может простоять в стакане долго.
            var filled = await WaitForFillAsync(order, ct);
            _executionQueue.ActiveEntries.Complete(order.ClientOrderId);
            return ValidateFilled(filled, signal.Symbol, "limit-entry");
        }
        catch (BinanceApiException ex) when (ex.IsPostOnlyRejection)
        {
            // P1.5: post-only-отказ агрегируется в метрику (раньше только лог сделки).
            _metrics?.RecordPostOnlyRejection();
            TradeLog.Rejected(signal.Symbol, "post-only",
                "цена пересекла спред (ордер был бы taker). Сигнал пропущен, ждём следующий цикл.");
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            TradeLog.Rejected(signal.Symbol, "exception", ex.Message, ex);
            return null;
        }
    }

    public async Task<Order?> ClosePositionAsync(
        Position position,
        CancellationToken ct = default,
        OrderBook? currentOrderBook = null,
        bool emergency = false)
    {
        var side = position.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;

        TradeLog.Info("CLOSE-START", position.Symbol, $"side={side} qty={LogOptions.N(position.Quantity)}");

        if (_tradingConfig.PaperTradingMode)
        {
            var fallbackPrice = position.CurrentPrice > 0 ? position.CurrentPrice : position.EntryPrice;
            var bypassCap = emergency && _tradingConfig.EmergencyMarketExitBypassSlippageCap;
            var useCap = _tradingConfig.MarketExitSlippageGuardEnabled && !bypassCap;
            decimal? worstPrice = null;

            if (_tradingConfig.PaperSlippageEnabled && currentOrderBook != null)
            {
                var reference = side == OrderSide.Sell
                    ? currentOrderBook.BestBid ?? fallbackPrice
                    : currentOrderBook.BestAsk ?? fallbackPrice;

                if (useCap && reference > 0m)
                {
                    worstPrice = side == OrderSide.Sell
                        ? reference * (1m - _tradingConfig.MarketExitMaxSlippage)
                        : reference * (1m + _tradingConfig.MarketExitMaxSlippage);
                }

                var fill = currentOrderBook.SimulateMarketFill(
                    side, position.Quantity, worstPrice, _tradingConfig.PaperSlippageMaxLevels);
                if (fill.FilledQuantity <= 0m)
                {
                    TradeLog.Rejected(position.Symbol, "paper-close",
                        "стакан/cap не исполнил paper-выход; позиция остаётся открытой");
                    return null;
                }

                var paperOrder = CreatePaperOrder(position.Symbol, side, position.Quantity, fill.FilledQuantity,
                    fill.AveragePrice, postOnly: false, _tradingConfig.PaperCommissionRate);
                position.SetProtection(null);

                ExchangeLog.Order("PAPER-CLOSE", position.Symbol, side.ToString(), fill.FilledQuantity, fill.AveragePrice,
                    $"fee={paperOrder.Commission:F4} slippage={fill.SlippagePercent:F4}%");
                TradeLog.Decision(position.Symbol, "PAPER-SLIPPAGE",
                    $"exit {side}: filled={fill.FilledQuantity}/{position.Quantity}, avg={fill.AveragePrice:F8}, " +
                    $"slippage={fill.SlippagePercent:F4}%, cap={(worstPrice.HasValue ? worstPrice.Value.ToString("F8") : "off")}");
                return paperOrder;
            }

            // Fallback: стакан недоступен или модель выключена — филл по текущей цене.
            var fallbackOrder = CreatePaperOrder(position.Symbol, side, position.Quantity, position.Quantity,
                fallbackPrice, postOnly: false, _tradingConfig.PaperCommissionRate);
            position.SetProtection(null);

            ExchangeLog.Order("PAPER-CLOSE", position.Symbol, side.ToString(), position.Quantity, fallbackPrice,
                $"fee={fallbackOrder.Commission:F4}");
            return fallbackOrder;
        }

        // P1.3: dust-закрытие фиксируется отдельно — метрика нужна и в catch (переменные try там не видны).
        var isDustClose = false;

        try
        {
            var info = await GetSymbolInfoCachedAsync(position.Symbol, ct);

            // P1.2: обычный выход — marketable LIMIT IOC с худшей допустимой ценой.
            // Аварийный выход при EmergencyMarketExitBypassSlippageCap=true идёт MARKET без cap.
            var bypassCap = emergency && _tradingConfig.EmergencyMarketExitBypassSlippageCap;
            var useIoc = _tradingConfig.MarketExitSlippageGuardEnabled && !bypassCap;

            // P1.3: ordinary MARKET не обходит LOT_SIZE/MARKET_LOT_SIZE/NOTIONAL.
            // Остаток ниже фильтров помечается для ручного аудита и не отправляется на биржу.
            var reference = side == OrderSide.Sell
                ? currentOrderBook?.BestBid ?? position.CurrentPrice
                : currentOrderBook?.BestAsk ?? position.CurrentPrice;
            if (reference <= 0m) reference = position.EntryPrice;

            // P0.1: сначала снимаем OCO — иначе базовый актив заблокирован защитными ногами
            // и MARKET-закрытие получит -2010 «недостаточно средств».
            if (!await CancelProtectionAsync(position, ct))
            {
                TradeLog.Warn("CLOSE-PROTECTION-CANCEL-FAIL", position.Symbol,
                    "защитную OCO снять не удалось — MARKET может быть отклонён по заблокированному объёму");
            }

            // Live SELL: нельзя отправлять gross-объём позиции, если часть базового актива
            // ушла на комиссию или уже заблокирована/продана. Берём свежий free-баланс биржи.
            var liveQuantity = position.Quantity;
            if (!_tradingConfig.PaperTradingMode && side == OrderSide.Sell)
            {
                liveQuantity = await GetLiveSellableQuantityAsync(position, info, ct);
                if (liveQuantity <= 0m)
                {
                    TradeLog.Rejected(position.Symbol, "close",
                        "free-баланс базового актива на бирже = 0 — нечего закрывать; позиция требует реконсиляции");
                    return null;
                }
            }

            var plan = DustPolicy.PlanClose(
                side, liveQuantity, reference,
                info?.StepSize ?? 0m, info?.MinQty ?? 0m, info?.MinNotional ?? 0m,
                useIoc, _tradingConfig.DustMarketCloseEnabled,
                info?.MarketStepSize ?? 0m, info?.MarketMinQty ?? 0m,
                info?.ApplyMinNotionalToMarket ?? true);

            if (plan.Route == DustPolicy.DustCloseRoute.Reject)
            {
                TradeLog.Rejected(position.Symbol, "close",
                    $"{plan.Reason ?? "quantity после округления <= 0"} (qty={LogOptions.N(liveQuantity)})");
                if ((plan.Reason ?? string.Empty).Contains("dust:", StringComparison.OrdinalIgnoreCase))
                {
                    position.MarkDustForManualReview(liveQuantity, plan.Reason ?? "dust below exchange filters");
                    _metrics?.RecordDustClose(rejected: true);
                }
                await RestoreProtectionAfterFailedCloseAsync(position, ct);
                return null;
            }

            var quantity = plan.Quantity;
            isDustClose = plan.Route == DustPolicy.DustCloseRoute.DustMarketSell;
            if (isDustClose)
                useIoc = false; // dust невозможно выставить лимитным ордером — только MARKET всего остатка.

            var exitLimitPrice = 0m;

            if (useIoc)
            {
                exitLimitPrice = side == OrderSide.Sell
                    ? reference * (1m - _tradingConfig.MarketExitMaxSlippage)
                    : reference * (1m + _tradingConfig.MarketExitMaxSlippage);

                if (info != null && info.TickSize > 0)
                    exitLimitPrice = RoundPriceToTick(exitLimitPrice, info.TickSize, side);

                if (exitLimitPrice <= 0m)
                {
                    TradeLog.Rejected(position.Symbol, "close", "не удалось рассчитать цену IOC-выхода");
                    await RestoreProtectionAfterFailedCloseAsync(position, ct);
                    return null;
                }
            }

            // P0.3: закрытие идёт с приоритетом Close — обгоняет входы в очереди.
            var order = await PlaceOrderIdempotentAsync(
                position.Symbol, ExecutionPriority.Close,
                StableClientOrderId("cls", position.Id, quantity, exitLimitPrice, useIoc),
                (id, token) => useIoc
                    ? _binanceClient.PlaceIocOrderAsync(position.Symbol, side, quantity, exitLimitPrice, id, token)
                    : _binanceClient.PlaceMarketOrderAsync(position.Symbol, side, quantity, id, token), ct);

            if (order.ExecutedQuantity <= 0)
            {
                // MARKET может не исполниться сразу в тонком стакане — проверяем статус один раз.
                order = await WaitForFillAsync(order, ct);
            }

            var validated = ValidateFilled(order, position.Symbol, "close");
            if (validated == null)
            {
                // P1.3: для dust восстановление OCO бессмысленно — ноги не пройдут minQty/minNotional,
                // остаток не защищаем, а фиксируем отказ для ручного аудита/реконсиляции.
                if (isDustClose)
                {
                    _metrics?.RecordDustClose(rejected: true);
                    TradeLog.Rejected(position.Symbol, "dust-close",
                        $"dust {position.Quantity} не закрылся MARKET — позиция остаётся открытой, нужен ручной аудит");
                }
                else
                {
                    await RestoreProtectionAfterFailedCloseAsync(position, ct);
                }
            }
            else
            {
                if (isDustClose)
                {
                    _metrics?.RecordDustClose(rejected: false);
                    TradeLog.Decision(position.Symbol, "DUST-CLOSE",
                        $"dust-остаток {validated.ExecutedQuantity} закрыт MARKET (объём был ниже stepSize/minQty/minNotional)");
                }

                if (validated.ExecutedQuantity < quantity)
                    TradeLog.Rejected(position.Symbol, "close-partial",
                        $"IOC/MARKET закрыл {validated.ExecutedQuantity} из {quantity}; остаток будет дозакрыт и защищён");
            }

            return validated;
        }
        catch (BinanceApiException ex) when (ex.IsOrderFilterFailure)
        {
            position.MarkDustForManualReview(position.Quantity, ex.ApiMessage);
            _metrics?.RecordDustClose(rejected: true);
            TradeLog.Rejected(position.Symbol, "close-filter",
                $"биржа отклонила ордер по фильтрам ({ex.ApiMessage}); автоматические повторы остановлены, нужен ручной аудит", ex);

            return null;
        }
        catch (BinanceApiException ex) when (ex.IsInsufficientBalance)
        {
            // Частая причина на споте: часть базового актива ушла на комиссию (BNB/актив),
            // либо позиция в учёте больше, чем реальный free-баланс монеты.
            if (isDustClose)
                _metrics?.RecordDustClose(rejected: true);
            TradeLog.Rejected(position.Symbol, "close",
                $"биржа отклонила закрытие: недостаточно средств ({ex.ApiMessage}). " +
                "Проверьте free-баланс базового актива на счёте — позиция останется открытой и будет пересчитана.", ex);

            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (isDustClose)
                _metrics?.RecordDustClose(rejected: true);
            TradeLog.Rejected(position.Symbol, "close", ex.Message, ex);
            return null;
        }
    }

    /// <summary>P1.2: если выход не исполнился после снятия OCO, возвращаем защиту на остаток/позицию.</summary>
    private async Task RestoreProtectionAfterFailedCloseAsync(Position position, CancellationToken ct)
    {
        if (position.Protection != null || _tradingConfig.PaperTradingMode || !_tradingConfig.ExchangeProtectionEnabled)
            return;
        await PlaceProtectionAsync(position, position.StopLoss, ct);
    }

    /// <summary>
    /// P0.1: биржевая защита позиции. Ставит OCO, если её ещё нет.
    /// Вызывается сразу после филла входа и на каждом круге сопровождения
    /// (в т.ч. для позиций, восстановленных из positions.json без защиты).
    /// </summary>
    public async Task<bool> EnsureProtectionAsync(Position position, CancellationToken ct = default)
    {
        if (!_tradingConfig.ExchangeProtectionEnabled)
            return true; // осознанный отказ от биржевой защиты (локальный SL — единственный)

        if (position.Protection != null)
            return true; // уже стоит на бирже либо симулируется в paper-режиме

        var protection = await PlaceProtectionAsync(position, position.StopLoss, ct);
        return protection != null;
    }

    /// <summary>
    /// Трейлинг через cancel/replace биржевой связки (P0.1).
    /// Локальный SL подтягивается ТОЛЬКО после подтверждённой замены на бирже —
    /// иначе локальный дублёр убежит вперёд реальной защиты.
    /// </summary>
    public async Task<bool> UpdateStopLossAsync(Position position, decimal newStopLoss, CancellationToken ct = default)
    {
        if (newStopLoss <= 0)
            return false;

        if (!ProtectiveOrderPricing.IsStopImprovementSignificant(
                position.Side, position.StopLoss, newStopLoss, _tradingConfig.TrailingReplaceMinStepPercent))
            return false;

        lock (_protectionLock)
        {
            if (_lastProtectionReplace.TryGetValue(position.Id, out var last) &&
                DateTime.UtcNow - last < TimeSpan.FromSeconds(_tradingConfig.TrailingReplaceMinIntervalSeconds))
                return false;
        }

        if (!_tradingConfig.ExchangeProtectionEnabled || _tradingConfig.PaperTradingMode)
        {
            var localChanged = position.UpdateTrailingStop(newStopLoss);
            if (localChanged && position.Protection is { IsSimulated: true } simulated)
            {
                simulated.StopTriggerPrice = newStopLoss;
                simulated.ReplacedAt = DateTime.UtcNow;
            }
            MarkProtectionReplaced(position.Id);
            return localChanged;
        }

        var previousStop = position.StopLoss;

        // cancel/replace: объём заблокирован старой связкой, поэтому сначала cancel, потом новая постановка.
        await CancelProtectionAsync(position, ct);
        MarkProtectionReplaced(position.Id);

        // Трейлинг уступает первичной постановке защиты и закрытию (P0.3).
        var replacement = await PlaceProtectionAsync(position, newStopLoss, ct, ExecutionPriority.Trailing);
        if (replacement != null)
        {
            position.UpdateTrailingStop(newStopLoss);
            replacement.ReplacedAt = DateTime.UtcNow;


            TradeLog.Decision(position.Symbol, "TRAILING-REPLACE",
                $"стоп {previousStop} → {newStopLoss}, OCO#{replacement.OrderListId}");
            return true;
        }

        // Новая связка не встала — возвращаем прежний стоп, чтобы не оставить позицию без защиты.
        TradeLog.Error("TRAILING-REPLACE-FAIL", position.Symbol,
            $"cancel/replace OCO не удался — возвращаем прежний стоп {LogOptions.N(previousStop)}");

        var restored = await PlaceProtectionAsync(position, previousStop, ct);
        if (restored == null)
        {
            position.SetProtection(null);
            TradeLog.Rejected(position.Symbol, "protection",
                "cancel/replace не удался и прежняя защита не восстановлена — позиция БЕЗ биржевого стопа");
        }

        return false;
    }

    /// <summary>Снять защитную OCO-связку позиции (перед market-закрытием).</summary>
    public async Task<bool> CancelProtectionAsync(Position position, CancellationToken ct = default)
    {
        var protection = position.Protection;
        if (protection == null)
            return true;

        if (!protection.IsLive)
        {
            position.SetProtection(null); // симуляция в paper-режиме
            return true;
        }

        try
        {
            await _executionQueue.SubmitAsync<object?>(
                position.Symbol, ExecutionPriority.Protection, null,
                async token =>
                {
                    await _binanceClient.CancelOcoAsync(
                        position.Symbol, protection.OrderListId, protection.ListClientOrderId, token);
                    return null;
                }, ct);
            position.SetProtection(null);
            // P0.3: ключ снятой связки больше не должен блокировать постановку такой же защиты.
            await _executionQueue.ForgetKeyAsync(
                position.Symbol, protection.ListClientOrderId, "OCO снята локально", ct);
            return true;
        }
        catch (BinanceApiException ex) when (ex.IsUnknownOrderOrList)
        {
            // Связки на бирже уже нет: сработала одна из ног (позицию закрыла биржа) либо отменена ранее.

            TradeLog.Decision(position.Symbol, "PROTECTION-GONE",
                $"OCO#{protection.OrderListId} отсутствует — возможное исполнение биржей, нужна реконсиляция (P0.5)");
            position.SetProtection(null);
            await _executionQueue.ForgetKeyAsync(
                position.Symbol, protection.ListClientOrderId, "OCO на бирже не найдена", ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            TradeLog.Rejected(position.Symbol, "protection-cancel",
                $"не удалось отменить OCO#{protection.OrderListId}: {ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// Постановка OCO: округление обеих ног к tick/step, проверка minQty/minNotional,
    /// отправка на биржу и запись id ног в позицию.
    /// </summary>
    private async Task<ProtectiveOrderList?> PlaceProtectionAsync(
        Position position, decimal stopLoss, CancellationToken ct,
        ExecutionPriority priority = ExecutionPriority.Protection)
    {
        var referencePrice = position.CurrentPrice > 0 ? position.CurrentPrice : position.EntryPrice;
        var info = await GetSymbolInfoCachedAsync(position.Symbol, ct);

        if (info == null && !_tradingConfig.PaperTradingMode)
            TradeLog.Warn("FILTERS-UNAVAILABLE", position.Symbol, "защитные ноги отправляются без округления",
                gateKey: "filters-unavailable-protection:" + position.Symbol);

        // Live OCO SELL защищаем только реально доступный net-объём базового актива.
        var protectionQuantity = position.Quantity;
        if (!_tradingConfig.PaperTradingMode && position.Side == OrderSide.Buy)
        {
            try
            {
                protectionQuantity = await GetLiveSellableQuantityAsync(position, info, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                TradeLog.Rejected(position.Symbol, "protection",
                    "не удалось получить free-баланс базового актива — OCO не ставится вслепую", ex);
                return null;
            }

            if (protectionQuantity <= 0m)
            {
                TradeLog.Rejected(position.Symbol, "protection",
                    "free-баланс базового актива на бирже = 0 — OCO нечего защищать; нужна реконсиляция");
                return null;
            }
        }

        if (!ProtectiveOrderPricing.TryBuild(
                position.Side, protectionQuantity, stopLoss, position.TakeProfit, referencePrice,
                info?.TickSize ?? 0m, info?.StepSize ?? 0m, info?.MinQty ?? 0m, info?.MinNotional ?? 0m,
                _tradingConfig.ProtectionLimitSlippage, out var prices, out var error) || prices == null)
        {
            TradeLog.Rejected(position.Symbol, "protection", error ?? "не удалось рассчитать цены защитных ног");
            return null;
        }

        // P1.3: «хвост» защиты не покрыт OCO. Если он ниже фильтров ordinary-ордера,
        // после срабатывания ноги он уйдёт в ручной аудит, а не в невалидный MARKET.
        var protectionTail = DustPolicy.ProtectionTail(position.Quantity, prices.Quantity);
        if (protectionTail > 0m)
        {
            _metrics?.RecordProtectionTail(protectionTail);
            TradeLog.Decision(position.Symbol, "PROTECTION-TAIL",
                $"OCO покрывает {prices.Quantity} из {position.Quantity}; хвост {protectionTail} (меньше stepSize) биржевой защитой не покрыт");
        }

        var protectiveSide = position.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy;

        if (_tradingConfig.PaperTradingMode)
        {
            var simulated = new ProtectiveOrderList
            {
                Symbol = position.Symbol,
                Side = protectiveSide,
                Quantity = prices.Quantity,
                StopTriggerPrice = prices.StopTrigger,
                StopLimitPrice = prices.StopLimit,
                TakeProfitTriggerPrice = prices.TakeProfitTrigger,
                TakeProfitLimitPrice = prices.TakeProfitLimit,
                ListClientOrderId = $"paper-oco-{Guid.NewGuid():N}",
                IsSimulated = true,
                ListOrderStatus = "SIMULATED"
            };

            position.SetProtection(simulated);
            ExchangeLog.Order("PAPER-OCO", position.Symbol, protectiveSide.ToString(), prices.Quantity,
                prices.StopTrigger, $"SL={prices.StopTrigger}/{prices.StopLimit} TP={prices.TakeProfitTrigger}/{prices.TakeProfitLimit}");
            return simulated;
        }

        try
        {
            // P0.3: listClientOrderId генерируется здесь и переиспользуется при ретрае —
            // повторная отправка той же связки не создаст вторую пару ног.
            var listClientId = StableClientOrderId(
                "oco", position.Id, prices.Quantity, prices.StopTrigger, prices.TakeProfitTrigger);
            var protection = await _executionQueue.SubmitAsync(
                position.Symbol, priority, listClientId,
                token => _binanceClient.PlaceOcoAsync(
                    position.Symbol, protectiveSide, prices.Quantity,
                    prices.StopTrigger, prices.StopLimit,
                    prices.TakeProfitTrigger, prices.TakeProfitLimit,
                    listClientOrderId: listClientId, ct: token), ct);

            position.SetProtection(protection);

            TradeLog.Decision(position.Symbol, "PROTECTION",
                $"OCO на бирже: SL={protection.StopTriggerPrice}/{protection.StopLimitPrice} " +
                $"TP={protection.TakeProfitTriggerPrice}/{protection.TakeProfitLimitPrice} " +
                $"qty={protection.Quantity} listId={protection.OrderListId}");

            return protection;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            TradeLog.Rejected(position.Symbol, "protection", ex.Message, ex);
            return null;
        }
    }

    private void MarkProtectionReplaced(string positionId)
    {
        lock (_protectionLock)
        {
            _lastProtectionReplace[positionId] = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Опрос статуса ордера до полного исполнения или таймаута.
    /// По таймауту неисполненный остаток отменяется, исполненная часть возвращается.
    /// </summary>
    private async Task<Order> WaitForFillAsync(Order order, CancellationToken ct)
    {
        if (order.Status == OrderStatus.Filled || order.BinanceOrderId <= 0)
            return order;

        var sw = Stopwatch.StartNew();
        var timeout = TimeSpan.FromSeconds(_tradingConfig.OrderFillTimeoutSeconds);
        var current = order;
        _executionQueue.ActiveEntries.Observe(order.ClientOrderId, order);

        // P0.5: основной источник статуса — executionReport user-data stream.
        // REST-polling остаётся только fallback, если stream выключен/не подключён.
        if (_tradingConfig.UserDataStreamEnabled && _ws.IsUserDataStreamConnected)
        {
            var keys = ReportKeys(order).ToArray();
            while (sw.Elapsed < timeout && !ct.IsCancellationRequested)
            {
                ExecutionReport? latest = null;
                foreach (var key in keys)
                {
                    if (_latestReports.TryGetValue(key, out var report) &&
                        (latest is null || report.TransactionTimeUtc >= latest.TransactionTimeUtc))
                        latest = report;
                }

                if (latest != null)
                {
                    current = ApplyReport(current, latest);
                    _executionQueue.ActiveEntries.Observe(current.ClientOrderId, current);
                    if (IsTerminalStatus(current.Status)) break;
                }

                var remaining = timeout - sw.Elapsed;
                if (remaining <= TimeSpan.Zero) break;

                var waiter = new TaskCompletionSource<ExecutionReport>(TaskCreationOptions.RunContinuationsAsynchronously);
                foreach (var key in keys) _fillWaiters[key] = waiter;
                try
                {
                    var completed = await Task.WhenAny(waiter.Task, Task.Delay(remaining, ct));
                    if (completed != waiter.Task) break; // timeout или отмена вызывающего
                    var report = await waiter.Task;
                    current = ApplyReport(current, report);
                    _executionQueue.ActiveEntries.Observe(current.ClientOrderId, current);
                    if (IsTerminalStatus(current.Status)) break;
                }
                finally
                {
                    foreach (var key in keys) _fillWaiters.TryRemove(key, out _);
                }
            }

            // Финальное состояние/комиссии забираем одним REST-запросом, не циклом опроса.
            try
            {
                current = await _binanceClient.GetOrderAsync(order.Symbol, order.BinanceOrderId, ct);
                _executionQueue.ActiveEntries.Observe(current.ClientOrderId, current);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось получить финальный статус ордера {OrderId} после executionReport", order.BinanceOrderId);
            }
        }
        else
        {
            while (sw.Elapsed < timeout && !ct.IsCancellationRequested)
            {
                await Task.Delay(_tradingConfig.OrderFillPollIntervalMs, ct);

                try
                {
                    current = await _binanceClient.GetOrderAsync(order.Symbol, order.BinanceOrderId, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Не удалось получить статус ордера {OrderId}", order.BinanceOrderId);
                    continue;
                }

                if (IsTerminalStatus(current.Status))
                    break;
            }
        }

        if (current.Status is OrderStatus.New or OrderStatus.PartiallyFilled)
        {
            try
            {
                await _executionQueue.SubmitAsync<object?>(
                    current.Symbol, ExecutionPriority.Close, null,
                    async token =>
                    {
                        await _binanceClient.CancelOrderAsync(current.Symbol, current.BinanceOrderId, token);
                        return null;
                    }, ct);
                // После отмены забираем финальное состояние (могли исполнить в момент отмены).
                current = await _binanceClient.GetOrderAsync(current.Symbol, current.BinanceOrderId, ct);
                _executionQueue.ActiveEntries.Observe(current.ClientOrderId, current);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось отменить неисполненный ордер {OrderId}", current.BinanceOrderId);
            }

            // P1.3: аудит остатка после отмены — «отменённый остаток vs фактический филл».
            // Позиция открывается на ExecutedQuantity; если отмена не прошла, остаток может
            // продолжить наливаться — расхождение фиксируется в метриках и ловится реконсиляцией P0.5.
            AuditCancelResidual(current);

            TradeLog.Warn("ORDER-FILL-TIMEOUT", current.Symbol,
                $"order_id={current.BinanceOrderId} executed={LogOptions.N(current.ExecutedQuantity)}/{LogOptions.N(current.OriginalQuantity)} " +
                $"status={current.Status} timeout_s={_tradingConfig.OrderFillTimeoutSeconds}");
        }

        return current;
    }

    /// <summary>
    /// P1.3: аудит «отменённый остаток vs фактический филл» после отмены неисполненной части.
    /// Чистый аудит: терминальный статус и сходимость объёмов (executed + residual == original).
    /// Нестабильный случай (статус не терминальный / объёмы не сходятся) — расхождение:
    /// остаток мог остаться живым на бирже, факт уходит в метрики и лог.
    /// </summary>
    private void AuditCancelResidual(Order order)
    {
        var audit = DustPolicy.ClassifyCancelResidual(
            order.Status, order.ExecutedQuantity, order.OriginalQuantity);

        _metrics?.RecordCancelAudit(audit.Executed, audit.Residual, audit.Mismatch);

        if (audit.Mismatch)
        {

            TradeLog.Rejected(order.Symbol, "cancel-audit",
                $"{audit.Detail}; исполнено {audit.Executed} из {order.OriginalQuantity} — остаток мог остаться живым на бирже (ордер {order.BinanceOrderId})");
            return;
        }

        TradeLog.Decision(order.Symbol, "CANCEL-AUDIT",
            $"исполнено={audit.Executed}, отменённый остаток={audit.Residual}, статус={order.Status}");
    }

    private Order? ValidateFilled(Order order, string symbol, string stage)
    {
        if (order.ExecutedQuantity <= 0)
        {
            TradeLog.Rejected(symbol, stage,
                $"ордер не исполнен (status={order.Status}, executed={order.ExecutedQuantity})");
            return null;
        }

        if (order.ExecutedQuantity < order.OriginalQuantity)
        {
            TradeLog.Warn("PARTIAL-FILL", symbol,
                $"executed={LogOptions.N(order.ExecutedQuantity)}/{LogOptions.N(order.OriginalQuantity)} — позиция открывается на исполненный объём");
        }

        ExchangeLog.Order("FILLED", symbol, order.Side.ToString(), order.ExecutedQuantity,
            order.AverageFillPrice ?? order.Price,
            $"id={order.Id} status={order.Status} fee={order.Commission}");

        return order;
    }

    /// <summary>Округление цены к tickSize в «безопасную» для maker сторону.</summary>
    private static decimal RoundPriceToTick(decimal price, decimal tickSize, OrderSide side)
    {
        if (tickSize <= 0) return price;

        var steps = price / tickSize;
        return side == OrderSide.Buy
            ? Math.Floor(steps) * tickSize
            : Math.Ceiling(steps) * tickSize;
    }

    private async Task<BinanceSymbolInfo?> GetSymbolInfoCachedAsync(string symbol, CancellationToken ct)
    {
        if (_symbolInfoCache.TryGetValue(symbol, out var cached) &&
            DateTime.UtcNow - cached.CachedAt < SymbolInfoTtl)
            return cached.Info;

        try
        {
            var info = await _binanceClient.GetSymbolInfoAsync(symbol, ct);
            if (info != null)
                _symbolInfoCache[symbol] = (info, DateTime.UtcNow);
            return info;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось получить фильтры символа {Symbol}", symbol);
            return null;
        }
    }

    /// <summary>
    /// Live SELL: объём, который реально можно отправить на биржу прямо сейчас.
    /// position.Quantity может быть gross-fill или содержать уже проданный/заблокированный остаток;
    /// authoritative источник для спота — free-баланс базового актива из GET /api/v3/account.
    /// </summary>
    private async Task<decimal> GetLiveSellableQuantityAsync(
        Position position, BinanceSymbolInfo? info, CancellationToken ct)
    {
        if (_tradingConfig.PaperTradingMode || position.Side != OrderSide.Buy)
            return position.Quantity;

        string baseAsset;
        if (!string.IsNullOrWhiteSpace(info?.BaseAsset))
            baseAsset = info!.BaseAsset;
        else if (SymbolNaming.TrySplit(position.Symbol, _tradingConfig.QuoteAssets, out var parsedBase, out _))
            baseAsset = parsedBase;
        else
        {
            TradeLog.Warn("BASE-ASSET-UNKNOWN", position.Symbol, "live SELL использует объём позиции");
            return position.Quantity;
        }

        var balance = await _binanceClient.GetAccountBalanceAsync(ct);
        var free = balance.Assets.TryGetValue(baseAsset, out var asset) ? asset.Free : 0m;
        var sellable = Math.Max(0m, Math.Min(position.Quantity, free));

        if (sellable < position.Quantity)
        {
            TradeLog.Decision(position.Symbol, "LIVE-BALANCE-CAP",
                $"объём позиции {position.Quantity} ограничен free-балансом {baseAsset}: free={free}, sendable={sellable}");
        }

        return sellable;
    }

    private static Order CreatePaperOrder(
        string symbol,
        OrderSide side,
        decimal requestedQuantity,
        decimal filledQuantity,
        decimal averagePrice,
        bool postOnly,
        decimal commissionRate)
    {
        var id = $"paper-{Guid.NewGuid():N}";
        var notional = filledQuantity * averagePrice;
        var status = filledQuantity >= requestedQuantity
            ? OrderStatus.Filled
            : filledQuantity > 0m ? OrderStatus.PartiallyFilled : OrderStatus.Rejected;

        return new Order
        {
            Id = id,
            ClientOrderId = id,
            Symbol = symbol,
            Side = side,
            Type = postOnly ? OrderType.Limit : OrderType.Market,
            Status = status,
            Price = averagePrice,
            OriginalQuantity = requestedQuantity,
            ExecutedQuantity = filledQuantity,
            FilledQuoteQuantity = notional,
            Commission = Math.Round(notional * commissionRate, 8),
            CommissionAsset = "USDT",
            CreatedAt = DateTime.UtcNow,
            IsPostOnly = postOnly
        };
    }
}
