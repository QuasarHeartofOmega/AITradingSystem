namespace AITradingSystem.Core.Models.Trading;

/// <summary>Что делать с объёмом входа после проверки биржевых фильтров.</summary>
public enum EntryFilterRoute
{
    /// <summary>Объём проходит фильтры (возможно, после округления вниз к шагу).</summary>
    Pass,

    /// <summary>Объём поднят до минимума биржи в пределах разрешённого превышения риск-сайзинга.</summary>
    Bump,

    /// <summary>Объём ниже минимума биржи и поднять его нельзя — вход пропускается.</summary>
    Reject
}

/// <param name="Quantity">Объём, приведённый к сетке шага (0 при Reject).</param>
/// <param name="Reason">Человекочитаемая причина для лога (null при Pass).</param>
public readonly record struct EntryFilterPlan(EntryFilterRoute Route, decimal Quantity, string? Reason);

/// <summary>
/// Минимальная сумма ордера Binance (фильтр NOTIONAL, на старых площадках MIN_NOTIONAL)
/// и лоты символа — единая точка правды для сайзинга входа и финальной валидации ордера.
///
/// Зачем отдельно от <see cref="DustPolicy"/>: DustPolicy решает, чем закрывать остаток,
/// а здесь решается, стоит ли вообще открывать позицию и какого объёма.
///
/// Ключевые правила:
///   • MARKET торгуется по MARKET_LOT_SIZE, LIMIT — по LOT_SIZE;
///   • NOTIONAL для MARKET применяется только при applyMinToMarket;
///   • минимум берётся с буфером на проскальзывание: биржа считает нотариал по средней
///     цене исполнения, а не по mid, поэтому ордер ровно «в 5.00 USDT» может отклониться;
///   • объём никогда не уменьшается ниже минимума молча — либо подъём до минимума
///     в пределах лимита, либо явный отказ.
/// </summary>
public static class EntryFilterPolicy
{
    public static decimal RoundDownToStep(decimal quantity, decimal step) =>
        step > 0m ? Math.Floor(quantity / step) * step : quantity;

    public static decimal RoundUpToStep(decimal quantity, decimal step) =>
        step > 0m ? Math.Ceiling(quantity / step) * step : quantity;

    /// <summary>minNotional с запасом на проскальзывание/округление цены.</summary>
    public static decimal BufferedMinNotional(decimal minNotional, decimal bufferPercent) =>
        minNotional > 0m && bufferPercent > 0m
            ? minNotional * (1m + bufferPercent / 100m)
            : minNotional;

    /// <summary>
    /// Привести объём входа к фильтрам символа.
    /// </summary>
    /// <param name="quantity">Объём из риск-сайзинга (уже ограниченный деньгами и лимитами).</param>
    /// <param name="referencePrice">Цена для оценки нотариала: mid/ask для MARKET, лимитная для LIMIT.</param>
    /// <param name="market">Ордер MARKET (иначе marketable LIMIT).</param>
    /// <param name="lotStepSize">LOT_SIZE.stepSize.</param>
    /// <param name="lotMinQty">LOT_SIZE.minQty.</param>
    /// <param name="marketStepSize">MARKET_LOT_SIZE.stepSize (0 — не задан).</param>
    /// <param name="marketMinQty">MARKET_LOT_SIZE.minQty (0 — не задан).</param>
    /// <param name="minNotional">NOTIONAL.minNotional / MIN_NOTIONAL.minNotional (0 — неизвестен).</param>
    /// <param name="applyMinNotionalToMarket">NOTIONAL.applyMinToMarket.</param>
    /// <param name="bufferPercent">Trading:MinNotionalBufferPercent.</param>
    /// <param name="bumpMaxPercent">
    /// Trading:MinNotionalBumpMaxPercent — на сколько процентов разрешено превысить риск-сайзинг
    /// ради минимума биржи. 0 — не увеличивать никогда (финальная валидация ордера).
    /// </param>
    /// <param name="quantityCeiling">
    /// Жёсткий потолок объёма: свободные деньги, лимит пары. Подъём выше него запрещён
    /// независимо от bumpMaxPercent (0 — потолок не задан).
    /// </param>
    public static EntryFilterPlan PlanEntry(
        decimal quantity,
        decimal referencePrice,
        bool market,
        decimal lotStepSize,
        decimal lotMinQty,
        decimal marketStepSize,
        decimal marketMinQty,
        decimal minNotional,
        bool applyMinNotionalToMarket,
        decimal bufferPercent,
        decimal bumpMaxPercent,
        decimal quantityCeiling)
    {
        if (quantity <= 0m)
            return new EntryFilterPlan(EntryFilterRoute.Reject, 0m, "объём <= 0");

        var step = market && marketStepSize > 0m ? marketStepSize : lotStepSize;
        var minQty = market && marketMinQty > 0m ? marketMinQty : lotMinQty;
        var grid = market ? "MARKET_LOT_SIZE" : "LOT_SIZE";

        var rounded = RoundDownToStep(quantity, step);
        if (rounded <= 0m)
            return new EntryFilterPlan(EntryFilterRoute.Reject, 0m,
                $"объём {quantity} после округления к {grid}.stepSize {step} = 0");

        var required = market && !applyMinNotionalToMarket ? 0m : BufferedMinNotional(minNotional, bufferPercent);
        var byNotional = required > 0m && referencePrice > 0m ? required / referencePrice : 0m;
        var floorQuantity = Math.Max(minQty, byNotional);

        if (rounded >= floorQuantity)
            return new EntryFilterPlan(EntryFilterRoute.Pass, rounded, null);

        // Ниже минимума: нужен подъём до сетки шага, но только в пределах разрешённого риска и денег.
        var target = RoundUpToStep(floorQuantity, step);
        var bumpLimit = quantity * (1m + Math.Max(bumpMaxPercent, 0m) / 100m);
        var ceiling = quantityCeiling > 0m ? Math.Min(quantityCeiling, bumpLimit) : bumpLimit;

        if (bumpMaxPercent > 0m && referencePrice > 0m && target > 0m && target <= ceiling)
            return new EntryFilterPlan(EntryFilterRoute.Bump, target,
                $"объём поднят до минимума биржи: {rounded} → {target} " +
                $"(notional {target * referencePrice:F2} >= {required:F2} = minNotional {minNotional} + буфер {bufferPercent}%)");

        var notionalText = referencePrice > 0m ? $"{rounded * referencePrice:F2}" : "n/a";
        return new EntryFilterPlan(EntryFilterRoute.Reject, rounded,
            $"notional {notionalText} < минимума {required:F2} (minNotional {minNotional} + буфер {bufferPercent}%), " +
            $"minQty {minQty} по {grid}; нужно >= {target}, сайзинг дал {rounded}, потолок подъёма {ceiling:F8}");
    }
}
