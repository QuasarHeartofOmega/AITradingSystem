using System.Diagnostics;
using System.Text;
using AITradingSystem.Common.Logging;
using LLama;
using LLama.Common;
using LLama.Exceptions;
using LLama.Native;
using LLama.Sampling;
using Microsoft.Extensions.Logging;

namespace AITradingSystem.Services.AI;

/// <summary>Параметры одной генерации (сэмплеры + бюджеты).</summary>
public sealed class KvGenerationOptions
{
    public int MaxTokens { get; init; } = 400;

    /// <summary>Жёсткий бюджет think-токенов; 0 — не ограничивать отдельно. При превышении
    /// блок рассуждений принудительно закрывается декодированием закрывающего тега в KV.</summary>
    public int ThinkingBudgetTokens { get; init; }

    public float Temperature { get; init; } = 0.6f;
    public float TopP { get; init; } = 0.95f;
    public int TopK { get; init; } = 20;
    public float MinP { get; init; }
    public float RepeatPenalty { get; init; } = 1.05f;
    public float PresencePenalty { get; init; } = 0.5f;
    public float FrequencyPenalty { get; init; }
    public int PenaltyLastN { get; init; } = 256;
    public bool DetectDegenerate { get; init; } = true;

    public IReadOnlyList<string> AntiPrompts { get; init; } =
        new[] { "<|im_end|>", "<|im_start|>", "<|endoftext|>" };
}

/// <summary>Результат генерации с диагностикой KV/бюджетов.</summary>
public sealed class KvGenerationResult
{
    public string Text { get; init; } = string.Empty;

    /// <summary>Не-null — вывод признан мусором (сторож), KV уже очищен.</summary>
    public string? DegenerateReason { get; init; }

    public bool TimedOut { get; init; }
    public bool ForcedThinkClose { get; init; }
    public bool HitMaxTokens { get; init; }
    public int ThinkTokens { get; init; }
    public int PromptTokens { get; init; }
    public int ReusedPrefixTokens { get; init; }
    public int GeneratedTokens { get; init; }
    public double ElapsedMs { get; init; }
}

/// <summary>Промпт не влезает в контекст вместе с резервом под генерацию.</summary>
public sealed class PromptTooLongException : Exception
{
    public int PromptTokens { get; }
    public int ContextSize { get; }

    public PromptTooLongException(int promptTokens, int contextSize)
        : base($"Промпт {promptTokens} токенов не влезает в контекст {contextSize} с резервом под генерацию")
    {
        PromptTokens = promptTokens;
        ContextSize = contextSize;
    }
}

/// <summary>
/// Генератор с ПОСТОЯННЫМ KV-контекстом (замена StatelessExecutor, который пересоздавал
/// контекст на каждый запрос — медленное пересоздание буферов + полный префилл каждый раз).
///
/// Управление памятью («чистить старое — приоритет, иначе сброс»):
///  - перед запросом сохраняется общий префикс с прошлым промптом (системный промпт ≈ 1000 токенов),
///    старый хвост удаляется из KV (MemorySequenceRemove) — это и есть «чистка старого»;
///  - после генерации хвост ответа удаляется, в KV остаётся только промпт;
///  - при деградации вывода/ошибке — полная чистка KV (MemoryClear);
///  - HardReset — пересоздание контекста (полный сброс), если чистка не помогла.
///
/// Дополнительно: жёсткий бюджет рассуждений (принудительное закрытие think-блока),
/// потоковый сторож мусора (DegenerateOutputDetector), сэмплеры по рекомендациям Qwen3.
/// Логика цикла генерации повторяет StatelessExecutor из LLamaSharp 0.30.0.
/// НЕ потокобезопасен — вызывающий держит лок инференса.
/// </summary>
public sealed class KvManagedExecutor : IDisposable
{
    private readonly LLamaWeights _weights;
    private readonly ModelParams _params;
    private readonly ILogger _logger;
    private readonly LLamaBatch _batch = new();
    private readonly bool _prefixCacheEnabled;
    private readonly int _minReusePrefix;

    private LLamaContext _context;
    private LLamaToken[] _cachedPrompt = Array.Empty<LLamaToken>();
    private bool _canShift;

    public int ContextSize => (int)_context.ContextSize;

    public KvManagedExecutor(
        LLamaWeights weights,
        ModelParams @params,
        ILogger logger,
        bool prefixCache = true,
        int minReusePrefix = 64)
    {
        _weights = weights ?? throw new ArgumentNullException(nameof(weights));
        _params = @params ?? throw new ArgumentNullException(nameof(@params));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _prefixCacheEnabled = prefixCache;
        _minReusePrefix = Math.Max(1, minReusePrefix);

        _context = weights.CreateContext(@params, logger);
        _canShift = _context.NativeHandle.MemoryCanShift;
        AiLog.Backend($"KV: постоянный контекст создан (ctx={ContextSize}, prefixCache={(_prefixCacheEnabled && _canShift)}, canShift={_canShift})");
    }

