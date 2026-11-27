using System.ComponentModel;
using System.Runtime.CompilerServices;
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