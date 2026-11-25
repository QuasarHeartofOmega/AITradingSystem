using AITradingSystem.Common.Configuration;
using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.Trading;
using AITradingSystem.Services.Analysis;

namespace AITradingSystem.Tests;

/// <summary>
/// P2.3: политика уверенности 0.7/0.3 — Blend взвешивает независимую оценку модели (0.7)
/// и prior движка (0.3); вес конфигурируется через AI:BlendModelWeight.
/// Дегенерат HOLD≈0.45, отказ инференса и ModelOnly/EngineOverride не меняются.
/// </summary>
public class SignalCalibratorTests
{
    private static TradingSignal Signal(SignalAction action, decimal modelConf) => new()
    {
        Symbol = "BTCUSDT",
        Action = action,
        ModelConfidence = modelConf,
        Confidence = modelConf
    };

    private static EngineScore Score(decimal prior, decimal direction = 0m) => new()
    {
        PriorConfidence = prior,
        Direction = direction
    };

    [Fact]
    public void Blend_Default_Weights_Model_07_Prior_03()
    {
        var calibrator = new SignalCalibrator();
        var signal = Signal(SignalAction.Buy, 0.80m);

        calibrator.Calibrate(signal, Score(0.30m), ConfidencePolicy.Blend, new CalibrationContext());

        // 0.7*0.80 + 0.3*0.30 = 0.65 (при старом 50/50 было бы 0.55)
        Assert.Equal(0.65m, signal.Confidence);
        Assert.Equal("Blend", signal.ConfidenceSource);
        Assert.Equal(0.80m, signal.ModelConfidence);
        Assert.Equal(0.30m, signal.EngineConfidence);
    }

    [Fact]
    public void Blend_Weight_Half_Reproduces_Legacy_50_50()
    {
        var calibrator = new SignalCalibrator();
        var signal = Signal(SignalAction.Buy, 0.80m);

        calibrator.Calibrate(signal, Score(0.30m), ConfidencePolicy.Blend, new CalibrationContext(),
            blendModelWeight: 0.5m);

        Assert.Equal(0.55m, signal.Confidence);
    }

    [Fact]
    public void Blend_Extremes_And_Clamp()
    {
        var calibrator = new SignalCalibrator();

        var modelOnly = Signal(SignalAction.Buy, 0.80m);
        calibrator.Calibrate(modelOnly, Score(0.30m), ConfidencePolicy.Blend, new CalibrationContext(),
            blendModelWeight: 1m);
        Assert.Equal(0.80m, modelOnly.Confidence);

        var priorOnly = Signal(SignalAction.Buy, 0.80m);
        calibrator.Calibrate(priorOnly, Score(0.30m), ConfidencePolicy.Blend, new CalibrationContext(),
            blendModelWeight: 0m);
        Assert.Equal(0.30m, priorOnly.Confidence);

        // Значения вне [0;1] зажимаются, а не ломают математику.
        var over = Signal(SignalAction.Buy, 0.80m);
        calibrator.Calibrate(over, Score(0.30m), ConfidencePolicy.Blend, new CalibrationContext(),
            blendModelWeight: 1.5m);
        Assert.Equal(0.80m, over.Confidence);

        var under = Signal(SignalAction.Buy, 0.80m);
        calibrator.Calibrate(under, Score(0.30m), ConfidencePolicy.Blend, new CalibrationContext(),
            blendModelWeight: -0.2m);
        Assert.Equal(0.30m, under.Confidence);
    }

    [Fact]
    public void Degenerate_Hold_Still_Goes_To_Prior_Regardless_Of_Weight()
    {
        var calibrator = new SignalCalibrator();
        var signal = Signal(SignalAction.Hold, 0.45m);

        calibrator.Calibrate(signal, Score(0.60m), ConfidencePolicy.Blend, new CalibrationContext(),
            blendModelWeight: 0.7m);

        Assert.True(signal.IsDegenerateConfidence);
        Assert.Equal(0.60m, signal.Confidence);
        Assert.Equal("Degenerate→Prior", signal.ConfidenceSource);
    }

