using AITradingSystem.Common.Configuration;
using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.Trading;

namespace AITradingSystem.Tests;

/// <summary>P1.3: dust-маршруты закрытия, аудит остатка после отмены, «хвост» защиты. Тесты не запускались в проходе 20.</summary>
public class DustPolicyTests
{
    // === PlanClose: обычные маршруты ===

    [Fact]
    public void PlanClose_NormalQuantity_IocEnabled_RoundsToStep_AndChoosesIocLimit()
    {
        var plan = DustPolicy.PlanClose(
            OrderSide.Sell, positionQuantity: 10.55m, referencePrice: 100m,
            stepSize: 0.1m, minQty: 0.01m, minNotional: 5m,
            iocEnabled: true, dustMarketCloseEnabled: true);

        Assert.Equal(DustPolicy.DustCloseRoute.IocLimit, plan.Route);
        Assert.Equal(10.5m, plan.Quantity); // округление ВНИЗ к stepSize
        Assert.Null(plan.Reason);
    }

    [Fact]
    public void PlanClose_NormalQuantity_IocDisabled_ChoosesMarket()
    {
        var plan = DustPolicy.PlanClose(
            OrderSide.Sell, 10.55m, 100m, 0.1m, 0.01m, 5m,
            iocEnabled: false, dustMarketCloseEnabled: true);

        Assert.Equal(DustPolicy.DustCloseRoute.Market, plan.Route);
        Assert.Equal(10.5m, plan.Quantity);
    }

    [Fact]
    public void PlanClose_ZeroQuantity_Rejects()
    {
        var plan = DustPolicy.PlanClose(
            OrderSide.Sell, 0m, 100m, 0.1m, 0.01m, 5m, true, true);

        Assert.Equal(DustPolicy.DustCloseRoute.Reject, plan.Route);
    }

    // === PlanClose: dust-маршруты ===

    [Fact]
    public void PlanClose_BelowStepSize_SellDust_Rejects_NoFilterExemption()
    {
        // 0.04 при stepSize 0.1 округляется в ноль — ordinary MARKET тоже будет отклонён.
        var plan = DustPolicy.PlanClose(
            OrderSide.Sell, positionQuantity: 0.04m, referencePrice: 100m,
            stepSize: 0.1m, minQty: 0.01m, minNotional: 5m,
            iocEnabled: true, dustMarketCloseEnabled: true);

        Assert.Equal(DustPolicy.DustCloseRoute.Reject, plan.Route);
        Assert.Contains("ручной аудит", plan.Reason);
    }

    [Fact]
    public void PlanClose_BelowStepSize_DustDisabled_Rejects()
    {
        var plan = DustPolicy.PlanClose(
            OrderSide.Sell, 0.04m, 100m, 0.1m, 0.01m, 5m,
            iocEnabled: true, dustMarketCloseEnabled: false);

        Assert.Equal(DustPolicy.DustCloseRoute.Reject, plan.Route);
        Assert.Contains("DustMarketCloseEnabled", plan.Reason);
    }

    [Fact]
    public void PlanClose_BelowMinQty_SellDust_Rejects()
    {
        // 0.5 кратна stepSize 0.1, но ниже minQty 1 — LIMIT/обычный MARKET будут отклонены.
        var plan = DustPolicy.PlanClose(
            OrderSide.Sell, positionQuantity: 0.5m, referencePrice: 100m,
            stepSize: 0.1m, minQty: 1m, minNotional: 5m,
            iocEnabled: true, dustMarketCloseEnabled: true);

        Assert.Equal(DustPolicy.DustCloseRoute.Reject, plan.Route);
    }

    [Fact]
    public void PlanClose_BelowMinNotional_SellDust_Rejects()
    {
        // Объём формально валиден (2 >= minQty 1), но нотариал 2×2=4 < minNotional 5.
        var plan = DustPolicy.PlanClose(
            OrderSide.Sell, positionQuantity: 2m, referencePrice: 2m,
            stepSize: 0.1m, minQty: 1m, minNotional: 5m,
            iocEnabled: true, dustMarketCloseEnabled: true);

        Assert.Equal(DustPolicy.DustCloseRoute.Reject, plan.Route);
    }

    [Fact]
    public void PlanClose_BuySideDust_Rejects_NoShortCoverExemption()
    {
        // Покрытие шорта dust-объёмом: full-balance exemption на споте только для SELL.
        var plan = DustPolicy.PlanClose(
            OrderSide.Buy, positionQuantity: 0.04m, referencePrice: 100m,
            stepSize: 0.1m, minQty: 0.01m, minNotional: 5m,
            iocEnabled: true, dustMarketCloseEnabled: true);

        Assert.Equal(DustPolicy.DustCloseRoute.Reject, plan.Route);
        Assert.Contains("ручной аудит", plan.Reason);
    }