    /// <summary>Число токенов промпта (для проверки влезания до генерации).</summary>
    public int CountTokens(string prompt) =>
        _context.Tokenize(prompt, addBos: true, special: true).Length;

    /// <summary>Приоритетная чистка: полный сброс KV без пересоздания контекста.</summary>
    public void ClearKv(string reason)
    {
        _context.NativeHandle.MemoryClear(data: true);
        _cachedPrompt = Array.Empty<LLamaToken>();
        AiLog.Backend($"KV: полная чистка ({reason})");
    }

    /// <summary>Полный сброс: пересоздание контекста, если чистка KV не помогла.</summary>
    public void HardReset(string reason)
    {
        _context.Dispose();
        _context = _weights.CreateContext(_params, _logger);
        _canShift = _context.NativeHandle.MemoryCanShift;
        _cachedPrompt = Array.Empty<LLamaToken>();
        AiLog.Backend($"KV: контекст пересоздан ({reason})");
    }

    public async Task<KvGenerationResult> GenerateAsync(
        string prompt, KvGenerationOptions opts, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var promptTokens = _context.Tokenize(prompt, addBos: true, special: true);

        if (promptTokens.Length + opts.MaxTokens + 8 > ContextSize)
            throw new PromptTooLongException(promptTokens.Length, ContextSize);

        var reuse = PrepareKv(promptTokens);

        try
        {
            var result = await RunGenerationAsync(promptTokens, reuse, opts, ct);
            FinishKv(promptTokens, result);
            sw.Stop();
            return new KvGenerationResult
            {
                Text = result.Text,
                DegenerateReason = result.DegenerateReason,
                TimedOut = result.TimedOut,
                ForcedThinkClose = result.ForcedThinkClose,
                HitMaxTokens = result.HitMaxTokens,
                ThinkTokens = result.ThinkTokens,
                PromptTokens = promptTokens.Length,
                ReusedPrefixTokens = reuse,
                GeneratedTokens = result.GeneratedTokens,
                ElapsedMs = sw.Elapsed.TotalMilliseconds
            };
        }
        catch
        {
            // Любая ошибка (decode error и т.п.) — KV мог остаться грязным: чистим.
            SafeClearAfterFailure();
            throw;
        }
    }

    /// <summary>
    /// Готовит KV к новому промпту: переиспользует общий префикс, удаляет старый хвост.
    /// Возвращает длину переиспользованного префикса.
    /// </summary>
    private int PrepareKv(LLamaToken[] promptTokens)
    {
        var reuse = 0;

        if (_prefixCacheEnabled && _canShift && _cachedPrompt.Length > 0)
        {
            reuse = CommonPrefixLength(_cachedPrompt, promptTokens);

            if (reuse >= _minReusePrefix)
            {
                // Не оставляем «старое»: хвост прошлого промпта сверх общего префикса удаляем.
                if (reuse >= promptTokens.Length)
                    reuse = promptTokens.Length - 1; // нужно декодировать хотя бы 1 токен (logits)

                _context.NativeHandle.MemorySequenceRemove(LLamaSeqId.Zero, reuse, _cachedPrompt.Length);
                AiLog.Backend($"KV: reuse префикса {reuse}/{promptTokens.Length} токенов, старый хвост удалён");
                return reuse;
            }

            reuse = 0;
        }

        _context.NativeHandle.MemoryClear(data: true);
        return 0;
    }

    private void FinishKv(LLamaToken[] promptTokens, GenerationState result)
    {
        if (result.DegenerateReason != null || result.TimedOut || !_canShift)
        {
            // Деградация/таймаут — полная чистка (приоритет), кэш префикса недействителен.
            _context.NativeHandle.MemoryClear(data: true);
            _cachedPrompt = Array.Empty<LLamaToken>();
            if (result.DegenerateReason != null)
                AiLog.Backend($"KV: DEGENERATE — {result.DegenerateReason}; KV очищен");
            return;
        }

        // Здоровая генерация: удаляем хвост ответа, оставляем в KV только промпт для reuse.
        _context.NativeHandle.MemorySequenceRemove(LLamaSeqId.Zero, promptTokens.Length, result.NPast);
        _cachedPrompt = promptTokens;
    }

    private void SafeClearAfterFailure()
    {
        try
        {
            _context.NativeHandle.MemoryClear(data: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "KV: чистка после ошибки не удалась");
        }
        _cachedPrompt = Array.Empty<LLamaToken>();
    }

    private static int CommonPrefixLength(LLamaToken[] a, LLamaToken[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && a[i] == b[i])
            i++;
        return i;
    }

    // ------------------------------------------------------------------
    // Цикл генерации (по StatelessExecutor 0.30.0 + бюджеты/сторож)
    // ------------------------------------------------------------------

    private sealed class GenerationState
    {
        public string Text = string.Empty;
        public string? DegenerateReason;
        public bool TimedOut;
        public bool ForcedThinkClose;
        public bool HitMaxTokens;
        public int ThinkTokens;
        public int GeneratedTokens;
        public int NPast;
    }

