using AITradingSystem.Common.Configuration;
using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.Trading;
using AITradingSystem.Data.Storage;
using AITradingSystem.Services.Analysis;
using AITradingSystem.Services.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITradingSystem.Tests;

/// <summary>
/// P2.4: порог MinConfidence из данных — expectancy после издержек, guards выборки,
/// коридор порога, метка уверенности в истории и режимы контура подбора.
/// Тесты добавлены в проходе 22 и не запускались (сборки/запуски запрещены заказчиком).
/// </summary>
public class ConfidenceThresholdTests
{
    /// <summary>Сделка с заданной уверенностью и заданным net R (риск 10 USDT на единицу).</summary>
    private static TradeRecord Trade(decimal confidence, decimal netR, decimal commission = 0m) => new()
    {
        Symbol = "BTCUSDT",
        Side = OrderSide.Buy,
        Quantity = 1m,
        EntryPrice = 100m,
        InitialStopLoss = 90m,
        ExitPrice = 100m + netR * 10m + commission,
        PnL = netR * 10m + commission,
        Commission = commission,
        SignalConfidence = confidence,
        ConfidenceSource = "Blend",
        ClosedAt = DateTime.UtcNow
    };

    private static List<TradeRecord> Sample(int losers, decimal loserConfidence, int winners, decimal winnerConfidence)
    {
        var trades = new List<TradeRecord>();
        for (var i = 0; i < losers; i++) trades.Add(Trade(loserConfidence, -1m));
        for (var i = 0; i < winners; i++) trades.Add(Trade(winnerConfidence, 2m));
        return trades;
    }

    private static ConfidenceExpectancyOptions Options(
        decimal floor = 0.45m, decimal ceiling = 0.80m, decimal step = 0.05m,
        int minTrades = 40, int minBucket = 15, decimal current = 0.55m) => new()
        {
            Floor = floor,
            Ceiling = ceiling,
            Step = step,
            MinTrades = minTrades,
            MinBucketTrades = minBucket,
            CurrentThreshold = current
        };

    // ------------------------------------------------------------------
    // Отбор данных
    // ------------------------------------------------------------------

    [Fact]
    public void LegacyHistory_WithoutConfidenceLabel_IsNotUsed()
    {
        var trades = Sample(0, 0m, 50, 0.7m);
        foreach (var trade in trades) trade.SignalConfidence = 0m;

        var report = ConfidenceExpectancyAnalyzer.Analyze(trades, Options());

        Assert.False(report.HasRecommendation);
        Assert.Equal(50, report.UnlabeledTrades);
        Assert.Equal(0, report.EvaluatedTrades);
    }

    [Fact]
    public void TradesWithoutInitialRisk_AreExcludedAndCounted()
    {
        var trades = Sample(0, 0m, 50, 0.7m);
        foreach (var trade in trades) trade.InitialStopLoss = 0m;

        var report = ConfidenceExpectancyAnalyzer.Analyze(trades, Options());

        Assert.False(report.HasRecommendation);
        Assert.Equal(50, report.NoRiskTrades);
        Assert.Equal(0, report.EvaluatedTrades);
    }

    [Fact]
    public void SmallSample_KeepsCurrentThreshold()
    {
        var report = ConfidenceExpectancyAnalyzer.Analyze(Sample(0, 0m, 20, 0.7m), Options());

        Assert.False(report.HasRecommendation);
        Assert.Contains("минимума", report.Reason);
    }

    [Fact]
    public void EmptyHistory_KeepsCurrentThreshold()
    {
        var report = ConfidenceExpectancyAnalyzer.Analyze(Array.Empty<TradeRecord>(), Options());

        Assert.False(report.HasRecommendation);
        Assert.Equal(0, report.TotalTrades);
    }

    // ------------------------------------------------------------------
    // Выбор порога
    // ------------------------------------------------------------------