    [Fact]
    public void PlanClose_UnknownFilters_NoRounding_NormalRoute()
    {
        // Фильтры недоступны (stepSize/minQty/minNotional = 0) — поведение как до P1.3: без dust-ветки.
        var plan = DustPolicy.PlanClose(
            OrderSide.Sell, 10.55m, 100m, 0m, 0m, 0m, true, true);

        Assert.Equal(DustPolicy.DustCloseRoute.IocLimit, plan.Route);
        Assert.Equal(10.55m, plan.Quantity);
    }

    [Fact]
    public void PlanClose_XrpDust_WithMarketFilters_StillRejectsBelowMinNotional()
    {
        var plan = DustPolicy.PlanClose(
            OrderSide.Sell, 0.0247m, 0.954m, 0.1m, 0.1m, 5m,
            iocEnabled: false, dustMarketCloseEnabled: true,
            marketStepSize: 0m, marketMinQty: 0m, applyMinNotionalToMarket: true);
        Assert.Equal(DustPolicy.DustCloseRoute.Reject, plan.Route);
        Assert.Contains("MARKET_LOT_SIZE", plan.Reason);
    }

    // === ClassifyCancelResidual: аудит «отменённый остаток vs фактический филл» ===

    [Fact]
    public void ClassifyCancelResidual_TerminalWithResidual_Clean()
    {
        var audit = DustPolicy.ClassifyCancelResidual(OrderStatus.Canceled, executedQuantity: 3m, originalQuantity: 10m);

        Assert.False(audit.Mismatch);
        Assert.Equal(3m, audit.Executed);
        Assert.Equal(7m, audit.Residual);
        Assert.Null(audit.Detail);
    }

    [Fact]
    public void ClassifyCancelResidual_FilledExactly_CleanZeroResidual()
    {
        var audit = DustPolicy.ClassifyCancelResidual(OrderStatus.Filled, 10m, 10m);

        Assert.False(audit.Mismatch);
        Assert.Equal(0m, audit.Residual);
    }

    [Fact]
    public void ClassifyCancelResidual_NonTerminalAfterCancel_Mismatch()
    {
        // Отмена не прошла / финальный статус не получен — остаток мог остаться живым на бирже.
        var audit = DustPolicy.ClassifyCancelResidual(OrderStatus.PartiallyFilled, 3m, 10m);

        Assert.True(audit.Mismatch);
        Assert.Equal(3m, audit.Executed);
        Assert.Equal(7m, audit.Residual);
        Assert.NotNull(audit.Detail);
    }

    [Fact]
    public void ClassifyCancelResidual_NewAfterCancel_Mismatch()
    {
        var audit = DustPolicy.ClassifyCancelResidual(OrderStatus.New, 0m, 10m);

        Assert.True(audit.Mismatch);
    }

    [Fact]
    public void ClassifyCancelResidual_ExecutedOverOriginal_Mismatch()
    {
        var audit = DustPolicy.ClassifyCancelResidual(OrderStatus.Canceled, 11m, 10m);

        Assert.True(audit.Mismatch);
        Assert.Equal(11m, audit.Executed);
        Assert.Equal(0m, audit.Residual); // отрицательный остаток не отдаётся наружу
    }

    // === ProtectionTail: «хвост» защиты ===

    [Fact]
    public void ProtectionTail_ComputesPositiveTail_AndClampsAtZero()
    {
        Assert.Equal(0.05m, DustPolicy.ProtectionTail(10.05m, 10m));
        Assert.Equal(0m, DustPolicy.ProtectionTail(10m, 10m));
        Assert.Equal(0m, DustPolicy.ProtectionTail(10m, 10.5m)); // защита больше позиции — хвоста нет
    }

    [Fact]
    public void ProtectiveOrderPricing_RoundedQuantity_LeavesTailForDustRoute()
    {
        // Интеграция P0.1 + P1.3: объём 10.9 при stepSize 1 округляется до 10 —
        // хвост 0.9 не покрыт OCO и после срабатывания ноги станет dust-остатком.
        var built = ProtectiveOrderPricing.TryBuild(
            OrderSide.Buy, quantity: 10.9m, stopLoss: 90m, takeProfit: 110m, referencePrice: 100m,
            tickSize: 0.01m, stepSize: 1m, minQty: 0.001m, minNotional: 5m,
            limitSlippage: 0.003m, out var prices, out var error);

        Assert.True(built, error);
        Assert.NotNull(prices);
        Assert.Equal(10m, prices!.Quantity);
        Assert.Equal(0.9m, DustPolicy.ProtectionTail(10.9m, prices.Quantity));
    }

    // === Конфигурация ===

    [Fact]
    public void TradingConfig_DustMarketCloseEnabled_DefaultTrue()
    {
        Assert.True(new TradingConfig().DustMarketCloseEnabled);
    }
}
