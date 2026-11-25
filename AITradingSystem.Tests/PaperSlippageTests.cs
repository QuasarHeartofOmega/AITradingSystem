using AITradingSystem.Common.Configuration;
using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.MarketData;

namespace AITradingSystem.Tests;

/// <summary>P1.1: paper taker-филл по стакану, частичное исполнение и IOC-cap. Тесты не запускались в проходе 19.</summary>
public class PaperSlippageTests
{
    private static OrderBook Book() => new()
    {
        Symbol = "BTCUSDT",
        Bids = new List<OrderBookEntry>
        {
            new() { Price = 99m, Quantity = 1m },
            new() { Price = 98m, Quantity = 2m },
            new() { Price = 97m, Quantity = 5m }
        },
        Asks = new List<OrderBookEntry>
        {
            new() { Price = 101m, Quantity = 1m },
            new() { Price = 102m, Quantity = 2m },
            new() { Price = 103m, Quantity = 5m }
        }
    };

    [Fact]
    public void Buy_WalksAsks_AndComputesAverageAndSlippage()
    {
        var fill = Book().SimulateMarketFill(OrderSide.Buy, 2m);

        Assert.True(fill.FullyFilled);
        Assert.Equal(2m, fill.FilledQuantity);
        Assert.Equal(1m * 101m + 1m * 102m, fill.QuoteQuantity);
        Assert.Equal(101.5m, fill.AveragePrice);
        Assert.True(fill.SlippagePercent > 0m);
    }

    [Fact]
    public void Sell_WalksBids_Mirrored()
    {
        var fill = Book().SimulateMarketFill(OrderSide.Sell, 2m);

        Assert.True(fill.FullyFilled);
        Assert.Equal(2m, fill.FilledQuantity);
        Assert.Equal(1m * 99m + 1m * 98m, fill.QuoteQuantity);
        Assert.Equal(98.5m, fill.AveragePrice);
        Assert.True(fill.SlippagePercent > 0m);
    }

    [Fact]
    public void InsufficientDepth_ReturnsPartialFill()
    {
        var fill = Book().SimulateMarketFill(OrderSide.Buy, 20m);

        Assert.False(fill.FullyFilled);
        Assert.Equal(8m, fill.FilledQuantity);
        Assert.True(fill.AveragePrice > 101m);
    }

    [Fact]
    public void WorstPrice_CapsExitLikeIoc()
    {
        // SELL cap 98.5: можно съесть только bid 99 (1 шт), bid 98 уже хуже cap.
        var fill = Book().SimulateMarketFill(OrderSide.Sell, 3m, worstPrice: 98.5m);

        Assert.False(fill.FullyFilled);
        Assert.Equal(1m, fill.FilledQuantity);
        Assert.Equal(99m, fill.AveragePrice);
    }

    [Fact]
    public void MaxLevels_LimitsWalk()
    {
        var fill = Book().SimulateMarketFill(OrderSide.Buy, 5m, maxLevels: 1);

        Assert.False(fill.FullyFilled);
        Assert.Equal(1m, fill.FilledQuantity);
        Assert.Equal(101m, fill.AveragePrice);
    }

    [Fact]
    public void EmptyBook_ReturnsNoFill()
    {
        var empty = new OrderBook { Symbol = "BTCUSDT" };
        var fill = empty.SimulateMarketFill(OrderSide.Buy, 1m);

        Assert.False(fill.FullyFilled);
        Assert.Equal(0m, fill.FilledQuantity);
        Assert.Equal(0m, fill.AveragePrice);
    }

    [Fact]
    public void Config_RejectsInvalidPaperSlippageLevels()
    {
        var config = new TradingConfig
        {
            Symbols = new List<string> { "BTCUSDT" },
            PaperSlippageMaxLevels = -1
        };

        Assert.Throws<InvalidOperationException>(() => config.Validate());
    }
}
