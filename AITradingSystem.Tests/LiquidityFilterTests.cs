using AITradingSystem.Common.Configuration;
using AITradingSystem.Core.Models.Config;
using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.MarketData;
using AITradingSystem.Services.Monitoring;

namespace AITradingSystem.Tests;

/// <summary>
/// P1.5: фильтр ликвидности перед входом (спред/глубина/объём) + метрика post-only-отказов.
/// Тесты добавлены в проходе 16; не запускались (сборки/запуски запрещены заказчиком).
/// </summary>
public class LiquidityFilterTests
{
    private static OrderBook Book(decimal bid, decimal ask, decimal bidQty = 100m, decimal askQty = 100m, int levels = 10)
    {
        var book = new OrderBook { Symbol = "BTCUSDT", Timestamp = DateTime.UtcNow };
        for (var i = 0; i < levels; i++)
        {
            book.Bids.Add(new OrderBookEntry { Price = bid - i * 0.01m, Quantity = bidQty });
            book.Asks.Add(new OrderBookEntry { Price = ask + i * 0.01m, Quantity = askQty });
        }
        return book;
    }

    private static LiquidityThresholds Thresholds() => new();

    // === Спред ===

    [Fact]
    public void WideSpread_BlocksEntry()
    {
        // bid 99 / ask 101 → спред 2% при пороге 0.35%
        var result = LiquidityFilter.Evaluate(Book(99m, 101m), OrderSide.Buy, 1m, 100m, 100_000m, Thresholds());
        Assert.False(result.Passed);
        Assert.Equal("spread", result.Reason);
    }

    [Fact]
    public void NarrowSpread_Passes()
    {
        // bid 99.9 / ask 100.1 → спред 0.2% ≤ 0.35%
        var result = LiquidityFilter.Evaluate(Book(99.9m, 100.1m), OrderSide.Buy, 1m, 100m, 100_000m, Thresholds());
        Assert.True(result.Passed);
    }

    // === Глубина стороны входа ===

    [Fact]
    public void ThinAskDepth_BlocksBuy_BidDepthIrrelevant()
    {
        // BUY съедает asks: 10 уровней × 1 × ~100 = ~1000 USDT < 5 × нотариал (10 × 100 = 1000 → 5000).
        // Bid-сторона при этом толстая — она не должна спасать вход.
        var book = Book(99.9m, 100.1m, bidQty: 1000m, askQty: 1m);
        var result = LiquidityFilter.Evaluate(book, OrderSide.Buy, 10m, 100m, 100_000m, Thresholds());
        Assert.False(result.Passed);
        Assert.Equal("depth", result.Reason);
    }

    [Fact]
    public void ThinBidDepth_BlocksSell()
    {
        var book = Book(99.9m, 100.1m, bidQty: 1m, askQty: 1000m);
        var result = LiquidityFilter.Evaluate(book, OrderSide.Sell, 10m, 100m, 100_000m, Thresholds());
        Assert.False(result.Passed);
        Assert.Equal("depth", result.Reason);
    }

    [Fact]
    public void AbsoluteDepthFloor_BlocksTinyOrder()
    {
        // Нотариал крошечный (0.001 × 100 = 0.1), но абсолютный минимум 1000 USDT всё равно требует глубины.
        var book = Book(99.9m, 100.1m, bidQty: 0.5m, askQty: 0.5m); // глубина ~500 USDT
        var result = LiquidityFilter.Evaluate(book, OrderSide.Buy, 0.001m, 100m, 100_000m, Thresholds());
        Assert.False(result.Passed);
        Assert.Equal("depth", result.Reason);
    }

    [Fact]
    public void DeepBook_PassesBothSides()
    {
        var book = Book(99.9m, 100.1m); // ~100k USDT на сторону
        Assert.True(LiquidityFilter.Evaluate(book, OrderSide.Buy, 10m, 100m, 100_000m, Thresholds()).Passed);
        Assert.True(LiquidityFilter.Evaluate(book, OrderSide.Sell, 10m, 100m, 100_000m, Thresholds()).Passed);
    }

    // === Объём свечей ===

    [Fact]
    public void LowCandleVolume_BlocksEntry()
    {
        var result = LiquidityFilter.Evaluate(Book(99.9m, 100.1m), OrderSide.Buy, 1m, 100m,
            avgCandleQuoteVolume: 100m, Thresholds());
        Assert.False(result.Passed);
        Assert.Equal("volume", result.Reason);
    }

    [Fact]
    public void VolumeCheckDisabled_ZeroVolumePasses()
    {
        var thresholds = new LiquidityThresholds { MinCandleQuoteVolume = 0m };
        var result = LiquidityFilter.Evaluate(Book(99.9m, 100.1m), OrderSide.Buy, 1m, 100m, 0m, thresholds);
        Assert.True(result.Passed);
    }

    // === Fail-closed: нет стакана ===

    [Fact]
    public void NoOrderBook_BlocksEntry()
    {
        var result = LiquidityFilter.Evaluate(null, OrderSide.Buy, 1m, 100m, 100_000m, Thresholds());
        Assert.False(result.Passed);
        Assert.Equal("no-book", result.Reason);
    }

