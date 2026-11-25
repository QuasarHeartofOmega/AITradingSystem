using AITradingSystem.Common.Configuration;
using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.MarketData;
using AITradingSystem.Core.Models.Trading;
using AITradingSystem.Core.RiskManagement;

namespace AITradingSystem.Tests;

public class PortfolioRiskTests
{
    private static TradingConfig Config() => new()
    {
        Symbols = new List<string> { "BTCUSDT", "ETHUSDT" },
        MakerCommissionRate = 0m,
        TakerCommissionRate = 0m,
        PaperCommissionRate = 0m,
        PortfolioRiskEnabled = true,
        MaxPortfolioExposurePercent = 60m,
        MaxCorrelatedExposurePercent = 35m,
        CorrelationRiskEnabled = false,
        SymbolDailyLossLimitPercent = 1m,
        LossStreakPauseThreshold = 3,
        LossStreakPauseMinutes = 60
    };

    private static Position Pos(string symbol, decimal notional, decimal current = 100m) => new()
    {
        Symbol = symbol,
        Side = OrderSide.Buy,
        Quantity = notional / 100m,
        EntryPrice = 100m,
        CurrentPrice = current
    };

    private static TradeRecord Trade(string symbol, decimal netPnl, DateTime closedAt, string? id = null) => new()
    {
        Id = id ?? Guid.NewGuid().ToString("N"),
        Symbol = symbol,
        Side = OrderSide.Buy,
        Quantity = 1m,
        EntryPrice = 100m,
        ExitPrice = 100m + netPnl,
        PnL = netPnl,
        Commission = 0m,
        ClosedAt = closedAt
    };

