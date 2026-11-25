using AITradingSystem.Common.Configuration;
using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.Trading;

namespace AITradingSystem.Core.RiskManagement;

/// <summary>
/// Расчет размера позиции на основе риска (FR-5.1).
/// </summary>
public sealed class PositionSizer
{
    private readonly TradingConfig _config;

    public PositionSizer(TradingConfig config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Рассчитать размер позиции.
    /// </summary>
    /// <param name="balance">Доступный баланс USDT.</param>
    /// <param name="entryPrice">Цена входа.</param>
    /// <param name="stopLoss">Цена Stop-Loss.</param>
    /// <param name="atr">Текущий ATR для валидации.</param>
    /// <returns>Размер позиции в базовом активе.</returns>
    public decimal CalculatePositionSize(
        decimal balance,
        decimal entryPrice,
        decimal stopLoss,
        decimal atr)
    {
        if (balance <= 0)
            throw new ArgumentException("Balance must be > 0", nameof(balance));
        if (entryPrice <= 0)
            throw new ArgumentException("EntryPrice must be > 0", nameof(entryPrice));
        if (stopLoss <= 0)
            throw new ArgumentException("StopLoss must be > 0", nameof(stopLoss));

        var riskAmount = balance * (decimal)_config.RiskPerTrade;
        var side = stopLoss < entryPrice ? OrderSide.Buy : OrderSide.Sell;
        var entryFee = TradeCostModel.EntryFeeRate(_config);
        var exitFee = TradeCostModel.ExitFeeRate(_config);

        // P1.2: риск на единицу включает комиссии входа и выхода, а не только |entry - SL|.
        var riskPerUnit = TradeCostModel.RiskPerUnit(side, entryPrice, stopLoss, entryFee, exitFee);
        if (riskPerUnit <= 0m)
            return 0;

        var quantity = riskAmount / riskPerUnit;

        // Проверка: не превышает ли позиция доступный баланс с учётом комиссии входа.
        var entryCostPerUnit = TradeCostModel.EffectiveEntryPrice(OrderSide.Buy, entryPrice, entryFee);
        var maxQuantity = entryCostPerUnit > 0m ? balance / entryCostPerUnit : 0m;
        quantity = Math.Min(quantity, maxQuantity);

        return quantity;
    }
}