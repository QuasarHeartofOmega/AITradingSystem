using AITradingSystem.Core.Models.Enums;

namespace AITradingSystem.Core.Models.MarketData;

/// <summary>
/// P1.5: пороги фильтра ликвидности для одной пары.
/// Глобальные значения приходят из TradingConfig, точечные — из symbols.json
/// (BTTC/STRAX/FLR и прочие тонкие пары могут требовать более строгих порогов).
/// </summary>
public sealed class LiquidityThresholds
{
    /// <summary>Максимальный спред (BestAsk−BestBid)/Mid в процентах. 0.35 = 0.35%.</summary>
    public decimal MaxSpreadPercent { get; init; } = 0.35m;

    /// <summary>Абсолютный минимум суммарной глубины стороны входа в котировке (USDT).</summary>
    public decimal MinDepthQuote { get; init; } = 1000m;

    /// <summary>Глубина стороны входа должна быть ≥ этого множителя от нотариала ордера.</summary>
    public decimal MinDepthNotionalRatio { get; init; } = 5m;

    /// <summary>Сколько уровней стакана суммировать для глубины.</summary>
    public int DepthLevels { get; init; } = 10;

    /// <summary>
    /// Минимальный средний объём закрытой свечи в котировке (SMA20 объёма × цена).
    /// 0 — проверка объёма выключена для этой пары.
    /// </summary>
    public decimal MinCandleQuoteVolume { get; init; } = 15000m;
}

/// <summary>Результат проверки ликвидности: пройден/не пройден + причина и детали для лога.</summary>
public sealed class LiquidityCheckResult
{
    public bool Passed { get; private init; }

    /// <summary>Категория отказа: spread | depth | volume | no-book (пусто, если пройден).</summary>
    public string Reason { get; private init; } = string.Empty;

    public string Detail { get; private init; } = string.Empty;

    public static LiquidityCheckResult Pass() => new() { Passed = true };

    public static LiquidityCheckResult Fail(string reason, string detail) =>
        new() { Passed = false, Reason = reason, Detail = detail };
}

/// <summary>
/// P1.5: фильтр ликвидности перед входом — спред / глубина стороны входа / объём свечей.
/// Раньше широкий спред только логировался, вход не блокировался: на тонких парах
/// (BTTC/STRAX/FLR) бот платил спред и получал post-only-отказы.
///
/// Чистая функция без I/O: стакан уже пришёл по WS, объём — из калькулятора индикаторов.
/// Fail-closed: нет стакана → вход не разрешён (ликвидность не подтверждена);
/// протухшесть данных после реконнекта WS — отдельный пункт P1.4.
/// </summary>
public static class LiquidityFilter
{
    public static LiquidityCheckResult Evaluate(
        OrderBook? orderBook,
        OrderSide side,
        decimal quantity,
        decimal fallbackPrice,
        decimal avgCandleQuoteVolume,
        LiquidityThresholds thresholds)
    {
        if (quantity <= 0 || fallbackPrice <= 0)
            return LiquidityCheckResult.Fail("no-book", "нет корректного объёма/цены для оценки ликвидности");

        // 1. Объём свечей: пара должна торговаться, а не «висеть» пустой.
        if (thresholds.MinCandleQuoteVolume > 0m && avgCandleQuoteVolume < thresholds.MinCandleQuoteVolume)
            return LiquidityCheckResult.Fail("volume",
                $"средний объём свечи {avgCandleQuoteVolume:F0} < минимума {thresholds.MinCandleQuoteVolume:F0} (котировка)");

        // 2. Стакан обязателен: без него спред/глубина не подтверждены.
        if (orderBook is null || !orderBook.BestBid.HasValue || !orderBook.BestAsk.HasValue ||
            orderBook.MidPrice is null or <= 0)
            return LiquidityCheckResult.Fail("no-book", "нет стакана — ликвидность не подтверждена");

        // 3. Спред: слишком широкий рынок съедает R/R ещё до входа.
        var spreadPercent = (orderBook.Spread!.Value / orderBook.MidPrice!.Value) * 100m;
        if (spreadPercent > thresholds.MaxSpreadPercent)
            return LiquidityCheckResult.Fail("spread",
                $"спред {spreadPercent:F3}% > порога {thresholds.MaxSpreadPercent:F3}% " +
                $"(bid {orderBook.BestBid}, ask {orderBook.BestAsk})");

        // 4. Глубина стороны исполнения: BUY съедает asks, SELL — bids.
        //    Ориентир цены входа — исполнимая сторона стакана, не закрытие свечи.
        var levels = Math.Max(1, thresholds.DepthLevels);
        var entryDepth = side == OrderSide.Buy ? orderBook.AskDepth(levels) : orderBook.BidDepth(levels);
        var referencePrice = side == OrderSide.Buy ? orderBook.BestAsk!.Value : orderBook.BestBid!.Value;
        if (referencePrice <= 0)
            referencePrice = fallbackPrice;
        var notional = quantity * referencePrice;
        var requiredDepth = Math.Max(thresholds.MinDepthQuote, thresholds.MinDepthNotionalRatio * notional);

        if (entryDepth < requiredDepth)
            return LiquidityCheckResult.Fail("depth",
                $"глубина {(side == OrderSide.Buy ? "ask" : "bid")} {entryDepth:F2} < требуемой {requiredDepth:F2} " +
                $"(нотариал {notional:F2} × {thresholds.MinDepthNotionalRatio}, минимум {thresholds.MinDepthQuote:F0}, уровней {levels})");

        return LiquidityCheckResult.Pass();
    }
}
