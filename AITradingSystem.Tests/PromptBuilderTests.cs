using AITradingSystem.Services.AI;
using AITradingSystem.Services.Analysis;

namespace AITradingSystem.Tests;

public class PromptBuilderTests
{
    private static Dictionary<string, decimal> Indicators() => new()
    {
        ["price"] = 100m,
        ["ema_align"] = 0m,
        ["supertrend"] = -1m,
        ["adx_raw"] = 15m,
        ["volume_ratio"] = 0.5m,
        ["atr_pct"] = 0.002m,
        ["bb_pos"] = 0.9m,
        ["rsi_raw"] = 50m,
        ["macd_hist"] = -0.05m
    };

    [Fact]
    public void Prompt_Uses_Independent_Rubric_And_Does_Not_Reapply_Engine_Penalties()
    {
        var builder = new PromptBuilder();
        var context = new AiRequestContext
        {
            EngineScore = -0.2m,
            EngineDirection = "SHORT",
            EnginePriorConfidence = 0.35m,
            EngineFacts = new[] { "adx_raw=15", "volume_ratio=0.5", "bb_pos=0.9" },
            EngineReasons = new[] { "слабый тренд: ADX 15 < 20 (штраф ×0.6)" }
        };

        var prompt = builder.BuildTradingPrompt("BTCUSDT", Indicators(), newsContext: null, enableThinking: false, context);

        Assert.Contains("independent assessment", prompt);
        Assert.Contains("Do NOT start from engine_prior_confidence", prompt);
        Assert.Contains("Do NOT subtract again", prompt);
        Assert.Contains("neutral facts: adx_raw=15; volume_ratio=0.5; bb_pos=0.9", prompt);
        Assert.DoesNotContain("Start from engine_prior_confidence", prompt);
        Assert.DoesNotContain("Subtract 0.10 to 0.20 for conflicts", prompt);
        Assert.DoesNotContain("штраф ×0.6", prompt);
    }

    [Fact]
    public void PreScore_Exposes_Neutral_Facts_Separately_From_Audit_Reasons()
    {
        var calibrator = new SignalCalibrator();
        var score = calibrator.PreScore(Indicators());

        Assert.Contains("adx_raw=15", score.Facts);
        Assert.Contains("volume_ratio=0.5", score.Facts);
        Assert.Contains("bb_pos=0.9", score.Facts);
        Assert.All(score.Facts, f => Assert.DoesNotContain("штраф", f));
        Assert.Contains(score.Reasons, r => r.Contains("штраф"));
    }

    [Fact]
    public void Repair_Prompt_Keeps_Independent_Rubric()
    {
        var builder = new PromptBuilder();
        var original = builder.BuildTradingPrompt("BTCUSDT", Indicators(), null, false, new AiRequestContext());
        var repair = builder.BuildRepairPrompt(original, "{}", "invalid JSON", enableThinking: false);

        Assert.Contains("independent rubric", repair);
        Assert.Contains("do not start from the engine prior", repair);
    }
}
