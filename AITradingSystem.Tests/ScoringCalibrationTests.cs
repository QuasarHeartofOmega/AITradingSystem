using AITradingSystem.Common.Configuration;
using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.Trading;
using AITradingSystem.Services.Analysis;

namespace AITradingSystem.Tests;

/// <summary>
/// P2.6: валидация весов скоринга — конфигурируемые веса (дефолты = прежние константы),
/// градуированное выравнивание EMA, общая математика ScoringMath, reliability diagram
/// (уверенность vs фактическая частота успеха) и вердикты по компонентам.
/// Тесты добавлены, но не запускались (сборки/запуски запрещены в этом проходе).
/// </summary>
public class ScoringCalibrationTests
{
    // ------------------------------------------------------------------
    // Хелперы
    // ------------------------------------------------------------------

    /// <summary>Словарь индикаторов с известным исходом по прежним константам: score = 0.72.</summary>
    private static Dictionary<string, decimal> KnownIndicators() => new()
    {
        ["ema_align"] = 1m,
        ["supertrend"] = 1m,
        ["ema20_pos"] = 0.5m,
        ["ema50_pos"] = 0.5m,
        ["ema200_pos"] = 0.5m,
        ["rsi_raw"] = 60m,          // (60−50)/25 = 0.4
        ["macd_hist"] = 0.2m,
        ["stoch_k_raw"] = 60m,      // cross = (0.6−0.4)·4 = 0.8
        ["stoch_d_raw"] = 40m,
        ["obv_trend"] = 0.5m,
        ["adx_raw"] = 30m,          // фактор 1.0
        ["volume_ratio"] = 1.2m,    // фактор 1.0
        ["atr_pct"] = 0.01m,        // фактор 1.0
        ["bb_pos"] = 0m             // фактор 1.0
    };

    private static TradeRecord Trade(decimal confidence, bool win,
        Dictionary<string, decimal>? features = null, decimal engineDirection = 0m) => new()
        {
            Symbol = "TESTUSDT",
            Side = OrderSide.Buy,
            Quantity = 1m,
            EntryPrice = 100m,
            ExitPrice = win ? 102m : 98m,
            PnL = win ? 2m : -2m,
            InitialStopLoss = 98m,      // риск = 2 → R = ±1
            SignalConfidence = confidence,
            ScoreFeatures = features ?? new Dictionary<string, decimal>(),
            EngineDirection = engineDirection
        };

    private static Dictionary<string, decimal> Features(params (string Key, decimal Value)[] overrides)
    {
        var result = ScoringMath.CaptureFeatures(null); // все 15 ключей = 0
        foreach (var (key, value) in overrides)
            result[key] = value;
        return result;
    }

    // ------------------------------------------------------------------
    // Веса: дефолты воспроизводят прежние константы, конфиг меняет скоринг
    // ------------------------------------------------------------------

    [Fact]
    public void Default_Weights_Replicate_Legacy_Constants()
    {
        var score = new SignalCalibrator().PreScore(KnownIndicators());

        // 0.30·1 + 0.20·1 + 0.15·0.5 + 0.15·0.4 + 0.10·0.2 + 0.05·0.8 + 0.05·0.5 = 0.72
        Assert.Equal(0.72m, score.Direction);
        Assert.Equal(0.2m + 0.7m * 0.72m, score.PriorConfidence); // 0.704
    }

    [Fact]
    public void Custom_Weights_Change_Direction()
    {
        var weights = new ScoringWeightsConfig { EmaAlignment = 0.50m, Supertrend = 0.10m };
        var score = new SignalCalibrator(weights).PreScore(KnownIndicators());

        // 0.50·1 + 0.10·1 + 0.075 + 0.06 + 0.02 + 0.04 + 0.025 = 0.82
        Assert.Equal(0.82m, score.Direction);
        Assert.Equal(0.2m + 0.7m * 0.82m, score.PriorConfidence);
    }

    [Fact]
    public void Custom_Prior_Map_Changes_Prior_Not_Direction()
    {
        var weights = new ScoringWeightsConfig { PriorBase = 0.3m, PriorScale = 0.5m };
        var score = new SignalCalibrator(weights).PreScore(KnownIndicators());

        Assert.Equal(0.72m, score.Direction);
        Assert.Equal(0.3m + 0.5m * 0.72m, score.PriorConfidence);
    }

