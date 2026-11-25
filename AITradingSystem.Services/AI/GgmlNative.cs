using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using AITradingSystem.Common.Logging;

namespace AITradingSystem.Services.AI;

/// <summary>Реальное устройство, перечисленное изнутри llama.cpp (ggml_backend_dev_*).</summary>
public sealed class NativeDeviceInfo
{
    public string Name { get; init; } = string.Empty;
    public string Backend { get; init; } = string.Empty;
    public long VramTotalMb { get; init; }
    public long VramFreeMb { get; init; }

    public override string ToString() =>
        $"{Name} [{Backend}] VRAM {VramFreeMb}/{VramTotalMb} MB";
}

/// <summary>
/// A1. Безопасные P/Invoke-обёртки поверх уже загруженного llama.dll/ggml.dll:
/// перехват нативного лога (llama_log_set), явная регистрация backend-DLL,
/// перечисление устройств и VRAM. Все вызовы деградируют без краша, если экспорт
/// отсутствует в конкретной сборке LLamaSharp 0.30.
/// Вызывать ПОСЛЕ загрузки нативных библиотек (LlamaNativeLoader.TryProbeBackend).
/// </summary>
public static class GgmlNative
{
    private static readonly object Sync = new();

    // Делегат хранится в статическом поле, чтобы его не собрал GC, пока native держит указатель.
    private static LlamaLogCallback? _logCallback;
    private static bool _logCaptureInstalled;
    private static int _logThreshold = 1; // Info

