using System.Globalization;

namespace AITradingSystem.Core.Models.Trading;

/// <summary>
/// P2.4: параметры подбора порога <c>MinConfidence</c> по expectancy после издержек.
/// Порог не «с потолка»: он выбирается по истории закрытых сделок, но только в пределах
/// коридора [Floor; Ceiling] и только при достаточной выборке — иначе остаётся прежним.
/// </summary>
public sealed class ConfidenceExpectancyOptions
{
    /// <summary>
    /// Прежнее значение «с потолка» — единственное место, где оно зашито.
    /// Используется как fallback, когда истории не хватает для подбора.
    /// </summary>
    public const decimal FallbackMinConfidence = 0.55m;

    /// <summary>Текущий порог (для сравнения «что даёт текущий порог vs рекомендованный»).</summary>
    public decimal CurrentThreshold { get; init; } = FallbackMinConfidence;

    /// <summary>Нижняя граница коридора подбора: ниже неё порог не опускается никогда.</summary>
    public decimal Floor { get; init; } = 0.45m;

    /// <summary>Верхняя граница коридора подбора.</summary>
    public decimal Ceiling { get; init; } = 0.80m;

    /// <summary>Шаг сетки порогов.</summary>
    public decimal Step { get; init; } = 0.05m;

    /// <summary>Минимум пригодных сделок во всей истории, чтобы вообще что-то рекомендовать.</summary>
    public int MinTrades { get; init; } = 40;

    /// <summary>Минимум сделок выше кандидата-порога, чтобы точка считалась значимой.</summary>
    public int MinBucketTrades { get; init; } = 15;
}

/// <summary>
/// Статистика сделок на сетке уверенности: корзина [Boundary; Boundary+Step) либо
/// «хвост» сделок с уверенностью ≥ порога (для точек sweep).
/// Expectancy считается ПОСЛЕ издержек: NetPnL = PnL − комиссии обеих ног (P1.2).
/// </summary>
public sealed class ConfidenceExpectancyPoint
{
    /// <summary>Нижняя граница корзины или значение порога.</summary>
    public decimal Boundary { get; init; }

    public int Trades { get; init; }
    public int Wins { get; init; }

    /// <summary>Суммарный net PnL в котировке (USDT).</summary>
    public decimal TotalNetPnL { get; init; }

    /// <summary>Средний net PnL на сделку в котировке — expectancy в деньгах.</summary>
    public decimal ExpectancyQuote { get; init; }

    /// <summary>Средний net R-мультипликатор (NetPnL / начальный риск) — основная метрика выбора.</summary>
    public decimal ExpectancyR { get; init; }

    public decimal WinRate => Trades == 0 ? 0m : (decimal)Wins / Trades;

    /// <summary>Точка значима: хватает сделок и expectancy после издержек положительная.</summary>
    public bool Qualifies { get; init; }

    public string Describe() => string.Format(CultureInfo.InvariantCulture,
        "conf>={0:0.##}: сделок {1}, win {2:P0}, expectancy {3:0.###}R / {4:0.##} USDT",
        Boundary, Trades, WinRate, ExpectancyR, ExpectancyQuote);
}

/// <summary>Итог разбора истории: корзины, sweep порогов и рекомендация (или причина отказа).</summary>
public sealed class ConfidenceExpectancyReport
{
    public int TotalTrades { get; init; }

    /// <summary>Сделки с записанной уверенностью сигнала (старая история её не содержит).</summary>
    public int LabeledTrades { get; init; }

    /// <summary>Сделки без уверенности — в подборе не участвуют.</summary>
    public int UnlabeledTrades { get; init; }

    /// <summary>Сделки с уверенностью, но без начального риска (нечем нормировать R).</summary>
    public int NoRiskTrades { get; init; }

    /// <summary>Сделки, реально участвующие в подборе.</summary>
    public int EvaluatedTrades { get; init; }

    public IReadOnlyList<ConfidenceExpectancyPoint> Buckets { get; init; } = Array.Empty<ConfidenceExpectancyPoint>();
    public IReadOnlyList<ConfidenceExpectancyPoint> Sweep { get; init; } = Array.Empty<ConfidenceExpectancyPoint>();

    /// <summary>Что даёт действующий порог на той же истории.</summary>
    public ConfidenceExpectancyPoint? Current { get; init; }

    /// <summary>Рекомендованный порог; null — данных не хватает, порог менять нельзя.</summary>
    public decimal? RecommendedThreshold { get; init; }