    private static List<OHLCV> Candles(string symbol, int count, Func<int, decimal> close)
    {
        var start = new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc);
        return Enumerable.Range(0, count).Select(i => new OHLCV
        {
            Symbol = symbol,
            OpenTime = start.AddMinutes(15 * i),
            CloseTime = start.AddMinutes(15 * (i + 1)),
            Open = close(i),
            High = close(i),
            Low = close(i),
            Close = close(i),
            Volume = 1m
        }).ToList();
    }

    [Fact]
    public void PortfolioExposure_Caps_Additional_Notional()
    {
        var mgr = new PortfolioRiskManager(Config(), () => new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc));
        var open = new List<Position> { Pos("ETHUSDT", 500m) };

        var decision = mgr.EvaluateEntry("BTCUSDT", 200m, open, equity: 1000m, dayStartBalance: 1000m);

        Assert.True(decision.Allowed);
        Assert.Equal(100m, decision.MaxAdditionalNotional); // cap 600 - existing 500
    }

    [Fact]
    public void PortfolioExposure_Rejects_When_Cap_Already_Reached()
    {
        var mgr = new PortfolioRiskManager(Config(), () => new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc));
        var open = new List<Position> { Pos("ETHUSDT", 600m) };

        var decision = mgr.EvaluateEntry("BTCUSDT", 100m, open, equity: 1000m, dayStartBalance: 1000m);

        Assert.False(decision.Allowed);
        Assert.Contains("portfolio exposure cap", decision.Reason);
    }

    [Fact]
    public void SymbolDailyLossLimit_Blocks_Entries_For_Losing_Symbol()
    {
        var now = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        var mgr = new PortfolioRiskManager(Config(), () => now);
        mgr.RecordClosedTrade(Trade("ETHUSDT", -11m, now.AddMinutes(-5)));

        Assert.True(mgr.IsEntryBlocked("ETHUSDT", 1000m, out var reason));
        Assert.Contains("symbol daily loss limit", reason);
        Assert.False(mgr.IsEntryBlocked("BTCUSDT", 1000m, out _));
    }

    [Fact]
    public void LossStreak_Pauses_Entries_And_Survives_Rebuild()
    {
        var now = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        var cfg = Config();
        var mgr = new PortfolioRiskManager(cfg, () => now);
        var trades = new[]
        {
            Trade("BTCUSDT", -1m, now.AddMinutes(-3)),
            Trade("ETHUSDT", -1m, now.AddMinutes(-2)),
            Trade("BTCUSDT", -1m, now.AddMinutes(-1))
        };
        foreach (var t in trades) mgr.RecordClosedTrade(t);

        Assert.True(mgr.IsEntryBlocked("BTCUSDT", 1000m, out var reason));
        Assert.Contains("loss-streak pause", reason);
        Assert.Equal(now.AddMinutes(59), mgr.PauseUntilUtc);

        var restored = new PortfolioRiskManager(cfg, () => now);
        restored.RebuildFromHistory(trades);
        Assert.True(restored.IsEntryBlocked("ETHUSDT", 1000m, out _));
        Assert.Equal(3, restored.ConsecutiveLosses);
    }

    [Fact]
    public void Profit_Resets_LossStreak()
    {
        var now = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        var mgr = new PortfolioRiskManager(Config(), () => now);
        mgr.RecordClosedTrade(Trade("BTCUSDT", -1m, now.AddMinutes(-4)));
        mgr.RecordClosedTrade(Trade("ETHUSDT", -1m, now.AddMinutes(-3)));
        mgr.RecordClosedTrade(Trade("BTCUSDT", 1m, now.AddMinutes(-2)));
        mgr.RecordClosedTrade(Trade("ETHUSDT", -1m, now.AddMinutes(-1)));

        Assert.Equal(1, mgr.ConsecutiveLosses);
        Assert.False(mgr.IsEntryBlocked("BTCUSDT", 1000m, out _));
    }

    [Fact]
    public void Duplicate_Trade_Id_Does_Not_Double_Count()
    {
        var now = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        var mgr = new PortfolioRiskManager(Config(), () => now);
        var trade = Trade("BTCUSDT", -11m, now, id: "dup");
        mgr.RecordClosedTrade(trade);
        mgr.RecordClosedTrade(trade);

        Assert.Equal(-11m, mgr.GetSymbolDailyPnL("BTCUSDT"));
        Assert.Equal(1, mgr.ConsecutiveLosses);
    }

    [Fact]
    public void Correlation_FailClosed_When_Benchmark_Missing()
    {
        var cfg = Config();
        cfg.CorrelationRiskEnabled = true;
        var mgr = new PortfolioRiskManager(cfg, () => new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc));

        var decision = mgr.EvaluateEntry("ETHUSDT", 100m, Array.Empty<Position>(), 1000m, 1000m,
            _ => Array.Empty<OHLCV>());

        Assert.False(decision.Allowed);
        Assert.Contains("benchmark unavailable", decision.Reason);
    }

    [Fact]
    public void Correlation_Caps_BtcCluster_Exposure()
    {
        var cfg = Config();
        cfg.CorrelationRiskEnabled = true;
        cfg.MinCorrelationSamples = 10;
        cfg.CorrelationLookbackCandles = 20;
        cfg.MaxPairwiseCorrelation = 0.8m;
        var mgr = new PortfolioRiskManager(cfg, () => new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc));

        // Perfectly correlated synthetic closes: ETH and SOL both track BTC.
        var btc = Candles("BTCUSDT", 25, i => 100m + i);
        var eth = Candles("ETHUSDT", 25, i => 50m + i * 0.5m);
        var sol = Candles("SOLUSDT", 25, i => 20m + i * 0.2m);
        var provider = new Dictionary<string, IReadOnlyList<OHLCV>>(StringComparer.OrdinalIgnoreCase)
        {
            ["BTCUSDT"] = btc,
            ["ETHUSDT"] = eth,
            ["SOLUSDT"] = sol
        };

        var open = new List<Position> { Pos("ETHUSDT", 300m) };
        var decision = mgr.EvaluateEntry("SOLUSDT", 200m, open, 1000m, 1000m, s => provider[s]);

        Assert.True(decision.Allowed);
        Assert.Equal(50m, decision.MaxAdditionalNotional); // correlated cap 350 - existing 300
        Assert.True(decision.MaxCorrelation >= 0.8m);
    }

    [Fact]
    public void Pearson_Aligns_By_Timestamp_And_Rejects_ZeroVariance()
    {
        var a = Candles("A", 10, i => 100m + i);
        var b = Candles("B", 10, i => 50m + i * 0.5m);
        var flat = Candles("C", 10, _ => 100m);

        var ra = PortfolioRiskManager.LogReturns(a, 20);
        var rb = PortfolioRiskManager.LogReturns(b, 20);
        var rc = PortfolioRiskManager.LogReturns(flat, 20);

        var r = PortfolioRiskManager.Pearson(ra, rb);
        Assert.NotNull(r);
        Assert.True(r!.Value > 0.99m);
        Assert.Null(PortfolioRiskManager.Pearson(ra, rc));
    }

    [Fact]
    public void Config_Validate_Rejects_Inconsistent_Correlation_Samples()
    {
        var cfg = Config();
        cfg.CorrelationLookbackCandles = 10;
        cfg.MinCorrelationSamples = 20;
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
    }

    [Fact]
    public void Config_Validate_Rejects_Correlated_Cap_Above_Portfolio_Cap()
    {
        var cfg = Config();
        cfg.MaxPortfolioExposurePercent = 30m;
        cfg.MaxCorrelatedExposurePercent = 40m;
        Assert.Throws<InvalidOperationException>(() => cfg.Validate());
    }

    [Fact]
    public void Disabled_PortfolioRisk_Allows_Entry()
    {
        var cfg = Config();
        cfg.PortfolioRiskEnabled = false;
        var mgr = new PortfolioRiskManager(cfg, () => new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc));
        mgr.RecordClosedTrade(Trade("BTCUSDT", -100m, DateTime.UtcNow));

        var decision = mgr.EvaluateEntry("BTCUSDT", 900m, new List<Position> { Pos("ETHUSDT", 900m) }, 1000m, 1000m);
        Assert.True(decision.Allowed);
        Assert.Equal(900m, decision.MaxAdditionalNotional);
    }
}
