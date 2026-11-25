namespace AITradingSystem.Common.Configuration;

/// <summary>
/// Вычислительный бэкенд llama.cpp.
/// Auto   — выбрать по железу: NVIDIA → CUDA, AMD/Intel → Vulkan, иначе CPU.
/// Cuda12 — NVIDIA, CUDA 12 (LLamaSharp.Backend.Cuda12).
/// Cuda11 — NVIDIA, CUDA 11 (LLamaSharp.Backend.Cuda11).
/// Vulkan — универсальный GPU-бэкенд (AMD Radeon/ROCm-независимый, NVIDIA, Intel).
/// Cpu    — только CPU (LLamaSharp.Backend.Cpu).
/// </summary>
public enum AiBackend
{
    Auto = 0,
    Cpu = 1,
    Cuda12 = 2,
    Cuda11 = 3,
    Vulkan = 4
}

/// <summary>
/// Конфигурация AI-модели Qwen (FR-4, NFR-1.1).
/// </summary>
public sealed class AIConfig
{
    /// <summary>Путь к файлу модели в формате GGUF.</summary>
    public string ModelPath { get; set; } = "models/qwen3-14b-q4_k_m.gguf";

    /// <summary>Размер контекстного окна.</summary>
    public int ContextSize { get; set; } = 8192;

    /// <summary>
    /// Бэкенд инференса: Auto | Cpu | Cuda12 | Cuda11 | Vulkan.
    /// Auto определяет GPU по железу и откатывается на CPU, если GPU-библиотек нет.
    /// </summary>
    public string Backend { get; set; } = "Auto";

    /// <summary>
    /// Количество слоёв модели на GPU. -1 = все слои (рекомендуется для GPU),
    /// 0 = только CPU. При Backend=Auto и найденном GPU значение -1 подставляется автоматически.
    /// </summary>
    public int GpuLayers { get; set; } = -1;

    /// <summary>
    /// Количество потоков CPU. 0 = авто (по числу физических ядер).
    /// </summary>
    public int Threads { get; set; } = 0;

    /// <summary>
    /// Использовать memory-mapped загрузку весов (быстрее старт, меньше RAM).
    /// </summary>
    public bool UseMemorymap { get; set; } = true;

    /// <summary>
    /// Батч обработки промпта. Больше = быстрее на GPU, больше VRAM.
    /// </summary>
    public int BatchSize { get; set; } = 512;

    /// <summary>Temperature для генерации (thinking-режим; рекомендация Qwen3 — 0.6).</summary>
    public float Temperature { get; set; } = 0.6f;

    /// <summary>Top-P (nucleus sampling), thinking-режим (рекомендация Qwen3 — 0.95).</summary>
    public float TopP { get; set; } = 0.95f;

    /// <summary>Temperature для режима без рассуждений (/no_think, рекомендация Qwen3 — 0.7).</summary>
    public float NoThinkTemperature { get; set; } = 0.7f;

    /// <summary>Top-P для режима без рассуждений (рекомендация Qwen3 — 0.8).</summary>
    public float NoThinkTopP { get; set; } = 0.8f;

    /// <summary>Top-K сэмплирования (рекомендация Qwen3 — 20; раньше молча применялся дефолт пайплайна 40).</summary>
    public int TopK { get; set; } = 20;

    /// <summary>Min-P сэмплирование (рекомендация Qwen3 — 0; раньше молча применялся дефолт пайплайна 0.1).</summary>
    public float MinP { get; set; } = 0f;

    /// <summary>Штраф за повтор токенов (repeat penalty, 1 = выкл). Против зацикливаний Qwen3.</summary>
    public float RepeatPenalty { get; set; } = 1.05f;

    /// <summary>Presence penalty (0…2): штраф за уже встречавшиеся токены. Qwen3 рекомендует &gt;0 против
    /// бесконечных повторов; 0.5 — умеренно, без риска смешения языков при высоких значениях.</summary>
    public float PresencePenalty { get; set; } = 0.5f;

    /// <summary>Frequency penalty (0…2): штраф пропорционально частоте токена. По умолчанию выкл.</summary>
    public float FrequencyPenalty { get; set; } = 0f;