    /// <summary>Expectancy (R) в рекомендованной точке.</summary>
    public decimal RecommendedExpectancyR { get; init; }

    /// <summary>Человекочитаемая причина решения (для лога/отчёта).</summary>
    public string Reason { get; init; } = string.Empty;

    public bool HasRecommendation => RecommendedThreshold.HasValue;

    /// <summary>Рекомендация отличается от действующего порога.</summary>
    public bool ChangesThreshold(decimal currentThreshold) =>
        HasRecommendation && RecommendedThreshold!.Value != currentThreshold;
}

/// <summary>
/// P2.4: чистый подбор порога уверенности по expectancy после издержек (без I/O, время не нужно).
///
/// Почему R, а не USDT: 12 альтов торгуются разными нотариалами, средняя прибыль в USDT
/// смешивает размер позиции с качеством сигнала. Net R-мультипликатор нормирует сделку
/// на начальный риск (|entry − initialSL| × объём), поэтому сравнивать пороги можно на общей истории.
///
/// Правила отбора (fail-safe):
/// 1. В подбор идут только сделки с записанной уверенностью и ненулевым начальным риском.
/// 2. Порог-кандидат значим, если выше него ≥ MinBucketTrades сделок и expectancy &gt; 0.
/// 3. Берётся максимум expectancy; при равенстве — более высокий порог (меньше сделок, строже вход).
/// 4. Нет значимых кандидатов или мало истории → рекомендации нет, порог остаётся прежним.
/// </summary>
public static class ConfidenceExpectancyAnalyzer
{
    /// <summary>Максимум точек сетки — защита от абсурдно мелкого шага в конфиге.</summary>
    private const int MaxGridPoints = 200;

    public static ConfidenceExpectancyReport Analyze(
        IReadOnlyList<TradeRecord>? trades,
        ConfidenceExpectancyOptions? options = null)
    {
        var opt = options ?? new ConfidenceExpectancyOptions();
        var all = trades ?? Array.Empty<TradeRecord>();

        var labeled = new List<TradeRecord>();
        var unlabeled = 0;
        var noRisk = 0;

        foreach (var trade in all)
        {
            if (trade is null) continue;
            if (!trade.HasConfidenceLabel) { unlabeled++; continue; }
            if (trade.RiskQuote <= 0m) { noRisk++; continue; }
            labeled.Add(trade);
        }

        var step = opt.Step > 0m ? opt.Step : 0.05m;
        var floor = Math.Clamp(opt.Floor, 0m, 1m);
        var ceiling = Math.Clamp(opt.Ceiling, floor, 1m);

        var buckets = BuildBuckets(labeled, step);
        var sweep = BuildSweep(labeled, floor, ceiling, step, opt.MinBucketTrades);
        var current = Evaluate(labeled, Math.Clamp(opt.CurrentThreshold, 0m, 1m), opt.MinBucketTrades);

        if (all.Count == 0)
            return Report(all.Count, labeled.Count, unlabeled, noRisk, buckets, sweep, current, null,
                "история сделок пуста — порог остаётся прежним");

        if (labeled.Count == 0)
            return Report(all.Count, labeled.Count, unlabeled, noRisk, buckets, sweep, current, null,
                $"нет сделок с записанной уверенностью и начальным риском (без метки {unlabeled}, без риска {noRisk}) — порог остаётся прежним");

        if (labeled.Count < opt.MinTrades)
            return Report(all.Count, labeled.Count, unlabeled, noRisk, buckets, sweep, current, null,
                $"пригодных сделок {labeled.Count} < минимума {opt.MinTrades} — порог остаётся прежним");

        ConfidenceExpectancyPoint? best = null;
        foreach (var point in sweep)
        {
            if (!point.Qualifies) continue;
            if (best is null || point.ExpectancyR > best.ExpectancyR ||
                (point.ExpectancyR == best.ExpectancyR && point.Boundary > best.Boundary))
                best = point;
        }

        if (best is null)
            return Report(all.Count, labeled.Count, unlabeled, noRisk, buckets, sweep, current, null,
                $"нет порога в [{floor:0.##}; {ceiling:0.##}] с положительной expectancy и выборкой ≥ {opt.MinBucketTrades} — порог остаётся прежним");

        var reason = string.Format(CultureInfo.InvariantCulture,
            "максимум expectancy {0:0.###}R на пороге {1:0.##} ({2} сделок, win {3:P0}); действующий порог {4:0.##} даёт {5:0.###}R",
            best.ExpectancyR, best.Boundary, best.Trades, best.WinRate, opt.CurrentThreshold, current.ExpectancyR);

        return Report(all.Count, labeled.Count, unlabeled, noRisk, buckets, sweep, current, best, reason);
    }

