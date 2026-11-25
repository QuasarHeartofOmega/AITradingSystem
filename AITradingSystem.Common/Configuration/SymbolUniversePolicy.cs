namespace AITradingSystem.Common.Configuration;

/// <summary>P0.6: торговая вселенная отделена от наблюдения и рыночных benchmark.</summary>
public enum SymbolUniverseClass
{
    Volatile,
    Stablecoin,
    Benchmark
}

/// <summary>
/// Единая политика для ConfigLoader, настроек UI и runtime-валидации.
/// Stablecoin-пары исключены до появления отдельной depeg/liquidity стратегии.
/// Benchmark-пары остаются источником рыночного контекста/корреляции, но не входом.
/// Неявный fallback для legacy-конфига — Volatile; поставляемый symbols.json задаёт списки явно.
/// </summary>
public static class SymbolUniversePolicy
{
    public static readonly string[] DefaultStablecoinPairs = { "USDCUSDT", "FDUSDUSDT" };
    public static readonly string[] DefaultBenchmarkSymbols = { "BTCUSDT", "ETHUSDT" };

    public static List<string> Normalize(IEnumerable<string>? symbols, IEnumerable<string> quotes)
    {
        var result = new List<string>();
        foreach (var raw in symbols ?? Enumerable.Empty<string>())
        {
            var symbol = SymbolNaming.NormalizeSymbol(raw, quotes)
                ?? throw new InvalidOperationException($"Список символов: некорректная пара или неизвестная котировка: {raw}");
            if (!result.Contains(symbol, StringComparer.OrdinalIgnoreCase)) result.Add(symbol);
        }
        return result;
    }

    public static void ValidateLists(IReadOnlyList<string> volatileSymbols,
        IReadOnlyList<string> stablecoinPairs, IReadOnlyList<string> benchmarkSymbols)
    {
        foreach (var (first, second) in new[]
        {
            (volatileSymbols, stablecoinPairs),
            (volatileSymbols, benchmarkSymbols),
            (stablecoinPairs, benchmarkSymbols)
        })
        {
            var overlap = first.Intersect(second, StringComparer.OrdinalIgnoreCase).ToList();
            if (overlap.Count > 0)
                throw new InvalidOperationException("Классы символов пересекаются: " + string.Join(", ", overlap));
        }
    }

    public static SymbolUniverseClass Classify(string symbol,
        IReadOnlyList<string> volatileSymbols, IReadOnlyList<string> stablecoinPairs,
        IReadOnlyList<string> benchmarkSymbols)
    {
        if (stablecoinPairs.Contains(symbol, StringComparer.OrdinalIgnoreCase)) return SymbolUniverseClass.Stablecoin;
        if (benchmarkSymbols.Contains(symbol, StringComparer.OrdinalIgnoreCase)) return SymbolUniverseClass.Benchmark;
        if (volatileSymbols.Count == 0 || volatileSymbols.Contains(symbol, StringComparer.OrdinalIgnoreCase))
            return SymbolUniverseClass.Volatile;
        throw new InvalidOperationException($"Символ {symbol} не классифицирован; добавьте его в VolatileSymbols, StablecoinPairs или BenchmarkSymbols.");
    }

    /// <summary>null — разрешён как торговый кандидат; иначе машинный reason_code исключения.</summary>
    public static string? EntryExclusionReason(string symbol,
        IReadOnlyList<string> volatileSymbols, IReadOnlyList<string> stablecoinPairs,
        IReadOnlyList<string> benchmarkSymbols) => Classify(symbol, volatileSymbols, stablecoinPairs, benchmarkSymbols) switch
        {
            SymbolUniverseClass.Stablecoin => "stablecoin_no_depeg_strategy",
            SymbolUniverseClass.Benchmark => "benchmark_context_only",
            _ => null
        };

    public static string FormatReasons(IReadOnlyDictionary<string, string> reasons) =>
        reasons.Count == 0 ? "none" : string.Join(",", reasons.OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value}"));
}
