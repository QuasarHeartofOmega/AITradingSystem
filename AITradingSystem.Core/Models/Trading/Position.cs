using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using AITradingSystem.Core.Models.Enums;

namespace AITradingSystem.Core.Models.Trading;

/// <summary>
/// Открытая позиция (FR-5).
/// Реализует INotifyPropertyChanged, чтобы UI обновлял PnL в реальном времени.
/// </summary>
public sealed class Position : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void RaisePriceDependent()
    {
        OnPropertyChanged(nameof(CurrentPrice));
        OnPropertyChanged(nameof(UnrealizedPnL));
        OnPropertyChanged(nameof(UnrealizedPnLPercent));
        OnPropertyChanged(nameof(DistanceToStopLossPercent));
        OnPropertyChanged(nameof(DistanceToTakeProfitPercent));
    }

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Symbol { get; set; } = string.Empty;
    public OrderSide Side { get; set; }
    public decimal Quantity { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal CurrentPrice { get; set; }
    public decimal StopLoss { get; set; }
    public decimal TakeProfit { get; set; }
    public decimal InitialStopLoss { get; set; }
    public DateTime OpenedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public bool NeedsManualDustReview { get; set; }
    public string DustReviewReason { get; set; } = string.Empty;
    public decimal DustExchangeQuantity { get; set; }
    public string OpeningOrderId { get; set; } = string.Empty;
    public string SignalId { get; set; } = string.Empty;

    /// <summary>Phase 1.7: источник lifecycle (Bot / External / Mixed). Старые записи = Bot.</summary>
    public PositionSource Source { get; set; } = PositionSource.Bot;

    /// <summary>Phase 1.7: основа цены входа (engine / mytrades-vwap / mark-price-estimate / manual / mixed).</summary>
    public string EntryBasis { get; set; } = "engine";

    /// <summary>Phase 1.7: доля Quantity, принадлежащая external-леджеру (остальное — бот).</summary>
    public decimal ExternalQuantity { get; set; }

    /// <summary>Phase 1.7: средняя цена входа external-доли (для атрибуции PnL при ручной продаже).</summary>
    public decimal ExternalEntryPrice { get; set; }

    /// <summary>Phase 1.7: последний наблюдавшийся биржевой total базового актива, отнесённый к этому lifecycle.
    /// Нужен для balance-delta recon: ручная докупка/продажа меняет total, а не только free.</summary>
    public decimal ExternalObservedTotal { get; set; }

    /// <summary>Phase 1.7: evidence реконструкции external entry (trade ids / окно / fallback).</summary>
    public string ExternalEntryEvidence { get; set; } = string.Empty;

    /// <summary>Есть external-доля: применяется политика ExternalProtectionFailurePolicy.</summary>
    [JsonIgnore]
    public bool HasExternalOwnership => Source is PositionSource.External or PositionSource.Mixed || ExternalQuantity > 0m;

    /// <summary>Доля бота в Quantity (персистится через ExternalQuantity).</summary>
    [JsonIgnore]
    public decimal BotQuantity => Math.Max(0m, Quantity - ExternalQuantity);

    /// <summary>E1 lifecycle linkage. Persisted so restart/polling cannot create a second outcome.</summary>
    public string E1DecisionId { get; set; } = string.Empty;
    public string E1EntryIntentId { get; set; } = string.Empty;
    public decimal E1InitialRiskQuote { get; set; }
    public bool E1ProtectionConfirmed { get; set; }
    public string E1ProtectionResolution { get; set; } = string.Empty;
    public List<ProtectiveOrderList> E1ProtectionHistory { get; set; } = new();

    /// <summary>
    /// Комиссия входа (уже списана из баланса в момент открытия).
    /// Нужна, чтобы в истории сделок показать полную комиссию round-trip.
    /// </summary>
    public decimal EntryCommission { get; set; }

    /// <summary>
    /// P2.4: уверенность сигнала, по которому открыта позиция (FINAL после калибратора),
    /// её составляющие и источник. Персистятся в positions.json, чтобы после рестарта
    /// закрытие попало в историю с меткой уверенности — иначе подбор порога по expectancy
    /// терял бы сделки, пережившие перезапуск. 0 — позиция без сигнала (сирота P0.5).
    /// </summary>
    public decimal SignalConfidence { get; set; }
    public decimal ModelConfidence { get; set; }
    public decimal EngineConfidence { get; set; }
    public string ConfidenceSource { get; set; } = string.Empty;

    /// <summary>
    /// P2.6: снимок фич direction-скоринга на входе (ScoringMath.FeatureKeys) и фактическое
    /// направление движка. Персистятся в positions.json — валидация весов на истории
    /// (ScoringCalibrationAnalyzer / tools/scoring_calibration.py) должна видеть те же факты,
    /// по которым вход был разрешён, включая закрытия после рестарта. Пусто — сирота P0.5.
    /// </summary>
    public Dictionary<string, decimal> ScoreFeatures { get; set; } = new();
    public decimal EngineDirection { get; set; }

    /// <summary>
    /// P0.5: сколько кумулятивного объёма уже применено к позиции по clientOrderId
    /// биржевого ордера (защитная нога / close). Персистится в positions.json, чтобы
    /// повторный executionReport или стартовая реконсиляция не закрыли объём дважды.
    /// </summary>
    public Dictionary<string, decimal> AppliedExchangeFills { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Новый объём филла, ещё не применённый к позиции (по кумулятиву ордера).</summary>
    public decimal GetUnappliedExchangeFill(string clientOrderId, decimal cumulativeQuantity)
    {
        if (string.IsNullOrWhiteSpace(clientOrderId) || cumulativeQuantity <= 0) return 0m;
        AppliedExchangeFills.TryGetValue(clientOrderId, out var applied);
        return Math.Max(0m, cumulativeQuantity - applied);
    }

    /// <summary>Зафиксировать применённый кумулятив филла по ордеру.</summary>
    public void RecordExchangeFill(string clientOrderId, decimal cumulativeQuantity)
    {
        if (string.IsNullOrWhiteSpace(clientOrderId) || cumulativeQuantity <= 0) return;
        AppliedExchangeFills.TryGetValue(clientOrderId, out var applied);
        if (cumulativeQuantity > applied)
            AppliedExchangeFills[clientOrderId] = cumulativeQuantity;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Phase 1.5 (Blocker 5): сколько базового актива куплено за весь lifecycle позиции.
    /// Персистится: суммарный проданный объём не может превышать купленный, даже если
    /// close-филл пришёл из нескольких контуров (SL, reconcile, manual, kill-switch).
    /// 0 = запись из старого positions.json; база восстанавливается лениво из Quantity.
    /// </summary>
    public decimal LifetimeBoughtQuantity { get; set; }

    /// <summary>Phase 1.5: сколько объёма уже реально закрыто/продано за lifecycle (все контуры).</summary>
    public decimal LifetimeSoldQuantity { get; set; }

    /// <summary>Phase 1.5: clientOrderId последнего отправленного close-ордера (аудит повторных продаж).</summary>
    public string CloseClientOrderId { get; set; } = string.Empty;

    /// <summary>Phase 1.5: сколько принудительных закрытий (NoExchangeProtection и т.п.) уже отправлено.</summary>
    public int ForcedCloseAttempts { get; set; }

    /// <summary>Phase 1.5: момент последней попытки принудительного закрытия (cooldown между циклами).</summary>
    public DateTime? LastForcedCloseAttemptUtc { get; set; }

    /// <summary>Phase 1.5: аномалия «продано больше, чем куплено» уже поднята оператору один раз.</summary>
    public bool CloseOversellAlerted { get; set; }

    /// <summary>
    /// Phase 1.5: close-ордер отправлен и ещё не разрешён. Только in-memory: после рестарта
    /// решение принимает реконсиляция по биржевой истории, а не протухший флаг.
    /// </summary>
    [JsonIgnore]
    public bool CloseInFlight { get; set; }

    /// <summary>Phase 1.5: когда взведён CloseInFlight (для снятия зависшего флага).</summary>
    [JsonIgnore]
    public DateTime CloseInFlightSinceUtc { get; set; }

    /// <summary>Phase 1.5: остаток купленного объёма, который ещё разрешено продать за lifecycle.</summary>
    public decimal RemainingSellableLifetimeQuantity
    {
        get
        {
            EnsureLifetimeBaseline();
            return Math.Max(0m, LifetimeBoughtQuantity - LifetimeSoldQuantity);
        }
    }

    /// <summary>
    /// Phase 1.5: зарегистрировать вход (в т.ч. долив частичного входа) в lifetime-базе.
    /// </summary>
    public void RegisterEntryQuantity(decimal quantity)
    {
        if (quantity <= 0m) return;
        EnsureLifetimeBaseline();
        LifetimeBoughtQuantity += quantity;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Phase 1.5: атомарно (под посимвольным шлюзом) зарезервировать объём к продаже.
    /// Возвращает разрешённый объём — не больше купленного за lifecycle; oversell=true,
    /// если запрошено больше, чем есть по lifetime-учёту. Ничего не продаёт «в долг».
    /// </summary>
    public bool TryReserveExitQuantity(decimal requested, out decimal allowed, out bool oversell)
    {
        EnsureLifetimeBaseline();
        var remaining = Math.Max(0m, LifetimeBoughtQuantity - LifetimeSoldQuantity);
        allowed = Math.Max(0m, Math.Min(requested, remaining));
        oversell = requested - allowed > QuantityEpsilon;
        if (allowed > 0m)
        {
            LifetimeSoldQuantity += allowed;
            UpdatedAt = DateTime.UtcNow;
        }
        return allowed > 0m;
    }

    /// <summary>Phase 1.5: суммарный проданный объём превысил купленный (аномалия учёта).</summary>
    public bool IsLifetimeOversold => LifetimeBoughtQuantity > 0m
        && LifetimeSoldQuantity - LifetimeBoughtQuantity > QuantityEpsilon;

    private const decimal QuantityEpsilon = 0.00000001m;

    private void EnsureLifetimeBaseline()
    {
        // Запись из старого positions.json без lifetime-учёта: текущий остаток + уже закрытое.
        if (LifetimeBoughtQuantity <= 0m)
            LifetimeBoughtQuantity = Math.Max(0m, Quantity + LifetimeSoldQuantity);
    }

    /// <summary>
    /// Биржевая защита позиции — OCO (STOP_LOSS_LIMIT + TAKE_PROFIT_LIMIT), P0.1.
    /// null = на бирже защиты нет (paper-режим, отказ постановки или защита выключена).
    /// Персистится вместе с позицией, чтобы после рестарта знать id ног.
    /// </summary>
    public ProtectiveOrderList? Protection { get; set; }

    /// <summary>Позиция защищена самой биржей (переживёт падение процесса).</summary>
    public bool HasExchangeProtection => Protection is { IsLive: true };

    /// <summary>Зафиксировать поставленную/заменённую защитную связку.</summary>
    public void SetProtection(ProtectiveOrderList? protection)
    {
        if (Protection != null && !ReferenceEquals(Protection, protection)
            && !E1ProtectionHistory.Any(x => x.ListClientOrderId == Protection.ListClientOrderId
                && x.OrderListId == Protection.OrderListId))
            E1ProtectionHistory.Add(Protection);
        Protection = protection;
        // Phase 1.5: бюджет принудительных закрытий относится к одному «эпизоду без защиты».
        // Защита восстановлена — счётчик попыток и cooldown обнуляются.
        if (protection is { IsLive: true })
        {
            ForcedCloseAttempts = 0;
            LastForcedCloseAttemptUtc = null;
        }
        UpdatedAt = DateTime.UtcNow;
        OnPropertyChanged(nameof(Protection));
        OnPropertyChanged(nameof(HasExchangeProtection));
    }

    /// <summary>Зафиксировать остаток, который нельзя закрыть ordinary-ордером из-за фильтров.</summary>
    public void MarkDustForManualReview(decimal exchangeQuantity, string reason)
    {
        NeedsManualDustReview = true;
        DustExchangeQuantity = Math.Max(0m, exchangeQuantity);
        DustReviewReason = reason ?? string.Empty;
        UpdatedAt = DateTime.UtcNow;
        OnPropertyChanged(nameof(NeedsManualDustReview));
        OnPropertyChanged(nameof(DustReviewReason));
        OnPropertyChanged(nameof(DustExchangeQuantity));
        OnPropertyChanged(nameof(UpdatedAt));
    }

    /// <summary>
    /// Свеча открылась РАНЬШЕ, чем позиция? Тогда её Low/High содержат экстремумы,
    /// которых при входе уже не было, и проверять по ним SL/TP нельзя —
    /// иначе стоп «срабатывает» в ту же секунду после входа.
    /// </summary>
    public bool CandleStartedBeforeEntry(DateTime candleOpenTime) => candleOpenTime < OpenedAt;

    /// <summary>
    /// Уменьшить объём после частичного закрытия (биржа исполнила не всё).
    /// Остаток остаётся открытой позицией, а не «теряется».
    /// </summary>
    public void ReduceQuantity(decimal closedQuantity)
    {
        if (closedQuantity <= 0) return;

        Quantity = Math.Max(0m, Quantity - closedQuantity);
        UpdatedAt = DateTime.UtcNow;
        OnPropertyChanged(nameof(Quantity));
        OnPropertyChanged(nameof(Notional));
        RaisePriceDependent();
    }

    /// <summary>
    /// Инвестиционная сумма (notional).
    /// </summary>
    public decimal Notional => Quantity * EntryPrice;

    /// <summary>
    /// Нереализованный PnL в USDT.
    /// </summary>
    public decimal UnrealizedPnL => Side == OrderSide.Buy
        ? (CurrentPrice - EntryPrice) * Quantity
        : (EntryPrice - CurrentPrice) * Quantity;

    /// <summary>
    /// Нереализованный PnL в процентах.
    /// </summary>
    public decimal UnrealizedPnLPercent => EntryPrice > 0
        ? UnrealizedPnL / Notional * 100m
        : 0;

    /// <summary>
    /// Расстояние до SL в процентах.
    /// </summary>
    public decimal DistanceToStopLossPercent => CurrentPrice <= 0
        ? 0m
        : Side == OrderSide.Buy
            ? (CurrentPrice - StopLoss) / CurrentPrice * 100m
            : (StopLoss - CurrentPrice) / CurrentPrice * 100m;

    /// <summary>
    /// Расстояние до TP в процентах.
    /// </summary>
    public decimal DistanceToTakeProfitPercent => CurrentPrice <= 0
        ? 0m
        : Side == OrderSide.Buy
            ? (TakeProfit - CurrentPrice) / CurrentPrice * 100m
            : (CurrentPrice - TakeProfit) / CurrentPrice * 100m;

    /// <summary>
    /// Обновить текущую цену (для расчета PnL).
    /// </summary>
    public void UpdatePrice(decimal newPrice)
    {
        CurrentPrice = newPrice;
        UpdatedAt = DateTime.UtcNow;
        RaisePriceDependent();
    }

    /// <summary>
    /// Обновить Trailing Stop (FR-5.5).
    /// Возвращает true, если SL был подтянут.
    /// </summary>
    public bool UpdateTrailingStop(decimal newStopLoss)
    {
        if (Side == OrderSide.Buy)
        {
            if (newStopLoss > StopLoss)
            {
                StopLoss = newStopLoss;
                UpdatedAt = DateTime.UtcNow;
                OnPropertyChanged(nameof(StopLoss));
                OnPropertyChanged(nameof(DistanceToStopLossPercent));
                return true;
            }
        }
        else
        {
            if (newStopLoss < StopLoss)
            {
                StopLoss = newStopLoss;
                UpdatedAt = DateTime.UtcNow;
                OnPropertyChanged(nameof(StopLoss));
                OnPropertyChanged(nameof(DistanceToStopLossPercent));
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Проверка, сработал ли Stop-Loss.
    /// </summary>
    public bool IsStopLossHit(decimal low, decimal high)
    {
        return Side == OrderSide.Buy ? low <= StopLoss : high >= StopLoss;
    }

    /// <summary>
    /// Проверка, сработал ли Take-Profit.
    /// </summary>
    public bool IsTakeProfitHit(decimal low, decimal high)
    {
        return Side == OrderSide.Buy ? high >= TakeProfit : low <= TakeProfit;
    }
}