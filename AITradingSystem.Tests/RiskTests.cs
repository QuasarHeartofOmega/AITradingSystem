using AITradingSystem.Common.Configuration;
using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.Trading;
using AITradingSystem.Core.RiskManagement;

namespace AITradingSystem.Tests;

public class RiskTests
{
    private static TradingConfig Config() => new()
    {
        Symbols = new List<string> { "BTCUSDT" },
        RiskPerTrade = 0.02,
        MaxDrawdown = 0.10,
        DailyLossLimit = 0.05,
        MakerCommissionRate = 0m,
        TakerCommissionRate = 0m,
        PaperCommissionRate = 0m
    };

    [Fact]
    public void PositionSizer_Risk_Based_Quantity()
    {
        var sizer = new PositionSizer(Config());
        // balance 10000, risk 2% = 200 USDT; entry 100, SL 90 => risk/unit 10 => qty 20
        var qty = sizer.CalculatePositionSize(10000m, 100m, 90m, 5m);
        Assert.Equal(20m, qty);
    }

    [Fact]
    public void PositionSizer_Caps_At_Balance()
    {
        var sizer = new PositionSizer(Config());
        // SL очень близко => огромный qty, но не больше balance/entry
        var qty = sizer.CalculatePositionSize(10000m, 100m, 99.99m, 1m);
        Assert.True(qty <= 100m);
    }

    [Fact]
    public void DrawdownTracker_Blocks_Trading_After_MaxDrawdown()
    {
        var tracker = new DrawdownTracker(Config(), 10000m);
        Assert.True(tracker.IsTradingAllowed);

        tracker.UpdateBalance(8900m); // -11% от пика
        Assert.True(tracker.IsMaxDrawdownHit);
        Assert.False(tracker.IsTradingAllowed);
    }

    [Fact]
    public void DrawdownTracker_DailyLossLimit()
    {
        var tracker = new DrawdownTracker(Config(), 10000m);
        tracker.UpdateBalance(9400m); // -6% за день, но просадка < 10%
        Assert.True(tracker.IsDailyLossLimitHit);
        Assert.False(tracker.IsTradingAllowed);
    }

    [Fact]
    public void RiskManager_StopLoss_And_TakeProfit_Directions()
    {
        var rm = new RiskManager(Config(), 10000m);

        var slBuy = rm.CalculateStopLoss(OrderSide.Buy, 100m, 2m);
        var tpBuy = rm.CalculateTakeProfit(OrderSide.Buy, 100m, slBuy);
        Assert.True(slBuy < 100m);
        Assert.True(tpBuy > 100m);

        var slSell = rm.CalculateStopLoss(OrderSide.Sell, 100m, 2m);
        var tpSell = rm.CalculateTakeProfit(OrderSide.Sell, 100m, slSell);
        Assert.True(slSell > 100m);
        Assert.True(tpSell < 100m);
    }

    [Fact]
    public void TradingSignal_Validation_Rejects_Bad_Geometry()
    {
        var signal = new TradingSignal
        {
            Symbol = "BTCUSDT",
            Action = SignalAction.Buy,
            Confidence = 0.8m,
            EntryPrice = 100m,
            StopLoss = 110m, // выше входа для Buy — невалидно
            TakeProfit = 120m
        };

        Assert.False(signal.IsValid(out _));

        signal.StopLoss = 90m;
        Assert.True(signal.IsValid(out _));
        Assert.Equal(2m, signal.RiskRewardRatio!.Value, 2);
    }

    [Fact]
    public void Position_PnL_And_TrailingStop()
    {
        var pos = new Position
        {
            Symbol = "BTCUSDT",
            Side = OrderSide.Buy,
            Quantity = 2m,
            EntryPrice = 100m,
            CurrentPrice = 100m,
            StopLoss = 90m,
            TakeProfit = 120m
        };

        pos.UpdatePrice(110m);
        Assert.Equal(20m, pos.UnrealizedPnL);
        Assert.Equal(10m, pos.UnrealizedPnLPercent);

        Assert.True(pos.UpdateTrailingStop(95m));  // подтянули вверх
        Assert.False(pos.UpdateTrailingStop(92m)); // назад не двигается
        Assert.True(pos.IsStopLossHit(94m, 111m));
        Assert.True(pos.IsTakeProfitHit(109m, 121m));
    }
}
