using System.Globalization;
using AITradingSystem.Core.Models.Enums;

namespace AITradingSystem.Core.Models.Trading;

/// <summary>
/// P2.6: параметры валидации весов скоринга и калибровки уверенности (reliability diagram).
/// Все правила fail-safe: мало данных → вердиктов нет, веса/пороги не меняются.
/// </summary>
public sealed class ScoringCalibrationOptions
{
    /// <summary>Минимум пригодных сделок (метка уверенности + начальный риск) для вердикта калибровки.</summary>
    public int MinTrades { get; init; } = 40;

    /// <summary>Минимум сделок в корзине reliability, чтобы её разрыв считался значимым.</summary>
    public int MinBinTrades { get; init; } = 10;

    /// <summary>Шаг корзин уверенности (ширина корзины reliability diagram).</summary>
    public decimal BinStep { get; init; } = 0.1m;

    /// <summary>Доля ненулевых значений компонента ниже неё → компонент «мёртв» (не contributes).</summary>
    public decimal DeadCoverage { get; init; } = 0.05m;

    /// <summary>Минимум сделок «за»/«против» компонента для вердикта по нему.</summary>
    public int MinComponentSideTrades { get; init; } = 10;

    /// <summary>Порог ECE/среднего разрыва, выше которого уверенность считается некалиброванной.</summary>
    public decimal MaxEce { get; init; } = 0.10m;

    /// <summary>Анализировать градуированное выравнивание EMA (как включено в ScoringWeightsConfig).</summary>
    public bool UseGraduatedEmaAlignment { get; init; }
}

/// <summary>
/// Корзина reliability diagram: заявленная уверенность против фактической частоты успеха.
/// Успех = net PnL &gt; 0 (после комиссий обеих ног, P1.2). Разрыв Gap = WinRate − AvgConfidence:
/// отрицательный — модель/движок переоценивают себя (overconfidence).
/// </summary>
public sealed class ReliabilityBin
{
    /// <summary>Нижняя граница корзины уверенности [Boundary; Boundary+Step).</summary>
    public decimal Boundary { get; init; }

    public int Trades { get; init; }
    public int Wins { get; init; }

    /// <summary>Средняя заявленная FINAL-уверенность в корзине.</summary>
    public decimal AvgConfidence { get; init; }

    /// <summary>Фактическая частота успеха (доля сделок с net PnL &gt; 0).</summary>
    public decimal WinRate => Trades == 0 ? 0m : (decimal)Wins / Trades;

    /// <summary>Средний net R-мультипликатор корзины (expectancy после издержек).</summary>
    public decimal ExpectancyR { get; init; }

    /// <summary>Разрыв калибровки: фактическая частота успеха минус заявленная уверенность.</summary>
    public decimal Gap => WinRate - AvgConfidence;

    /// <summary>Корзина значима (хватает сделок для вывода о разрыве).</summary>
    public bool Qualifies { get; init; }

    public string Describe() => string.Format(CultureInfo.InvariantCulture,
        "conf[{0:0.##};+): сделок {1}, заявлено {2:P0}, факт {3:P0}, разрыв {4:+0.##;-0.##;0}, expectancy {5:0.###}R",
        Boundary, Trades, AvgConfidence, WinRate, Gap, ExpectancyR);
}

/// <summary>Вердикт по компоненту скоринга на истории (только диагностика, веса не меняются).</summary>
public enum ComponentVerdict
{
    /// <summary>Данных не хватает — вывод делать нельзя.</summary>
    InsufficientData = 0,

    /// <summary>Компонент почти всегда ноль — вес не работает (кейс ema_align из P2.1).</summary>
    Dead = 1,

    /// <summary>Сделки, согласованные с компонентом, в среднем убыточны после издержек.</summary>
    Contradicts = 2,

    /// <summary>Согласованные сделки прибыльны и лучше несогласованных (или «против» мало).</summary>
    Confirmed = 3,

    /// <summary>Значимого свидетельства ни за, ни против.</summary>
    Neutral = 4
}

/// <summary>Статистика одного компонента скоринга против исходов сделок.</summary>
public sealed class ComponentCalibration
{
    public string Name { get; init; } = string.Empty;

    /// <summary>Доля сделок с ненулевым значением компонента (покрытие).</summary>
    public decimal Coverage { get; init; }

    /// <summary>Сделки, где компонент поддерживал сторону входа (value·side &gt; 0).</summary>
    public int SupportedTrades { get; init; }

    /// <summary>Сделки, где компонент был против стороны входа (value·side &lt; 0).</summary>
    public int OpposedTrades { get; init; }

    public decimal SupportWinRate { get; init; }
    public decimal OpposeWinRate { get; init; }