    [Fact]
    public void PicksThreshold_WithMaxNetExpectancy()
    {
        // 30 убыточных на conf 0.60 и 20 прибыльных на 0.75: действующий порог 0.55
        // пропускает обе группы (0.2R), порог 0.75 оставляет только прибыльные (2R).
        var report = ConfidenceExpectancyAnalyzer.Analyze(Sample(30, 0.60m, 20, 0.75m), Options());

        Assert.True(report.HasRecommendation);
        Assert.Equal(0.75m, report.RecommendedThreshold);
        Assert.Equal(2m, report.RecommendedExpectancyR, 6);
        Assert.Equal(50, report.EvaluatedTrades);
        Assert.Equal(0.2m, report.Current!.ExpectancyR, 6);
        Assert.Equal(50, report.Current.Trades);
    }

    [Fact]
    public void EqualExpectancy_TieBreaksToHigherThreshold()
    {
        var report = ConfidenceExpectancyAnalyzer.Analyze(Sample(0, 0m, 40, 0.60m), Options());

        Assert.Equal(0.60m, report.RecommendedThreshold);
    }

    [Fact]
    public void NegativeExpectancyEverywhere_GivesNoRecommendation()
    {
        var trades = new List<TradeRecord>();
        for (var i = 0; i < 50; i++) trades.Add(Trade(0.60m, -1m));

        var report = ConfidenceExpectancyAnalyzer.Analyze(trades, Options());

        Assert.False(report.HasRecommendation);
        Assert.Contains("положительной expectancy", report.Reason);
    }

    [Fact]
    public void ExpectancyIsNetOfCommissions()
    {
        // Валовая прибыль +1 USDT, комиссия 2 USDT → net убыток. Порог рекомендовать нельзя.
        var trades = new List<TradeRecord>();
        for (var i = 0; i < 50; i++)
        {
            var trade = Trade(0.70m, 0m);
            trade.PnL = 1m;
            trade.Commission = 2m;
            trades.Add(trade);
        }

        var report = ConfidenceExpectancyAnalyzer.Analyze(trades, Options());

        Assert.False(report.HasRecommendation);
        Assert.True(report.Current!.ExpectancyQuote < 0m);
    }

    [Fact]
    public void NeverRecommendsBelowFloor()
    {
        var trades = Sample(40, 0.20m, 20, 0.50m);

        var report = ConfidenceExpectancyAnalyzer.Analyze(trades, Options(floor: 0.45m));
        Assert.Equal(0.50m, report.RecommendedThreshold);

        // Коридор поднят выше всех сделок — опустить порог до данных контур не имеет права.
        var strict = ConfidenceExpectancyAnalyzer.Analyze(trades, Options(floor: 0.60m, ceiling: 0.80m));
        Assert.False(strict.HasRecommendation);
    }

    [Fact]
    public void BucketSampleRequirement_IsEnforced()
    {
        // 45 сделок на 0.50 и 5 на 0.75: хвост 0.75 прибыльнее, но выборка 5 < 15.
        var report = ConfidenceExpectancyAnalyzer.Analyze(Sample(0, 0m, 45, 0.50m), Options());
        var withTail = Sample(0, 0m, 45, 0.50m);
        for (var i = 0; i < 5; i++) withTail.Add(Trade(0.75m, 5m));

        var guarded = ConfidenceExpectancyAnalyzer.Analyze(withTail, Options());

        Assert.Equal(0.50m, report.RecommendedThreshold);
        Assert.Equal(0.50m, guarded.RecommendedThreshold);
    }

    [Fact]
    public void BucketsAndSweep_AreConsistent()
    {
        var trades = Sample(30, 0.50m, 20, 0.70m);
        var report = ConfidenceExpectancyAnalyzer.Analyze(trades, Options());

        Assert.Equal(report.EvaluatedTrades, report.Buckets.Sum(b => b.Trades));
        Assert.Equal(0.80m, report.Sweep[^1].Boundary);
        Assert.Equal(20, report.Current!.Trades);
    }

