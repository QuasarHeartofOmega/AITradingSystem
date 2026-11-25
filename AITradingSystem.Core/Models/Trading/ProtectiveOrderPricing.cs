using AITradingSystem.Core.Models.Enums;

namespace AITradingSystem.Core.Models.Trading;

/// <summary>
/// Цены и объём защитных ног OCO (P0.1 + P1.3 для стоп/тейк-ног).
///
/// Правила Binance для OCO (SELL — защита long):
///   TP.stopPrice (above) &gt; LastPrice &gt; SL.stopPrice (below)
/// Для BUY (защита short) — зеркально.
///
/// Здесь же округления tick/step и проверка minQty/minNotional для ОБЕИХ ног:
/// входная нога округлялась и раньше, защитные — нет (их не существовало).
/// </summary>
public static class ProtectiveOrderPricing
{
    /// <summary>Готовые к отправке на биржу параметры защитной связки.</summary>
    public sealed record Prices(
        decimal Quantity,
        decimal StopTrigger,
        decimal StopLimit,
        decimal TakeProfitTrigger,
        decimal TakeProfitLimit);

    /// <summary>
    /// Построить параметры OCO. false + <paramref name="error"/> — защита невозможна
    /// (например, цена уже ушла за стоп): вызывающий обязан закрыть позицию market'ом,
    /// а не оставлять её без биржевой защиты.
    /// </summary>
    /// <param name="positionSide">Сторона позиции (не защитных ордеров).</param>
    /// <param name="quantity">Объём позиции.</param>
    /// <param name="stopLoss">Желаемый триггер стопа.</param>
    /// <param name="takeProfit">Желаемый триггер тейка.</param>
    /// <param name="referencePrice">Текущая цена (last/mid) для проверки ограничений Binance.</param>
    /// <param name="tickSize">Шаг цены символа (0 — неизвестен).</param>
    /// <param name="stepSize">Шаг объёма символа (0 — неизвестен).</param>
    /// <param name="minQty">LOT_SIZE.minQty (0 — неизвестен).</param>
    /// <param name="minNotional">NOTIONAL.minNotional (0 — неизвестен).</param>
    /// <param name="limitSlippage">Отступ лимитной цены от триггера в долях (0.003 = 0.3%), чтобы нога налилась.</param>
    public static bool TryBuild(
        OrderSide positionSide,
        decimal quantity,
        decimal stopLoss,
        decimal takeProfit,
        decimal referencePrice,
        decimal tickSize,
        decimal stepSize,
        decimal minQty,
        decimal minNotional,
        decimal limitSlippage,
        out Prices? prices,
        out string? error)
    {
        prices = null;
        error = null;

        var isLong = positionSide == OrderSide.Buy;

        if (quantity <= 0) { error = "объём позиции <= 0"; return false; }
        if (stopLoss <= 0 || takeProfit <= 0) { error = "нет уровней SL/TP"; return false; }
        if (referencePrice <= 0) { error = "нет текущей цены для проверки ограничений OCO"; return false; }

        if (isLong && stopLoss >= referencePrice)
        {
            error = $"стоп {stopLoss} >= текущей цены {referencePrice}: OCO неприменим, нужен market-выход";
            return false;
        }
        if (!isLong && stopLoss <= referencePrice)
        {
            error = $"стоп {stopLoss} <= текущей цены {referencePrice}: OCO неприменим, нужен market-выход";
            return false;
        }
        if (isLong && takeProfit <= referencePrice)
        {
            error = $"тейк {takeProfit} <= текущей цены {referencePrice}: OCO неприменим";
            return false;
        }
        if (!isLong && takeProfit >= referencePrice)
        {
            error = $"тейк {takeProfit} >= текущей цены {referencePrice}: OCO неприменим";
            return false;
        }

        if (stepSize > 0)
            quantity = Math.Floor(quantity / stepSize) * stepSize;

        if (quantity <= 0 || (minQty > 0 && quantity < minQty))
        {
            error = $"объём защиты {quantity} < minQty {minQty}";
            return false;
        }

        limitSlippage = Math.Clamp(limitSlippage, 0m, 0.05m);

        // Лимитная цена отступает от триггера в сторону исполнения:
        // SELL — вниз (продаём дешевле триггера), BUY — вверх. Иначе нога может не налиться.
        var stopTrigger = RoundToTick(stopLoss, tickSize, roundUp: !isLong);
        var takeTrigger = RoundToTick(takeProfit, tickSize, roundUp: isLong);
        var stopLimit = RoundToTick(stopTrigger * (isLong ? 1m - limitSlippage : 1m + limitSlippage), tickSize, roundUp: !isLong);
        var takeLimit = RoundToTick(takeTrigger * (isLong ? 1m - limitSlippage : 1m + limitSlippage), tickSize, roundUp: !isLong);

        if (stopTrigger <= 0 || takeTrigger <= 0 || stopLimit <= 0 || takeLimit <= 0)
        {
            error = "после округления к tickSize цена защитной ноги <= 0";
            return false;
        }

        // Триггеры после округления не должны «переехать» текущую цену.
        if (isLong && (stopTrigger >= referencePrice || takeTrigger <= referencePrice))
        {
            error = $"после округления триггеры SL={stopTrigger}/TP={takeTrigger} нарушают правило OCO относительно цены {referencePrice}";
            return false;
        }
        if (!isLong && (stopTrigger <= referencePrice || takeTrigger >= referencePrice))
        {
            error = $"после округления триггеры SL={stopTrigger}/TP={takeTrigger} нарушают правило OCO относительно цены {referencePrice}";
            return false;
        }

        if (minNotional > 0)
        {
            var worstNotional = quantity * Math.Min(stopLimit, takeLimit);
            if (worstNotional < minNotional)
            {
                error = $"notional защиты {worstNotional:F2} < minNotional {minNotional}";
                return false;
            }
        }

        prices = new Prices(quantity, stopTrigger, stopLimit, takeTrigger, takeLimit);
        return true;
    }

    /// <summary>Округление к tickSize в указанную сторону (для SELL-ног — вниз, для BUY — вверх).</summary>
    public static decimal RoundToTick(decimal price, decimal tickSize, bool roundUp)
    {
        if (tickSize <= 0) return price;
        var steps = price / tickSize;
        return (roundUp ? Math.Ceiling(steps) : Math.Floor(steps)) * tickSize;
    }

    /// <summary>
    /// Достаточно ли улучшение стопа для cancel/replace связки на бирже.
    /// Порог в процентах от текущего стопа — иначе trailing дёргает биржу на каждом тике.
    /// </summary>
    public static bool IsStopImprovementSignificant(
        OrderSide positionSide, decimal currentStop, decimal candidateStop, decimal minStepPercent)
    {
        if (currentStop <= 0) return candidateStop > 0;

        var improved = positionSide == OrderSide.Buy
            ? candidateStop > currentStop
            : candidateStop < currentStop;

        if (!improved) return false;
        if (minStepPercent <= 0) return true;

        var changePercent = Math.Abs(candidateStop - currentStop) / currentStop * 100m;
        return changePercent >= minStepPercent;
    }
}