    /// <summary>Средний net R сделок «за» компонент.</summary>
    public decimal SupportExpectancyR { get; init; }

    /// <summary>Средний net R сделок «против» компонента.</summary>
    public decimal OpposeExpectancyR { get; init; }

    /// <summary>Среднее value·side·R: знак = компонент в среднем указывает на прибыль или на убыток.</summary>
    public decimal MeanSignedR { get; init; }

    public ComponentVerdict Verdict { get; init; }

    public string Describe() => string.Format(CultureInfo.InvariantCulture,
        "{0}: покрытие {1:P0}, за {2} ({3:0.###}R), против {4} ({5:0.###}R), вердикт {6}",
        Name, Coverage, SupportedTrades, SupportExpectancyR, OpposedTrades, OpposeExpectancyR, Verdict);
}

/// <summary>Итог валидации скоринга: reliability diagram, калибровка и вердикты по компонентам.</summary>
public sealed class ScoringCalibrationReport
{
    public int TotalTrades { get; init; }

    /// <summary>Сделки без метки уверенности — не участвуют.</summary>
    public int UnlabeledTrades { get; init; }

    /// <summary>Сделки с меткой, но без начального риска — нечем нормировать R.</summary>
    public int NoRiskTrades { get; init; }

    /// <summary>Сделки с меткой уверенности и риском — ось reliability diagram.</summary>
    public int EvaluatedTrades { get; init; }

    /// <summary>Из них — с записанными фичами скоринга (ось валидации весов).</summary>
    public int FeatureTrades { get; init; }

    /// <summary>Пригодные, но без фич (история до прохода 24) — в валидации весов не участвуют.</summary>
    public int NoFeatureTrades { get; init; }

    public IReadOnlyList<ReliabilityBin> Bins { get; init; } = Array.Empty<ReliabilityBin>();

    /// <summary>Expected Calibration Error: взвешенный |WinRate − AvgConfidence| по корзинам.</summary>
    public decimal Ece { get; init; }

    /// <summary>Brier score: среднее (conf − win)² по сделкам (0 — идеально, 0.25 — монетка).</summary>
    public decimal BrierScore { get; init; }

    /// <summary>Максимальный |разрыв| среди значимых корзин.</summary>
    public decimal MaxAbsGap { get; init; }

    /// <summary>insufficient-data | calibrated | overconfident | underconfident | miscalibrated.</summary>
    public string CalibrationVerdict { get; init; } = "insufficient-data";

    /// <summary>Доля сделок, где знак направления движка совпал со знаком исхода (по фичам).</summary>
    public decimal DirectionalHitRate { get; init; }

    /// <summary>Среднее EngineDirection·side·R по сделкам с фичами.</summary>
    public decimal MeanSignedR { get; init; }

    public IReadOnlyList<ComponentCalibration> Components { get; init; } = Array.Empty<ComponentCalibration>();

    public int DeadComponents { get; init; }
    public int ContradictedComponents { get; init; }

    /// <summary>Человекочитаемая причина/итог (для лога/отчёта).</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Вердикт калибровки вынесен (хватает выборки).</summary>
    public bool HasVerdict => CalibrationVerdict != "insufficient-data";
}

/// <summary>
/// P2.6: чистый анализатор калибровки (без I/O): reliability diagram «уверенность должна
/// соответствовать реальной частоте успеха» + валидация компонентов direction-скоринга
/// на истории сделок с записанными фичами (TradeRecord.ScoreFeatures, проход 24).
///
/// Правила (fail-safe, как в P2.4):
/// 1. Reliability — только сделки с меткой уверенности и ненулевым риском; успех = NetPnL &gt; 0.
/// 2. Компоненты — только сделки с фичами скоринга; значения пересчитываются ScoringMath
///    (те же формулы, что в рантайме), поддержка = value·side &gt; 0.
/// 3. Мало выборки → InsufficientData; покрытие &lt; DeadCoverage → Dead; согласованные сделки
///    убыточны при достаточной выборке → Contradicts; прибыльны и лучше несогласованных → Confirmed.
/// 4. Анализатор НИЧЕГО не меняет: ни веса, ни порог. Подгонка весов — оффлайн-инструмент
///    tools/scoring_calibration.py --fit, применение — решение оператора через конфиг.
/// </summary>
public static class ScoringCalibrationAnalyzer
{
    /// <summary>Максимум корзин — защита от абсурдно мелкого шага.</summary>
    private const int MaxBins = 100;

