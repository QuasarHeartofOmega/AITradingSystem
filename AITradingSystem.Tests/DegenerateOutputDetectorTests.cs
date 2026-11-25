using AITradingSystem.Services.AI;

namespace AITradingSystem.Tests;

/// <summary>
/// Юнит-тесты сторожа деградации вывода (llama.cpp #24812 / зацикливания Qwen3).
/// Класс чистый, без нативных зависимостей — тесты не требуют GGUF/llama.dll.
/// </summary>
public class DegenerateOutputDetectorTests
{
    [Fact]
    public void LongQuestionMarkRun_IsDegenerate()
    {
        var detector = new DegenerateOutputDetector();

        var flagged = detector.Feed(new string('?', 40));

        Assert.True(flagged);
        Assert.True(detector.IsDegenerate);
        Assert.NotNull(detector.Reason);
        Assert.Contains("одинаковых символов", detector.Reason);
    }

    [Fact]
    public void QuestionMarkRun_AcrossChunks_IsDegenerate()
    {
        var detector = new DegenerateOutputDetector();

        Assert.False(detector.Feed(new string('?', 20)));
        Assert.True(detector.Feed(new string('?', 20)));
    }

    [Fact]
    public void UnusedSpecialTokenCycle_IsDegenerate()
    {
        var detector = new DegenerateOutputDetector();
        var cycle = string.Concat(Enumerable.Repeat("<unused49>", 6));

        var flagged = detector.Feed(cycle);

        Assert.True(flagged);
        Assert.Contains("спецтокенов", detector.Reason);
    }

    [Fact]
    public void RepeatedNumericBlock_IsDegenerate()
    {
        var detector = new DegenerateOutputDetector();
        var loop = string.Concat(Enumerable.Repeat("0.45, ", 8));

        var flagged = detector.Feed(loop);

        Assert.True(flagged);
        Assert.Contains("цикл", detector.Reason);
    }

    [Fact]
    public void ReplacementCharacters_AreDegenerate()
    {
        var detector = new DegenerateOutputDetector();

        var flagged = detector.Feed("\uFFFD\uFFFD\uFFFD\uFFFD");

        Assert.True(flagged);
        Assert.Contains("U+FFFD", detector.Reason);
    }

    [Fact]
    public void CleanJson_IsNotDegenerate()
    {
        var detector = new DegenerateOutputDetector();
        const string json = "{\"action\":\"HOLD\",\"confidence\":0.5,\"entry_price\":null,\"stop_loss\":null,\"take_profit\":null,\"reasoning\":\"Mixed setup, no independent confirmation.\"}";

        var flagged = detector.Feed(json);

        Assert.False(flagged);
        Assert.False(detector.IsDegenerate);
        Assert.Null(detector.Reason);
    }

    [Fact]
    public void CleanThinkThenJson_IsNotDegenerate()
    {
        var detector = new DegenerateOutputDetector();
        const string response = "No asset-specific news; structure is mixed, so HOLD with neutral confidence.\n{\"action\":\"HOLD\",\"confidence\":0.5}";

        var flagged = detector.Feed(response);

        Assert.False(flagged);
        Assert.Null(detector.Reason);
    }

    [Fact]
    public void DisabledDetector_DoesNotFlag()
    {
        var detector = new DegenerateOutputDetector(enabled: false);

        Assert.False(detector.Feed(new string('?', 100)));
        Assert.False(detector.IsDegenerate);
        Assert.Null(detector.Reason);
    }

    [Fact]
    public void OnceDegenerate_StaysDegenerate()
    {
        var detector = new DegenerateOutputDetector();
        Assert.True(detector.Feed(new string('?', 40)));

        Assert.True(detector.Feed("ok"));
        Assert.True(detector.IsDegenerate);
    }
}