    [Fact]
    public void Graduated_Ema_Disabled_By_Default_Strict_Key_Used()
    {
        var indicators = new Dictionary<string, decimal>
        {
            ["ema_align"] = 0m,
            ["ema_align_grad"] = 0.5m
        };

        var score = new SignalCalibrator().PreScore(indicators);
        Assert.Equal(0m, score.Components.Single(c => c.Name == "ema_align").Value);
    }

    [Fact]
    public void Graduated_Ema_Enabled_Uses_Graduated_Key()
    {
        var indicators = new Dictionary<string, decimal>
        {
            ["ema_align"] = 0m,
            ["ema_align_grad"] = 0.5m
        };
        var weights = new ScoringWeightsConfig { UseGraduatedEmaAlignment = true };

        var score = new SignalCalibrator(weights).PreScore(indicators);
        Assert.Equal(0.5m, score.Components.Single(c => c.Name == "ema_align").Value);
        Assert.Equal(0.15m, score.Direction); // 0.30 · 0.5, остальные компоненты нулевые
    }

    [Fact]
    public void Graduated_Ema_Missing_Key_Falls_Back_To_Strict()
    {
        var indicators = new Dictionary<string, decimal> { ["ema_align"] = 1m };
        var weights = new ScoringWeightsConfig { UseGraduatedEmaAlignment = true };

        var score = new SignalCalibrator(weights).PreScore(indicators);
        Assert.Equal(1m, score.Components.Single(c => c.Name == "ema_align").Value);
    }