    public static ScoringCalibrationReport Analyze(
        IReadOnlyList<TradeRecord>? trades,
        ScoringCalibrationOptions? options = null)
    {
        var opt = options ?? new ScoringCalibrationOptions();
        var all = trades ?? Array.Empty<TradeRecord>();

        var evaluated = new List<TradeRecord>();
        var unlabeled = 0;
        var noRisk = 0;
        foreach (var trade in all)
        {
            if (trade is null) continue;
            if (!trade.HasConfidenceLabel) { unlabeled++; continue; }
            if (trade.RiskQuote <= 0m) { noRisk++; continue; }
            evaluated.Add(trade);
        }

        var features = evaluated.FindAll(t => t.HasScoreFeatures);
        var step = opt.BinStep > 0m ? opt.BinStep : 0.1m;
        var bins = BuildBins(evaluated, step, opt.MinBinTrades);

        decimal ece = 0m, brier = 0m, signedGap = 0m, maxAbsGap = 0m;
        if (evaluated.Count > 0)
        {
            foreach (var trade in evaluated)
            {
                var win = trade.NetPnL > 0m ? 1m : 0m;
                var diff = trade.SignalConfidence - win;
                brier += diff * diff;
            }
            brier /= evaluated.Count;

            foreach (var bin in bins)
            {
                var share = (decimal)bin.Trades / evaluated.Count;
                ece += share * Math.Abs(bin.Gap);
                signedGap += share * (bin.AvgConfidence - bin.WinRate);
                if (bin.Qualifies && Math.Abs(bin.Gap) > maxAbsGap)
                    maxAbsGap = Math.Abs(bin.Gap);
            }
        }

        var verdict = evaluated.Count < opt.MinTrades ? "insufficient-data"
            : ece <= opt.MaxEce ? "calibrated"
            : signedGap > opt.MaxEce ? "overconfident"
            : signedGap < -opt.MaxEce ? "underconfident"
            : "miscalibrated";

        var components = AnalyzeComponents(features, opt);
        var dead = 0;
        var contradicted = 0;
        foreach (var component in components)
        {
            if (component.Verdict == ComponentVerdict.Dead) dead++;
            if (component.Verdict == ComponentVerdict.Contradicts) contradicted++;
        }

        var (hitRate, meanSignedR) = DirectionalStats(features);

        var reason = evaluated.Count == 0
            ? $"нет сделок с меткой уверенности и риском (без метки {unlabeled}, без риска {noRisk}) — вердиктов нет"
            : evaluated.Count < opt.MinTrades
                ? string.Format(CultureInfo.InvariantCulture,
                    "пригодных сделок {0} < минимума {1} — вердиктов нет", evaluated.Count, opt.MinTrades)
                : string.Format(CultureInfo.InvariantCulture,
                    "калибровка {0}: ECE {1:0.###}, Brier {2:0.###}, max |разрыв| {3:0.##}; компонентов мёртвых {4}, противоречат {5}; фичи у {6}/{7} сделок",
                    verdict, ece, brier, maxAbsGap, dead, contradicted, features.Count, evaluated.Count);

        return new ScoringCalibrationReport
        {
            TotalTrades = all.Count,
            UnlabeledTrades = unlabeled,
            NoRiskTrades = noRisk,
            EvaluatedTrades = evaluated.Count,
            FeatureTrades = features.Count,
            NoFeatureTrades = evaluated.Count - features.Count,
            Bins = bins,
            Ece = ece,
            BrierScore = brier,
            MaxAbsGap = maxAbsGap,
            CalibrationVerdict = verdict,
            DirectionalHitRate = hitRate,
            MeanSignedR = meanSignedR,
            Components = components,
            DeadComponents = dead,
            ContradictedComponents = contradicted,
            Reason = reason
        };
    }

    /// <summary>Корзины reliability diagram (публично — для тестов и инструмента).</summary>
    public static IReadOnlyList<ReliabilityBin> BuildBins(
        IReadOnlyList<TradeRecord> evaluated, decimal step, int minBinTrades)
    {
        var result = new List<ReliabilityBin>();
        if (evaluated.Count == 0) return result;

        for (var lower = 0m; lower < 1m && result.Count < MaxBins; lower += step)
        {
            var upper = lower + step;
            var inBin = new List<TradeRecord>();
            foreach (var trade in evaluated)
                if (trade.SignalConfidence >= lower && trade.SignalConfidence < upper)
                    inBin.Add(trade);
            if (inBin.Count > 0)
                result.Add(BuildBin(lower, inBin, minBinTrades));
        }

        // Уверенность ровно 1.0 попадает в последнюю корзину (как в P2.4).
        var atOne = new List<TradeRecord>();
        foreach (var trade in evaluated)
            if (trade.SignalConfidence >= 1m)
                atOne.Add(trade);
        if (atOne.Count > 0 && result.Count > 0)
        {
            var last = result[^1];
            var merged = new List<TradeRecord>();
            foreach (var trade in evaluated)
                if (trade.SignalConfidence >= last.Boundary)
                    merged.Add(trade);
            result[^1] = BuildBin(last.Boundary, merged, minBinTrades);
        }

        return result;
    }