    private async Task<GenerationState> RunGenerationAsync(
        LLamaToken[] promptTokens, int reuse, KvGenerationOptions opts, CancellationToken ct)
    {
        var state = new GenerationState();

        // Префилл непрошенного куска промпта (батчами внутри DecodeAsync).
        var toDecode = new List<LLamaToken>(promptTokens.Length - reuse);
        for (var i = reuse; i < promptTokens.Length; i++)
            toDecode.Add(promptTokens[i]);

        var nPast = reuse;
        var (r, _, past) = await _context.DecodeAsync(toDecode, LLamaSeqId.Zero, _batch, nPast, ct);
        if (r != DecodeResult.Ok)
            throw new LLamaDecodeError(r);
        nPast = past;

        using var pipeline = new DefaultSamplingPipeline
        {
            Temperature = opts.Temperature,
            TopP = opts.TopP,
            TopK = opts.TopK,
            MinP = opts.MinP,
            RepeatPenalty = opts.RepeatPenalty,
            PresencePenalty = opts.PresencePenalty,
            FrequencyPenalty = opts.FrequencyPenalty,
            PenaltyCount = opts.PenaltyLastN,
            PenalizeNewline = false // JSON нужны переносы — не штрафуем
        };

        // DecodeSpecialTokens=true: мусор спецтокенов (#24812) должен быть ВИДИМ сторожу,
        // иначе он декодируется в пустоту и молча съедает бюджет генерации.
        var decoder = new StreamingTokenDecoder(_context) { DecodeSpecialTokens = true };
        var anti = new AntipromptProcessor(opts.AntiPrompts);
        var detector = new DegenerateOutputDetector(opts.DetectDegenerate);
        var sb = new StringBuilder();

        var thinkOpen = false;
        var thinkClosed = false;
        var forcedClose = false;

        try
        {
            for (var i = 0; i < opts.MaxTokens; i++)
            {
                ct.ThrowIfCancellationRequested();

                var id = pipeline.Sample(_context.NativeHandle, _batch.TokenCount - 1);
                if (id.IsEndOfGeneration(_weights.Vocab))
                    break;

                state.GeneratedTokens++;
                decoder.Add(id);
                var piece = decoder.Read();

                if (piece.Length > 0)
                {
                    sb.Append(piece);

                    if (anti.Add(piece))
                        break; // нормальный стоп по <|im_end|> и т.п.

                    if (detector.Feed(piece))
                    {
                        state.DegenerateReason = detector.Reason;
                        break;
                    }

                    if (!thinkClosed)
                    {
                        var text = sb.ToString();
                        if (text.Contains("</think>", StringComparison.OrdinalIgnoreCase))
                            thinkClosed = true;
                        else if (!thinkOpen && text.Contains("<think>", StringComparison.OrdinalIgnoreCase))
                            thinkOpen = true;
                    }
                }

                if (thinkOpen && !thinkClosed)
                    state.ThinkTokens++;

                // Жёсткий бюджет рассуждений: принудительно закрываем think-блок,
                // декодируя закрывающий тег прямо в KV (приём reasoning-budget llama-server).
                if (opts.ThinkingBudgetTokens > 0 && thinkOpen && !thinkClosed &&
                    state.ThinkTokens >= opts.ThinkingBudgetTokens)
                {
                    var closeTokens = _context.Tokenize("\n</think>\n\n", addBos: false, special: false);
                    _batch.Clear();
                    for (var t = 0; t < closeTokens.Length; t++)
                        _batch.Add(closeTokens[t], nPast++, LLamaSeqId.Zero, t == closeTokens.Length - 1);

                    var cr = await _context.DecodeAsync(_batch, ct);
                    if (cr != DecodeResult.Ok)
                        throw new LLamaDecodeError(cr);

                    sb.Append("\n</think>\n\n");
                    thinkClosed = true;
                    forcedClose = true;
                    AiLog.Backend($"KV: think budget {opts.ThinkingBudgetTokens} исчерпан — блок закрыт принудительно");
                    continue; // следующий Sample пойдёт по logits закрывающего тега → JSON
                }

                _batch.Clear();
                _batch.Add(id, nPast++, LLamaSeqId.Zero, true);
                var dr = await _context.DecodeAsync(_batch, ct);
                if (dr != DecodeResult.Ok)
                    throw new LLamaDecodeError(dr);

                if (i == opts.MaxTokens - 1)
                    state.HitMaxTokens = true;
            }
        }
        catch (OperationCanceledException)
        {
            state.TimedOut = true; // частичный ответ сохраняем — вызывающий попробует распарсить
        }

        state.Text = sb.ToString();
        state.ForcedThinkClose = forcedClose;
        state.NPast = nPast;
        return state;
    }

    public void Dispose()
    {
        _cachedPrompt = Array.Empty<LLamaToken>();
        _context.Dispose();
    }
}
