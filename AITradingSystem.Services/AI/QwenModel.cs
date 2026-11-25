using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AITradingSystem.Common.Configuration;
using AITradingSystem.Common.Logging;
using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.Trading;
using LLama;
using LLama.Common;
using LLama.Native;
using Microsoft.Extensions.Logging;

namespace AITradingSystem.Services.AI;

/// <summary>
/// AI-модель Qwen через LLamaSharp (FR-4.1).
///
/// Возможности:
///  - автовыбор бэкенда под железо: CUDA (NVIDIA) → Vulkan (AMD/Intel) → CPU;
///  - режим рассуждений (thinking) с отдельным бюджетом токенов и самопроверкой;
///  - полное логирование: промпт → сырой ответ → рассуждения → распарсенный сигнал (logs/ai-*.log);
///  - «ремонт» невалидного JSON вместо слепого повтора;
///  - учёт открытой позиции и спот-ограничений (сигнал CLOSE, запрет шорта).
/// KvManagedExecutor: постоянный KV-контекст с чисткой старого хвоста и переиспользованием
/// префикса промпта; при деградации вывода — чистка KV → сброс контекста → перезагрузка → CPU.
/// </summary>
public sealed class QwenModel : IAIModel
{
    private readonly AIConfig _config;
    private readonly ILogger<QwenModel> _logger;
    private readonly PromptBuilder _promptBuilder;
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private LLamaWeights? _weights;
    private ModelParams? _params;
    private KvManagedExecutor? _executor;
    private GpuDetectionResult? _gpu;
    private GgufModelInfo? _modelInfo;
    private int _effectiveContextSize;
    private int _effectiveGpuLayers;
    private string? _loadedModelPath;
    private int _loadedThreads;
    private int _consecutiveDegenerate;
    private bool _weightsReloadedOnce;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public bool IsLoaded => _weights != null && _executor != null;
    public double LastInferenceTimeMs { get; private set; }

    /// <summary>Фактически выбранный бэкенд инференса.</summary>
    public AiBackend ActiveBackend { get; private set; } = AiBackend.Cpu;

    /// <summary>Последний блок рассуждений модели (для UI/диагностики).</summary>
    public string LastThinking { get; private set; } = string.Empty;

    /// <summary>Фактический размер контекста после AutoFit (A5).</summary>
    public int EffectiveContextSize => _effectiveContextSize > 0 ? _effectiveContextSize : _config.ContextSize;

    /// <summary>Слоёв, запрошенных у llama при загрузке (после AutoFit/OOM-ретраев).</summary>
    public int EffectiveGpuLayers => _effectiveGpuLayers;

    /// <summary>Реальный оффлоад из нативного лога llama.cpp (null — строки не было).</summary>
    public int? OffloadedLayers => GgmlNative.OffloadedLayers;

    /// <summary>Всего слоёв модели по нативному логу.</summary>
    public int? TotalLayers => GgmlNative.TotalLayers ?? _modelInfo?.BlockCount;

    /// <summary>Имя GPU для UI.</summary>
    public string GpuName => _gpu?.GpuName ?? string.Empty;

    /// <summary>VRAM всего/свободно, MiB (0 — неизвестно).</summary>
    public long VramTotalMb => _gpu?.VramTotalMb ?? 0;

    /// <summary>Свободная VRAM на момент последней разведки, MiB.</summary>
    public long VramFreeMb => _gpu?.VramFreeMb ?? 0;

    /// <summary>Предупреждение о деградации (CPU-фолбэк, частичный оффлоад) — для UI.</summary>
    public string? LoadWarning { get; private set; }

    /// <summary>Строка для панели «AI/GPU» в Dashboard: backend, GPU, VRAM, offload, ctx.</summary>
    public string BackendSummary
    {
        get
        {
            var offload = OffloadedLayers.HasValue
                ? $"offload {OffloadedLayers}/{TotalLayers ?? _modelInfo?.BlockCount ?? 0}"
                : $"gpuLayers={_effectiveGpuLayers} (offload неизвестен)";

            var vram = VramTotalMb > 0 ? $", VRAM {VramFreeMb}/{VramTotalMb} MB" : string.Empty;
            var gpu = string.IsNullOrEmpty(GpuName) ? string.Empty : $", {GpuName}";
            return $"{ActiveBackend}{gpu}{vram}, {offload}, ctx={EffectiveContextSize}";
        }
    }

    public QwenModel(
        AIConfig config,
        ILogger<QwenModel> logger,
        PromptBuilder promptBuilder)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _promptBuilder = promptBuilder ?? throw new ArgumentNullException(nameof(promptBuilder));
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (IsLoaded)
        {
            _logger.LogWarning("Модель уже загружена");
            return;
        }

