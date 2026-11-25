using AITradingSystem.Common.Configuration;
using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.Trading;
using AITradingSystem.Services.AI;
using AITradingSystem.Services.Trading;

namespace AITradingSystem.Tests;

public class PromptBuilderTests
{
    private static AiRequestContext Context(AiRole role = AiRole.VetoOnly) => new()
    { Role = role, EngineDirection = "LONG", EngineScore = 0.4m, EngineFacts = new[] { "adx_raw=15" } };
    private static Dictionary<string, decimal> Indicators() => new() { ["price"] = 100m, ["adx_raw"] = 15m };

    [Fact] public void Off_Does_Not_Build_Prompt() => Assert.Empty(new PromptBuilder().BuildTradingPrompt("DASHUSDT", Indicators(), null, false, Context(AiRole.Off)));
    [Fact] public void Veto_Uses_Only_Veto_Contract()
    {
        var prompt = new PromptBuilder().BuildTradingPrompt("DASHUSDT", Indicators(), null, false, Context());
        Assert.Contains("\"veto\"", prompt); Assert.Contains("\"risk_flags\"", prompt);
        Assert.Contains("UNTRUSTED NEWS DATA", prompt); Assert.Contains("direction_mismatch", prompt);
        Assert.DoesNotContain("entry_price", prompt); Assert.DoesNotContain("Execution threshold", prompt);
    }
    [Fact] public void Shadow_Has_No_Execution_Authority() => Assert.Contains("SHADOW: observe only", new PromptBuilder().BuildTradingPrompt("DASHUSDT", Indicators(), null, false, Context(AiRole.Shadow)));
    [Fact] public void Explainer_Has_No_Close_Authority() => Assert.Contains("Do not open, close or modify", new PromptBuilder().BuildTradingPrompt("DASHUSDT", Indicators(), null, false, Context(AiRole.Explainer)));
    [Theory] [InlineData(SymbolUniverseClass.Stablecoin, "stablecoin_no_depeg_strategy")]
    [InlineData(SymbolUniverseClass.Benchmark, "benchmark_context_only")]
    public void Excluded_Symbol_Has_No_Prompt(SymbolUniverseClass kind, string reason)
    {
        var context = Context() with { SymbolClass = kind, SymbolExclusionReason = reason };
        Assert.False(PromptBuilder.CanBuild(context));
        Assert.Empty(new PromptBuilder().BuildTradingPrompt("BTCUSDT", Indicators(), null, false, context));
    }
    [Fact] public void Unavailable_Symbol_Has_No_Prompt() => Assert.Empty(new PromptBuilder().BuildTradingPrompt("DASHUSDT", Indicators(), null, false, Context() with { SymbolExclusionReason = "exchange_not_trading" }));
    [Fact] public void Repair_Keeps_Role_Contract()
    {
        var builder = new PromptBuilder(); var context = Context();
        var repair = builder.BuildRepairPrompt(builder.BuildTradingPrompt("DASHUSDT", Indicators(), null, false, context), "{}", "invalid", false, context);
        Assert.Contains("veto_reason", repair); Assert.DoesNotContain("action, confidence, entry_price", repair);
    }
    [Fact] public void Opposite_Direction_Is_Veto()
    {
        var signal = new TradingSignal { Veto = false, RiskFlags = new[] { "direction_mismatch" } };
        Assert.True(LlmVetoService.EvaluateVeto(signal, Context(), out var reason));
        Assert.Contains("opposite engine direction", reason);
    }
    [Fact] public void Missing_Veto_Contract_Fails_Closed() => Assert.True(LlmVetoService.EvaluateVeto(new TradingSignal(), Context(), out _));
    [Fact] public void Shadow_Veto_Does_Not_Authorize_Or_Block()
    {
        Assert.False(LlmVetoService.EvaluateVeto(new TradingSignal { Veto = true }, Context(AiRole.Shadow), out var reason));
        Assert.Contains("observation only", reason);
    }
}