    [Fact]
    public void Inference_Failure_Stays_Hold_Under_Any_Weight()
    {
        var calibrator = new SignalCalibrator();
        var signal = Signal(SignalAction.Hold, 0.9m);
        signal.IsInferenceFailure = true;

        calibrator.Calibrate(signal, Score(0.9m, direction: 0.8m), ConfidencePolicy.Blend,
            new CalibrationContext(), blendModelWeight: 0.7m);

        Assert.Equal(SignalAction.Hold, signal.Action);
        Assert.Equal(0m, signal.Confidence);
        Assert.Equal("Failure→Hold", signal.ConfidenceSource);
    }

    [Fact]
    public void ModelOnly_And_EngineOverride_Ignore_Weight()
    {
        var calibrator = new SignalCalibrator();

        var model = Signal(SignalAction.Buy, 0.80m);
        calibrator.Calibrate(model, Score(0.30m), ConfidencePolicy.ModelOnly, new CalibrationContext(),
            blendModelWeight: 0.1m);
        Assert.Equal(0.80m, model.Confidence);
        Assert.Equal("ModelOnly", model.ConfidenceSource);

        var engine = Signal(SignalAction.Buy, 0.80m);
        calibrator.Calibrate(engine, Score(0.30m), ConfidencePolicy.EngineOverride, new CalibrationContext(),
            blendModelWeight: 0.9m);
        Assert.Equal(0.30m, engine.Confidence);
        Assert.Equal("EngineOverride", engine.ConfidenceSource);
    }

    [Fact]
    public void Config_Default_Is_07_And_Validation_Rejects_Out_Of_Range()
    {
        var config = new AIConfig();
        Assert.Equal(0.7m, config.BlendModelWeight);
        config.Validate(); // дефолт валиден

        config.BlendModelWeight = 1.01m;
        Assert.Throws<InvalidOperationException>(() => config.Validate());

        config.BlendModelWeight = -0.01m;
        Assert.Throws<InvalidOperationException>(() => config.Validate());

        config.BlendModelWeight = 0.5m; // прежнее 50/50 остаётся допустимым
        config.Validate();
    }

    // ------------------------------------------------------------------
    // P2.5: пересмотр любого HOLD с сильным согласованным prior
    // ------------------------------------------------------------------

    [Fact]
    public void P25_Any_Hold_With_Strong_Aligned_Prior_Upgrades_To_Buy()
    {
        var calibrator = new SignalCalibrator();
        var signal = Signal(SignalAction.Hold, 0.10m); // вне окна 0.45±0.02

        calibrator.Calibrate(signal, Score(0.60m, direction: 0.4m), ConfidencePolicy.Blend,
            new CalibrationContext { Price = 100m, AtrAbsolute = 1m });

        Assert.Equal(SignalAction.Buy, signal.Action);
        Assert.Equal(0.60m, signal.Confidence);
        Assert.Equal("Hold→Prior", signal.ConfidenceSource);
        Assert.False(signal.IsDegenerateConfidence);
        // Цены считает математика: SL = 100 − 2×ATR(1) = 98, TP = 100 + R/R(2)×2 = 104.
        Assert.Equal(100m, signal.EntryPrice);
        Assert.Equal(98m, signal.StopLoss);
        Assert.Equal(104m, signal.TakeProfit);
    }

    [Fact]
    public void P25_Weak_Prior_Keeps_Blend_And_Hold()
    {
        var calibrator = new SignalCalibrator();
        var signal = Signal(SignalAction.Hold, 0.10m);

        calibrator.Calibrate(signal, Score(0.42m, direction: 0.4m), ConfidencePolicy.Blend,
            new CalibrationContext { Price = 100m, AtrAbsolute = 1m });

        Assert.Equal(SignalAction.Hold, signal.Action);
        Assert.Equal("Blend", signal.ConfidenceSource);
        Assert.Equal(0.196m, signal.Confidence); // 0.7*0.10 + 0.3*0.42
    }

