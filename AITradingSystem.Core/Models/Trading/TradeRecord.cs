using AITradingSystem.Core.Models.Enums;

namespace AITradingSystem.Core.Models.Trading;

/// <summary>
/// Запись о закрытой сделке (для истории, FR-7.1).
/// </summary>
public sealed class TradeRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Symbol { get; set; } = string.Empty;
    public OrderSide Side { get; set; }
    public decimal Quantity { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal ExitPrice { get; set; }
    /// <summary>Валовой PnL по ценам входа/выхода (без комиссий). Сохраняется для совместимости истории.</summary>
    public decimal PnL { get; set; }
    public decimal PnLPercent { get; set; }
    public decimal Commission { get; set; }

    /// <summary>P1.2: чистый PnL после комиссий входа и выхода.</summary>
    public decimal NetPnL => PnL - Commission;
    public string ExitReason { get; set; } = string.Empty; // "TP", "SL", "TrailingStop", "Manual"
    public DateTime OpenedAt { get; set; }
    public DateTime ClosedAt { get; set; }
    public string SignalId { get; set; } = string.Empty;

    /// <summary>
    /// P2.4: начальный стоп позиции (до трейлинга/безубытка). Нужен, чтобы нормировать
    /// результат сделки на принятый риск: R = NetPnL / (|entry − initialSL| × объём).
    /// 0 — запись старой истории без риска, в подборе порога не участвует.
    /// </summary>
    public decimal InitialStopLoss { get; set; }

    /// <summary>P2.4: итоговая (FINAL) уверенность сигнала на входе — ось подбора MinConfidence.</summary>
    public decimal SignalConfidence { get; set; }

    /// <summary>P2.4: уверенность модели и prior движка на входе (для разбора причин).</summary>
    public decimal ModelConfidence { get; set; }
    public decimal EngineConfidence { get; set; }

    /// <summary>P2.4: источник итоговой уверенности (Blend / Hold→Prior / Degenerate→Prior / …).</summary>
    public string ConfidenceSource { get; set; } = string.Empty;

    /// <summary>
    /// P2.6: фичи direction-скоринга на входе (ScoringMath.FeatureKeys) — материал для
    /// валидации весов и reliability diagram на истории. Пустой словарь — старая история
    /// (до прохода 24) или сирота: в валидации весов такая сделка не участвует.
    /// </summary>
    public Dictionary<string, decimal> ScoreFeatures { get; set; } = new();

    /// <summary>P2.6: направление движка (score ∈ [-1; 1]) на входе — для directional hit rate.</summary>
    public decimal EngineDirection { get; set; }

    /// <summary>Сделка пригодна для валидации весов скоринга (фичи записаны).</summary>
    public bool HasScoreFeatures => ScoreFeatures is { Count: > 0 };

    /// <summary>Сделка пригодна для подбора порога по уверенности (метка есть).</summary>
    public bool HasConfidenceLabel => SignalConfidence > 0m;

    /// <summary>Принятый риск в котировке: |entry − начальный SL| × объём. 0 — риск неизвестен.</summary>
    public decimal RiskQuote => InitialStopLoss > 0m && EntryPrice > 0m
        ? Math.Abs(EntryPrice - InitialStopLoss) * Quantity
        : 0m;

    /// <summary>Net R-мультипликатор сделки; null — начальный риск неизвестен.</summary>
    public decimal? NetRMultiple => RiskQuote > 0m ? NetPnL / RiskQuote : null;

    /// <summary>
    /// Длительность сделки.
    /// </summary>
    public TimeSpan Duration => ClosedAt - OpenedAt;
}