    // ---- Статистика, распарсенная из нативного лога ----
    private static int? _offloadedLayers;
    private static int? _totalLayers;
    private static bool _cpuFallbackDetected;
    private static bool _outOfMemoryDetected;
    private static readonly Dictionary<string, double> GpuBuffers = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Regex OffloadRegex =
        new(@"offloaded\s+(\d+)/(\d+)\s+layers to GPU", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BufferSizeRegex =
        new(@"(CUDA\d+|Vulkan\d+|ROCm\d+|Metal)\s+buffer size\s*=\s*([\d.,]+)\s*MiB",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Фактический оффлоад из нативного лога (null, если строки не было).</summary>
    public static int? OffloadedLayers { get { lock (Sync) return _offloadedLayers; } }

    /// <summary>Всего слоёв модели по нативному логу.</summary>
    public static int? TotalLayers { get { lock (Sync) return _totalLayers; } }

    /// <summary>Нативный лог сообщил «using CPU instead».</summary>
    public static bool CpuFallbackDetected { get { lock (Sync) return _cpuFallbackDetected; } }

    /// <summary>Нативный лог сообщил об OOM (CUDA/Vulkan/буферы).</summary>
    public static bool OutOfMemoryDetected { get { lock (Sync) return _outOfMemoryDetected; } }

    /// <summary>Размеры GPU-буферов из нативного лога: CUDA0 → MiB.</summary>
    public static IReadOnlyDictionary<string, double> GpuBufferSizesMb
    {
        get { lock (Sync) return new Dictionary<string, double>(GpuBuffers); }
    }

    /// <summary>Сброс статистики перед каждой загрузкой модели.</summary>
    public static void ResetLoadStats()
    {
        lock (Sync)
        {
            _offloadedLayers = null;
            _totalLayers = null;
            _cpuFallbackDetected = false;
            _outOfMemoryDetected = false;
            GpuBuffers.Clear();
        }
    }

    /// <summary>Короткая сводка для логов/UI: «offload 48/49, CUDA0=9123 MiB».</summary>
    public static string StatsSummary()
    {
        lock (Sync)
        {
            var parts = new List<string>();
            if (_offloadedLayers.HasValue && _totalLayers.HasValue)
                parts.Add($"offload {_offloadedLayers}/{_totalLayers}");
            foreach (var kv in GpuBuffers)
                parts.Add($"{kv.Key}={kv.Value:F0} MiB");
            if (_cpuFallbackDetected) parts.Add("CPU fallback!");
            if (_outOfMemoryDetected) parts.Add("OOM!");
            return parts.Count > 0 ? string.Join(", ", parts) : "нативных данных пока нет";
        }
    }

    /// <summary>
    /// Перехватывает нативный лог llama.cpp через llama_log_set и маршрутизирует в AiLog.
    /// level: Off | Error | Warn | Info | Debug. Идемпотентно; повторный вызов меняет только порог.
    /// Возвращает false, если экспорт llama_log_set не найден (деградация без краша).
    /// </summary>
    public static bool InstallLogCapture(string level = "Info")
    {
        lock (Sync)
        {
            _logThreshold = ParseLevel(level);

            if (_logCaptureInstalled)
                return true;

            if (!TryResolve("llama_log_set", out var ptr))
            {
                AiLog.Backend("llama_log_set: экспорт не найден — нативный лог не перехватывается");
                return false;
            }

            try
            {
                var setter = Marshal.GetDelegateForFunctionPointer<LlamaLogSetFn>(ptr);
                _logCallback = OnNativeLog; // статическая ссылка — GC не соберёт
                setter(_logCallback, IntPtr.Zero);
                _logCaptureInstalled = true;
                AiLog.Backend($"нативный лог llama.cpp перехвачен (уровень {level})");
                return true;
            }
            catch (Exception ex)
            {
                AiLog.Backend($"llama_log_set не удалось установить: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Сброс состояния перехвата лога: после освобождения/смены нативной библиотеки
    /// старый llama_log_set относится к выгруженной DLL — перехват нужно установить заново.
    /// </summary>
    public static void ResetLogCapture()
    {
        lock (Sync)
        {
            _logCaptureInstalled = false;
        }
    }

    /// <summary>
    /// Явная регистрация всех backend-DLL из каталога (ggml_backend_load_all_from_path).
    /// Нужно при раскладке GGML_BACKEND_DL, когда llama сама не находит ggml-cuda/ggml-vulkan.
    /// </summary>
    public static bool TryLoadAllBackends(string directory)
    {
        if (!TryResolve("ggml_backend_load_all_from_path", out var ptr))
        {
            AiLog.Backend("ggml_backend_load_all_from_path: экспорт не найден — полагаемся на автоскан llama");
            return false;
        }

        try
        {
            var fn = Marshal.GetDelegateForFunctionPointer<LoadAllFromPathFn>(ptr);
            fn(directory);
            AiLog.Backend($"ggml: явная регистрация бэкендов из {directory}");
            return true;
        }
        catch (Exception ex)
        {
            AiLog.Backend($"ggml_backend_load_all_from_path({directory}) ошибка: {ex.Message}");
            return false;
        }
    }

    /// <summary>Явная загрузка одной backend-DLL (ggml_backend_load). true = зарегистрирована.</summary>
    public static bool TryLoadBackend(string dllPath)
    {
        if (!TryResolve("ggml_backend_load", out var ptr))
            return false;

        try
        {
            var fn = Marshal.GetDelegateForFunctionPointer<LoadBackendFn>(ptr);
            var reg = fn(dllPath);
            if (reg != IntPtr.Zero)
            {
                AiLog.Backend($"ggml: бэкенд загружен явно: {dllPath}");
                return true;
            }

            AiLog.Backend($"ggml_backend_load вернул null для {dllPath}");
            return false;
        }
        catch (Exception ex)
        {
            AiLog.Backend($"ggml_backend_load({dllPath}) ошибка: {ex.Message}");
            return false;
        }
    }

    /// <summary>llama_supports_gpu_offload(); null, если экспорт недоступен.</summary>
    public static bool? SupportsGpuOffload()
    {
        if (!TryResolve("llama_supports_gpu_offload", out var ptr))
            return null;

        try
        {
            var fn = Marshal.GetDelegateForFunctionPointer<SupportsGpuOffloadFn>(ptr);
            return fn();
        }
        catch (Exception ex)
        {
            AiLog.Backend($"llama_supports_gpu_offload ошибка: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Перечисление реальных устройств и VRAM (free/total) изнутри llama —
    /// точнее nvidia-smi, т.к. учитывает то, что видит сам ggml.
    /// Пустой список, если экспорты ggml_backend_dev_* недоступны.
    /// </summary>
    public static List<NativeDeviceInfo> GetDevices()
    {
        var result = new List<NativeDeviceInfo>();

        if (!TryResolve("ggml_backend_dev_count", out var countPtr) ||
            !TryResolve("ggml_backend_dev_get", out var getPtr) ||
            !TryResolve("ggml_backend_dev_name", out var namePtr))
        {
            AiLog.Backend("ggml_backend_dev_*: экспорты не найдены — список устройств недоступен");
            return result;
        }

        try
        {
            var count = Marshal.GetDelegateForFunctionPointer<DevCountFn>(countPtr)();
            var get = Marshal.GetDelegateForFunctionPointer<DevGetFn>(getPtr);
            var name = Marshal.GetDelegateForFunctionPointer<DevNameFn>(namePtr);

            DevBackendFn? backendOf = null;
            RegNameFn? regName = null;
            DevMemoryFn? memory = null;

            if (TryResolve("ggml_backend_dev_backend", out var backendPtr))
                backendOf = Marshal.GetDelegateForFunctionPointer<DevBackendFn>(backendPtr);
            if (TryResolve("ggml_backend_reg_name", out var regNamePtr))
                regName = Marshal.GetDelegateForFunctionPointer<RegNameFn>(regNamePtr);
            if (TryResolve("ggml_backend_dev_memory", out var memPtr))
                memory = Marshal.GetDelegateForFunctionPointer<DevMemoryFn>(memPtr);

            for (nuint i = 0; i < count && i < 64; i++)
            {
                var dev = get(i);
                if (dev == IntPtr.Zero) continue;

                var devName = Marshal.PtrToStringUTF8(name(dev)) ?? $"device{i}";

                var backendName = string.Empty;
                if (backendOf != null && regName != null)
                {
                    var reg = backendOf(dev);
                    if (reg != IntPtr.Zero)
                        backendName = Marshal.PtrToStringUTF8(regName(reg)) ?? string.Empty;
                }

                long freeMb = 0, totalMb = 0;
                if (memory != null)
                {
                    memory(dev, out var free, out var total);
                    freeMb = (long)(free / (1024 * 1024));
                    totalMb = (long)(total / (1024 * 1024));
                }

                result.Add(new NativeDeviceInfo
                {
                    Name = devName,
                    Backend = backendName,
                    VramFreeMb = freeMb,
                    VramTotalMb = totalMb
                });
            }
        }
        catch (Exception ex)
        {
            AiLog.Backend($"ggml_backend_dev_* ошибка перечисления: {ex.Message}");
        }

        return result;
    }

    // ------------------------------------------------------------------
    // Внутреннее
    // ------------------------------------------------------------------

    private static bool TryResolve(string export, out IntPtr ptr)
    {
        // Экспорты ggml живут в ggml.dll / ggml-base.dll / llama.dll в зависимости от сборки.
        foreach (var lib in new[] { "llama", "ggml", "ggml-base" })
        {
            if (LlamaNativeLoader.TryGetExport(lib, export, out ptr) && ptr != IntPtr.Zero)
                return true;
        }

        ptr = IntPtr.Zero;
        return false;
    }

    // Порог как «минимальный ранг важности»: ERROR=3, WARN=2, INFO=1, DEBUG=0.
    // Off=100 — не логируем ничего (парсинг статистики всё равно работает).
    private static int ParseLevel(string level) => level.Trim().ToLowerInvariant() switch
    {
        "off" => 100,
        "error" => 3,
        "warn" or "warning" => 2,
        "debug" => 0,
        _ => 1 // Info
    };

    private static void OnNativeLog(int level, IntPtr text, IntPtr userData)
    {
        try
        {
            var line = Marshal.PtrToStringUTF8(text);
            if (string.IsNullOrEmpty(line)) return;
            line = line.TrimEnd('\n', '\r');
            if (line.Length == 0) return;

            ParseLine(line);

            // ggml levels: 0=NONE, 1=INFO, 2=WARN, 3=ERROR, 4=DEBUG, 5=CONT (продолжение строки).
            // Ранг важности: ERROR=3 > WARN=2 > INFO=1 > DEBUG=0. Логируем, если ранг >= порога.
            var rank = level switch
            {
                3 => 3,
                2 => 2,
                4 => 0,
                _ => 1 // INFO, NONE, CONT
            };

            int threshold;
            lock (Sync) threshold = _logThreshold;
            if (rank < threshold) return;

            var prefix = level switch
            {
                3 => "[native ERROR] ",
                2 => "[native WARN] ",
                4 => "[native DEBUG] ",
                _ => "[native] "
            };
            AiLog.Backend(prefix + line);
        }
        catch
        {
            // Нативный callback не должен бросать исключения в native-код.
        }
    }

    private static void ParseLine(string line)
    {
        lock (Sync)
        {
            var offload = OffloadRegex.Match(line);
            if (offload.Success)
            {
                _offloadedLayers = int.Parse(offload.Groups[1].Value, CultureInfo.InvariantCulture);
                _totalLayers = int.Parse(offload.Groups[2].Value, CultureInfo.InvariantCulture);
            }

            if (line.Contains("using CPU instead", StringComparison.OrdinalIgnoreCase))
                _cpuFallbackDetected = true;

            if (line.Contains("out of memory", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("ErrorOutOfDeviceMemory", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("failed to allocate", StringComparison.OrdinalIgnoreCase))
                _outOfMemoryDetected = true;

            var buffer = BufferSizeRegex.Match(line);
            if (buffer.Success &&
                double.TryParse(buffer.Groups[2].Value.Replace(',', '.'),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var mib))
            {
                GpuBuffers[buffer.Groups[1].Value] = mib;
            }
        }
    }

    // ------------------------------------------------------------------
    // Нативные сигнатуры (cdecl)
    // ------------------------------------------------------------------

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void LlamaLogCallback(int level, IntPtr text, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void LlamaLogSetFn(LlamaLogCallback callback, IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void LoadAllFromPathFn([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr LoadBackendFn([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private delegate bool SupportsGpuOffloadFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nuint DevCountFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr DevGetFn(nuint index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr DevNameFn(IntPtr dev);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr DevBackendFn(IntPtr dev);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr RegNameFn(IntPtr reg);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DevMemoryFn(IntPtr dev, out nuint free, out nuint total);
}
