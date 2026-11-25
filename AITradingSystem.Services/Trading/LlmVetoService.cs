using AITradingSystem.Common.Configuration;
using AITradingSystem.Common.Logging;
using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.Trading;
using AITradingSystem.Services.AI;
using AITradingSystem.Services.Monitoring;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace AITradingSystem.Services.Trading;

/// <summary>
/// P0.5: результат асинхронного мнения LLM по закрытой свече.
/// Veto=true запрещает вход; в VetoOnly отсутствие готового вердикта тоже не разрешает вход
/// (fail-closed), но детерминированный цикл НИКОГДА не ждёт инференс.
/// </summary>
public sealed record LlmVetoVerdict(
    string Symbol,
    DateTime CandleCloseUtc,
    string ConfigHash,
    bool Veto,
    string Reason,
    double LatencyMs,
    DateTime CompletedAtUtc,
    bool WithinBudget,
    string Role);

/// <summary>
/// P0.5: LLM вне последовательного критического пути.
/// - вызов асинхронный (fire-and-forget), один in-flight на символ;
/// - кэш вердиктов по (symbol, candle_close_utc, config_hash);
/// - жёсткий бюджет AI:VetoBudgetSeconds; опоздавший вердикт логируется, но не применяется;
/// - timeout/parse fail/degenerate → Veto=true (не разрешают вход), защиту не блокируют;
/// - thinking отключается для veto/shadow-запросов (AiRequestContext.DisableThinking).
/// Сервис не создаёт ордеров и не закрывает позиции.
/// </summary>
public sealed class LlmVetoService : IAsyncDisposable
{
    private readonly IAIModel _aiModel;
    private readonly AIConfig _config;
    private readonly MetricsCollector _metrics;
    private readonly ILogger<LlmVetoService> _logger;

    private readonly ConcurrentDictionary<string, LlmVetoVerdict> _verdicts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _tasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _shutdownCts = new();

    private const int MaxCachedVerdicts = 512;

