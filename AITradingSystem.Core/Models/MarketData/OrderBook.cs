using AITradingSystem.Core.Models.Enums;

namespace AITradingSystem.Core.Models.MarketData;

/// <summary>
/// Снимок стакана ордеров (FR-1.3).
/// </summary>
public sealed class OrderBook
{
    public string Symbol { get; set; } = string.Empty;
    public List<OrderBookEntry> Bids { get; set; } = new();
    public List<OrderBookEntry> Asks { get; set; } = new();
    public DateTime Timestamp { get; set; }
    public long LastUpdateId { get; set; }

    /// <summary>
    /// Лучшая цена покупки.
    /// </summary>
    public decimal? BestBid => Bids.Count > 0 ? Bids[0].Price : null;

    /// <summary>
    /// Лучшая цена продажи.
    /// </summary>
    public decimal? BestAsk => Asks.Count > 0 ? Asks[0].Price : null;

    /// <summary>
    /// Spread между лучшим bid и ask.
    /// </summary>
    public decimal? Spread => (BestAsk.HasValue && BestBid.HasValue)
        ? BestAsk.Value - BestBid.Value
        : null;

    /// <summary>
    /// Mid-price — средняя цена между лучшим bid и ask.
    /// </summary>
    public decimal? MidPrice => (BestAsk.HasValue && BestBid.HasValue)
        ? (BestAsk.Value + BestBid.Value) / 2m
        : null;

    /// <summary>
    /// Суммарный объем bid на первых N уровнях.
    /// </summary>
    public decimal BidDepth(int levels = 10)
    {
        return Bids.Take(levels).Sum(b => b.Quantity * b.Price);
    }

    /// <summary>
    /// Суммарный объем ask на первых N уровнях.
    /// </summary>
    public decimal AskDepth(int levels = 10)
    {
        return Asks.Take(levels).Sum(a => a.Quantity * a.Price);
    }

    /// <summary>
    /// Соотношение давления покупателей/продавцов (bid/ask volume ratio).
    /// > 1 — давление покупателей, < 1 — давление продавцов.
    /// </summary>
    public decimal? OrderBookImbalance(int levels = 10)
    {
        var bidDepth = BidDepth(levels);
        var askDepth = AskDepth(levels);
        var total = bidDepth + askDepth;
        return total > 0 ? (bidDepth - askDepth) / total : null;
    }

    /// <summary>
    /// P1.1: пройти стакан как taker: BUY ест asks, SELL ест bids.
    /// worstPrice ограничивает цену (IOC-cap выхода); maxLevels=0 — все доступные уровни.
    /// Возвращает фактический объём, среднюю цену и проскальзывание от best bid/ask.
    /// </summary>
    public OrderBookFill SimulateMarketFill(
        OrderSide side,
        decimal quantity,
        decimal? worstPrice = null,
        int maxLevels = 0)
    {
        if (quantity <= 0m)
            return new OrderBookFill(0m, 0m, 0m, 0m, false);

        IEnumerable<OrderBookEntry> levels = side == OrderSide.Buy
            ? Asks.Where(l => l.Price > 0m && l.Quantity > 0m).OrderBy(l => l.Price)
            : Bids.Where(l => l.Price > 0m && l.Quantity > 0m).OrderByDescending(l => l.Price);

        if (maxLevels > 0)
            levels = levels.Take(maxLevels);

        var remaining = quantity;
        var filled = 0m;
        var quote = 0m;

        foreach (var level in levels)
        {
            if (worstPrice.HasValue)
            {
                if (side == OrderSide.Buy && level.Price > worstPrice.Value) break;
                if (side == OrderSide.Sell && level.Price < worstPrice.Value) break;
            }

            var take = Math.Min(remaining, level.Quantity);
            filled += take;
            quote += take * level.Price;
            remaining -= take;

            if (remaining <= 0m) break;
        }

        var average = filled > 0m ? quote / filled : 0m;
        var reference = side == OrderSide.Buy ? BestAsk : BestBid;
        var slippage = reference is > 0m && average > 0m
            ? Math.Abs(average - reference.Value) / reference.Value * 100m
            : 0m;

        return new OrderBookFill(filled, quote, average, slippage, remaining <= 0m);
    }
}

public sealed class OrderBookEntry
{
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
}

/// <summary>P1.1: результат симуляции taker-филла по стакану (paper).</summary>
public sealed record OrderBookFill(
    decimal FilledQuantity,
    decimal QuoteQuantity,
    decimal AveragePrice,
    decimal SlippagePercent,
    bool FullyFilled);