        var modelPath = ResolveModelPath(_config.ModelPath);
        _logger.LogInformation("Загрузка модели из {Path}", modelPath);

        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException(
                $"GGUF-модель не найдена: {modelPath}. " +
                "Скачайте модель (например Qwen3-8B/14B Q4_K_M) и укажите путь в appsettings.json (AI:ModelPath). " +
                "Относительный путь отсчитывается от каталога с AITradingSystem.UI.exe.");
        }

        // === 1. Разведка железа (VRAM) → выбор бэкенда → стейдж DLL → проба загрузки ===
        _gpu = GpuProbe.Detect(_logger);
        var requested = GpuProbe.SelectBackend(
            _config.ResolvedBackend, _gpu, LlamaNativeLoader.HasBackendFiles, _logger);

        if (!ActivateBackend(requested))
        {
            var switched = false;

            // Явный выбор Cpu не подменяем GPU-бэкендом: фолбэк-цепочка только для GPU-запросов.
            var candidates = requested == AiBackend.Cpu
                ? Array.Empty<AiBackend>()
                : new[] { AiBackend.Vulkan, AiBackend.Cpu };

            foreach (var candidate in candidates)
            {
                if (candidate == requested)
                    continue;

                if (candidate == AiBackend.Cpu && !_config.AllowCpuFallback)
                    break;

                AiLog.Backend($"бэкенд {requested} не активировался, пробуем {candidate}");

                if (!ActivateBackend(candidate))
                    continue;

                switched = true;
                break;
            }

            if (!switched)
            {
                // Стейдж мог не удаться (read-only каталог) — пробуем штатный поиск набора.
                LlamaNativeLoader.UseBackend(requested, _logger);
                ActiveBackend = requested;
            }
        }

        LlamaNativeLoader.EnsureAvailable(_config.NativeLibraryPath, _logger);
        AiLog.Backend($"{ActiveBackend}; {_gpu.Summary}; {LlamaNativeLoader.GetDiagnostics()}");

        // === 2. Метаданные GGUF без загрузки весов: слои, размеры, KV ===
        _modelInfo = GgufInspector.TryInspect(modelPath);
        AiLog.Backend(_modelInfo?.Summary() ?? "GGUF: метаданные прочитать не удалось");

        // === 3. AutoFit: контекст и число слоёв под свободную VRAM ===
        var fit = ResolveFit();
        _effectiveContextSize = fit.Ctx;
        _effectiveGpuLayers = fit.Layers;

        if (fit.Note != null)
        {
            LoadWarning = fit.Note;
            AiLog.Backend($"AUTOFIT: {fit.Note}");
        }

        var threads = _config.Threads > 0 ? _config.Threads : Math.Max(1, Environment.ProcessorCount / 2);

        // === 4–6. Загрузка с OOM-ретраями, проверкой реального оффлоада и CPU-фолбэком ===
        await Task.Run(() => LoadWithRetries(modelPath, threads, fit.Ctx, fit.Layers), ct);
    }

    /// <summary>
    /// Стейдж выбранного набора DLL в плоский каталог → регистрация резолвера → проба загрузки →
    /// перехват нативного лога → явная регистрация ggml-бэкендов → уточнение VRAM из llama.
    /// Всё ДО первого обращения к API LLamaSharp (иначе NativeApi..cctor «ломается» на весь процесс).
    /// </summary>
    private bool ActivateBackend(AiBackend backend)
    {
        var staged = NativeStaging.Stage(backend, _config.NativeLibraryPath, _logger);
        LlamaNativeLoader.Initialize(staged ?? _config.NativeLibraryPath, _logger);

        if (!LlamaNativeLoader.TryProbeBackend(backend, _logger))
            return false;

        GgmlNative.ResetLoadStats();
        GgmlNative.InstallLogCapture(_config.NativeLogLevel);

        var dir = staged ?? LlamaNativeLoader.GetBackendDirectory();
        if (dir != null)
            GgmlNative.TryLoadAllBackends(dir);

        // A3: строгая проверка. Если CUDA/Vulkan-рантайм не зарегистрировался
        // (llama_supports_gpu_offload()=false), GPU-бэкенд ОТКЛОНЯЕТСЯ — иначе модель
        // молча считалась бы на CPU под видом Cuda12 (диагноз F1), а фолбэк-цепочка
        // Vulkan → CPU никогда бы не сработала. null = экспорт недоступен (старая сборка).
        if (backend != AiBackend.Cpu && GgmlNative.SupportsGpuOffload() == false)
        {
            AiLog.Backend($"бэкенд {backend} отклонён: llama_supports_gpu_offload()=false — " +
                          "GPU-рантайм не зарегистрирован (драйвер/раскладка ggml-*.dll)");
            return false;
        }

        ActiveBackend = backend;

        if (backend == AiBackend.Vulkan)
        {
            const string vulkanWarning =
                "Vulkan: обновите llama.dll/ggml*.dll до llama.cpp >= b9892 (fix #24812: кластерный мусор на AMD Polaris/RX590). " +
                "Сторож деградации + чистка KV в приложении — временная защита, не замена обновлению DLL.";
            AiLog.Backend("WARN: " + vulkanWarning);
            _logger.LogWarning("{Warning}", vulkanWarning);
        }

        GpuProbe.EnrichFromNative(_logger);
        _gpu = GpuProbe.Detect(_logger);
        return true;
    }

    /// <summary>
    /// A5: CPU-фолбэк. Сначала пробуем чистый CPU-стейдж. Если он невозможен (нет набора
    /// CPU-DLL на диске или хэндлы уже закэшированы маршаллером), работаем на УЖЕ загруженной
    /// llama с GpuLayers=0: ggml-cpu присутствует в любой сборке llama.cpp, поэтому вычисления
    /// идут на CPU без смены DLL и без риска висячих указателей.
    /// </summary>
    private bool ActivateCpuFallback(out string note)
    {
        if (ActivateBackend(AiBackend.Cpu))
        {
            note = "чистый CPU-бэкенд";
            return true;
        }

        if (LlamaNativeLoader.GetBackendDirectory() != null)
        {
            ActiveBackend = AiBackend.Cpu;
            note = $"CPU-режим (GpuLayers=0) на уже загруженной библиотеке {LlamaNativeLoader.ActiveBackend}";
            AiLog.Backend("WARN: " + note);
            return true;
        }

        note = "CPU-фолбэк недоступен";
        return false;
    }

    /// <summary>
    /// AutoFit: сколько слоёв и какой контекст влезает в свободную VRAM.
    /// Сначала урезаем контекст (8192 → 4096), затем, если слоёв всё равно меньше MinGpuLayers,
    /// честно уходим на CPU (частичный оффлоад 2–3 слоёв не окупается).
    /// </summary>
    private (int Ctx, int Layers, string? Note) ResolveFit()
    {
        var ctx = _config.ContextSize;

        if (ActiveBackend == AiBackend.Cpu)
            return (ctx, 0, null);

        if (!_config.AutoFitGpu)
            return (ctx, ResolveConfiguredGpuLayers(), null);

        var info = _modelInfo;
        var free = _gpu?.VramFreeMb ?? 0;

        if (info == null || info.BlockCount <= 0)
            return (ctx, ResolveConfiguredGpuLayers(), "GGUF-метаданные недоступны — GpuLayers взят из конфига");

        if (free <= 0)
            return (ctx, ResolveConfiguredGpuLayers(), "свободная VRAM неизвестна — GpuLayers взят из конфига");

        var kv = ParseKvType(_config.KvCacheType);
        var maxAlloc = _gpu?.IsLegacyAmd == true ? 1024L : 0L;
        var reserve = _config.VramReserveMb;
        var batch = _config.BatchSize;

        var layers = info.RecommendGpuLayers(free, reserve, ctx, _config.FlashAttention, kv, batch, maxAlloc);
        string? note = null;

        if (layers < _config.MinGpuLayers && ctx > 4096)
        {
            ctx = 4096;
            layers = info.RecommendGpuLayers(free, reserve, ctx, _config.FlashAttention, kv, batch, maxAlloc);
            note = $"контекст урезан {_config.ContextSize} → {ctx} ради оффлоада";
        }

        if (layers < _config.MinGpuLayers)
        {
            var reason = $"в VRAM {free} MiB (reserve {reserve} MiB) влезает {layers} слоёв из {info.BlockCount} " +
                         $"(< MinGpuLayers={_config.MinGpuLayers})";

            if (_config.AllowCpuFallback && ActivateCpuFallback(out var cpuNote))
                return (ctx, 0, reason + $" — работаем на CPU ({cpuNote})");

            return (ctx, Math.Max(layers, 0), reason + " — грузим как есть (CPU-фолбэк недоступен/выключен)");
        }

        var scope = layers >= info.BlockCount ? "полный" : "частичный";
        var detail = $"{scope} оффлоад {layers}/{info.BlockCount} слоёв, ctx={ctx}, VRAM free={free} MiB, " +
                     $"KV={info.KvCacheMb(ctx, kv):F0} MiB, compute≈{info.ComputeBufferEstimate(batch, ctx, _config.FlashAttention):F0} MiB";

        return (ctx, layers, note == null ? detail : note + "; " + detail);
    }

    private static KvCacheType ParseKvType(string? value) =>
        value?.Trim().ToUpperInvariant() switch
        {
            "Q8_0" => KvCacheType.Q8_0,
            "Q4_0" => KvCacheType.Q4_0,
            _ => KvCacheType.F16
        };

    /// <summary>Слои из конфига: -1 = все слои (999 больше числа слоёв любой текущей модели).</summary>
    private int ResolveConfiguredGpuLayers() => _config.GpuLayers < 0 ? 999 : _config.GpuLayers;

    /// <summary>
    /// Загрузка весов с деградацией при OOM: до 2 повторов (слои ×0.7, ctx/2),
    /// затем явный CPU-фолбэк с причиной в логе. Никаких «тихих» откатов.
    /// </summary>
    private void LoadWithRetries(string modelPath, int threads, int ctx, int layers)
    {
        var attempts = new List<(int Ctx, int Layers, string Reason)> { (ctx, layers, "первая попытка") };

        if (ActiveBackend != AiBackend.Cpu && layers > 0)
        {
            attempts.Add((Math.Max(2048, ctx / 2), Math.Max(1, (int)(layers * 0.7)), "OOM-ретрай 1: слои ×0.7, ctx/2"));
            attempts.Add((2048, Math.Max(1, (int)(layers * 0.49)), "OOM-ретрай 2: слои ×0.49, ctx=2048"));
        }

        Exception? last = null;

        for (var i = 0; i < attempts.Count; i++)
        {
            var attempt = attempts[i];
            GgmlNative.ResetLoadStats();

            try
            {
                LoadOnce(modelPath, threads, attempt.Ctx, attempt.Layers);
                _effectiveContextSize = attempt.Ctx;
                _effectiveGpuLayers = attempt.Layers;

                if (i > 0)
                {
                    LoadWarning = $"GPU-загрузка удалась после деградации ({attempt.Reason})";
                    AiLog.Backend("WARN: " + LoadWarning);
                }

                VerifyOffload(modelPath, threads, attempt.Ctx, attempt.Layers);
                return;
            }
            catch (Exception ex) when (ex is TypeInitializationException or DllNotFoundException or BadImageFormatException)
            {
                last = ex;
                AiLog.Backend($"нативная библиотека llama.cpp не загрузилась ({ActiveBackend}): {ex.Message}");
                break;
            }
            catch (Exception ex)
            {
                last = ex;
                var oom = IsOutOfMemory(ex);
                AiLog.Backend($"загрузка не удалась ({attempt.Reason}; ctx={attempt.Ctx}, layers={attempt.Layers}, OOM={oom}): {ex.Message}");

                if (!oom)
                    break;

                if (i + 1 < attempts.Count)
                    AiLog.Backend("уменьшаю оффлоад: " + attempts[i + 1].Reason);
            }
        }

        if (ActiveBackend != AiBackend.Cpu && _config.AllowCpuFallback)
        {
            AiLog.Backend($"бэкенд {ActiveBackend} не смог загрузить модель — чистый откат на CPU. Причина: {last?.Message}");

            if (ActivateCpuFallback(out var cpuNote))
            {
                var cpuCtx = Math.Min(ctx, 4096);
                LoadOnce(modelPath, threads, cpuCtx, 0);
                _effectiveContextSize = cpuCtx;
                _effectiveGpuLayers = 0;
                LoadWarning = $"Работаем на CPU ({cpuNote}): {last?.Message ?? "GPU-загрузка не удалась"}";
                AiLog.Backend("WARN: " + LoadWarning);
                return;
            }
        }

        throw new InvalidOperationException(
            $"Не удалось загрузить модель на бэкенде {ActiveBackend}: {last?.Message}. " +
            "Проверьте драйвер GPU, наличие llama.dll/ggml*.dll и Microsoft Visual C++ Redistributable x64. " +
            "Диагностика: " + LlamaNativeLoader.GetDiagnostics(), last);
    }

    /// <summary>Одна попытка загрузки весов + создание executor'а с постоянным KV-контекстом.</summary>
    private void LoadOnce(string modelPath, int threads, int ctx, int layers)
    {
        var kvType = ResolveKvType();

        var modelParams = new ModelParams(modelPath)
        {
            ContextSize = (uint)ctx,
            GpuLayerCount = layers,
            Threads = threads,
            BatchSize = (uint)_config.BatchSize,
            UBatchSize = (uint)_config.BatchSize,
            FlashAttention = _config.FlashAttention,
            TypeK = kvType,
            TypeV = kvType,
            LoadMode = _config.UseMemorymap
                ? LLamaLoadMode.MemoryMap
                : LLamaLoadMode.None
        };

        // Освобождаем предыдущий контекст/веса ДО загрузки новых: перезагрузка не должна удваивать VRAM.
        _executor?.Dispose();
        _executor = null;
        _weights?.Dispose();
        _weights = null;
        _params = null;

        var weights = LLamaWeights.LoadFromFile(modelParams);
        KvManagedExecutor executor;
        try
        {
            executor = new KvManagedExecutor(
                weights, modelParams, _logger, _config.ReuseKvContext, _config.KvMinReusePrefix);
        }
        catch
        {
            weights.Dispose();
            throw;
        }

        _params = modelParams;
        _weights = weights;
        _executor = executor;
        _loadedModelPath = modelPath;
        _loadedThreads = threads;

        _logger.LogInformation(
            "Модель загружена. Backend={Backend}, Context={Context}, GpuLayers={Layers}, Threads={Threads}, Thinking={Thinking}",
            ActiveBackend, ctx, layers, threads, _config.EnableThinking);

        AiLog.Backend($"модель загружена: backend={ActiveBackend}, ctx={ctx}, gpuLayers={layers}, " +
                      $"threads={threads}, maxTokens={_config.EffectiveMaxTokens}, thinking={_config.EnableThinking}, " +
                      $"thinkingBudget={_config.ThinkingBudgetTokens}, flashAttn={_config.FlashAttention}, kv={kvType}, " +
                      $"samplers: temp={(_config.EnableThinking ? _config.Temperature : _config.NoThinkTemperature)}, " +
                      $"topP={(_config.EnableThinking ? _config.TopP : _config.NoThinkTopP)}, topK={_config.TopK}, minP={_config.MinP}, " +
                      $"repeat={_config.RepeatPenalty}, presence={_config.PresencePenalty}, penaltyN={_config.PenaltyLastN}");
    }

    /// <summary>
    /// Тип KV-кэша. Квантованный V-кэш требует FlashAttention; на legacy AMD (Vulkan, RX 4xx/5xx)
    /// консервативно остаёмся на F16 (класс сбоя #24812 жил именно на таких картах).
    /// </summary>
    private GGMLType ResolveKvType()
    {
        var kv = _config.KvCacheType?.Trim().ToUpperInvariant();
        if (kv is not ("Q8_0" or "Q4_0"))
            return GGMLType.GGML_TYPE_F16;

        if (ActiveBackend == AiBackend.Vulkan && _gpu?.IsLegacyAmd == true)
        {
            AiLog.Backend("KV: legacy AMD Vulkan — квантованный KV отключён, F16 (консервативно, #24812)");
            return GGMLType.GGML_TYPE_F16;
        }

        if (!_config.FlashAttention)
        {
            AiLog.Backend("KV: квантованный V-кэш требует FlashAttention — KV=F16");
            return GGMLType.GGML_TYPE_F16;
        }

        return kv == "Q8_0" ? GGMLType.GGML_TYPE_Q8_0 : GGMLType.GGML_TYPE_Q4_0;
    }

    /// <summary>
    /// Проверяет ФАКТИЧЕСКИЙ оффлоад по нативному логу (offloaded N/M layers to GPU).
    /// Если выбран GPU, а оффлоада нет — громкий WARN и один рестейдж/перезагрузка.
    /// </summary>
    private void VerifyOffload(string modelPath, int threads, int ctx, int layers)
    {
        var offloaded = GgmlNative.OffloadedLayers;
        var total = GgmlNative.TotalLayers ?? _modelInfo?.BlockCount;

        AiLog.Backend($"фактический оффлоад: {(offloaded.HasValue ? $"{offloaded}/{total}" : "нет данных нативного лога")}; " +
                      GgmlNative.StatsSummary());

        if (ActiveBackend == AiBackend.Cpu || layers <= 0)
            return;

        if (offloaded.HasValue && offloaded.Value > 0)
        {
            LoadWarning = null;
            return;
        }

        LoadWarning = $"Бэкенд {ActiveBackend}, но оффлоад на GPU = 0 — модель фактически считается на CPU";
        _logger.LogWarning("{Warning}", LoadWarning);
        AiLog.Backend("WARN: " + LoadWarning + " — пробую один рестейдж/перезагрузку");

        try
        {
            var staged = NativeStaging.Stage(ActiveBackend, _config.NativeLibraryPath, _logger);
            if (staged != null)
                GgmlNative.TryLoadAllBackends(staged);

            GgmlNative.ResetLoadStats();
            _executor?.Dispose();
            _executor = null;
            _params = null;
            _weights?.Dispose();
            _weights = null;

            LoadOnce(modelPath, threads, ctx, layers);

            var after = GgmlNative.OffloadedLayers;
            if (after.HasValue && after.Value > 0)
            {
                LoadWarning = null;
                AiLog.Backend($"после рестейджа оффлоад {after}/{GgmlNative.TotalLayers ?? total}");
            }
            else
            {
                AiLog.Backend("WARN: рестейдж не помог — оффлоада нет; проверьте раскладку ggml-cuda/ggml-vulkan и драйвер GPU");
            }
        }
        catch (Exception ex)
        {
            AiLog.Backend($"рестейдж/перезагрузка не удались: {ex.Message}");

            if (IsLoaded)
                return;

            if (_config.AllowCpuFallback && ActivateCpuFallback(out var cpuNote))
            {
                var cpuCtx = Math.Min(ctx, 4096);
                LoadOnce(modelPath, threads, cpuCtx, 0);
                _effectiveContextSize = cpuCtx;
                _effectiveGpuLayers = 0;
                LoadWarning = $"Работаем на CPU после неудачного рестейджа GPU ({cpuNote})";
                AiLog.Backend("WARN: " + LoadWarning);
                return;
            }

            throw;
        }
    }

    /// <summary>OOM распознаём и по исключению LLamaSharp/CUDA/Vulkan, и по нативному логу ggml.</summary>
    private static bool IsOutOfMemory(Exception ex)
    {
        if (GgmlNative.OutOfMemoryDetected)
            return true;

        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            var m = e.Message;
            if (string.IsNullOrEmpty(m))
                continue;

            if (m.Contains("out of memory", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("OutOfDeviceMemory", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("unable to allocate", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("failed to allocate", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("cudaMalloc", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("createBuffer", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("ggml_backend_alloc", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string ResolveModelPath(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
            return configuredPath;

        var path = configuredPath.Trim().Replace('/', Path.DirectorySeparatorChar);

        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);

        var fromBase = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
        if (File.Exists(fromBase))
            return fromBase;

        var fromCwd = Path.GetFullPath(path);
        return File.Exists(fromCwd) ? fromCwd : fromBase;
    }

    public async Task UnloadAsync()
    {
        await _inferenceLock.WaitAsync();
        try
        {
            if (_weights == null && _executor == null)
                return;

            _logger.LogInformation("Выгрузка модели");

            await Task.Run(() =>
            {
                _executor?.Dispose();
                _executor = null;
                _params = null;
                _weights?.Dispose();
                _weights = null;
            });

            _logger.LogInformation("Модель выгружена");
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    public async Task<TradingSignal> GetTradingSignalAsync(
        string symbol,
        Dictionary<string, decimal> indicators,
        string? newsContext,
        AiRequestContext? context = null,
        CancellationToken ct = default)
    {
        if (!IsLoaded)
            throw new InvalidOperationException("Модель не загружена. Вызовите LoadAsync.");

        context ??= new AiRequestContext
        {
            AllowShort = false,
            MinConfidence = _config.MinConfidence
        };

        await _inferenceLock.WaitAsync(ct);
        try
        {
            var prompt = BuildPromptWithinBudget(symbol, indicators, newsContext, context);

            TradingSignal? signal = null;
            Exception? lastException = null;
            string? lastRawResponse = null;
            string? lastParseError = null;
            var currentPrompt = prompt;

            // Деградация вывода — отдельная ветка восстановления; даём дойти до CPU-фолбэка
            // после порога перезагрузки весов, даже если MaxRetriesOnInvalidJson меньше.
            var maxAttempts = _config.MaxRetriesOnInvalidJson;
            if (_config.DetectDegenerateOutput)
                maxAttempts = Math.Max(maxAttempts, _config.DegenerateReloadThreshold + 1);

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                // «Ремонт»: со второй попытки показываем модели её невалидный ответ и ошибку.
                if (attempt > 1 && _config.RepairInvalidJson && lastRawResponse != null)
                {
                    currentPrompt = BuildRepairPromptWithinBudget(
                        prompt, lastRawResponse, lastParseError ?? "invalid JSON");
                    AiLog.Retry(symbol, attempt, lastParseError ?? "invalid JSON");
                }

                var meta = $"backend={ActiveBackend}, thinking={_config.EnableThinking}, " +
                           $"position={(context.HasPosition ? context.PositionSide ?? "LONG" : "none")}";
                AiLog.Prompt(symbol, attempt, maxAttempts, currentPrompt, meta);

                var sw = Stopwatch.StartNew();
                try
                {
                    var result = await GenerateResponseAsync(currentPrompt, ct);
                    sw.Stop();
                    LastInferenceTimeMs = sw.Elapsed.TotalMilliseconds;
                    lastRawResponse = result.Text;

                    LogGenerationDiagnostics(symbol, attempt, result);
                    AiLog.Response(symbol, attempt, result.Text, LastInferenceTimeMs);

                    if (result.DegenerateReason != null)
                    {
                        lastParseError = result.DegenerateReason;
                        lastRawResponse = "(дегенеративный вывод остановлен сторожем)";
                        AiLog.ParseFailed(symbol, attempt, result.DegenerateReason, result.Text);
                        EscalateAfterDegeneration(symbol, result.DegenerateReason);
                        continue;
                    }

                    // F3: пустой ответ — не «невалидный JSON», а anti-prompt/EOS (модель сразу закрыла
                    // think-блок или уперлась в стоп-последовательность). Причина в лог отдельной строкой,
                    // следующий заход — repair-промпт БЕЗ префилла think.
                    if (string.IsNullOrWhiteSpace(result.Text))
                    {
                        lastParseError = "пустой ответ (anti-prompt/EOS)";
                        lastRawResponse = "(пустой ответ модели)";
                        AiLog.ParseFailed(symbol, attempt, lastParseError, result.Text);
                        _logger.LogWarning(
                            "Модель вернула пустой ответ для {Symbol} за {Ms:F0} мс (попытка {Attempt}/{Max})",
                            symbol, LastInferenceTimeMs, attempt, maxAttempts);
                        continue;
                    }

                    var thinking = ExtractThinking(result.Text);
                    if (!string.IsNullOrWhiteSpace(thinking))
                    {
                        LastThinking = thinking;
                        AiLog.Thinking(symbol, thinking, LastInferenceTimeMs);
                    }

                    signal = ParseSignalFromResponse(symbol, result.Text, out var parseError);
                    lastParseError = parseError;

                    if (signal != null)
                    {
                        _consecutiveDegenerate = 0;
                        _weightsReloadedOnce = false;

                        // Порог MinConfidence применяется в TradingEngine к FINAL confidence
                        // (после SignalCalibrator). Здесь сырую уверенность модели не трогаем,
                        // иначе движок не сможет отличить «модель не уверена» от «сетап слабый».
                        AiLog.Parsed(symbol, signal.Action.ToString(), signal.Confidence,
                            $"model_conf={signal.Confidence:0.##}, degenerate={(signal.IsDegenerateConfidence ? "yes" : "no")}, " +
                            $"entry={signal.EntryPrice}, sl={signal.StopLoss}, tp={signal.TakeProfit}, reason={signal.Reasoning}",
                            LastInferenceTimeMs);

                        _logger.LogInformation(
                            "Сигнал для {Symbol}: {Action} (Confidence: {Confidence:P0}, {Time:F0} мс, backend {Backend})",
                            symbol, signal.Action, signal.Confidence, LastInferenceTimeMs, ActiveBackend);

                        return signal;
                    }

                    AiLog.ParseFailed(symbol, attempt, parseError ?? "unknown", result.Text);
                    _logger.LogWarning("Не удалось распарсить JSON ответа модели. Попытка {Attempt}/{Max}: {Error}",
                        attempt, maxAttempts, parseError);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    LastInferenceTimeMs = sw.Elapsed.TotalMilliseconds;
                    lastException = ex;
                    lastParseError = ex.Message;
                    AiLog.Failure(symbol, ex, $"инференс, попытка {attempt}");
                    _logger.LogError(ex, "Ошибка инференса для {Symbol}. Попытка {Attempt}", symbol, attempt);
                }
            }

            _logger.LogError("Все попытки получить валидный сигнал для {Symbol} провалились. Возвращаю Hold.", symbol);

            return new TradingSignal
            {
                Symbol = symbol,
                Action = SignalAction.Hold,
                Confidence = 0,
                // B9: отказ инференса — не «слабый сетап»: калибратор не пересматривает такой HOLD.
                IsInferenceFailure = true,
                Reasoning = $"Ошибка инференса: {lastException?.Message ?? lastParseError ?? "невалидный JSON в ответе модели"}",
                Timestamp = DateTime.UtcNow,
                ModelVersion = $"Qwen-GGUF/{ActiveBackend}"
            };
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    /// <summary>Промпт с новостями, урезанными до влезания в контекст (сначала MaxNewsChars, затем половинное урезание).</summary>
    private string BuildPromptWithinBudget(
        string symbol,
        Dictionary<string, decimal> indicators,
        string? newsContext,
        AiRequestContext context)
    {
        var executor = _executor ?? throw new InvalidOperationException("Executor не инициализирован");
        var reserve = _config.EffectiveMaxTokens + 8;
        var news = LimitNews(newsContext, _config.MaxNewsChars);
        var prompt = _promptBuilder.BuildTradingPrompt(symbol, indicators, news, _config.EnableThinking, context);

        while (true)
        {
            var tokens = executor.CountTokens(prompt);
            if (tokens + reserve <= executor.ContextSize)
                return prompt;

            if (news is { Length: > 0 })
            {
                var next = news.Length > 1 ? news[..(news.Length / 2)] : null;
                AiLog.Backend($"промпт не влезает в контекст: новости {news.Length} → {next?.Length ?? 0} символов");
                news = next;
                prompt = _promptBuilder.BuildTradingPrompt(symbol, indicators, news, _config.EnableThinking, context);
                continue;
            }

            throw new PromptTooLongException(tokens, executor.ContextSize);
        }
    }

    /// <summary>Repair-промпт; если он не влезает — откат к базовому промпту вместо ContextOverflowException.</summary>
    private string BuildRepairPromptWithinBudget(string basePrompt, string invalidResponse, string error)
    {
        var executor = _executor ?? throw new InvalidOperationException("Executor не инициализирован");
        var repair = _promptBuilder.BuildRepairPrompt(basePrompt, invalidResponse, error, _config.EnableThinking);
        var reserve = _config.EffectiveMaxTokens + 8;

        if (executor.CountTokens(repair) + reserve <= executor.ContextSize)
            return repair;

        AiLog.Backend("repair-промпт не влезает в контекст — откат к базовому промпту");
        return basePrompt;
    }

    private static string? LimitNews(string? newsContext, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(newsContext))
            return null;
        return newsContext.Length <= maxChars ? newsContext : newsContext[..maxChars];
    }

    private static void LogGenerationDiagnostics(string symbol, int attempt, KvGenerationResult result)
    {
        AiLog.Backend(
            $"AI GEN {symbol} attempt={attempt}: generated={result.GeneratedTokens}, think={result.ThinkTokens}, " +
            $"prompt={result.PromptTokens}, reuse={result.ReusedPrefixTokens}, forcedThinkClose={result.ForcedThinkClose}, " +
            $"hitMax={result.HitMaxTokens}, timedOut={result.TimedOut}, elapsed={result.ElapsedMs:F0}ms");
    }

    /// <summary>
    /// Эскалация после деградации: KV уже очищен executor'ом → при пороге сброса HardReset →
    /// при пороге перезагрузки весов LoadOnce → далее CPU-фолбэк. Вызывается под _inferenceLock.
    /// </summary>
    private void EscalateAfterDegeneration(string symbol, string reason)
    {
        _consecutiveDegenerate++;
        AiLog.Backend($"AI KV/DEGENERATE {symbol}: {reason}; подряд={_consecutiveDegenerate}");

        var executor = _executor;
        if (executor == null)
            return;

        if (_consecutiveDegenerate > _config.DegenerateReloadThreshold)
        {
            if (ActiveBackend != AiBackend.Cpu && _config.AllowCpuFallback && ActivateCpuFallback(out var cpuNote))
            {
                var cpuCtx = Math.Min(EffectiveContextSize, 4096);
                LoadOnce(_loadedModelPath!, _loadedThreads, cpuCtx, 0);
                _effectiveContextSize = cpuCtx;
                _effectiveGpuLayers = 0;
                LoadWarning = $"CPU-фолбэк после повторной деградации вывода ({cpuNote}): {reason}";
                AiLog.Backend("WARN: " + LoadWarning);
                return;
            }

            AiLog.Backend("CPU-фолбэк после деградации недоступен/выключен — полный сброс контекста");
            executor.HardReset(reason);
            return;
        }

        if (_consecutiveDegenerate >= _config.DegenerateReloadThreshold && !_weightsReloadedOnce)
        {
            _weightsReloadedOnce = true;
            ReloadWeights(reason);
            return;
        }

        if (_consecutiveDegenerate >= _config.DegenerateResetThreshold)
        {
            executor.HardReset(reason);
            return;
        }

        AiLog.Backend("KV уже очищен executor'ом — повтор запроса без пересоздания контекста");
    }

    private void ReloadWeights(string reason)
    {
        var path = _loadedModelPath ?? throw new InvalidOperationException("Путь загруженной модели неизвестен");
        var ctx = EffectiveContextSize;
        var layers = EffectiveGpuLayers;

        AiLog.Backend($"перезагрузка весов модели ({reason}): backend={ActiveBackend}, ctx={ctx}, gpuLayers={layers}");
        GgmlNative.ResetLoadStats();
        LoadOnce(path, _loadedThreads, ctx, layers);
    }

    /// <summary>Генерация через KvManagedExecutor с таймаутом; частичный ответ при таймауте сохраняется.</summary>
    private async Task<KvGenerationResult> GenerateResponseAsync(string prompt, CancellationToken ct)
    {
        var executor = _executor ?? throw new InvalidOperationException("Executor не инициализирован");

        var opts = new KvGenerationOptions
        {
            MaxTokens = _config.EffectiveMaxTokens,
            ThinkingBudgetTokens = _config.EnableThinking ? _config.ThinkingBudgetTokens : 0,
            Temperature = _config.EnableThinking ? _config.Temperature : _config.NoThinkTemperature,
            TopP = _config.EnableThinking ? _config.TopP : _config.NoThinkTopP,
            TopK = _config.TopK,
            MinP = _config.MinP,
            RepeatPenalty = _config.RepeatPenalty,
            PresencePenalty = _config.PresencePenalty,
            FrequencyPenalty = _config.FrequencyPenalty,
            PenaltyLastN = _config.PenaltyLastN,
            DetectDegenerate = _config.DetectDegenerateOutput
        };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_config.InferenceTimeoutMs);

        try
        {
            var result = await executor.GenerateAsync(prompt, opts, timeoutCts.Token);
            ct.ThrowIfCancellationRequested();

            if (result.TimedOut)
            {
                _logger.LogWarning(
                    "Инференс превысил таймаут {Timeout} мс. Использую частичный ответ ({Length} символов).",
                    _config.InferenceTimeoutMs, result.Text.Length);

                if (string.IsNullOrWhiteSpace(result.Text))
                    throw new TimeoutException(
                        $"Инференс не успел сгенерировать ни одного токена за {_config.InferenceTimeoutMs} мс. " +
                        "Увеличьте AI:InferenceTimeoutMs, уменьшите AI:MaxTokens/ThinkingBudgetTokens " +
                        "или включите GPU-бэкенд (AI:Backend = Auto/Cuda12/Vulkan).");
            }

            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Инференс превысил таймаут {_config.InferenceTimeoutMs} мс до получения частичного ответа.");
        }
    }

    private TradingSignal? ParseSignalFromResponse(string symbol, string response, out string? error)
    {
        error = null;

        try
        {
            var cleaned = StripThinking(response);
            var json = ExtractJsonObject(cleaned);

            if (json == null)
            {
                error = "в ответе нет JSON-объекта";
                return null;
            }

            var signalDto = JsonSerializer.Deserialize<TradingSignalDto>(json, _jsonOptions);
            if (signalDto == null || string.IsNullOrWhiteSpace(signalDto.Action))
            {
                error = "в JSON отсутствует поле action";
                return null;
            }

            if (!Enum.TryParse<SignalAction>(signalDto.Action.Trim(), true, out var action))
            {
                error = $"неизвестное действие '{signalDto.Action}'";
                return null;
            }

            var modelConfidence = Math.Clamp(signalDto.Confidence, 0m, 1m);

            return new TradingSignal
            {
                Symbol = symbol,
                Action = action,
                Confidence = modelConfidence,
                ModelConfidence = modelConfidence,
                // Дегенерат промпт-якоря: HOLD с conf ≈ 0.45 (правило «below 0.55» из старого промпта).
                IsDegenerateConfidence = action == SignalAction.Hold && Math.Abs(modelConfidence - 0.45m) <= 0.02m,
                EntryPrice = NormalizePrice(signalDto.EntryPrice),
                StopLoss = NormalizePrice(signalDto.StopLoss),
                TakeProfit = NormalizePrice(signalDto.TakeProfit),
                Reasoning = signalDto.Reasoning ?? string.Empty,
                Timestamp = DateTime.UtcNow,
                ModelVersion = $"Qwen-GGUF/{ActiveBackend}"
            };
        }
        catch (JsonException ex)
        {
            error = $"ошибка JSON: {ex.Message}";
            _logger.LogError(ex, "Ошибка парсинга JSON из ответа модели");
            return null;
        }
        catch (Exception ex)
        {
            error = $"ошибка разбора: {ex.Message}";
            _logger.LogError(ex, "Ошибка парсинга ответа модели");
            return null;
        }
    }

    /// <summary>Модель иногда присылает 0 или отрицательные цены вместо null.</summary>
    private static decimal? NormalizePrice(decimal? price) =>
        price.HasValue && price.Value > 0 ? price : null;

    /// <summary>Достаёт содержимое блока рассуждений (для лога).</summary>
    private static string ExtractThinking(string response)
    {
        if (string.IsNullOrEmpty(response)) return string.Empty;

        var match = Regex.Match(response, @"<think>(.*?)</think>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        if (match.Success)
            return match.Groups[1].Value.Trim();

        var open = response.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        return open >= 0 ? response[(open + 7)..].Trim() : string.Empty;
    }

    /// <summary>Убирает блоки рассуждений и markdown-ограждения.</summary>
    private static string StripThinking(string response)
    {
        if (string.IsNullOrEmpty(response))
            return string.Empty;

        var text = Regex.Replace(response, "<think>.*?</think>", string.Empty,
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        // Незакрытый блок рассуждений (обрыв по таймауту/лимиту токенов) — отбрасываем.
        var open = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        if (open >= 0)
            text = text[..open];

        return text.Replace("```json", string.Empty, StringComparison.OrdinalIgnoreCase)
                   .Replace("```", string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// Достаёт первый сбалансированный JSON-объект (с учётом строк и экранирования).
    /// Предпочитает объект с полем "action".
    /// </summary>
    private static string? ExtractJsonObject(string text)
    {
        string? fallback = null;
        var index = 0;

        while (index < text.Length)
        {
            var start = text.IndexOf('{', index);
            if (start < 0)
                break;

            if (TryReadBalancedObject(text, start, out var end))
            {
                var candidate = text.Substring(start, end - start + 1);
                if (candidate.Contains("\"action\"", StringComparison.OrdinalIgnoreCase))
                    return candidate;

                fallback ??= candidate;
                index = end + 1;
            }
            else
            {
                index = start + 1;
            }
        }

        return fallback;
    }

    private static bool TryReadBalancedObject(string text, int start, out int end)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    end = i;
                    return true;
                }
            }
        }

        end = -1;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        await UnloadAsync();
        _inferenceLock.Dispose();
    }
}

/// <summary>DTO для парсинга JSON-ответа модели (snake_case, как в промпте).</summary>
internal sealed class TradingSignalDto
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("confidence")]
    public decimal Confidence { get; set; }

    [JsonPropertyName("entry_price")]
    public decimal? EntryPrice { get; set; }

    [JsonPropertyName("stop_loss")]
    public decimal? StopLoss { get; set; }

    [JsonPropertyName("take_profit")]
    public decimal? TakeProfit { get; set; }

    [JsonPropertyName("reasoning")]
    public string? Reasoning { get; set; }
}
