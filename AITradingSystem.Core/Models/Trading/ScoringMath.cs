namespace AITradingSystem.Core.Models.Trading;

/// <summary>Значение компонента direction-скоринга (имя совпадает с ScoreComponent калибратора).</summary>
public sealed record ScoreComponentValue(string Name, decimal Value);

/// <summary>
/// P2.6: чистая математика direction-скоринга, общая для рантайма (SignalCalibrator),
/// анализатора калибровки (ScoringCalibrationAnalyzer) и оффлайн-инструмента
/// tools/scoring_calibration.py. Без I/O и без весов: веса применяет вызывающая сторона.
///
/// Формулы компонентов ТОЧНО совпадают с SignalCalibrator.PreScore — расхождение
/// сделало бы валидацию весов фикцией (анализатор проверял бы не ту модель, что торгует).
/// </summary>
public static class ScoringMath
{
    /// <summary>
    /// Ключи снимка индикаторов, которых достаточно, чтобы пересчитать все 7 компонентов,
    /// модификаторы и prior с ЛЮБЫМИ весами. Записываются в Position/TradeRecord при входе.
    /// </summary>
    public static readonly IReadOnlyList<string> FeatureKeys = new[]
    {
        "ema_align", "ema_align_grad", "supertrend",
        "ema20_pos", "ema50_pos", "ema200_pos",
        "rsi_raw", "macd_hist", "stoch_k_raw", "stoch_d_raw", "obv_trend",
        "adx_raw", "volume_ratio", "atr_pct", "bb_pos"
    };

    /// <summary>
    /// Снимок фич скоринга из словаря индикаторов. Отсутствующий ключ → 0 (как Get в
    /// калибраторе): запись детерминирована и пригодна для пересчёта на истории.
    /// </summary>
    public static Dictionary<string, decimal> CaptureFeatures(IReadOnlyDictionary<string, decimal>? indicators)
    {
        var result = new Dictionary<string, decimal>(FeatureKeys.Count);
        foreach (var key in FeatureKeys)
            result[key] = indicators != null && indicators.TryGetValue(key, out var value) ? value : 0m;
        return result;
    }

    /// <summary>
    /// Значения 7 компонентов направления из записанных фич, каждый в [-1; 1].
    /// useGraduatedEmaAlignment: брать `ema_align_grad` (±1/±0.5/0) вместо строгого
    /// `ema_align`; при отсутствии градуированного ключа — fallback на строгий.
    /// </summary>
    public static IReadOnlyList<ScoreComponentValue> ComponentValues(
        IReadOnlyDictionary<string, decimal> features, bool useGraduatedEmaAlignment = false)
    {
        var align = useGraduatedEmaAlignment && features.TryGetValue("ema_align_grad", out var grad)
            ? grad
            : Get(features, "ema_align");

        var emaPos = (Get(features, "ema20_pos") + Get(features, "ema50_pos") + Get(features, "ema200_pos")) / 3m;
        var stoch = StochScore(Get(features, "stoch_k_raw") / 100m, Get(features, "stoch_d_raw") / 100m);

        return new[]
        {
            new ScoreComponentValue("ema_align", Clamp(align)),
            new ScoreComponentValue("supertrend", Clamp(Get(features, "supertrend"))),
            new ScoreComponentValue("ema_position", Clamp(emaPos)),
            new ScoreComponentValue("rsi", Clamp(RsiScore(Get(features, "rsi_raw")))),
            new ScoreComponentValue("macd_hist", Clamp(Get(features, "macd_hist"))),
            new ScoreComponentValue("stoch_rsi", Clamp(stoch)),
            new ScoreComponentValue("obv_trend", Clamp(Get(features, "obv_trend")))
        };
    }

    /// <summary>RSI 0..100 → [-1; 1] с поправкой на перекупленность/перепроданность (как в калибраторе).</summary>
    public static decimal RsiScore(decimal rsi)
    {
        if (rsi <= 0m) return 0m;
        if (rsi >= 80m) return 0.2m;   // перекупленность — не повод покупать
        if (rsi <= 20m) return -0.2m;  // перепроданность в падающем тренде — не повод продавать
        return Math.Clamp((rsi - 50m) / 25m, -1m, 1m);
    }

    /// <summary>StochRSI K/D (0..1) → [-1; 1]: направление креста, крайние зоны гасим (как в калибраторе).</summary>
    public static decimal StochScore(decimal k, decimal d)
    {
        var cross = Math.Clamp((k - d) * 4m, -1m, 1m);
        if (k > 0.9m) return Math.Min(cross, 0.2m);
        if (k < 0.1m) return Math.Max(cross, -0.2m);
        return cross;
    }

    private static decimal Get(IReadOnlyDictionary<string, decimal> dict, string key) =>
        dict.TryGetValue(key, out var value) ? value : 0m;

    private static decimal Clamp(decimal value) => Math.Clamp(value, -1m, 1m);
}
