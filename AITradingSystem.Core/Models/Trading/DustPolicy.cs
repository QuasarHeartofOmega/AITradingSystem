using AITradingSystem.Core.Models.Enums;

namespace AITradingSystem.Core.Models.Trading;

/// <summary>
/// P1.3: dust и аудит остатка после отмены.
///
/// «Dust» — остаток объёма, который невозможно выставить обычным ордером:
/// он меньше stepSize (округляется в ноль), либо ниже LOT_SIZE.minQty / MIN_NOTIONAL.
/// Такой остаток возникает при округлении защиты к stepSize («хвост» OCO),
/// после частичных филлов и при подхвате сиротских балансов (P0.5).
///
/// Обычный спотовый ордер не получает «full-balance exemption»: MARKET SELL ниже
/// LOT_SIZE/MARKET_LOT_SIZE или применимого MIN_NOTIONAL/NOTIONAL будет отклонён.
/// Такой остаток нельзя автоматически закрыть через /api/v3/order; он помечается для
/// ручного аудита/конвертации пыли, а не отправляется в бесконечный цикл отказов.
///
/// Класс чистый (без I/O) — решения тестируются без сети и биржи.
/// </summary>
public static class DustPolicy
{
    /// <summary>Маршрут закрытия позиции/остатка.</summary>
    public enum DustCloseRoute
    {
        /// <summary>Обычный выход P1.2: marketable LIMIT IOC с cap проскальзывания.</summary>
        IocLimit = 0,

        /// <summary>MARKET без cap (аварийный выход или выключенный slippage-guard).</summary>
        Market = 1,

        /// <summary>Legacy-маршрут; больше не используется: ordinary MARKET не обходит фильтры.</summary>
        DustMarketSell = 2,

        /// <summary>Закрытие невозможно — остаток слишком мал/сторона не поддерживает dust-маршрут.</summary>
        Reject = 3
    }

    /// <summary>
    /// Решение по закрытию: маршрут, объём к отправке и причина отказа.
    /// Dust-остаток ниже фильтров возвращается как <see cref="DustCloseRoute.Reject"/>;
    /// автоматическая отправка заведомо невалидного MARKET запрещена.
    /// </summary>
    public sealed record ClosePlan(DustCloseRoute Route, decimal Quantity, string? Reason);

    /// <summary>Результат аудита «отменённый остаток vs фактический филл».</summary>
    /// <param name="Mismatch">true — ордер не дошёл до терминального статуса после отмены
    /// либо объёмы не сходятся (остаток может оставаться живым на бирже).</param>
    /// <param name="Executed">Фактически исполненный объём (>= 0).</param>
    /// <param name="Residual">Отменённый остаток (>= 0); при mismatch — последняя известная оценка.</param>
    /// <param name="Detail">Описание расхождения (null при чистом аудите).</param>
    public sealed record CancelAudit(bool Mismatch, decimal Executed, decimal Residual, string? Detail);

    /// <summary>Округление объёма вниз к stepSize (0 — шаг неизвестен, объём не меняется).</summary>
    public static decimal RoundDownToStep(decimal quantity, decimal stepSize)
    {
        if (stepSize <= 0m) return quantity;
        return Math.Floor(quantity / stepSize) * stepSize;
    }