    /// <summary>Сколько последних токенов учитывают штрафы (дефолт пайплайна 64 короток для циклов).</summary>
    public int PenaltyLastN { get; set; } = 256;

    /// <summary>Максимальное количество токенов в финальном ответе (без блока рассуждений).</summary>
    public int MaxTokens { get; set; } = 600;

    /// <summary>Максимальное время инференса в мс (NFR-1.1). 180 с: с жёстким think-бюджетом
    /// и ранним стопом мусора более long timeout не нужен; 10 минут «тупизны» на символ недопустимы.</summary>
    public int InferenceTimeoutMs { get; set; } = 180_000;

    /// <summary>Количество попыток при невалидном JSON (FR-4.4).</summary>
    public int MaxRetriesOnInvalidJson { get; set; } = 3;

    /// <summary>
    /// Путь к нативной библиотеке llama.cpp: каталог с llama.dll/ggml*.dll
    /// или полный путь к llama.dll. Если null — поиск рядом с exe и в runtimes/&lt;rid&gt;/native.
    /// </summary>
    public string? NativeLibraryPath { get; set; }

    /// <summary>
    /// Режим рассуждений (thinking): модель сначала думает, затем выдаёт JSON.
    /// Снижает число ошибок в решении; блок рассуждений логируется в logs/ai-*.log.
    /// </summary>
    public bool EnableThinking { get; set; } = true;

    /// <summary>
    /// ЖЁСТКИЙ бюджет токенов блока рассуждений при EnableThinking=true.
    /// При превышении think-блок принудительно закрывается (декодирование закрывающего тега в KV),
    /// и модель сразу выдаёт JSON. Дополнительно входит в общий лимит генерации.
    /// </summary>
    public int ThinkingBudgetTokens { get; set; } = 96;

    /// <summary>
    /// Повторно спрашивать модель с указанием ошибки, если JSON не распарсился
    /// («ремонт» ответа вместо слепого повтора того же промпта).
    /// </summary>
    public bool RepairInvalidJson { get; set; } = true;

    /// <summary>
    /// Минимальная уверенность сигнала, ниже которой он считается Hold.
    /// Применяется к FINAL confidence после калибратора (см. ConfidencePolicy).
    /// P2.4: значение по умолчанию — fallback «с потолка»; рабочий порог подбирается
    /// по expectancy после издержек на истории (ConfidenceThresholdTuner) и, при
    /// MinConfidenceAutoTuneEnabled=true, подставляется сюда в пределах коридора ниже.
    /// </summary>
    public decimal MinConfidence { get; set; } = 0.55m;

    // ------------------------------------------------------------------
    // P2.4. Подбор порога MinConfidence по expectancy после издержек
    // ------------------------------------------------------------------

    /// <summary>
    /// Разрешить контуру подбора порога по истории сделок (разбор, лог, метрики).
    /// Выключено — подбор не выполняется вовсе.
    /// </summary>
    public bool MinConfidenceTuneEnabled { get; set; } = true;

    /// <summary>
    /// Автоматически применять рекомендованный порог к MinConfidence.
    /// По умолчанию ВЫКЛ: контур только рекомендует, решение об изменении порога — за оператором.
    /// </summary>
    public bool MinConfidenceAutoTuneEnabled { get; set; } = false;

    /// <summary>Период разбора истории, минуты.</summary>
    public int MinConfidenceTuneIntervalMinutes { get; set; } = 60;

    /// <summary>Нижняя граница коридора подбора: ниже неё порог не опускается никогда.</summary>
    public decimal MinConfidenceFloor { get; set; } = 0.45m;

    /// <summary>Верхняя граница коридора подбора.</summary>
    public decimal MinConfidenceCeiling { get; set; } = 0.80m;

    /// <summary>Шаг сетки порогов при подборе.</summary>
    public decimal MinConfidenceTuneStep { get; set; } = 0.05m;

    /// <summary>Минимум пригодных сделок в истории, чтобы вообще что-то рекомендовать.</summary>
    public int MinConfidenceTuneMinTrades { get; set; } = 40;

    /// <summary>Минимум сделок выше порога-кандидата, чтобы точка считалась значимой.</summary>
    public int MinConfidenceTuneMinBucketTrades { get; set; } = 15;

