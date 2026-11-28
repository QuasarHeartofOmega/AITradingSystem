namespace AITradingSystem.Common.Configuration;

/// <summary>
/// P2.6: веса direction-скоринга движка (SignalCalibrator.PreScore), отображение
/// |score| → prior и модификаторы силы сетапа. Раньше всё было зашито константами
/// в SignalCalibrator — подтвердить/подогнать их на истории было нечем и негде.
///
/// Значения по умолчанию ТОЧНО воспроизводят прежние константы (поведение не меняется):
/// EMA 0.30 / Supertrend 0.20 / EMA-pos 0.15 / RSI 0.15 / MACD 0.10 / Stoch 0.05 / OBV 0.05,
/// prior = 0.2 + 0.7·|score|, штрафы ×0.6/×0.8/… .
///
/// Подбор значений — оффлайн-инструмент tools/scoring_calibration.py (--fit) по истории
/// сделок с записанными фичами скоринга (TradeRecord.ScoreFeatures). Рантайм веса
/// НЕ меняет: contour P2.6 только рекомендует (решение за оператором), как P2.4 для порога.
/// </summary>
public sealed class ScoringWeightsConfig
{
    // --- Веса компонентов направления (сумма = 1.0) ---

    /// <summary>Вес выравнивания EMA20/50/200 (крупнейший; в логе P2.1 был мёртв: ema_align=0 в 28/30).</summary>
    public decimal EmaAlignment { get; set; } = 0.30m;

    /// <summary>Вес направления Supertrend.</summary>
    public decimal Supertrend { get; set; } = 0.20m;

    /// <summary>Вес позиции цены относительно EMA20/50/200 (среднее трёх).</summary>
    public decimal EmaPosition { get; set; } = 0.15m;

    /// <summary>Вес RSI-компоненты (с поправкой на перекупленность/перепроданность).</summary>
    public decimal Rsi { get; set; } = 0.15m;

    /// <summary>Вес нормализованной MACD-гистограммы.</summary>
    public decimal Macd { get; set; } = 0.10m;

    /// <summary>Вес StochRSI-креста.</summary>
    public decimal StochRsi { get; set; } = 0.05m;

    /// <summary>Вес тренда OBV.</summary>
    public decimal Obv { get; set; } = 0.05m;

    // --- Отображение |score| → prior: prior = clamp(PriorBase + PriorScale·|score|, 0.1, 0.9) ---

    /// <summary>База prior при нулевом score (прежняя константа 0.2).</summary>
    public decimal PriorBase { get; set; } = 0.2m;

    /// <summary>Масштаб prior по |score| (прежняя константа 0.7).</summary>
    public decimal PriorScale { get; set; } = 0.7m;

    // --- Модификаторы силы сетапа (умножают direction, не меняют знак) ---

    /// <summary>ADX &lt; AdxWeakThreshold — слабый тренд (прежний штраф ×0.6).</summary>
    public decimal AdxWeakFactor { get; set; } = 0.6m;

    /// <summary>ADX в [AdxWeakThreshold; AdxStrongThreshold) — умеренный тренд (прежний ×0.85).</summary>
    public decimal AdxMidFactor { get; set; } = 0.85m;

    /// <summary>1.6: порог слабого тренда ADX (прежде константа 20, зашитая в
    /// SignalCalibrator.PreScore — P2.6 не мог её калибровать). Дефолт = прежняя константа.</summary>
    public decimal AdxWeakThreshold { get; set; } = 20m;

    /// <summary>1.6: порог подтверждённого тренда ADX (прежде зашитая константа 25).
    /// ADX ≥ порога → без штрафа. Дефолт = прежняя константа.</summary>
    public decimal AdxStrongThreshold { get; set; } = 25m;

    /// <summary>volume_ratio &lt; 0.6 — объём ниже среднего (прежний ×0.8).</summary>
    public decimal VolumeLowFactor { get; set; } = 0.8m;

    /// <summary>volume_ratio в [0.6; 1.0) — прежний ×0.9.</summary>
    public decimal VolumeMidFactor { get; set; } = 0.9m;

    /// <summary>volume_ratio ≥ 1.5 — объём подтверждает движение (прежний ×1.1).</summary>
    public decimal VolumeHighFactor { get; set; } = 1.1m;

    /// <summary>ATR% &lt; 0.1% — волатильность слишком низкая (прежний ×0.8).</summary>
    public decimal LowVolatilityFactor { get; set; } = 0.8m;