    [Fact]
    public void EmptyOrderBook_BlocksEntry()
    {
        var empty = new OrderBook { Symbol = "BTCUSDT" };
        var result = LiquidityFilter.Evaluate(empty, OrderSide.Buy, 1m, 100m, 100_000m, Thresholds());
        Assert.False(result.Passed);
        Assert.Equal("no-book", result.Reason);
    }

    // === Точечные пороги symbols.json ===

    [Fact]
    public void SymbolOverride_StricterSpread_BlocksWhatGlobalAllows()
    {
        // Спред 0.3%: глобальный порог 0.35% пропускает, точечный 0.25% (BTTC/STRAX/FLR) — нет.
        var book = Book(99.85m, 100.15m);
        Assert.True(LiquidityFilter.Evaluate(book, OrderSide.Buy, 1m, 100m, 100_000m, Thresholds()).Passed);

        var strict = new LiquidityThresholds { MaxSpreadPercent = 0.25m };
        var result = LiquidityFilter.Evaluate(book, OrderSide.Buy, 1m, 100m, 100_000m, strict);
        Assert.False(result.Passed);
        Assert.Equal("spread", result.Reason);
    }

    [Fact]
    public void SymbolConfig_LiquidityOverride_Roundtrip_And_Validation()
    {
        var plain = new SymbolConfig { Symbol = "BTCUSDT", KnownQuotes = new[] { "USDT" } };
        plain.Validate();
        Assert.False(plain.HasLiquidityOverrides);
        Assert.Null(plain.ToLiquidityOverride());

        var thin = new SymbolConfig
        {
            Symbol = "BTTCUSDT",
            KnownQuotes = new[] { "USDT" },
            MaxSpreadPercent = 0.25m,
            MinDepthQuote = 2000m,
            MinCandleQuoteVolume = 25000m
        };
        thin.Validate();
        var ov = thin.ToLiquidityOverride();
        Assert.NotNull(ov);
        Assert.Equal(0.25m, ov!.MaxSpreadPercent);
        Assert.Equal(2000m, ov.MinDepthQuote);
        Assert.Equal(25000m, ov.MinCandleQuoteVolume);

        var bad = new SymbolConfig { Symbol = "BTTCUSDT", KnownQuotes = new[] { "USDT" }, MaxSpreadPercent = 0m };
        Assert.Throws<InvalidOperationException>(() => bad.Validate());

        var badVolume = new SymbolConfig { Symbol = "BTTCUSDT", KnownQuotes = new[] { "USDT" }, MinCandleQuoteVolume = -1m };
        Assert.Throws<InvalidOperationException>(() => badVolume.Validate());
    }

    // === Конфигурация ===

    private static TradingConfig ValidConfig() => new() { Symbols = new List<string> { "BTCUSDT" } };

    [Fact]
    public void TradingConfig_LiquidityDefaults_AreValid()
    {
        var config = ValidConfig();
        config.Validate();
        Assert.True(config.LiquidityFilterEnabled);
        Assert.Equal(0.35m, config.MaxEntrySpreadPercent);
        Assert.Equal(1000m, config.MinEntryDepthQuote);
        Assert.Equal(5m, config.MinDepthNotionalRatio);
        Assert.Equal(10, config.LiquidityDepthLevels);
        Assert.Equal(15000m, config.MinCandleQuoteVolume);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void TradingConfig_InvalidSpreadThreshold_Throws(double spread)
    {
        var config = ValidConfig();
        config.MaxEntrySpreadPercent = (decimal)spread;
        Assert.Throws<InvalidOperationException>(() => config.Validate());
    }

    [Fact]
    public void TradingConfig_InvalidDepthLevels_Throws()
    {
        var config = ValidConfig();
        config.LiquidityDepthLevels = 21; // partial-depth stream отдаёт только 20 уровней
        Assert.Throws<InvalidOperationException>(() => config.Validate());
    }

    // === Метрики ===

    [Fact]
    public void Metrics_LiquidityRejections_CountedByReason()
    {
        var metrics = new MetricsCollector();
        metrics.RecordLiquidityRejection("spread");
        metrics.RecordLiquidityRejection("spread");
        metrics.RecordLiquidityRejection("depth");
        metrics.RecordLiquidityRejection("volume");
        metrics.RecordLiquidityRejection("no-book");

        Assert.Equal(5, metrics.LiquidityRejections);
        Assert.Equal(2, metrics.LiquiditySpreadRejections);
        Assert.Equal(1, metrics.LiquidityDepthRejections);
        Assert.Equal(1, metrics.LiquidityVolumeRejections);
        Assert.Equal(1, metrics.LiquidityNoBookRejections);
        Assert.Contains("liquidity rejections 5", metrics.GetSummary());
    }

    [Fact]
    public void Metrics_PostOnlyRejections_Aggregated()
    {
        var metrics = new MetricsCollector();
        metrics.RecordPostOnlyRejection();
        metrics.RecordPostOnlyRejection();

        Assert.Equal(2, metrics.PostOnlyRejections);
        Assert.Contains("post-only rejections 2", metrics.GetSummary());
    }
}