    // ------------------------------------------------------------------
    // P2.6. Валидация весов скоринга + reliability diagram
    // ------------------------------------------------------------------

    /// <summary>
    /// Веса direction-скоринга, отображение |score| → prior и модификаторы (SignalCalibrator).
    /// Дефолты воспроизводят прежние константы; подбор — tools/scoring_calibration.py --fit,
    /// применение — вручную оператором. Рантайм веса не меняет (только рекомендует).
    /// </summary>
    public ScoringWeightsConfig ScoringWeights { get; set; } = new();

    /// <summary>
    /// Разрешить контур валидации скоринга (reliability diagram + вердикты по компонентам).
    /// Работает в том же цикле, что и подбор порога P2.4 (ConfidenceThresholdTuner),
    /// на той же полной истории сделок; только рекомендует, ничего не применяет.
    /// </summary>
    public bool ScoringCalibrationEnabled { get; set; } = true;

    /// <summary>Минимум пригодных сделок для вердикта калибровки/компонентов.</summary>
    public int ScoringCalibrationMinTrades { get; set; } = 40;

    /// <summary>Минимум сделок в корзине reliability, чтобы разрыв считался значимым.</summary>
    public int ScoringCalibrationMinBinTrades { get; set; } = 10;

    /// <summary>Шаг корзин уверенности reliability diagram.</summary>
    public decimal ScoringCalibrationBinStep { get; set; } = 0.1m;

    // ------------------------------------------------------------------
    // A7. GPU AutoFit / native / confidence policy
    // ------------------------------------------------------------------

    /// <summary>
    /// Авторасчёт GpuLayers/ContextSize по свободной VRAM и метаданным GGUF (A2/A5).
    /// false — использовать GpuLayers/ContextSize из конфига как есть (старое поведение).
    /// </summary>
    public bool AutoFitGpu { get; set; } = true;

    /// <summary>Запас VRAM (MiB) под рабочий стол Windows, драйвер и непредвиденные буферы.</summary>
    public int VramReserveMb { get; set; } = 1024;

    /// <summary>
    /// Минимум слоёв на GPU, при котором оффлоад имеет смысл.
    /// Если AutoFit насчитал меньше — сразу CPU + предупреждение (частичный оффлоад не окупается).
    /// </summary>
    public int MinGpuLayers { get; set; } = 8;

    /// <summary>Разрешить чистый откат на CPU при OOM/неудаче GPU-загрузки (с явной причиной в логе).</summary>
    public bool AllowCpuFallback { get; set; } = true;

    /// <summary>
    /// FlashAttention в llama.cpp: меньше VRAM под attention-буферы, быстрее префилл.
    /// Пробрасывается в ModelParams, если свойство доступно в LLamaSharp 0.30.
    /// </summary>
    public bool FlashAttention { get; set; } = true;

    /// <summary>Тип KV-кэша: F16 | Q8_0 | Q4_0. Q8_0 вдвое уменьшает KV и ускоряет attention
    /// (требует FlashAttention). На legacy AMD (Vulkan) принудительно F16 — консервативно.</summary>
    public string KvCacheType { get; set; } = "Q8_0";

    // ------------------------------------------------------------------
    // Управление KV-памятью и деградацией вывода («чистить старое, иначе сброс»)
    // ------------------------------------------------------------------

    /// <summary>Переиспользовать общий префикс промпта в KV между запросами (системный промпт
    /// не пересчитывается). Старый хвост удаляется. false — полная чистка KV перед каждым запросом.</summary>
    public bool ReuseKvContext { get; set; } = true;

    /// <summary>Минимальная длина общего префикса (токенов) для переиспользования KV.</summary>
    public int KvMinReusePrefix { get; set; } = 64;

    /// <summary>Потоковый сторож мусора/циклов в выводе (llama.cpp #24812): немедленный стоп генерации.</summary>
    public bool DetectDegenerateOutput { get; set; } = true;

    /// <summary>Столько деградаций подряд → пересоздание контекста (сброс KV-буферов).</summary>
    public int DegenerateResetThreshold { get; set; } = 2;

    /// <summary>Столько деградаций подряд → перезагрузка весов; далее CPU-фолбэк (если разрешён).</summary>
    public int DegenerateReloadThreshold { get; set; } = 3;

