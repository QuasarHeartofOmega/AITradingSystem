using System.Collections.Concurrent;
using System.Globalization;
using AITradingSystem.Common.Configuration;
using AITradingSystem.Common.Logging;
using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.Trading;
using AITradingSystem.Data.Binance;
using AITradingSystem.Data.Storage;
using Microsoft.Extensions.Logging;

namespace AITradingSystem.Services.Trading;

/// <summary>
/// Phase 1.7 (Blocker 7): детект, реконструкция входа, подтверждённый/авто подхват и
/// balance-delta recon ручных/external спотовых остатков по символам реестра.
///
/// Правила безопасности:
///  - external = free+locked − объём, уже учтённый ботом, минус отпущенное оператором (Release);
///  - stablecoin/quote-активы и не-торговые классы реестра исключаются;
///  - пороги stepSize/minNotional/dust: остаток ниже фильтров не становится кандидатом на ордер;
///  - внешние ордера не трогаем; OCO ставится только на free-объём (OrderExecutor);
///  - Confirm (дефолт) ждёт согласия оператора; Auto подхватывает сразу; Off — только алерт;
///  - отказ защиты external-позиции по умолчанию AlertNotSell (не продаём чужой актив).
/// </summary>
public sealed class ExternalPositionService
{
    private static readonly HashSet<string> StableAssets = new(StringComparer.OrdinalIgnoreCase)
    {
        "USDT", "USDC", "FDUSD", "TUSD", "USDP", "DAI", "BUSD", "USD", "EUR", "USDE"
    };

    private readonly TradingConfig _config;
    private readonly IBinanceRestClient _rest;
    private readonly PositionManager _positions;
    private readonly JsonExternalPositionLedger _ledger;
    private readonly ILogger<ExternalPositionService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Отдельный шлюз инициализации: ObserveAsync вызывается в том числе изнутри ReconcileAsync,
    // который уже держит _gate — повторный вход в тот же семафор дал бы дедлок.
    private readonly SemaphoreSlim _initGate = new(1, 1);
    // _state читается из ObserveAsync (контур снимка аккаунта) и пишется из Reconcile/Release/RememberBotOrder.
    // Короткий lock защищает словари от гонки чтения во время удаления/добавления ключей.
    private readonly object _stateLock = new();
    private readonly ConcurrentDictionary<string, ExternalPositionCandidate> _candidates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _lastAlertUtc = new(StringComparer.OrdinalIgnoreCase);
    private ExternalPositionLedgerState _state = new();
    private bool _initialized;

    private Func<ExternalPositionCandidate, decimal?, CancellationToken, Task<bool>>? _adopt;
    private Func<Position, decimal, decimal, CancellationToken, Task>? _applyDelta;
    private Func<Position, CancellationToken, Task<bool>>? _release;
    private Func<string, decimal?>? _atrAccessor;

    public event Action? CandidatesChanged;
    public IReadOnlyCollection<ExternalPositionCandidate> Candidates => _candidates.Values.ToArray();
    public int UnadoptedCount => _candidates.Values.Count(c => c.IsEligible && !c.IsReleased);
    public decimal UnadoptedNotionalQuote => _candidates.Values.Where(c => c.IsEligible && !c.IsReleased).Sum(c => c.ExternalNotionalQuote);
    public ExternalAdoptionMode Mode => _config.AdoptExternalPositions;

    /// <summary>Phase 1.7: отпущенный объём не может превышать неучтённый ботом остаток на бирже.
    /// Если оператор продал часть отпущенного, baseline уменьшается, иначе будущие покупки
    /// ошибочно считались бы «уже отпущенными» и оставались невидимыми.</summary>
    public static decimal ClampReleasedQuantity(decimal released, decimal exchangeTotal, decimal managed) =>
        Math.Max(0m, Math.Min(released, Math.Max(0m, exchangeTotal - managed)));

    public ExternalPositionService(
        TradingConfig config,
        IBinanceRestClient rest,
        PositionManager positions,
        JsonExternalPositionLedger ledger,
        ILogger<ExternalPositionService> logger)
    {
        _config = config;
        _rest = rest;
        _positions = positions;
        _ledger = ledger;
        _logger = logger;
    }

