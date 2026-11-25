using AITradingSystem.Common.Configuration;
using AITradingSystem.Core.Models.Enums;

namespace AITradingSystem.Core.RiskManagement;

/// <summary>
/// P1.2: единая денежная математика сделки с комиссиями обеих ног.
/// Все цены — в котировке на единицу базового актива; комиссии — доли нотариала.
/// Long: вход увеличивает себестоимость, выход уменьшает выручку.
/// Short: зеркально (вход уменьшает выручку, выход увеличивает стоимость закрытия).
/// </summary>
public static class TradeCostModel
{
    public static decimal EntryFeeRate(TradingConfig config) =>
        config.PaperTradingMode
            ? config.PaperCommissionRate
            : config.UseMarketEntry ? config.TakerCommissionRate : config.MakerCommissionRate;

    public static decimal ExitFeeRate(TradingConfig config) =>
        config.PaperTradingMode ? config.PaperCommissionRate : config.TakerCommissionRate;

    /// <summary>Себестоимость входа long / чистая выручка входа short на единицу.</summary>
    public static decimal EffectiveEntryPrice(OrderSide side, decimal entryPrice, decimal entryFeeRate) =>
        side == OrderSide.Buy
            ? entryPrice * (1m + entryFeeRate)
            : entryPrice * (1m - entryFeeRate);

    /// <summary>Чистая выручка выхода long / стоимость выхода short на единицу.</summary>
    public static decimal EffectiveExitPrice(OrderSide side, decimal exitPrice, decimal exitFeeRate) =>
        side == OrderSide.Buy
            ? exitPrice * (1m - exitFeeRate)
            : exitPrice * (1m + exitFeeRate);

    public static decimal RiskPerUnit(
        OrderSide side, decimal entryPrice, decimal stopLoss, decimal entryFeeRate, decimal exitFeeRate)
    {
        var entry = EffectiveEntryPrice(side, entryPrice, entryFeeRate);
        var stop = EffectiveExitPrice(side, stopLoss, exitFeeRate);
        return side == OrderSide.Buy ? entry - stop : stop - entry;
    }

    public static decimal RewardPerUnit(
        OrderSide side, decimal entryPrice, decimal takeProfit, decimal entryFeeRate, decimal exitFeeRate)
    {
        var entry = EffectiveEntryPrice(side, entryPrice, entryFeeRate);
        var target = EffectiveExitPrice(side, takeProfit, exitFeeRate);
        return side == OrderSide.Buy ? target - entry : entry - target;
    }

    /// <summary>Net R/R после комиссий входа и выхода. null, если риск не положителен.</summary>
    public static decimal? NetRiskReward(
        OrderSide side, decimal entryPrice, decimal stopLoss, decimal takeProfit,
        decimal entryFeeRate, decimal exitFeeRate)
    {
        var risk = RiskPerUnit(side, entryPrice, stopLoss, entryFeeRate, exitFeeRate);
        if (risk <= 0m) return null;
        return RewardPerUnit(side, entryPrice, takeProfit, entryFeeRate, exitFeeRate) / risk;
    }

    /// <summary>TP, дающий заданный net R/R после комиссий. Возвращает 0, если риск не положителен.</summary>
    public static decimal TakeProfitForNetRiskReward(
        OrderSide side, decimal entryPrice, decimal stopLoss, decimal targetNetRiskReward,
        decimal entryFeeRate, decimal exitFeeRate)
    {
        var risk = RiskPerUnit(side, entryPrice, stopLoss, entryFeeRate, exitFeeRate);
        if (risk <= 0m) return 0m;

        var entry = EffectiveEntryPrice(side, entryPrice, entryFeeRate);
        var target = side == OrderSide.Buy
            ? entry + targetNetRiskReward * risk
            : entry - targetNetRiskReward * risk;

        return side == OrderSide.Buy
            ? target / (1m - exitFeeRate)
            : target / (1m + exitFeeRate);
    }

    /// <summary>Цена выхода, при которой net PnL сделки = 0 после комиссий обеих ног.</summary>
    public static decimal BreakEvenPrice(OrderSide side, decimal entryPrice, decimal entryFeeRate, decimal exitFeeRate)
    {
        var entry = EffectiveEntryPrice(side, entryPrice, entryFeeRate);
        return side == OrderSide.Buy
            ? entry / (1m - exitFeeRate)
            : entry / (1m + exitFeeRate);
    }

    /// <summary>Net PnL на единицу при фактической цене выхода (без модели проскальзывания).</summary>
    public static decimal NetPnlPerUnit(
        OrderSide side, decimal entryPrice, decimal exitPrice, decimal entryFeeRate, decimal exitFeeRate)
    {
        var entry = EffectiveEntryPrice(side, entryPrice, entryFeeRate);
        var exit = EffectiveExitPrice(side, exitPrice, exitFeeRate);
        return side == OrderSide.Buy ? exit - entry : entry - exit;
    }
}