    [Fact]
    public void P25_Flat_Direction_Does_Not_Override()
    {
        var calibrator = new SignalCalibrator();
        var signal = Signal(SignalAction.Hold, 0.10m);

        calibrator.Calibrate(signal, Score(0.70m, direction: 0m), ConfidencePolicy.Blend,
            new CalibrationContext { Price = 100m, AtrAbsolute = 1m });

        Assert.Equal(SignalAction.Hold, signal.Action);
        Assert.Equal("Blend", signal.ConfidenceSource);
        Assert.Equal(0.28m, signal.Confidence); // 0.7*0.10 + 0.3*0.70
    }

    [Fact]
    public void P25_Short_Override_Requires_AllowShort()
    {
        var calibrator = new SignalCalibrator();

        var blocked = Signal(SignalAction.Hold, 0.10m);
        calibrator.Calibrate(blocked, Score(0.70m, direction: -0.4m), ConfidencePolicy.Blend,
            new CalibrationContext { Price = 100m, AtrAbsolute = 1m, AllowShort = false });
        Assert.Equal(SignalAction.Hold, blocked.Action);
        Assert.Equal("Blend", blocked.ConfidenceSource);

        var allowed = Signal(SignalAction.Hold, 0.10m);
        calibrator.Calibrate(allowed, Score(0.70m, direction: -0.4m), ConfidencePolicy.Blend,
            new CalibrationContext { Price = 100m, AtrAbsolute = 1m, AllowShort = true });
        Assert.Equal(SignalAction.Sell, allowed.Action);
        Assert.Equal(0.70m, allowed.Confidence);
        Assert.Equal("Hold→Prior", allowed.ConfidenceSource);
        Assert.Equal(102m, allowed.StopLoss);
        Assert.Equal(96m, allowed.TakeProfit);
    }

    [Fact]
    public void P25_Guards_Position_ZeroConf_ModelOnly_Block_Override()
    {
        var calibrator = new SignalCalibrator();

        var withPosition = Signal(SignalAction.Hold, 0.10m);
        calibrator.Calibrate(withPosition, Score(0.70m, direction: 0.4m), ConfidencePolicy.Blend,
            new CalibrationContext { Price = 100m, AtrAbsolute = 1m, HasPosition = true });
        Assert.Equal(SignalAction.Hold, withPosition.Action);
        Assert.Equal("Blend", withPosition.ConfidenceSource);

        var zeroConf = Signal(SignalAction.Hold, 0m);
        calibrator.Calibrate(zeroConf, Score(0.70m, direction: 0.4m), ConfidencePolicy.Blend,
            new CalibrationContext { Price = 100m, AtrAbsolute = 1m });
        Assert.Equal(SignalAction.Hold, zeroConf.Action);

        var modelOnly = Signal(SignalAction.Hold, 0.10m);
        calibrator.Calibrate(modelOnly, Score(0.70m, direction: 0.4m), ConfidencePolicy.ModelOnly,
            new CalibrationContext { Price = 100m, AtrAbsolute = 1m });
        Assert.Equal(SignalAction.Hold, modelOnly.Action);
        Assert.Equal("ModelOnly", modelOnly.ConfidenceSource);
    }

    [Fact]
    public void P25_Degenerate_Window_Keeps_Priority_And_Source()
    {
        var calibrator = new SignalCalibrator();
        var signal = Signal(SignalAction.Hold, 0.45m);

        calibrator.Calibrate(signal, Score(0.60m, direction: 0.4m), ConfidencePolicy.Blend,
            new CalibrationContext { Price = 100m, AtrAbsolute = 1m });

        Assert.True(signal.IsDegenerateConfidence);
        Assert.Equal("Degenerate→Prior", signal.ConfidenceSource);
        Assert.Equal(SignalAction.Buy, signal.Action);
        Assert.Equal(0.60m, signal.Confidence);
    }
}