    public void Configure(
        Func<ExternalPositionCandidate, decimal?, CancellationToken, Task<bool>> adopt,
        Func<Position, decimal, decimal, CancellationToken, Task> applyDelta,
        Func<Position, CancellationToken, Task<bool>> release,
        Func<string, decimal?> atrAccessor)
    {
        _adopt = adopt;
        _applyDelta = applyDelta;
        _release = release;
        _atrAccessor = atrAccessor;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        await _initGate.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            var loaded = await _ledger.LoadAsync(ct);
            lock (_stateLock) { _state = loaded; }
            _initialized = true;
        }
        finally { _initGate.Release(); }
    }

    /// <summary>Phase 1.7: запомнить биржевой orderId входа бота (durable). Вызывается движком
    /// под посимвольным шлюзом; берёт только шлюз леджера, чтобы не пересечься с _gate реконсиляции.</summary>
    public async Task RememberBotOrderAsync(string symbol, long orderId, CancellationToken ct = default)
    {
        if (orderId <= 0 || string.IsNullOrWhiteSpace(symbol)) return;
        if (!_initialized) await InitializeAsync(ct);
        await _initGate.WaitAsync(ct);
        try
        {
            bool added;
            lock (_stateLock)
            {
                if (!_state.BotOrderIds.TryGetValue(symbol, out var set))
                {
                    set = new HashSet<long>();
                    _state.BotOrderIds[symbol] = set;
                }
                added = set.Add(orderId);
            }
            if (added) await _ledger.SaveAsync(_state, ct);
        }
        finally { _initGate.Release(); }
    }

    /// <summary>
    /// Детект кандидатов по снимку баланса. Не отправляет ордеров и не берёт посимвольные шлюзы —
    /// безопасно вызывать из AccountSnapshotService под любым контуром.
    /// </summary>
    public async Task<IReadOnlyList<ExternalPositionCandidate>> ObserveAsync(
        AccountBalance balance,
        IReadOnlyDictionary<string, decimal> prices,
        IReadOnlyList<Order>? openOrders = null,
        CancellationToken ct = default)
    {
        if (!_initialized) await InitializeAsync(ct);
        var result = new List<ExternalPositionCandidate>();
        var managedBySymbol = _positions.GetOpenPositions()
            .GroupBy(p => p.Symbol, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.Quantity), StringComparer.OrdinalIgnoreCase);

        foreach (var symbol in _config.Symbols.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (!SymbolNaming.TrySplit(symbol, _config.QuoteAssets, out var baseAsset, out var quoteAsset))
                continue;
            if (StableAssets.Contains(baseAsset) || _config.QuoteAssets.Contains(baseAsset, StringComparer.OrdinalIgnoreCase))
                continue;
            var exclusion = SymbolUniversePolicy.EntryExclusionReason(symbol, _config.VolatileSymbols, _config.StablecoinPairs, _config.BenchmarkSymbols);
            if (exclusion != null) continue;

            if (!balance.Assets.TryGetValue(baseAsset, out var asset) || asset.Total <= 0m)
            {
                _candidates.TryRemove(symbol, out _);
                continue;
            }

            var info = await _rest.GetSymbolInfoAsync(symbol, ct);
            var step = info?.StepSize ?? 0m;
            var minQty = info?.MinQty ?? 0m;
            var minNotional = Math.Max(info?.MinNotional ?? 0m, _config.MinExchangePositionNotional);
            var price = prices.TryGetValue(symbol, out var p) && p > 0m ? p : 0m;

            managedBySymbol.TryGetValue(symbol, out var managed);
            ExternalReleaseRecord? release;
            lock (_stateLock) { _state.Released.TryGetValue(symbol, out release); }
            var releasedQty = release?.ReleasedQuantity ?? 0m;
            var externalQty = asset.Total - managed - releasedQty;
            if (step > 0m) externalQty = Math.Floor(externalQty / step) * step;

            var hasExternalOrders = openOrders?.Any(o =>
                o.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase) && !IsBotClientOrderId(o.ClientOrderId)) ?? false;

            if (externalQty < minQty || externalQty <= 0m || price <= 0m || externalQty * price < minNotional)
            {
                // Ниже фильтров/dust или без цены — видно как остаток, но не кандидат на ордер.
                if (externalQty > 0m && !IsAlertSuppressed("dust:" + symbol))
                    TradeLog.Warn("EXTERNAL-DUST", symbol,
                        $"external_qty={LogOptions.N(externalQty)} price={LogOptions.N(price)} minQty={LogOptions.N(minQty)} minNotional={LogOptions.N(minNotional)} — ниже порогов, подхват/ордер запрещены");
                _candidates.TryRemove(symbol, out _);
                continue;
            }

            var (entry, basis, evidence, firstTrade) = await ReconstructEntryAsync(symbol, externalQty, price, managed, ct);
            var atr = _atrAccessor?.Invoke(symbol) ?? entry * 0.02m;
            if (atr <= 0m) atr = entry * 0.02m;
            var sl = entry - (decimal)_config.StopLossAtrMultiplier * atr;
            var tp = entry + (entry - sl) * (decimal)_config.MinRiskRewardRatio;
            var isReleased = release != null && externalQty <= step;
            var candidate = new ExternalPositionCandidate
            {
                Symbol = symbol,
                BaseAsset = baseAsset,
                ExchangeTotalQuantity = asset.Total,
                ExchangeFreeQuantity = asset.Free,
                ExchangeLockedQuantity = asset.Locked,
                BotQuantity = managed,
                ExternalQuantity = externalQty,
                MarkPrice = price,
                ProposedEntryPrice = entry,
                ProposedStopLoss = sl > 0m ? sl : entry * 0.98m,
                ProposedTakeProfit = tp,
                EntryBasis = basis,
                EntryEvidence = evidence,
                FirstExternalTradeUtc = firstTrade,
                HasExternalOpenOrders = hasExternalOrders,
                IsReleased = isReleased,
                IsEligible = !isReleased,
                Status = isReleased ? "released" : hasExternalOrders ? "external-orders-present" : "awaiting-consent",
                DetectedAtUtc = DateTime.UtcNow
            };
            _candidates[symbol] = candidate;
            result.Add(candidate);
        }

        // Символы, исчезнувшие из баланса/реестра, больше не кандидаты.
        foreach (var key in _candidates.Keys.Where(k => result.All(c => !c.Symbol.Equals(k, StringComparison.OrdinalIgnoreCase))).ToList())
            _candidates.TryRemove(key, out _);
        CandidatesChanged?.Invoke();
        return result;
    }

    /// <summary>
    /// Полный цикл: детект + balance-delta recon уже подхваченных позиций + Auto-подхват.
    /// Вызывается движком вне посимвольных шлюзов (колбэки сами берут шлюз).
    /// </summary>
    public async Task ReconcileAsync(AccountBalance balance, IReadOnlyDictionary<string, decimal> prices, CancellationToken ct = default)
    {
        if (_config.PaperTradingMode || !_rest.HasApiKeys) return;
        if (!_initialized) await InitializeAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            // 0. Release-baseline следует за биржей: оператор мог продать часть отпущенного.
            // Иначе старый объём Release навсегда прятал бы новые покупки от детекта.
            var ledgerChanged = false;
            var managedBySymbol = _positions.GetOpenPositions()
                .GroupBy(p => p.Symbol, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Sum(p => p.Quantity), StringComparer.OrdinalIgnoreCase);
            List<KeyValuePair<string, ExternalReleaseRecord>> releases;
            lock (_stateLock) { releases = _state.Released.ToList(); }
            foreach (var (symbol, release) in releases)
            {
                if (!SymbolNaming.TrySplit(symbol, _config.QuoteAssets, out var baseAsset, out _)) continue;
                var total = balance.Assets.TryGetValue(baseAsset, out var asset) ? asset.Total : 0m;
                managedBySymbol.TryGetValue(symbol, out var managed);
                var clamped = ClampReleasedQuantity(release.ReleasedQuantity, total, managed);
                if (clamped != release.ReleasedQuantity)
                {
                    TradeLog.Info("EXTERNAL-RELEASE-CLAMP", symbol,
                        $"released={LogOptions.N(release.ReleasedQuantity)} -> {LogOptions.N(clamped)} exchange_total={LogOptions.N(total)} — отпущенный объём уменьшен вслед за биржей");
                    release.ReleasedQuantity = clamped;
                    ledgerChanged = true;
                }
                if (clamped <= 0m) { lock (_stateLock) { _state.Released.Remove(symbol); } ledgerChanged = true; }
            }
            if (ledgerChanged) await _ledger.SaveAsync(_state, ct);

            var orders = await _rest.GetOpenOrdersAsync(ct: ct);
            var candidates = await ObserveAsync(balance, prices, orders, ct);

            // 1. Balance-delta уже учтённых позиций: ручная докупка/продажа после подхвата.
            foreach (var position in _positions.GetOpenPositions())
            {
                if (!SymbolNaming.TrySplit(position.Symbol, _config.QuoteAssets, out var baseAsset, out _)) continue;
                if (!balance.Assets.TryGetValue(baseAsset, out var asset)) continue;
                var price = prices.TryGetValue(position.Symbol, out var p) && p > 0m ? p : position.CurrentPrice;
                var botQty = position.BotQuantity;
                if (position.ExternalObservedTotal <= 0m || position.ExternalObservedBotQuantity < 0m)
                {
                    // Legacy/бот-позиция без baseline: первый снимок фиксируем без delta, иначе фантомная докупка.
                    position.ExternalObservedTotal = asset.Total;
                    position.ExternalObservedBotQuantity = botQty;
                    await _positions.SavePositionsAsync(ct);
                    continue;
                }
                // Собственные филлы бота меняют total так же, как ручные. Вычитаем изменение доли бота:
                // остаток — то, что сделал оператор (или другой бот) на бирже.
                var totalDelta = asset.Total - position.ExternalObservedTotal;
                var botDelta = botQty - position.ExternalObservedBotQuantity;
                var delta = totalDelta - botDelta;
                var info = await _rest.GetSymbolInfoAsync(position.Symbol, ct);
                var step = info?.StepSize ?? 0m;
                if (Math.Abs(delta) <= step || Math.Abs(delta) < 0.00000001m)
                {
                    // Нечего применять, но baseline обязан следовать за филлами бота.
                    if (botDelta != 0m || totalDelta != 0m)
                    {
                        position.ExternalObservedTotal = asset.Total;
                        position.ExternalObservedBotQuantity = botQty;
                        await _positions.SavePositionsAsync(ct);
                    }
                    continue;
                }
                // Положительная delta на чисто бот-позиции в Confirm/Off — это НЕ согласие оператора:
                // объём остаётся кандидатом до явного Adopt/Auto. Отрицательная delta (ручная продажа
                // актива бота) применяется всегда, иначе локальная позиция стала бы фантомом.
                if (delta > 0m && !position.HasExternalOwnership && _config.AdoptExternalPositions != ExternalAdoptionMode.Auto)
                {
                    position.ExternalObservedTotal = asset.Total;
                    position.ExternalObservedBotQuantity = botQty;
                    await _positions.SavePositionsAsync(ct);
                    continue;
                }
                if (_applyDelta != null)
                {
                    TradeLog.Info("EXTERNAL-BALANCE-DELTA", position.Symbol,
                        $"lifecycle={position.Id} observed={LogOptions.N(position.ExternalObservedTotal)} exchange={LogOptions.N(asset.Total)} delta={LogOptions.N(delta)}");
                    await _applyDelta(position, delta, price, ct);
                    // Baseline обновляет движок под посимвольным шлюзом (applyDelta).
                }
            }

            // 2. Auto-подхват или алерт для Confirm/Off.
            foreach (var candidate in candidates.Where(c => c.IsEligible))
            {
                if (_config.AdoptExternalPositions == ExternalAdoptionMode.Auto && _adopt != null)
                {
                    if (await _adopt(candidate, null, ct))
                        _candidates.TryRemove(candidate.Symbol, out _);
                    else if (!IsAlertSuppressed("adopt-fail:" + candidate.Symbol))
                        TradeLog.Warn("EXTERNAL-ADOPT-FAILED", candidate.Symbol,
                            "Auto-подхват не выполнен (kill-switch, отказ защиты или устаревший снимок) — кандидат остаётся видимым");
                }
                else if (!IsAlertSuppressed("detect:" + candidate.Symbol))
                {
                    TradeLog.Warn("EXTERNAL-POSITION-DETECTED", candidate.Symbol,
                        $"qty={LogOptions.N(candidate.ExternalQuantity)} notional={LogOptions.N(candidate.ExternalNotionalQuote)} " +
                        $"entry={LogOptions.N(candidate.ProposedEntryPrice)} basis={candidate.EntryBasis} mode={_config.AdoptExternalPositions} " +
                        $"— {( _config.AdoptExternalPositions == ExternalAdoptionMode.Off ? "только алерт (Off)" : "ожидает согласия оператора (Confirm)")}" +
                        (candidate.HasExternalOpenOrders ? " external-orders-present" : string.Empty));
                }
            }
            CandidatesChanged?.Invoke();
        }
        finally { _gate.Release(); }
    }

    /// <summary>Операторское согласие на подхват кандидата (Confirm). entryOverride — ручной ввод.</summary>
    public async Task<bool> AdoptAsync(string symbol, decimal? entryOverride = null, CancellationToken ct = default)
    {
        if (_adopt == null) throw new InvalidOperationException("ExternalPositionService не настроен движком.");
        if (!_candidates.TryGetValue(symbol, out var candidate) || !candidate.IsEligible)
            return false;
        if (entryOverride.HasValue && entryOverride.Value <= 0m) return false;

        // Согласие оператора относится к конкретному объёму. Между показом кандидата и кликом
        // баланс мог измениться (ручная продажа/докупка) — переспрашиваем биржу под общим шлюзом
        // и принимаем только тот же объём; иначе Adopt отправил бы ордер по устаревшему снимку.
        await _gate.WaitAsync(ct);
        ExternalPositionCandidate? fresh = null;
        try
        {
            var balance = await _rest.GetAccountBalanceAsync(ct);
            var prices = await _rest.GetAllPricesAsync(ct);
            var orders = await _rest.GetOpenOrdersAsync(ct: ct);
            await ObserveAsync(balance, prices, orders, ct);
            if (!_candidates.TryGetValue(symbol, out fresh) || !fresh.IsEligible
                || fresh.ExternalQuantity != candidate.ExternalQuantity
                || fresh.ExchangeTotalQuantity != candidate.ExchangeTotalQuantity)
            {
                TradeLog.Warn("EXTERNAL-ADOPT-STALE", symbol,
                    $"consent_qty={LogOptions.N(candidate.ExternalQuantity)} — снимок изменился или кандидат исчез; требуется новое согласие оператора");
                CandidatesChanged?.Invoke();
                return false;
            }
            // Снимаем кандидата ДО выхода из шлюза: параллельный клик/круг не должен
            // подхватить тот же объём второй раз. При неудаче детект вернёт его следующим кругом.
            _candidates.TryRemove(symbol, out _);
        }
        finally { _gate.Release(); }

        var ok = await _adopt(fresh!, entryOverride, ct);
        if (ok)
        {
            _candidates.TryRemove(symbol, out _);
            CandidatesChanged?.Invoke();
        }
        return ok;
    }

    /// <summary>
    /// Release: оператор забирает остаток себе. Снимаем только защиту бота (если была),
    /// убираем lifecycle из positions.json без продажи, запоминаем базовый объём.
    /// Новые покупки выше отпущенного объёма снова станут кандидатами.
    /// </summary>
    public async Task<bool> ReleaseAsync(string symbol, string reason = "operator-release", CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var position = _positions.GetPositionBySymbol(symbol);
            var candidate = _candidates.TryGetValue(symbol, out var c) ? c : null;
            decimal releasedTotal;
            if (position != null)
            {
                if (_release == null) return false;
                if (!await _release(position, ct)) return false;
                // Отпускаем весь наблюдавшийся остаток символа (доля бота тоже переходит оператору):
                // lifecycle снимается целиком, иначе хвост вернулся бы кандидатом на следующем круге.
                releasedTotal = position.ExternalObservedTotal > 0m ? position.ExternalObservedTotal : position.Quantity;
            }
            else
            {
                releasedTotal = candidate?.ExchangeTotalQuantity ?? 0m;
                if (releasedTotal <= 0m) return false;
            }

            lock (_stateLock)
            {
                _state.Released[symbol] = new ExternalReleaseRecord
                {
                    Symbol = symbol,
                    ReleasedQuantity = releasedTotal,
                    ReleasedAtUtc = DateTime.UtcNow,
                    Reason = reason
                };
            }
            await _ledger.SaveAsync(_state, ct);
            _candidates.TryRemove(symbol, out _);
            TradeLog.Warn("EXTERNAL-RELEASED", symbol,
                $"qty={LogOptions.N(releasedTotal)} reason={reason} — остаток больше не сопровождается ботом; kill-switch его не закроет");
            CandidatesChanged?.Invoke();
            return true;
        }
        finally { _gate.Release(); }
    }

    private async Task<(decimal Entry, string Basis, string Evidence, DateTime? FirstTrade)> ReconstructEntryAsync(
        string symbol, decimal externalQty, decimal markPrice, decimal managedQty, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var start = now.AddHours(-_config.ExternalAdoptionLookbackHours);
        try
        {
            var trades = await _rest.GetMyTradesAsync(symbol, startTime: start.ToUnixTimeMilliseconds(),
                endTime: now.ToUnixTimeMilliseconds(), limit: 1000, ct: ct);
            var botOrderIds = _positions.GetOpenPositions()
                .Where(p => p.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase) && long.TryParse(p.OpeningOrderId, out _))
                .Select(p => long.Parse(p.OpeningOrderId, CultureInfo.InvariantCulture))
                .ToHashSet();
            // Закрытые позиции бота тоже не должны загрязнять VWAP ручного остатка:
            // их orderId хранятся в durable-леджере (RememberBotOrderAsync).
            lock (_stateLock)
            {
                if (_state.BotOrderIds.TryGetValue(symbol, out var remembered))
                    botOrderIds.UnionWith(remembered);
            }
            var buys = trades.Where(t => t.IsBuyer && !botOrderIds.Contains(t.OrderId)).ToList();
            var qty = buys.Sum(t => Dec(t.Quantity));
            var quote = buys.Sum(t => Dec(t.Price) * Dec(t.Quantity));
            if (qty > 0m && quote > 0m)
            {
                var vwap = quote / qty;
                var complete = qty >= externalQty * 0.999m;
                var evidence = $"myTrades[{start:O}..{now:O}] buys={buys.Count} qty={LogOptions.N(qty)} vwap={LogOptions.N(vwap)} ids={string.Join(",", buys.Take(20).Select(t => t.Id))}";
                return complete
                    ? (vwap, "mytrades-vwap", evidence, buys.Count > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(buys.Min(t => t.Time)).UtcDateTime : null)
                    : (markPrice, "mark-price-estimate", evidence + " incomplete-cover-fallback-mark", null);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "myTrades lookback failed for {Symbol}; fallback mark-price", symbol);
        }
        return (markPrice, "mark-price-estimate", $"mark-price fallback at {now:O}; myTrades unavailable/empty in {_config.ExternalAdoptionLookbackHours}h", null);
    }

    /// <summary>Троттлинг алертов на ключ (символ+код). Побочный эффект — только при фактическом алерте.</summary>
    private bool IsAlertSuppressed(string key)
    {
        var now = DateTime.UtcNow;
        if (_lastAlertUtc.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(5)) return true;
        _lastAlertUtc[key] = now;
        return false;
    }

    private static bool IsBotClientOrderId(string id) =>
        id.StartsWith("mkt-", StringComparison.OrdinalIgnoreCase) || id.StartsWith("ent-", StringComparison.OrdinalIgnoreCase)
        || id.StartsWith("cls-", StringComparison.OrdinalIgnoreCase) || id.StartsWith("oco-", StringComparison.OrdinalIgnoreCase)
        || id.StartsWith("sl-", StringComparison.OrdinalIgnoreCase) || id.StartsWith("tp-", StringComparison.OrdinalIgnoreCase)
        || id.StartsWith("e1", StringComparison.OrdinalIgnoreCase);

    private static decimal Dec(string s) =>
        decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0m;
}
