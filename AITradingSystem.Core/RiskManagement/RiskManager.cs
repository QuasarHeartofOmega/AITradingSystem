using AITradingSystem.Common.Configuration;
using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.Trading;

namespace AITradingSystem.Core.RiskManagement;

/// <summary>
/// Центральный менеджер рисков (FR-5).
/// </summary>
public sealed class RiskManager
{
    private readonly TradingConfig _config;
    private readonly PositionSizer _positionSizer;
    private readonly DrawdownTracker _drawdownTracker;
    private readonly PortfolioRiskManager? _portfolioRisk;

    public DrawdownTracker DrawdownTracker => _drawdownTracker;

    /// <summary>P1.7: портфельный контур (опционально для совместимости старых вызовов).</summary>
    public PortfolioRiskManager? PortfolioRisk => _portfolioRisk;

    /// <summary>
    /// P0.6: условие авто-kill-switch — лимит просадки/дня пробит и автозакрытие включено.
    /// Решение «закрывать ли всё» принимает движок; здесь только чистый предикат (покрыт тестом).
    /// </summary>
    public bool ShouldEngageKillSwitch =>
        _config.KillSwitchAutoCloseOnRiskBreach && !_drawdownTracker.IsTradingAllowed;

    public RiskManager(TradingConfig config, decimal initialBalance, Func<DateTime>? exchangeUtcNow = null, PortfolioRiskManager? portfolioRisk = null)
    {
        _portfolioRisk = portfolioRisk;
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _positionSizer = new PositionSizer(config);
        // P0.4: трекер создаётся здесь один раз на процесс; состояние (пик/день)
        // восстанавливается через DrawdownTracker.RestoreState в TradingEngine.StartAsync.
        _drawdownTracker = new DrawdownTracker(config, initialBalance, exchangeUtcNow);
    }

    /// <summary>
    /// Рассчитать SL на основе ATR (FR-5.3).
    /// </summary>
    public decimal CalculateStopLoss(OrderSide side, decimal entryPrice, decimal atr)
    {
        var distance = atr * (decimal)_config.StopLossAtrMultiplier;
        return side == OrderSide.Buy
            ? entryPrice - distance
            : entryPrice + distance;
    }

    /// <summary>
    /// Рассчитать TP на основе Risk/Reward (FR-5.4).
    /// </summary>
    public decimal CalculateTakeProfit(OrderSide side, decimal entryPrice, decimal stopLoss)
    {
        // P1.2: TP решает уравнение net R/R после комиссий обеих ног.
        return TradeCostModel.TakeProfitForNetRiskReward(
            side, entryPrice, stopLoss, (decimal)_config.MinRiskRewardRatio,
            TradeCostModel.EntryFeeRate(_config), TradeCostModel.ExitFeeRate(_config));
    }

    /// <summary>P1.2: net R/R сигнала после комиссий входа и выхода.</summary>
    public decimal? CalculateNetRiskReward(OrderSide side, decimal entryPrice, decimal stopLoss, decimal takeProfit) =>
        TradeCostModel.NetRiskReward(
            side, entryPrice, stopLoss, takeProfit,
            TradeCostModel.EntryFeeRate(_config), TradeCostModel.ExitFeeRate(_config));

    /// <summary>P1.2: цена выхода с нулевым net PnL после комиссий обеих ног.</summary>
    public decimal CalculateBreakEvenPrice(OrderSide side, decimal entryPrice) =>
        TradeCostModel.BreakEvenPrice(
            side, entryPrice, TradeCostModel.EntryFeeRate(_config), TradeCostModel.ExitFeeRate(_config));

    /// <summary>P1.2: достигнут ли порог активации безубытка относительно начального net риска.</summary>
    public bool IsBreakEvenActivated(Position position, decimal currentPrice)
    {
        if (!_config.BreakEvenEnabled || currentPrice <= 0m) return false;

        var initialStop = position.InitialStopLoss > 0m ? position.InitialStopLoss : position.StopLoss;
        if (initialStop <= 0m) return false;

        var entryFee = TradeCostModel.EntryFeeRate(_config);
        var exitFee = TradeCostModel.ExitFeeRate(_config);
        var initialRisk = TradeCostModel.RiskPerUnit(position.Side, position.EntryPrice, initialStop, entryFee, exitFee);
        if (initialRisk <= 0m) return false;

        var netProfit = TradeCostModel.NetPnlPerUnit(position.Side, position.EntryPrice, currentPrice, entryFee, exitFee);
        return netProfit >= initialRisk * _config.BreakEvenActivationRR;
    }

    /// <summary>
    /// Рассчитать Trailing Stop (FR-5.5).
    /// </summary>
    public decimal CalculateTrailingStop(OrderSide side, decimal currentPrice, decimal atr)
    {
        var distance = atr * (decimal)_config.TrailingStopAtrMultiplier;
        return side == OrderSide.Buy
            ? currentPrice - distance
            : currentPrice + distance;
    }

    /// <summary>
    /// Валидация сигнала перед исполнением.
    /// </summary>
    public bool ValidateSignal(TradingSignal signal, decimal balance, out string reason)
    {
        if (!_drawdownTracker.IsTradingAllowed)
        {
            reason = _drawdownTracker.IsMaxDrawdownHit
                ? $"Max drawdown hit: {_drawdownTracker.CurrentDrawdownPercent:P2}"
                : $"Daily loss limit hit: {_drawdownTracker.DailyPnLPercent:P2}";
            return false;
        }

        if (_portfolioRisk?.IsEntryBlocked(signal.Symbol, _drawdownTracker.DayStartBalance, out var portfolioReason) == true)
        {
            reason = $"Portfolio risk: {portfolioReason}";
            return false;
        }

        if (!signal.IsValid(out var signalError))
        {
            reason = $"Invalid signal: {signalError}";
            return false;
        }

        if (signal.Action == SignalAction.Hold)
        {
            reason = "Hold signal";
            return false;
        }

        // P1.2: проверка net R/R после комиссий обеих ног.
        var side = signal.Action == SignalAction.Buy ? OrderSide.Buy : OrderSide.Sell;
        var rr = CalculateNetRiskReward(side, signal.EntryPrice!.Value, signal.StopLoss!.Value, signal.TakeProfit!.Value);
        if (!rr.HasValue || rr.Value < (decimal)_config.MinRiskRewardRatio)
        {
            reason = rr.HasValue
                ? $"Net Risk/Reward too low after fees: {rr.Value:F2} < {_config.MinRiskRewardRatio}"
                : "Net Risk/Reward invalid: risk after fees is not positive";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Обновить баланс после сделки.
    /// </summary>
    public void UpdateBalance(decimal newBalance)
    {
        _drawdownTracker.UpdateBalance(newBalance);
    }

    /// <summary>
    /// Рассчитать размер позиции.
    /// </summary>
    public decimal CalculatePositionSize(decimal balance, decimal entryPrice, decimal stopLoss, decimal atr)
    {
        return _positionSizer.CalculatePositionSize(balance, entryPrice, stopLoss, atr);
    }
}