    [Fact]
    public void ScoringMath_Components_Match_Calibrator()
    {
        var indicators = KnownIndicators();
        var calibrator = new SignalCalibrator().PreScore(indicators);
        var math = ScoringMath.ComponentValues(indicators);

        Assert.Equal(calibrator.Components.Count, math.Count);
        for (var i = 0; i < math.Count; i++)
        {
            Assert.Equal(calibrator.Components[i].Name, math[i].Name);
            Assert.Equal(calibrator.Components[i].Value, math[i].Value);
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(85, 0.2)]     // перекупленность — не повод покупать
    [InlineData(15, -0.2)]    // перепроданность — не повод продавать
    [InlineData(60, 0.4)]
    public void RsiScore_Edges(double rsi, double expected)
    {
        Assert.Equal((decimal)expected, ScoringMath.RsiScore((decimal)rsi));
    }

    [Fact]
    public void StochScore_Extreme_Zones_Damped()
    {
        Assert.Equal(0.2m, ScoringMath.StochScore(0.95m, 0.90m));
        Assert.Equal(-0.2m, ScoringMath.StochScore(0.05m, 0.10m));
        Assert.Equal(0.8m, ScoringMath.StochScore(0.60m, 0.40m));
    }

    [Fact]
    public void CaptureFeatures_Covers_All_Keys_Missing_Default_Zero()
    {
        var features = ScoringMath.CaptureFeatures(new Dictionary<string, decimal> { ["rsi_raw"] = 55m });

        Assert.Equal(ScoringMath.FeatureKeys.Count, features.Count);
        Assert.Equal(55m, features["rsi_raw"]);
        Assert.Equal(0m, features["ema_align_grad"]);
        Assert.True(features.ContainsKey("bb_pos"));
    }

    // ------------------------------------------------------------------
    // Валидация конфигурации
    // ------------------------------------------------------------------

    [Fact]
    public void Weights_Sum_Must_Be_One()
    {
        var weights = new ScoringWeightsConfig { EmaAlignment = 0.40m };
        Assert.Throws<InvalidOperationException>(() => weights.Validate());
    }

    [Fact]
    public void Factor_And_Prior_Ranges_Validated()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new ScoringWeightsConfig { AdxWeakFactor = 0m }.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new ScoringWeightsConfig { PriorScale = 0m }.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new ScoringWeightsConfig { PriorBase = 0.6m }.Validate());
    }

    [Fact]
    public void AIConfig_Defaults_Pass_Validation()
    {
        var config = new AIConfig();
        config.Validate();

        Assert.True(config.ScoringCalibrationEnabled);
        Assert.False(config.ScoringWeights.UseGraduatedEmaAlignment);
        Assert.Equal(0.30m, config.ScoringWeights.EmaAlignment);
    }

    [Fact]
    public void ScoringCalibration_Config_Ranges_Validated()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new AIConfig { ScoringCalibrationBinStep = 0m }.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new AIConfig { ScoringCalibrationMinBinTrades = 50, ScoringCalibrationMinTrades = 40 }.Validate());
    }

    [Fact]
    public void Tuner_BuildScoringOptions_Maps_Config()
    {
        var ai = new AIConfig
        {
            ScoringCalibrationMinTrades = 60,
            ScoringCalibrationMinBinTrades = 12,
            ScoringCalibrationBinStep = 0.05m,
            ScoringWeights = new ScoringWeightsConfig { UseGraduatedEmaAlignment = true }
        };

        var options = ConfidenceThresholdTuner.BuildScoringOptions(ai);

        Assert.Equal(60, options.MinTrades);
        Assert.Equal(12, options.MinBinTrades);
        Assert.Equal(0.05m, options.BinStep);
        Assert.True(options.UseGraduatedEmaAlignment);
    }

    // ------------------------------------------------------------------
    // Reliability diagram: уверенность vs фактическая частота успеха
    // ------------------------------------------------------------------

    [Fact]
    public void Reliability_Gap_Ece_Brier_Computed_After_Costs()
    {
        var trades = new List<TradeRecord>();
        // Корзина [0.8; 0.9): 10 сделок, 4 победы → заявлено 0.80, факт 0.40, разрыв −0.40.
        for (var i = 0; i < 10; i++) trades.Add(Trade(0.8m, i < 4));
        // Корзина [0.6; 0.7): 10 сделок, 6 побед → разрыв 0.
        for (var i = 0; i < 10; i++) trades.Add(Trade(0.6m, i < 6));

        var report = ScoringCalibrationAnalyzer.Analyze(trades,
            new ScoringCalibrationOptions { MinTrades = 20, MinBinTrades = 10 });

        Assert.Equal(2, report.Bins.Count);
        var high = report.Bins.Single(b => b.Boundary == 0.8m);
        Assert.Equal(0.4m, high.WinRate);
        Assert.Equal(-0.4m, high.Gap);
        Assert.Equal(0.2m, report.Ece);          // 0.5·0.4 + 0.5·0
        Assert.Equal(0.32m, report.BrierScore);  // (4·0.04 + 6·0.64 + 6·0.16 + 4·0.36) / 20
        Assert.Equal(0.4m, report.MaxAbsGap);
        Assert.Equal("overconfident", report.CalibrationVerdict);
        Assert.True(report.HasVerdict);
    }

    [Fact]
    public void Reliability_Well_Calibrated_Verdict()
    {
        var trades = new List<TradeRecord>();
        // Заявлено 0.60, факт 0.60 — разрыв ноль.
        for (var i = 0; i < 20; i++) trades.Add(Trade(0.6m, i < 12));

        var report = ScoringCalibrationAnalyzer.Analyze(trades,
            new ScoringCalibrationOptions { MinTrades = 20, MinBinTrades = 10 });

        Assert.Equal(0m, report.Ece);
        Assert.Equal("calibrated", report.CalibrationVerdict);
    }

    [Fact]
    public void Confidence_Exactly_One_Merges_Into_Last_Bin()
    {
        var trades = new List<TradeRecord> { Trade(0.95m, true), Trade(1.0m, true) };
        var bins = ScoringCalibrationAnalyzer.BuildBins(trades, 0.1m, minBinTrades: 1);

        var last = bins[^1];
        Assert.Equal(0.9m, last.Boundary);
        Assert.Equal(2, last.Trades);
    }

    [Fact]
    public void Unlabeled_And_NoRisk_Trades_Excluded_And_Counted()
    {
        var trades = new List<TradeRecord>
        {
            Trade(0.7m, true),
            Trade(0m, true),                                   // без метки
            new() { SignalConfidence = 0.7m, PnL = 2m }        // без риска
        };

        var report = ScoringCalibrationAnalyzer.Analyze(trades);

        Assert.Equal(3, report.TotalTrades);
        Assert.Equal(1, report.EvaluatedTrades);
        Assert.Equal(1, report.UnlabeledTrades);
        Assert.Equal(1, report.NoRiskTrades);
        Assert.Equal("insufficient-data", report.CalibrationVerdict);
        Assert.False(report.HasVerdict);
    }

    // ------------------------------------------------------------------
    // Вердикты по компонентам скоринга
    // ------------------------------------------------------------------

    [Fact]
    public void Dead_Component_Detected_Confirmed_Component_Detected()
    {
        // ema_align всегда 0 (кейс P2.1), supertrend всегда +1 и все сделки выигрывают.
        var trades = new List<TradeRecord>();
        for (var i = 0; i < 40; i++)
            trades.Add(Trade(0.7m, true, Features(("supertrend", 1m))));

        var report = ScoringCalibrationAnalyzer.Analyze(trades,
            new ScoringCalibrationOptions { MinTrades = 40, MinComponentSideTrades = 10 });

        var ema = report.Components.Single(c => c.Name == "ema_align");
        Assert.Equal(ComponentVerdict.Dead, ema.Verdict);
        Assert.Equal(0m, ema.Coverage);

        var supertrend = report.Components.Single(c => c.Name == "supertrend");
        Assert.Equal(ComponentVerdict.Confirmed, supertrend.Verdict);
        Assert.Equal(40, supertrend.SupportedTrades);
        Assert.Equal(1m, supertrend.SupportExpectancyR);
        Assert.True(report.DeadComponents >= 1);
    }

    [Fact]
    public void Contradicting_Component_Detected()
    {
        // Supertrend поддерживает вход, но все сделки убыточны после издержек.
        var trades = new List<TradeRecord>();
        for (var i = 0; i < 40; i++)
            trades.Add(Trade(0.7m, false, Features(("supertrend", 1m))));

        var report = ScoringCalibrationAnalyzer.Analyze(trades,
            new ScoringCalibrationOptions { MinTrades = 40, MinComponentSideTrades = 10 });

        var supertrend = report.Components.Single(c => c.Name == "supertrend");
        Assert.Equal(ComponentVerdict.Contradicts, supertrend.Verdict);
        Assert.Equal(-1m, supertrend.SupportExpectancyR);
        Assert.Equal(1, report.ContradictedComponents);
    }

    [Fact]
    public void Small_Sample_No_Component_Verdicts()
    {
        var trades = new List<TradeRecord>();
        for (var i = 0; i < 5; i++)
            trades.Add(Trade(0.7m, true, Features(("supertrend", 1m))));

        var report = ScoringCalibrationAnalyzer.Analyze(trades);

        Assert.All(report.Components, c => Assert.Equal(ComponentVerdict.InsufficientData, c.Verdict));
        Assert.Equal("insufficient-data", report.CalibrationVerdict);
    }

    [Fact]
    public void Labeled_Trades_Without_Features_Counted_Separately()
    {
        var trades = new List<TradeRecord>();
        for (var i = 0; i < 40; i++)
            trades.Add(Trade(0.7m, true)); // фич нет — старая история

        var report = ScoringCalibrationAnalyzer.Analyze(trades,
            new ScoringCalibrationOptions { MinTrades = 40 });

        Assert.Equal(40, report.EvaluatedTrades);
        Assert.Equal(0, report.FeatureTrades);
        Assert.Equal(40, report.NoFeatureTrades);
        Assert.All(report.Components, c => Assert.Equal(ComponentVerdict.InsufficientData, c.Verdict));
        // Reliability при этом работает: калибровка не требует фич.
        Assert.Equal("calibrated", report.CalibrationVerdict);
    }

    [Fact]
    public void Directional_HitRate_And_MeanSignedR()
    {
        var trades = new List<TradeRecord>
        {
            Trade(0.7m, true, Features(), engineDirection: 0.5m),
            Trade(0.7m, false, Features(), engineDirection: 0.5m)
        };

        var report = ScoringCalibrationAnalyzer.Analyze(trades);

        Assert.Equal(0.5m, report.DirectionalHitRate); // 1 из 2 совпал со знаком исхода
        Assert.Equal(0m, report.MeanSignedR);          // (0.5·1 + 0.5·(−1)) / 2
    }

    [Fact]
    public void Graduated_Features_Change_Component_Support()
    {
        // Строгий ema_align = 0, градуированный = +0.5: с флагом компонент поддерживает вход.
        var trades = new List<TradeRecord>();
        for (var i = 0; i < 40; i++)
            trades.Add(Trade(0.7m, true, Features(("ema_align_grad", 0.5m))));

        var strict = ScoringCalibrationAnalyzer.Analyze(trades,
            new ScoringCalibrationOptions { MinTrades = 40 });
        var graduated = ScoringCalibrationAnalyzer.Analyze(trades,
            new ScoringCalibrationOptions { MinTrades = 40, UseGraduatedEmaAlignment = true });

        Assert.Equal(ComponentVerdict.Dead, strict.Components.Single(c => c.Name == "ema_align").Verdict);
        Assert.Equal(ComponentVerdict.Confirmed, graduated.Components.Single(c => c.Name == "ema_align").Verdict);
    }

    [Fact]
    public void TradeRecord_HasScoreFeatures_Only_When_Captured()
    {
        Assert.False(Trade(0.7m, true).HasScoreFeatures);
        Assert.True(Trade(0.7m, true, Features(("supertrend", 1m))).HasScoreFeatures);
    }
}