    private static ReliabilityBin BuildBin(decimal boundary, List<TradeRecord> trades, int minBinTrades)
    {
        decimal confSum = 0m, rSum = 0m;
        var wins = 0;
        foreach (var trade in trades)
        {
            confSum += trade.SignalConfidence;
            rSum += trade.NetPnL / trade.RiskQuote;
            if (trade.NetPnL > 0m) wins++;
        }

        return new ReliabilityBin
        {
            Boundary = boundary,
            Trades = trades.Count,
            Wins = wins,
            AvgConfidence = confSum / trades.Count,
            ExpectancyR = rSum / trades.Count,
            Qualifies = trades.Count >= minBinTrades
        };
    }

    private static IReadOnlyList<ComponentCalibration> AnalyzeComponents(
        List<TradeRecord> features, ScoringCalibrationOptions opt)
    {
        // Порядок и имена — как в ScoringMath.ComponentValues / SignalCalibrator.
        var names = new[] { "ema_align", "supertrend", "ema_position", "rsi", "macd_hist", "stoch_rsi", "obv_trend" };
        var result = new List<ComponentCalibration>(names.Length);
        if (features.Count == 0)
        {
            foreach (var name in names)
                result.Add(new ComponentCalibration { Name = name, Verdict = ComponentVerdict.InsufficientData });
            return result;
        }

        // Пересчёт значений компонентов один раз на сделку.
        var perTrade = new List<(decimal Side, decimal R, IReadOnlyList<ScoreComponentValue> Values)>(features.Count);
        foreach (var trade in features)
        {
            var side = trade.Side == OrderSide.Buy ? 1m : -1m;
            var r = trade.NetPnL / trade.RiskQuote;
            perTrade.Add((side, r, ScoringMath.ComponentValues(trade.ScoreFeatures, opt.UseGraduatedEmaAlignment)));
        }

        for (var i = 0; i < names.Length; i++)
        {
            int nonZero = 0, support = 0, oppose = 0, supportWins = 0, opposeWins = 0;
            decimal supportR = 0m, opposeR = 0m, signedR = 0m;

            foreach (var (side, r, values) in perTrade)
            {
                var value = values[i].Value;
                if (value != 0m) nonZero++;
                var aligned = value * side;
                signedR += aligned * r;
                if (aligned > 0m)
                {
                    support++;
                    supportR += r;
                    if (r > 0m) supportWins++;
                }
                else if (aligned < 0m)
                {
                    oppose++;
                    opposeR += r;
                    if (r > 0m) opposeWins++;
                }
            }

            var coverage = (decimal)nonZero / features.Count;
            var supportExp = support > 0 ? supportR / support : 0m;
            var opposeExp = oppose > 0 ? opposeR / oppose : 0m;

            var verdict =
                features.Count < opt.MinTrades ? ComponentVerdict.InsufficientData :
                coverage < opt.DeadCoverage ? ComponentVerdict.Dead :
                support >= opt.MinComponentSideTrades && supportExp < 0m ? ComponentVerdict.Contradicts :
                support >= opt.MinComponentSideTrades && supportExp > 0m &&
                    (oppose < opt.MinComponentSideTrades || supportExp > opposeExp) ? ComponentVerdict.Confirmed :
                ComponentVerdict.Neutral;

            result.Add(new ComponentCalibration
            {
                Name = names[i],
                Coverage = coverage,
                SupportedTrades = support,
                OpposedTrades = oppose,
                SupportWinRate = support > 0 ? (decimal)supportWins / support : 0m,
                OpposeWinRate = oppose > 0 ? (decimal)opposeWins / oppose : 0m,
                SupportExpectancyR = supportExp,
                OpposeExpectancyR = opposeExp,
                MeanSignedR = signedR / features.Count,
                Verdict = verdict
            });
        }

        return result;
    }

    private static (decimal HitRate, decimal MeanSignedR) DirectionalStats(List<TradeRecord> features)
    {
        if (features.Count == 0) return (0m, 0m);

        int hits = 0, counted = 0;
        decimal signedR = 0m;
        foreach (var trade in features)
        {
            var side = trade.Side == OrderSide.Buy ? 1m : -1m;
            var r = trade.NetPnL / trade.RiskQuote;
            var aligned = trade.EngineDirection * side;
            signedR += aligned * r;
            if (trade.EngineDirection == 0m) continue;
            counted++;
            if ((aligned > 0m && r > 0m) || (aligned < 0m && r < 0m)) hits++;
        }

        return (counted > 0 ? (decimal)hits / counted : 0m, signedR / features.Count);
    }
}