    /// <summary>Статистика «хвоста» сделок с уверенностью ≥ порога (публично — для тестов и инструмента).</summary>
    public static ConfidenceExpectancyPoint Evaluate(
        IReadOnlyList<TradeRecord> trades, decimal threshold, int minBucketTrades)
    {
        var selected = new List<TradeRecord>();
        foreach (var trade in trades)
        {
            if (trade is null) continue;
            if (!trade.HasConfidenceLabel || trade.RiskQuote <= 0m) continue;
            if (trade.SignalConfidence >= threshold) selected.Add(trade);
        }

        return BuildPoint(threshold, selected, minBucketTrades);
    }

    private static ConfidenceExpectancyReport Report(
        int total, int evaluated, int unlabeled, int noRisk,
        IReadOnlyList<ConfidenceExpectancyPoint> buckets,
        IReadOnlyList<ConfidenceExpectancyPoint> sweep,
        ConfidenceExpectancyPoint current,
        ConfidenceExpectancyPoint? best,
        string reason) => new()
        {
            TotalTrades = total,
            LabeledTrades = evaluated + noRisk,
            UnlabeledTrades = unlabeled,
            NoRiskTrades = noRisk,
            EvaluatedTrades = evaluated,
            Buckets = buckets,
            Sweep = sweep,
            Current = current,
            RecommendedThreshold = best?.Boundary,
            RecommendedExpectancyR = best?.ExpectancyR ?? 0m,
            Reason = reason
        };

    private static IReadOnlyList<ConfidenceExpectancyPoint> BuildBuckets(List<TradeRecord> trades, decimal step)
    {
        var result = new List<ConfidenceExpectancyPoint>();
        if (trades.Count == 0) return result;

        for (var lower = 0m; lower < 1m && result.Count < MaxGridPoints; lower += step)
        {
            var upper = lower + step;
            var inBucket = trades.FindAll(t => t.SignalConfidence >= lower && t.SignalConfidence < upper);
            if (inBucket.Count == 0) continue;
            result.Add(BuildPoint(lower, inBucket, minBucketTrades: 0));
        }

        // Уверенность ровно 1.0 попадает в последнюю корзину [1-step; 1].
        var atOne = trades.FindAll(t => t.SignalConfidence >= 1m);
        if (atOne.Count > 0 && result.Count > 0)
        {
            var last = result[^1];
            var merged = trades.FindAll(t => t.SignalConfidence >= last.Boundary);
            result[^1] = BuildPoint(last.Boundary, merged, minBucketTrades: 0);
        }

        return result;
    }

    private static IReadOnlyList<ConfidenceExpectancyPoint> BuildSweep(
        List<TradeRecord> trades, decimal floor, decimal ceiling, decimal step, int minBucketTrades)
    {
        var result = new List<ConfidenceExpectancyPoint>();
        for (var threshold = floor; threshold <= ceiling && result.Count < MaxGridPoints; threshold += step)
            result.Add(Evaluate(trades, threshold, minBucketTrades));

        // Верхняя граница коридора всегда присутствует в сетке, даже если шаг её не делит.
        if (result.Count == 0 || result[^1].Boundary != ceiling)
            result.Add(Evaluate(trades, ceiling, minBucketTrades));

        return result;
    }

    private static ConfidenceExpectancyPoint BuildPoint(
        decimal boundary, List<TradeRecord> trades, int minBucketTrades)
    {
        if (trades.Count == 0)
            return new ConfidenceExpectancyPoint { Boundary = boundary };

        decimal netSum = 0m, rSum = 0m;
        var wins = 0;
        foreach (var trade in trades)
        {
            var net = trade.NetPnL;
            netSum += net;
            rSum += net / trade.RiskQuote;
            if (net > 0m) wins++;
        }

        var expectancyR = rSum / trades.Count;
        return new ConfidenceExpectancyPoint
        {
            Boundary = boundary,
            Trades = trades.Count,
            Wins = wins,
            TotalNetPnL = netSum,
            ExpectancyQuote = netSum / trades.Count,
            ExpectancyR = expectancyR,
            Qualifies = trades.Count >= minBucketTrades && expectancyR > 0m
        };
    }
}