    [Fact]
    public void SweepAlwaysIncludesCeiling()
    {
        var report = ConfidenceExpectancyAnalyzer.Analyze(
            Sample(0, 0m, 50, 0.70m), Options(floor: 0.45m, ceiling: 0.62m));

        Assert.Equal(0.62m, report.Sweep[^1].Boundary);
    }

    [Fact]
    public void ConfidenceOfOne_FallsIntoLastBucket()
    {
        var trades = Sample(0, 0m, 45, 0.98m);
        trades.AddRange(Enumerable.Range(0, 5).Select(_ => Trade(1.0m, 2m)));

        var report = ConfidenceExpectancyAnalyzer.Analyze(trades, Options());

        Assert.Equal(50, report.Buckets.Sum(b => b.Trades));
    }

    // ------------------------------------------------------------------
    // Модель сделки
    // ------------------------------------------------------------------

    [Fact]
    public void TradeRecord_NetRMultiple_UsesInitialRiskAndNetPnL()
    {
        var trade = new TradeRecord
        {
            Quantity = 2m,
            EntryPrice = 100m,
            InitialStopLoss = 90m,
            PnL = 40m,
            Commission = 4m,
            SignalConfidence = 0.7m
        };

        Assert.Equal(20m, trade.RiskQuote);
        Assert.Equal(36m, trade.NetPnL);
        Assert.Equal(1.8m, trade.NetRMultiple!.Value, 6);
        Assert.True(trade.HasConfidenceLabel);
    }

    [Fact]
    public void TradeRecord_ShortRisk_IsAbsolute()
    {
        var trade = new TradeRecord
        {
            Side = OrderSide.Sell,
            Quantity = 1m,
            EntryPrice = 100m,
            InitialStopLoss = 110m
        };

        Assert.Equal(10m, trade.RiskQuote);
    }

    [Fact]
    public void TradeRecord_WithoutInitialStop_HasNoRMultiple()
    {
        var trade = new TradeRecord { Quantity = 1m, EntryPrice = 100m, PnL = 5m };

        Assert.Equal(0m, trade.RiskQuote);
        Assert.Null(trade.NetRMultiple);
    }

    // ------------------------------------------------------------------
    // История и контур подбора
    // ------------------------------------------------------------------

    [Fact]
    public async Task Repository_GetAllTrades_IsNotCutByLimit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aitrading-p24-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repo = new JsonTradeHistoryRepository(
                Path.Combine(directory, "trades.json"), NullLogger<JsonTradeHistoryRepository>.Instance);

            for (var i = 0; i < 150; i++)
            {
                var trade = Trade(0.7m, 1m);
                trade.ClosedAt = DateTime.UtcNow.AddMinutes(i);
                await repo.SaveTradeAsync(trade);
            }

            var limited = await repo.GetTradesAsync(100);
            var all = await repo.GetAllTradesAsync();

            Assert.Equal(100, limited.Count);
            Assert.Equal(150, all.Count);
            Assert.True(all[0].ClosedAt <= all[^1].ClosedAt);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static (ConfidenceThresholdTuner tuner, AppConfig config, MetricsCollector metrics) NewTuner(
        string directory, List<TradeRecord> trades, bool auto)
    {
        var config = new AppConfig();
        config.AI.MinConfidence = 0.55m;
        config.AI.MinConfidenceAutoTuneEnabled = auto;
        config.Telegram.Enabled = false;

        var repo = new JsonTradeHistoryRepository(
            Path.Combine(directory, "trades.json"), NullLogger<JsonTradeHistoryRepository>.Instance);
        foreach (var trade in trades) repo.SaveTradeAsync(trade).GetAwaiter().GetResult();

        var metrics = new MetricsCollector();
        var alerts = new TelegramAlertService(config.Telegram, NullLogger<TelegramAlertService>.Instance);
        var tuner = new ConfidenceThresholdTuner(config, repo, metrics, alerts,
            NullLogger<ConfidenceThresholdTuner>.Instance);
        return (tuner, config, metrics);
    }

