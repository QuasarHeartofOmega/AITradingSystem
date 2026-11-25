using AITradingSystem.Common.Configuration;
using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.Trading;
using AITradingSystem.Services.AI;
using AITradingSystem.Services.Analysis;
using AITradingSystem.Services.Trading;

namespace AITradingSystem.Tests;

/// <summary>
/// P0.5: LLM вне последовательного критического пути.
/// Тесты добавлены, но не запускались (запрет сборок/прогонов в этом проходе).
/// </summary>
public class LlmRoleTests
{
    private static AiRequestContext Ctx(decimal? engineScore = 0.5m) => new()
    {
        EngineScore = engineScore,
        EngineDirection = engineScore > 0 ? "LONG" : "SHORT",
        AllowShort = false
    };

    private static TradingSignal Sig(SignalAction action, decimal conf = 0.7m) => new()
    {
        Symbol = "BTCUSDT",
        Action = action,
        Confidence = conf,
        ModelConfidence = conf
    };

    // --- Конфигурация роли ---

    [Theory]
    [InlineData("Off", AiRole.Off)]
    [InlineData("Shadow", AiRole.Shadow)]
    [InlineData("VetoOnly", AiRole.VetoOnly)]
    [InlineData("Explainer", AiRole.Explainer)]
    [InlineData("vetoonly", AiRole.VetoOnly)]
    public void Role_Parses_Case_Insensitive(string role, AiRole expected)
    {
        var config = new AIConfig { Role = role };
        Assert.Equal(expected, config.ResolvedRole);
    }

    [Fact]
    public void Disabled_Forces_Off()
    {
        var config = new AIConfig { Enabled = false, Role = "VetoOnly" };
        Assert.Equal(AiRole.Off, config.ResolvedRole);
    }

    [Fact]
    public void Invalid_Role_Fails_Validation()
    {
        var config = new AIConfig { Role = "AutoTrader" };
        Assert.Throws<InvalidOperationException>(() => config.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    public void VetoBudget_Out_Of_Range_Fails_Validation(int seconds)
    {
        var config = new AIConfig { VetoBudgetSeconds = seconds };
        Assert.Throws<InvalidOperationException>(() => config.Validate());
    }

    // --- Чистая политика veto: модель может только запретить ---

    [Fact]
    public void Inference_Failure_Is_Veto()
    {
        var signal = Sig(SignalAction.Hold, 0m);
        signal.IsInferenceFailure = true;
        Assert.True(LlmVetoService.EvaluateVeto(signal, Ctx(), out _));
    }

    [Fact]
    public void Degenerate_Confidence_Is_Veto()
    {
        var signal = Sig(SignalAction.Buy);
        signal.IsDegenerateConfidence = true;
        Assert.True(LlmVetoService.EvaluateVeto(signal, Ctx(), out _));
    }

    [Theory]
    [InlineData(SignalAction.Hold)]
    [InlineData(SignalAction.Close)]
    public void Hold_Or_Close_Is_Veto(SignalAction action)
    {
        Assert.True(LlmVetoService.EvaluateVeto(Sig(action), Ctx(), out _));
    }

    [Fact]
    public void Opposite_Direction_Is_Veto()
    {
        Assert.True(LlmVetoService.EvaluateVeto(Sig(SignalAction.Sell), Ctx(engineScore: 0.5m), out _));
        Assert.True(LlmVetoService.EvaluateVeto(Sig(SignalAction.Buy), Ctx(engineScore: -0.5m), out _));
    }

    [Fact]
    public void Aligned_Buy_Is_Not_Veto()
    {
        Assert.False(LlmVetoService.EvaluateVeto(Sig(SignalAction.Buy), Ctx(engineScore: 0.5m), out _));
    }

    // --- Детерминированный сигнал без LLM ---

    private static CalibrationContext CalCtx(decimal minConfidence = 0.5m) => new()
    {
        MinConfidence = minConfidence,
        Price = 100m,
        AtrAbsolute = 1m,
        StopLossAtrMultiplier = 2m,
        MinRiskReward = 2m,
        PriceDecimals = 2,
        AllowShort = false
    };

    private static EngineScore Score(decimal direction, decimal prior) => new()
    {
        Direction = direction,
        PriorConfidence = prior
    };

    [Fact]
    public void Strong_Long_Setup_Produces_Buy_Without_Model()
    {
        var calibrator = new SignalCalibrator();
        var signal = calibrator.BuildEngineSignal("BTCUSDT", Score(0.6m, 0.7m), CalCtx());

        Assert.Equal(SignalAction.Buy, signal.Action);
        Assert.Equal("EngineOnly", signal.ConfidenceSource);
        Assert.Equal(0m, signal.ModelConfidence);
        Assert.Equal(100m, signal.EntryPrice);
        Assert.Equal(98m, signal.StopLoss);   // entry − 2·ATR
        Assert.Equal(104m, signal.TakeProfit); // entry + 2·(2·ATR)
    }

    [Fact]
    public void Weak_Setup_Stays_Hold()
    {
        var calibrator = new SignalCalibrator();
        var signal = calibrator.BuildEngineSignal("BTCUSDT", Score(0.6m, 0.3m), CalCtx(minConfidence: 0.5m));
        Assert.Equal(SignalAction.Hold, signal.Action);
    }

    [Fact]
    public void Flat_Setup_Stays_Hold()
    {
        var calibrator = new SignalCalibrator();
        var signal = calibrator.BuildEngineSignal("BTCUSDT", Score(0.0m, 0.9m), CalCtx());
        Assert.Equal(SignalAction.Hold, signal.Action);
    }

    [Fact]
    public void Short_Disallowed_On_Spot_Stays_Hold()
    {
        var calibrator = new SignalCalibrator();
        var signal = calibrator.BuildEngineSignal("BTCUSDT", Score(-0.6m, 0.7m), CalCtx());
        Assert.Equal(SignalAction.Hold, signal.Action);
    }
}