    /// <summary>Лимит символов новостного контекста в промпте (защита от переполнения контекста).</summary>
    public int MaxNewsChars { get; set; } = 2500;

    /// <summary>Уровень нативного лога llama.cpp (llama_log_set): Off | Error | Warn | Info | Debug.</summary>
    public string NativeLogLevel { get; set; } = "Info";

    /// <summary>
    /// Политика итоговой уверенности: ModelOnly | Blend | EngineOverride (см. B9 SignalCalibrator).
    /// Blend: final = w*model + (1-w)*engine_prior, где w = BlendModelWeight (P2.3, по умолчанию 0.7);
    /// дегенерат модели (HOLD conf≈0.45) → prior.
    /// </summary>
    public string ConfidencePolicy { get; set; } = "Blend";

    /// <summary>
    /// P2.3: вес уверенности модели в политике Blend; вес prior движка = 1 − BlendModelWeight.
    /// По умолчанию 0.7/0.3 в пользу независимой оценки модели: после P2.2 рубрика модели
    /// больше не пересчитывает prior, поэтому её мнение весит больше. 0.5 — прежнее поведение 50/50.
    /// На ModelOnly/EngineOverride и на дегенерат HOLD≈0.45 не влияет.
    /// </summary>
    public decimal BlendModelWeight { get; set; } = 0.7m;

    /// <summary>Итоговый бэкенд с учётом значения по умолчанию.</summary>
    public AiBackend ResolvedBackend =>
        Enum.TryParse<AiBackend>(Backend?.Trim(), true, out var parsed) ? parsed : AiBackend.Auto;

    /// <summary>Итоговая политика уверенности (невалидное значение → Blend).</summary>
    public ConfidencePolicy ResolvedConfidencePolicy =>
        Enum.TryParse<ConfidencePolicy>(ConfidencePolicy?.Trim(), true, out var parsed)
            ? parsed
            : Configuration.ConfidencePolicy.Blend;

