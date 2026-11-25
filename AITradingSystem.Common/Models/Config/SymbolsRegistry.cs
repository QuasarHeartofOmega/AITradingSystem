using AITradingSystem.Common.Configuration;
namespace AITradingSystem.Core.Models.Config;

public sealed class SymbolsRegistry
{
    public string Version { get; set; } = "1.1";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<SymbolConfig> Symbols { get; set; } = new();
    public List<string> VolatileSymbols { get; set; } = new();
    public List<string> StablecoinPairs { get; set; } = new(SymbolUniversePolicy.DefaultStablecoinPairs);
    public List<string> BenchmarkSymbols { get; set; } = new(SymbolUniversePolicy.DefaultBenchmarkSymbols);
    public List<string> WatchAssets { get; set; } = new();
    public List<string> QuoteAssets { get; set; } = new(SymbolNaming.DefaultQuotes);

    public void Validate()
    {
        QuoteAssets = SymbolNaming.NormalizeAssets(QuoteAssets);
        WatchAssets = SymbolNaming.NormalizeAssets(WatchAssets);
        VolatileSymbols = SymbolUniversePolicy.Normalize(VolatileSymbols, QuoteAssets);
        StablecoinPairs = SymbolUniversePolicy.Normalize(StablecoinPairs, QuoteAssets);
        BenchmarkSymbols = SymbolUniversePolicy.Normalize(BenchmarkSymbols, QuoteAssets);
        SymbolUniversePolicy.ValidateLists(VolatileSymbols, StablecoinPairs, BenchmarkSymbols);
        if (Symbols is null || Symbols.Count is 0 or > 32)
            throw new InvalidOperationException("Реестр должен содержать от 1 до 32 торговых пар.");
        foreach (var symbol in Symbols)
        {
            symbol.KnownQuotes = QuoteAssets;
            symbol.Validate();
            SymbolUniversePolicy.Classify(symbol.Symbol, VolatileSymbols, StablecoinPairs, BenchmarkSymbols);
            if (!WatchAssets.Contains(symbol.BaseAsset)) WatchAssets.Add(symbol.BaseAsset);
            if (!WatchAssets.Contains(symbol.QuoteAsset)) WatchAssets.Add(symbol.QuoteAsset);
        }
        var duplicates = Symbols.GroupBy(s => s.Symbol).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0) throw new InvalidOperationException("Дубликаты: " + string.Join(", ", duplicates));
        var sum = Symbols.Where(s => s.Enabled).Sum(s => s.MaxPositionPercent);
        if (sum > 100) throw new InvalidOperationException($"Сумма лимитов активных пар {sum}% превышает 100%.");
    }
}
