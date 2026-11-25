using AITradingSystem.Common.Configuration;
using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.Trading;
using AITradingSystem.Core.RiskManagement;

namespace AITradingSystem.Tests;

/// <summary>P1.2: комиссии в R/R, сайзинге, безубытке и чистой истории. Тесты не запускались в проходе 18.</summary>
public class TradeCostModelTests
{
    private static TradingConfig Config(decimal fee = 0.001m) => new()
    {
        Symbols = new List<string> { "BTCUSDT" },
        RiskPerTrade = 0.02,
        MaxDrawdown = 0.10,
        DailyLossLimit = 0.05,
        MinRiskRewardRatio = 2.0,
        MakerCommissionRate = fee,
        TakerCommissionRate = fee,
        PaperCommissionRate = fee,
        BreakEvenEnabled = true,
        BreakEvenActivationRR = 1.0m,
        BreakEvenBufferPercent = 0.05m
    };

    [Fact]
    public void NetRiskReward_Long_IsLowerThanGross_WithFees()
    {
        var gross = 2m; // (120 - 100) / (100 - 90)
        var net = TradeCostModel.NetRiskReward(OrderSide.Buy, 100m, 90m, 120m, 0.001m, 0.001m);

        Assert.NotNull(net);
        Assert.True(net!.Value < gross);
        Assert.True(net.Value < 2m);
    }

    [Fact]
    public void TakeProfitForNetRiskReward_SolvesTargetNetRR()
    {
        var tp = TradeCostModel.TakeProfitForNetRiskReward(
            OrderSide.Buy, 100m, 90m, 2m, 0.001m, 0.001m);
        var net = TradeCostModel.NetRiskReward(OrderSide.Buy, 100m, 90m, tp, 0.001m, 0.001m);

        Assert.True(tp > 120m);
        Assert.NotNull(net);
        Assert.Equal(2m, net!.Value, 6);
    }

    [Fact]
    public void BreakEven_Long_IsAboveEntry_WithFees()
    {
        var be = TradeCostModel.BreakEvenPrice(OrderSide.Buy, 100m, 0.001m, 0.001m);
        Assert.True(be > 100m);

        var pnl = TradeCostModel.NetPnlPerUnit(OrderSide.Buy, 100m, be, 0.001m, 0.001m);
        Assert.Equal(0m, pnl, 6);
    }

    [Fact]
    public void Short_CostMath_IsMirrored()
    {
        var net = TradeCostModel.NetRiskReward(OrderSide.Sell, 100m, 110m, 80m, 0.001m, 0.001m);
        Assert.NotNull(net);
        Assert.True(net!.Value < 2m);

        var be = TradeCostModel.BreakEvenPrice(OrderSide.Sell, 100m, 0.001m, 0.001m);
        Assert.True(be < 100m);

        var tp = TradeCostModel.TakeProfitForNetRiskReward(
            OrderSide.Sell, 100m, 110m, 2m, 0.001m, 0.001m);
        var solved = TradeCostModel.NetRiskReward(OrderSide.Sell, 100m, 110m, tp, 0.001m, 0.001m);
        Assert.Equal(2m, solved!.Value, 6);
    }

    [Fact]
    public void PositionSizer_UsesNetRiskPerUnit()
    {
        var sizer = new PositionSizer(Config(0.001m));
        var qty = sizer.CalculatePositionSize(10000m, 100m, 90m, 5m);

        // gross risk/unit = 10 => qty 20; fees increase risk/unit => qty < 20.
        Assert.True(qty > 0m);
        Assert.True(qty < 20m);
    }

    [Fact]
    public void RiskManager_RejectsGrossRR_WhenNetRRBelowMinimum()
    {
        var rm = new RiskManager(Config(0.001m), 10000m);
        var signal = new TradingSignal
        {
            Symbol = "BTCUSDT",
            Action = SignalAction.Buy,
            Confidence = 0.9m,
            EntryPrice = 100m,
            StopLoss = 90m,
            TakeProfit = 120m // gross R/R = 2, net R/R < 2
        };

        Assert.False(rm.ValidateSignal(signal, 10000m, out var reason));
        Assert.Contains("Net Risk/Reward", reason);
    }

    [Fact]
    public void RiskManager_CalculateTakeProfit_AchievesNetMinimum()
    {
        var rm = new RiskManager(Config(0.001m), 10000m);
        var tp = rm.CalculateTakeProfit(OrderSide.Buy, 100m, 90m);
        var net = rm.CalculateNetRiskReward(OrderSide.Buy, 100m, 90m, tp);

        Assert.NotNull(net);
        Assert.True(net!.Value >= 2m);
    }

    [Fact]
    public void BreakEvenActivation_RequiresInitialNetRiskProfit()
    {
        var rm = new RiskManager(Config(0.001m), 10000m);
        var position = new Position
        {
            Symbol = "BTCUSDT",
            Side = OrderSide.Buy,
            Quantity = 1m,
            EntryPrice = 100m,
            StopLoss = 90m,
            InitialStopLoss = 90m,
            TakeProfit = 125m,
            CurrentPrice = 110m
        };

        Assert.False(rm.IsBreakEvenActivated(position, 110m));
        Assert.True(rm.IsBreakEvenActivated(position, 111m));
    }

    [Fact]
    public void TradeRecord_NetPnL_SubtractsCommission()
    {
        var trade = new TradeRecord { PnL = 10m, Commission = 0.3m };
        Assert.Equal(9.7m, trade.NetPnL);
    }

    [Fact]
    public void TradingConfig_RejectsInvalidSlippageCap()
    {
        var config = Config();
        config.MarketExitMaxSlippage = 0.2m;
        Assert.Throws<InvalidOperationException>(() => config.Validate());
    }
}