    /// <summary>ATR% &gt; 6% — волатильность экстремальная (прежний ×0.75).</summary>
    public decimal ExtremeVolatilityFactor { get; set; } = 0.75m;

    /// <summary>Цена у противоположной полосы Bollinger — перегретый вход (прежний ×0.85).</summary>
    public decimal BollingerOverheatFactor { get; set; } = 0.85m;

    /// <summary>
    /// Использовать градуированное выравнивание EMA (`ema_align_grad`: ±1 полное, ±0.5 частичное)
    /// вместо строгого троичного `ema_align`. По умолчанию ВЫКЛ — поведение прежнее;
    /// включение — эксперимент, который сравнивается на истории инструментом P2.6
    /// (диагностика P2.1: строгий ключ = 0 почти всегда, крупнейший вес не contributes).
    /// </summary>
    public bool UseGraduatedEmaAlignment { get; set; } = false;

    /// <summary>Проверка диапазонов и согласованности (вызывается из AIConfig.Validate).</summary>
    public void Validate()
    {
        CheckWeight(EmaAlignment, nameof(EmaAlignment));
        CheckWeight(Supertrend, nameof(Supertrend));
        CheckWeight(EmaPosition, nameof(EmaPosition));
        CheckWeight(Rsi, nameof(Rsi));
        CheckWeight(Macd, nameof(Macd));
        CheckWeight(StochRsi, nameof(StochRsi));
        CheckWeight(Obv, nameof(Obv));

        var sum = EmaAlignment + Supertrend + EmaPosition + Rsi + Macd + StochRsi + Obv;
        if (Math.Abs(sum - 1m) > 0.001m)
            throw new InvalidOperationException(
                $"AI:ScoringWeights: сумма весов компонентов должна быть 1.0 (±0.001). Текущая: {sum}");

        if (PriorBase < 0m || PriorBase > 0.5m)
            throw new InvalidOperationException(
                $"AI:ScoringWeights:PriorBase должен быть в диапазоне [0; 0.5]. Текущее: {PriorBase}");

        if (PriorScale <= 0m || PriorScale > 1m)
            throw new InvalidOperationException(
                $"AI:ScoringWeights:PriorScale должен быть в диапазоне (0; 1]. Текущее: {PriorScale}");

        if (PriorBase + PriorScale > 1m)
            throw new InvalidOperationException(
                $"AI:ScoringWeights: PriorBase + PriorScale должен быть <= 1. Текущее: {PriorBase + PriorScale}");

        CheckFactor(AdxWeakFactor, nameof(AdxWeakFactor));
        CheckFactor(AdxMidFactor, nameof(AdxMidFactor));

        if (AdxWeakThreshold <= 0m || AdxWeakThreshold >= 100m)
            throw new InvalidOperationException(
                $"AI:ScoringWeights:AdxWeakThreshold должен быть в диапазоне (0; 100). Текущее: {AdxWeakThreshold}");

        if (AdxStrongThreshold <= AdxWeakThreshold || AdxStrongThreshold > 100m)
            throw new InvalidOperationException(
                $"AI:ScoringWeights:AdxStrongThreshold должен быть в диапазоне (AdxWeakThreshold; 100]. Текущее: {AdxStrongThreshold}");
        CheckFactor(VolumeLowFactor, nameof(VolumeLowFactor));
        CheckFactor(VolumeMidFactor, nameof(VolumeMidFactor));
        CheckFactor(VolumeHighFactor, nameof(VolumeHighFactor));
        CheckFactor(LowVolatilityFactor, nameof(LowVolatilityFactor));
        CheckFactor(ExtremeVolatilityFactor, nameof(ExtremeVolatilityFactor));
        CheckFactor(BollingerOverheatFactor, nameof(BollingerOverheatFactor));
    }

    private static void CheckWeight(decimal value, string name)
    {
        if (value < 0m || value > 1m)
            throw new InvalidOperationException(
                $"AI:ScoringWeights:{name} должен быть в диапазоне [0; 1]. Текущее: {value}");
    }

    private static void CheckFactor(decimal value, string name)
    {
        if (value <= 0m || value > 1.5m)
            throw new InvalidOperationException(
                $"AI:ScoringWeights:{name} должен быть в диапазоне (0; 1.5]. Текущее: {value}");
    }
}