    [Fact]
    public async Task Tuner_AdvisoryByDefault_DoesNotChangeThreshold()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aitrading-p24-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (tuner, config, metrics) = NewTuner(directory, Sample(30, 0.60m, 20, 0.75m), auto: false);

            var report = await tuner.TuneOnceAsync();

            Assert.True(report.HasRecommendation);
            Assert.Equal(0.55m, config.AI.MinConfidence);
            Assert.Equal(1, metrics.ConfidenceTuneRuns);
            Assert.Equal(0, metrics.ConfidenceTuneApplications);
            Assert.Equal(0.55m, metrics.ActiveMinConfidence);
            Assert.Equal(0.75m, metrics.LastRecommendedMinConfidence);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Tuner_AutoMode_AppliesRecommendedThreshold()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aitrading-p24-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (tuner, config, metrics) = NewTuner(directory, Sample(30, 0.60m, 20, 0.75m), auto: true);

            await tuner.TuneOnceAsync();

            Assert.Equal(0.75m, config.AI.MinConfidence);
            Assert.Equal(1, metrics.ConfidenceTuneApplications);
            Assert.Equal(0.75m, metrics.ActiveMinConfidence);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Tuner_AutoMode_KeepsThresholdOnSmallSample()
    {
        var directory = Path.Combine(Path.GetTempPath(), "aitrading-p24-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (tuner, config, metrics) = NewTuner(directory, Sample(0, 0m, 10, 0.70m), auto: true);

            var report = await tuner.TuneOnceAsync();

            Assert.False(report.HasRecommendation);
            Assert.Equal(0.55m, config.AI.MinConfidence);
            Assert.Equal(0, metrics.ConfidenceTuneApplications);
            Assert.Equal(1, metrics.ConfidenceTuneNoRecommendation);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    // ------------------------------------------------------------------
    // Конфигурация и связь с калибратором
    // ------------------------------------------------------------------

    [Fact]
    public void Config_Defaults_AdvisoryModeWithCorridor()
    {
        var ai = new AIConfig();

        Assert.True(ai.MinConfidenceTuneEnabled);
        Assert.False(ai.MinConfidenceAutoTuneEnabled);
        Assert.Equal(0.45m, ai.MinConfidenceFloor);
        Assert.Equal(0.80m, ai.MinConfidenceCeiling);
        Assert.Equal(0.05m, ai.MinConfidenceTuneStep);
        Assert.Equal(40, ai.MinConfidenceTuneMinTrades);
        Assert.Equal(15, ai.MinConfidenceTuneMinBucketTrades);
        ai.Validate();
    }

    [Theory]
    [InlineData(0.9, 0.8)]   // ceiling < floor
    [InlineData(0.45, 1.1)]  // ceiling > 1
    public void Config_InvalidCorridor_Fails(double floor, double ceiling)
    {
        var ai = new AIConfig
        {
            MinConfidenceFloor = (decimal)floor,
            MinConfidenceCeiling = (decimal)ceiling
        };

        Assert.Throws<InvalidOperationException>(() => ai.Validate());
    }

    [Fact]
    public void Config_InvalidStepAndSample_Fails()
    {
        Assert.Throws<InvalidOperationException>(() => new AIConfig { MinConfidenceTuneStep = 0m }.Validate());
        Assert.Throws<InvalidOperationException>(() => new AIConfig { MinConfidenceTuneMinBucketTrades = 100 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new AIConfig { MinConfidenceTuneIntervalMinutes = 0 }.Validate());
    }

    [Fact]
    public void CalibrationContext_FallbackThreshold_IsSingleDocumentedConstant()
    {
        Assert.Equal(ConfidenceExpectancyOptions.FallbackMinConfidence, new CalibrationContext().MinConfidence);
        Assert.Equal(0.55m, ConfidenceExpectancyOptions.FallbackMinConfidence);
    }
}
