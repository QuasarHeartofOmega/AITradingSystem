using AITradingSystem.Core.Models.Enums;

namespace AITradingSystem.Core.Models.Trading;

/// <summary>
/// Торговый сигнал от AI-модели (FR-4.3).
/// </summary>
public sealed class TradingSignal
{
    public string Symbol { get; set; } = string.Empty;
    public SignalAction Action { get; set; }

    /// <summary>
    /// Итоговая (FINAL) уверенность после SignalCalibrator. Именно к ней применяется AI:MinConfidence.
    /// </summary>
    public decimal Confidence { get; set; }

    /// <summary>«Сырая» уверенность модели из JSON — не теряется при калибровке (B9/A5).</summary>
    public decimal ModelConfidence { get; set; }

    /// <summary>Детерминированный prior движка (SignalCalibrator.PreScore), если рассчитан.</summary>
    public decimal? EngineConfidence { get; set; }

    /// <summary>
    /// Модель выдала «якорный» ответ: HOLD с confidence ≈ 0.45.
    /// Калибратор в политике Blend заменяет такую уверенность на engine prior.
    /// </summary>
    public bool IsDegenerateConfidence { get; set; }

    /// <summary>
    /// B9: сигнал — фолбэк после отказа инференса (все попытки провалились), а не мнение модели.
    /// Калибратор ОБЯЗАН оставить HOLD: EngineOverride не имеет права превращать отказ в BUY.
    /// </summary>
    public bool IsInferenceFailure { get; set; }

    /// <summary>Как получена итоговая уверенность: ModelOnly | Blend | EngineOverride | Degenerate→Prior | Hold→Prior (P2.5).</summary>
    public string? ConfidenceSource { get; set; }
    public decimal? EntryPrice { get; set; }
    public decimal? StopLoss { get; set; }
    public decimal? TakeProfit { get; set; }
    public bool? Veto { get; set; }
    public string? EventType { get; set; }
    public IReadOnlyList<string> RiskFlags { get; set; } = Array.Empty<string>();
    public string? Invalidation { get; set; }
    public string Reasoning { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string ModelVersion { get; set; } = string.Empty;

    /// <summary>
    /// Risk/Reward ratio, рассчитанный из SL/TP.
    /// </summary>
    public decimal? RiskRewardRatio
    {
        get
        {
            if (!EntryPrice.HasValue || !StopLoss.HasValue || !TakeProfit.HasValue)
                return null;

            var risk = Math.Abs(EntryPrice.Value - StopLoss.Value);
            var reward = Math.Abs(TakeProfit.Value - EntryPrice.Value);

            return risk > 0 ? reward / risk : null;
        }
    }

    /// <summary>
    /// Валидация сигнала перед исполнением.
    /// </summary>
    public bool IsValid(out string error)
    {
        if (Action == SignalAction.Hold || Action == SignalAction.Close)
        {
            error = string.Empty;
            return true;
        }

        if (Confidence < 0 || Confidence > 1)
        {
            error = $"Confidence должен быть в диапазоне [0; 1]. Текущее: {Confidence}";
            return false;
        }

        if (!EntryPrice.HasValue || EntryPrice.Value <= 0)
        {
            error = "EntryPrice должна быть > 0";
            return false;
        }

        if (!StopLoss.HasValue || StopLoss.Value <= 0)
        {
            error = "StopLoss должна быть > 0";
            return false;
        }

        if (!TakeProfit.HasValue || TakeProfit.Value <= 0)
        {
            error = "TakeProfit должна быть > 0";
            return false;
        }

        if (Action == SignalAction.Buy)
        {
            if (StopLoss.Value >= EntryPrice.Value)
            {
                error = "Для Buy сигнала StopLoss должна быть ниже EntryPrice";
                return false;
            }
            if (TakeProfit.Value <= EntryPrice.Value)
            {
                error = "Для Buy сигнала TakeProfit должна быть выше EntryPrice";
                return false;
            }
        }
        else if (Action == SignalAction.Sell)
        {
            if (StopLoss.Value <= EntryPrice.Value)
            {
                error = "Для Sell сигнала StopLoss должна быть выше EntryPrice";
                return false;
            }
            if (TakeProfit.Value >= EntryPrice.Value)
            {
                error = "Для Sell сигнала TakeProfit должна быть ниже EntryPrice";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }
}