using AITradingSystem.Common.Configuration;
using AITradingSystem.Core.Models.Config;
using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Services.Analysis;

namespace AITradingSystem.Tests;

/// <summary>
/// P0.6: торговые/стабильные/benchmark классы и согласованность направления.
/// Тесты добавлены, но не запускались: сборки и тестовые прогоны запрещены в этом проходе.
/// </summary>
public class SymbolUniverseTests
{
    private static readonly string[] Volatile = { "DOGEUSDT", "XRPUSDT" };
    private static readonly string[] Stable = { "USDCUSDT", "FDUSDUSDT" };
    private static readonly string[] Benchmark = { "BTCUSDT", "ETHUSDT" };

    [Theory]
    [InlineData("USDCUSDT", "stablecoin_no_depeg_strategy")]
    [InlineData("FDUSDUSDT", "stablecoin_no_depeg_strategy")]
    [InlineData("BTCUSDT", "benchmark_context_only")]
    [InlineData("ETHUSDT", "benchmark_context_only")]
    public void NonTradingClasses_Are_Excluded_With_Reason(string symbol, string expected)
    {
        Assert.Equal(expected, SymbolUniversePolicy.EntryExclusionReason(symbol, Volatile, Stable, Benchmark));
    }

    [Fact]
    public void VolatileSymbol_Is_Entry_Candidate()
    {
        Assert.Null(SymbolUniversePolicy.EntryExclusionReason("DOGEUSDT", Volatile, Stable, Benchmark));
        Assert.Equal(SymbolUniverseClass.Volatile,
            SymbolUniversePolicy.Classify("DOGEUSDT", Volatile, Stable, Benchmark));
    }

    [Fact]
    public void ExplicitUniverse_Rejects_Unknown_Symbol()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SymbolUniversePolicy.Classify("SOLUSDT", Volatile, Stable, Benchmark));
    }

    [Fact]
    public void LegacyEmptyVolatileList_Uses_Volatile_Fallback()
    {
        Assert.Null(SymbolUniversePolicy.EntryExclusionReason("SOLUSDT", Array.Empty<string>(), Stable, Benchmark));
    }

    [Fact]
    public void OverlappingClasses_Fail_Validation()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SymbolUniversePolicy.ValidateLists(new[] { "BTCUSDT" }, Stable, Benchmark));
    }

    [Fact]
    public void TradingConfig_Normalizes_And_Rejects_NonTrading_Candidates()
    {
        var config = new TradingConfig
        {
            Symbols = new List<string> { "USDCUSDT" },
            VolatileSymbols = new List<string> { " dogeusdt " }
        };
        Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Equal("DOGEUSDT", config.VolatileSymbols[0]);
    }

    [Fact]
    public void Registry_Requires_Explicit_Class_With_Nonempty_Volatile_List()
    {
        var registry = new SymbolsRegistry
        {
            Symbols = new List<SymbolConfig> { new() { Symbol = "SOLUSDT" } },
            VolatileSymbols = Volatile.ToList(),
            StablecoinPairs = Stable.ToList(),
            BenchmarkSymbols = Benchmark.ToList()
        };
        Assert.Throws<InvalidOperationException>(() => registry.Validate());
    }

    [Fact]
    public void Registry_Keeps_Stable_And_Benchmark_As_NonTrading_Classes()
    {
        var registry = new SymbolsRegistry
        {
            Symbols = new List<SymbolConfig>
            {
                new() { Symbol = "DOGEUSDT" },
                new() { Symbol = "USDCUSDT" },
                new() { Symbol = "BTCUSDT" }
            },
            VolatileSymbols = Volatile.ToList(),
            StablecoinPairs = Stable.ToList(),
            BenchmarkSymbols = Benchmark.ToList()
        };
        registry.Validate();
        Assert.Equal(3, registry.Symbols.Count);
        Assert.Equal(2, registry.Symbols.Count(s =>
            SymbolUniversePolicy.EntryExclusionReason(s.Symbol,
                registry.VolatileSymbols, registry.StablecoinPairs, registry.BenchmarkSymbols) != null));
    }

    [Theory]
    [InlineData(SignalAction.Buy, -0.6, false)]
    [InlineData(SignalAction.Buy, 0.0, false)]
    [InlineData(SignalAction.Buy, 0.6, true)]
    [InlineData(SignalAction.Sell, 0.6, false)]
    [InlineData(SignalAction.Sell, -0.6, true)]
    [InlineData(SignalAction.Hold, 0.6, false)]
    public void Entry_Requires_Aligned_Engine_Direction(SignalAction action, double direction, bool expected)
    {
        Assert.Equal(expected, SignalCalibrator.IsEntryDirectionAllowed(action, (decimal)direction));
    }

    [Fact]
    public void Short_Engine_Score_Never_Produces_Buy()
    {
        var signal = new SignalCalibrator().BuildEngineSignal("DOGEUSDT",
            new EngineScore { Direction = -0.6m, PriorConfidence = 0.9m },
            new CalibrationContext { MinConfidence = 0.5m, Price = 100m, AtrAbsolute = 1m, PriceDecimals = 2 });
        Assert.Equal(SignalAction.Hold, signal.Action);
    }
}