    /// <summary>Сколько токенов разрешить генерации с учётом рассуждений.</summary>
    public int EffectiveMaxTokens => EnableThinking
        ? Math.Min(MaxTokens + ThinkingBudgetTokens, 8192)
        : MaxTokens;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ModelPath))
            throw new InvalidOperationException("ModelPath не указан.");

        if (ContextSize < 2048 || ContextSize > 131072)
            throw new InvalidOperationException(
                $"ContextSize должен быть в диапазоне [2048; 131072]. Текущее: {ContextSize}");

        if (GpuLayers < -1)
            throw new InvalidOperationException("GpuLayers должен быть >= -1 (-1 = все слои на GPU).");

        if (Threads < 0 || Threads > 512)
            throw new InvalidOperationException("Threads должен быть в диапазоне [0; 512].");

        if (BatchSize < 32 || BatchSize > 8192)
            throw new InvalidOperationException("BatchSize должен быть в диапазоне [32; 8192].");

        if (Temperature < 0 || Temperature > 2)
            throw new InvalidOperationException(
                $"Temperature должен быть в диапазоне [0; 2]. Текущее: {Temperature}");

        if (MaxTokens < 100 || MaxTokens > 4096)
            throw new InvalidOperationException(
                $"MaxTokens должен быть в диапазоне [100; 4096]. Текущее: {MaxTokens}");

        if (ThinkingBudgetTokens < 0 || ThinkingBudgetTokens > 8192)
            throw new InvalidOperationException("ThinkingBudgetTokens должен быть в диапазоне [0; 8192].");

        if (TopK < 1 || TopK > 1000)
            throw new InvalidOperationException($"AI:TopK должен быть в диапазоне [1; 1000]. Текущее: {TopK}");

        if (MinP < 0 || MinP > 1)
            throw new InvalidOperationException($"AI:MinP должен быть в диапазоне [0; 1]. Текущее: {MinP}");

        if (RepeatPenalty < 0.5f || RepeatPenalty > 2f)
            throw new InvalidOperationException($"AI:RepeatPenalty должен быть в диапазоне [0.5; 2]. Текущее: {RepeatPenalty}");

        if (PresencePenalty < 0 || PresencePenalty > 2)
            throw new InvalidOperationException($"AI:PresencePenalty должен быть в диапазоне [0; 2]. Текущее: {PresencePenalty}");

        if (FrequencyPenalty < 0 || FrequencyPenalty > 2)
            throw new InvalidOperationException($"AI:FrequencyPenalty должен быть в диапазоне [0; 2]. Текущее: {FrequencyPenalty}");

        if (PenaltyLastN < 0 || PenaltyLastN > 8192)
            throw new InvalidOperationException($"AI:PenaltyLastN должен быть в диапазоне [0; 8192]. Текущее: {PenaltyLastN}");

        if (NoThinkTemperature < 0 || NoThinkTemperature > 2)
            throw new InvalidOperationException($"AI:NoThinkTemperature должен быть в диапазоне [0; 2]. Текущее: {NoThinkTemperature}");

        if (NoThinkTopP < 0 || NoThinkTopP > 1)
            throw new InvalidOperationException($"AI:NoThinkTopP должен быть в диапазоне [0; 1]. Текущее: {NoThinkTopP}");

        if (KvMinReusePrefix < 1 || KvMinReusePrefix > 8192)
            throw new InvalidOperationException($"AI:KvMinReusePrefix должен быть в диапазоне [1; 8192]. Текущее: {KvMinReusePrefix}");

        if (DegenerateResetThreshold < 1 || DegenerateResetThreshold > 10)
            throw new InvalidOperationException($"AI:DegenerateResetThreshold должен быть в диапазоне [1; 10]. Текущее: {DegenerateResetThreshold}");

        if (DegenerateReloadThreshold < DegenerateResetThreshold || DegenerateReloadThreshold > 20)
            throw new InvalidOperationException(
                $"AI:DegenerateReloadThreshold должен быть в диапазоне [DegenerateResetThreshold; 20]. Текущее: {DegenerateReloadThreshold}");

        if (MaxNewsChars < 200 || MaxNewsChars > 20000)
            throw new InvalidOperationException($"AI:MaxNewsChars должен быть в диапазоне [200; 20000]. Текущее: {MaxNewsChars}");

        if (InferenceTimeoutMs < 500 || InferenceTimeoutMs > 900_000)
            throw new InvalidOperationException(
                $"InferenceTimeoutMs должен быть в диапазоне [500; 900000]. Текущее: {InferenceTimeoutMs}");

        if (MaxRetriesOnInvalidJson < 1 || MaxRetriesOnInvalidJson > 10)
            throw new InvalidOperationException("MaxRetriesOnInvalidJson должен быть в диапазоне [1; 10].");

        if (!string.IsNullOrWhiteSpace(Backend) &&
            !Enum.TryParse<AiBackend>(Backend.Trim(), true, out _))
            throw new InvalidOperationException(
                $"AI:Backend должен быть Auto, Cpu, Cuda12, Cuda11 или Vulkan. Текущее: {Backend}");

        if (MinConfidence < 0 || MinConfidence > 1)
            throw new InvalidOperationException("MinConfidence должен быть в диапазоне [0; 1].");

        if (VramReserveMb < 0 || VramReserveMb > 16384)
            throw new InvalidOperationException(
                $"VramReserveMb должен быть в диапазоне [0; 16384]. Текущее: {VramReserveMb}");

        if (MinGpuLayers < 0 || MinGpuLayers > 256)
            throw new InvalidOperationException(
                $"MinGpuLayers должен быть в диапазоне [0; 256]. Текущее: {MinGpuLayers}");

        if (!string.IsNullOrWhiteSpace(NativeLogLevel) &&
            NativeLogLevel.Trim().ToLowerInvariant() is not ("off" or "error" or "warn" or "warning" or "info" or "debug"))
            throw new InvalidOperationException(
                $"AI:NativeLogLevel должен быть Off, Error, Warn, Info или Debug. Текущее: {NativeLogLevel}");

        if (!string.IsNullOrWhiteSpace(KvCacheType) &&
            KvCacheType.Trim().ToUpperInvariant() is not ("F16" or "Q8_0" or "Q4_0"))
            throw new InvalidOperationException(
                $"AI:KvCacheType должен быть F16, Q8_0 или Q4_0. Текущее: {KvCacheType}");

        if (!string.IsNullOrWhiteSpace(ConfidencePolicy) &&
            !Enum.TryParse<ConfidencePolicy>(ConfidencePolicy.Trim(), true, out _))
            throw new InvalidOperationException(
                $"AI:ConfidencePolicy должен быть ModelOnly, Blend или EngineOverride. Текущее: {ConfidencePolicy}");

        if (BlendModelWeight < 0 || BlendModelWeight > 1)
            throw new InvalidOperationException(
                $"AI:BlendModelWeight должен быть в диапазоне [0; 1]. Текущее: {BlendModelWeight}");

        if (MinConfidenceFloor < 0 || MinConfidenceFloor > 1)
            throw new InvalidOperationException(
                $"AI:MinConfidenceFloor должен быть в диапазоне [0; 1]. Текущее: {MinConfidenceFloor}");

        if (MinConfidenceCeiling < MinConfidenceFloor || MinConfidenceCeiling > 1)
            throw new InvalidOperationException(
                $"AI:MinConfidenceCeiling должен быть в диапазоне [MinConfidenceFloor; 1]. Текущее: {MinConfidenceCeiling}");

        if (MinConfidenceTuneStep <= 0 || MinConfidenceTuneStep > 0.5m)
            throw new InvalidOperationException(
                $"AI:MinConfidenceTuneStep должен быть в диапазоне (0; 0.5]. Текущее: {MinConfidenceTuneStep}");

        if (MinConfidenceTuneIntervalMinutes < 1 || MinConfidenceTuneIntervalMinutes > 1440)
            throw new InvalidOperationException(
                $"AI:MinConfidenceTuneIntervalMinutes должен быть в диапазоне [1; 1440]. Текущее: {MinConfidenceTuneIntervalMinutes}");

        if (MinConfidenceTuneMinTrades < 10 || MinConfidenceTuneMinTrades > 100000)
            throw new InvalidOperationException(
                $"AI:MinConfidenceTuneMinTrades должен быть в диапазоне [10; 100000]. Текущее: {MinConfidenceTuneMinTrades}");

        if (MinConfidenceTuneMinBucketTrades < 5 || MinConfidenceTuneMinBucketTrades > MinConfidenceTuneMinTrades)
            throw new InvalidOperationException(
                $"AI:MinConfidenceTuneMinBucketTrades должен быть в диапазоне [5; MinConfidenceTuneMinTrades]. Текущее: {MinConfidenceTuneMinBucketTrades}");

        // P2.6: веса скоринга и параметры валидации.
        if (ScoringWeights is null)
            throw new InvalidOperationException("AI:ScoringWeights не может быть null.");
        ScoringWeights.Validate();

        if (ScoringCalibrationMinTrades < 10 || ScoringCalibrationMinTrades > 100000)
            throw new InvalidOperationException(
                $"AI:ScoringCalibrationMinTrades должен быть в диапазоне [10; 100000]. Текущее: {ScoringCalibrationMinTrades}");

        if (ScoringCalibrationMinBinTrades < 5 || ScoringCalibrationMinBinTrades > ScoringCalibrationMinTrades)
            throw new InvalidOperationException(
                $"AI:ScoringCalibrationMinBinTrades должен быть в диапазоне [5; ScoringCalibrationMinTrades]. Текущее: {ScoringCalibrationMinBinTrades}");

        if (ScoringCalibrationBinStep <= 0 || ScoringCalibrationBinStep > 0.5m)
            throw new InvalidOperationException(
                $"AI:ScoringCalibrationBinStep должен быть в диапазоне (0; 0.5]. Текущее: {ScoringCalibrationBinStep}");
    }
}

/// <summary>Политика итоговой уверенности сигнала (B9).</summary>
public enum ConfidencePolicy
{
    /// <summary>Как сейчас: уверенность модели без калибровки.</summary>
    ModelOnly = 0,

    /// <summary>final = w*model + (1-w)*engine_prior (w = AI:BlendModelWeight, по умолчанию 0.7); дегенерат модели → engine_prior.</summary>
    Blend = 1,

    /// <summary>final = engine_prior всегда (математика решает).</summary>
    EngineOverride = 2
}