    /// <summary>
    /// Выбрать маршрут закрытия остатка с учётом фильтров символа.
    /// </summary>
    /// <param name="closeSide">Сторона закрывающего ордера (SELL — закрытие long).</param>
    /// <param name="positionQuantity">Текущий объём позиции/остатка (точный, как в учёте).</param>
    /// <param name="referencePrice">Исполнимая цена для оценки нотариала (bid для SELL / ask для BUY).</param>
    /// <param name="stepSize">Шаг объёма символа (0 — неизвестен).</param>
    /// <param name="minQty">LOT_SIZE.minQty (0 — неизвестен).</param>
    /// <param name="minNotional">MIN_NOTIONAL (0 — неизвестен).</param>
    /// <param name="iocEnabled">Разрешён ли обычный выход marketable LIMIT IOC (P1.2 cap).</param>
    /// <param name="dustMarketCloseEnabled">Trading:DustMarketCloseEnabled — разрешён ли dust MARKET SELL.</param>
    public static ClosePlan PlanClose(
        OrderSide closeSide,
        decimal positionQuantity,
        decimal referencePrice,
        decimal stepSize,
        decimal minQty,
        decimal minNotional,
        bool iocEnabled,
        bool dustMarketCloseEnabled,
        decimal marketStepSize = 0m,
        decimal marketMinQty = 0m,
        bool applyMinNotionalToMarket = true)
    {
        if (positionQuantity <= 0m)
            return new ClosePlan(DustCloseRoute.Reject, positionQuantity, "объём позиции <= 0");

        // LIMIT/OCO используют LOT_SIZE; MARKET — MARKET_LOT_SIZE, если он задан.
        var effectiveStep = iocEnabled ? stepSize : (marketStepSize > 0m ? marketStepSize : stepSize);
        var effectiveMinQty = iocEnabled ? minQty : (marketMinQty > 0m ? marketMinQty : minQty);
        var rounded = RoundDownToStep(positionQuantity, effectiveStep);
        var notionalApplies = iocEnabled || applyMinNotionalToMarket;
        var isDust = rounded <= 0m
            || (effectiveMinQty > 0m && rounded < effectiveMinQty)
            || (notionalApplies && minNotional > 0m && referencePrice > 0m && rounded * referencePrice < minNotional);

        if (isDust)
        {
            return new ClosePlan(
                DustCloseRoute.Reject, Math.Max(rounded, 0m),
                $"dust: остаток {positionQuantity} ниже фильтров {(iocEnabled ? "LOT_SIZE" : "MARKET_LOT_SIZE")}/minNotional — ordinary MARKET не обходит фильтры; нужен ручной аудит/конвертация пыли");
        }

        return iocEnabled
            ? new ClosePlan(DustCloseRoute.IocLimit, rounded, null)
            : new ClosePlan(DustCloseRoute.Market, rounded, null);
    }

    /// <summary>
    /// P1.3: классификация результата отмены неисполненного остатка.
    /// Чистый аудит: терминальный статус + executed + residual == original.
    /// Нестабильный случай — статус НЕ терминальный после cancel (отмена не удалась
    /// или биржа не отдала финальное состояние): остаток может продолжать наливаться,
    /// а позиция уже открыта на ExecutedQuantity — расхождение ловит реконсиляция P0.5,
    /// но факт обязан попасть в метрики и лог.
    /// </summary>
    public static CancelAudit ClassifyCancelResidual(
        OrderStatus status, decimal executedQuantity, decimal originalQuantity)
    {
        var terminal = status is OrderStatus.Filled or OrderStatus.Canceled
            or OrderStatus.Rejected or OrderStatus.Expired;
        var residual = originalQuantity - executedQuantity;

        if (!terminal || executedQuantity < 0m || residual < 0m)
            return new CancelAudit(
                true,
                Math.Max(executedQuantity, 0m),
                Math.Max(residual, 0m),
                $"ордер не дошёл до терминального статуса после отмены (status={status}) либо объёмы не сходятся (executed={executedQuantity}, original={originalQuantity})");

        return new CancelAudit(false, executedQuantity, residual, null);
    }

    /// <summary>
    /// P1.3: «хвост» защиты — насколько объём OCO (после округления к stepSize)
    /// меньше объёма позиции. Хвост не защищён биржевыми ногами; после срабатывания
    /// ноги он становится dust-остатком и уходит в ручной аудит, а не в невалидный MARKET.
    /// </summary>
    public static decimal ProtectionTail(decimal positionQuantity, decimal protectionQuantity)
    {
        var tail = positionQuantity - protectionQuantity;
        return tail > 0m ? tail : 0m;
    }
}