    public LlmVetoService(IAIModel aiModel, AppConfig config, MetricsCollector metrics, ILogger<LlmVetoService> logger)
    {
        _aiModel = aiModel ?? throw new ArgumentNullException(nameof(aiModel));
        _config = (config ?? throw new ArgumentNullException(nameof(config))).AI;
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private static string Key(string symbol, DateTime candleCloseUtc, string configHash) =>
        $"{symbol.ToUpperInvariant()}|{candleCloseUtc:O}|{configHash}";

    /// <summary>Готовый вердикт по ключу (symbol, закрытая свеча, config hash), если есть.</summary>
    public bool TryGetVerdict(string symbol, DateTime candleCloseUtc, string configHash, out LlmVetoVerdict? verdict) =>
        _verdicts.TryGetValue(Key(symbol, candleCloseUtc, configHash), out verdict);

    /// <summary>
    /// Запросить асинхронное мнение по закрытой свече. Не блокирует вызывающий цикл.
    /// Повторный запрос по тому же ключу или при занятом символе игнорируется.
    /// </summary>
    public void RequestOpinion(
        string symbol,
        DateTime candleCloseUtc,
        string configHash,
        Dictionary<string, decimal> indicators,
        string? newsContext,
        AiRequestContext context)
    {
        if (_config.ResolvedRole == AiRole.Off || !_aiModel.IsLoaded)
            return;

        var key = Key(symbol, candleCloseUtc, configHash);
        if (_verdicts.ContainsKey(key) || !_inFlight.TryAdd(symbol, 0))
            return;

        // Кэш не должен расти бесконечно: старые вердикты бесполезны (свеча устарела).
        if (_verdicts.Count > MaxCachedVerdicts)
        {
            foreach (var old in _verdicts.OrderBy(kv => kv.Value.CompletedAtUtc).Take(_verdicts.Count - MaxCachedVerdicts / 2))
                _verdicts.TryRemove(old.Key, out _);
        }

        _metrics.RecordLlmOpinionRequest();
        var task = Task.Run(() => RunOpinionAsync(key, symbol, candleCloseUtc, configHash,
            indicators, newsContext, context));
        _tasks[symbol] = task;
    }

    private async Task RunOpinionAsync(
        string key, string symbol, DateTime candleCloseUtc, string configHash,
        Dictionary<string, decimal> indicators, string? newsContext, AiRequestContext context)
    {
        var role = _config.ResolvedRole;
        var budget = TimeSpan.FromSeconds(_config.VetoBudgetSeconds);
        var started = DateTime.UtcNow;
        LlmVetoVerdict verdict;

        try
        {
            using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
            budgetCts.CancelAfter(budget);

            // P0.5: veto/shadow без thinking — короткий ответ, жёсткий бюджет.
            var request = new AiRequestContext
            {
                HasPosition = context.HasPosition,
                PositionSide = context.PositionSide,
                EntryPrice = context.EntryPrice,
                Quantity = context.Quantity,
                UnrealizedPnlPercent = context.UnrealizedPnlPercent,
                StopLoss = context.StopLoss,
                TakeProfit = context.TakeProfit,
                MinutesInPosition = context.MinutesInPosition,
                AllowShort = context.AllowShort,
                MinRiskReward = context.MinRiskReward,
                MinConfidence = context.MinConfidence,
                StopLossAtrMultiplier = context.StopLossAtrMultiplier,
                EngineScore = context.EngineScore,
                EngineDirection = context.EngineDirection,
                EnginePriorConfidence = context.EnginePriorConfidence,
                EngineReasons = context.EngineReasons,
                EngineFacts = context.EngineFacts,
                PriceDecimals = context.PriceDecimals,
                TickSize = context.TickSize,
                DisableThinking = true
            };

            var signal = await _aiModel.GetTradingSignalAsync(symbol, indicators, newsContext, request, budgetCts.Token);
            var latency = (DateTime.UtcNow - started).TotalMilliseconds;
            _metrics.RecordInferenceTime(latency);

            var withinBudget = latency <= budget.TotalMilliseconds && !_shutdownCts.IsCancellationRequested;
            var veto = EvaluateVeto(signal, context, out var reason);

            verdict = new LlmVetoVerdict(symbol, candleCloseUtc, configHash, veto, reason,
                latency, DateTime.UtcNow, withinBudget, role.ToString());
            _metrics.RecordLlmOpinionResult(withinBudget, veto);
        }
        catch (OperationCanceledException)
        {
            var latency = (DateTime.UtcNow - started).TotalMilliseconds;
            verdict = new LlmVetoVerdict(symbol, candleCloseUtc, configHash, true,
                $"таймаут LLM (> {_config.VetoBudgetSeconds}s) — fail-closed veto", latency,
                DateTime.UtcNow, false, role.ToString());
            _metrics.RecordLlmOpinionTimeout();
        }
        catch (Exception ex)
        {
            var latency = (DateTime.UtcNow - started).TotalMilliseconds;
            verdict = new LlmVetoVerdict(symbol, candleCloseUtc, configHash, true,
                $"ошибка LLM: {ex.Message} — fail-closed veto", latency,
                DateTime.UtcNow, false, role.ToString());
            _metrics.RecordLlmOpinionFailure();
            _logger.LogWarning(ex, "P0.5: асинхронное мнение LLM по {Symbol} завершилось ошибкой", symbol);
        }
        finally
        {
            _inFlight.TryRemove(symbol, out _);
        }

        _verdicts[key] = verdict;

        if (!verdict.WithinBudget)
        {
            // Опоздавший вердикт не применяется к уже принятому решению — только аудит.
            TradeLog.LlmVetoLate(symbol, verdict.Reason, verdict.LatencyMs);
            _metrics.RecordLlmOpinionLate();
        }

        TradeLog.LlmVetoResult(symbol, role.ToString(), verdict.Veto, verdict.Reason,
            verdict.LatencyMs, verdict.WithinBudget, candleCloseUtc);
    }

    /// <summary>
    /// Чистая политика veto: модель может только запретить. HOLD/CLOSE/противоположное действие,
    /// отказ инференса и дегенерат — veto. BUY/SELL в сторону сетапа — не veto.
    /// </summary>
    public static bool EvaluateVeto(TradingSignal signal, AiRequestContext context, out string reason)
    {
        if (signal.IsInferenceFailure)
        {
            reason = $"отказ инференса: {signal.Reasoning}";
            return true;
        }

        if (signal.IsDegenerateConfidence)
        {
            reason = "дегенеративная уверенность модели";
            return true;
        }

        if (signal.Action is SignalAction.Hold or SignalAction.Close)
        {
            reason = $"модель против входа ({signal.Action}, conf={signal.Confidence:0.##}): {signal.Reasoning}";
            return true;
        }

        var engineLong = context.EngineScore is > 0.05m;
        var engineShort = context.EngineScore is < -0.05m;
        if ((signal.Action == SignalAction.Buy && engineShort) ||
            (signal.Action == SignalAction.Sell && engineLong))
        {
            reason = $"модель предлагает {signal.Action} против направления движка {context.EngineDirection}";
            return true;
        }

        reason = $"модель не возражает ({signal.Action}, conf={signal.Confidence:0.##})";
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        _shutdownCts.Cancel();
        try
        {
            await Task.WhenAll(_tasks.Values).ConfigureAwait(false);
        }
        catch
        {
            // Завершение процесса: ошибки фоновых мнений уже залогированы.
        }
        _shutdownCts.Dispose();
    }
}
