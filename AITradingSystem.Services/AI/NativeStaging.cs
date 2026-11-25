using System.Runtime.InteropServices;
using AITradingSystem.Common.Configuration;
using AITradingSystem.Common.Logging;
using Microsoft.Extensions.Logging;

namespace AITradingSystem.Services.AI;

/// <summary>
/// A3. Стейджинг нативных библиотек: копирует ОДИН выбранный набор DLL
/// (llama + ggml-base + ggml + ggml-cpu* + backend) в плоский каталог ДО первой
/// загрузки llama и направляет туда GGML_BACKEND_PATH.
///
/// Решает главную причину F1: при GGML_BACKEND_DL llama.dll сама сканирует каталог
/// exe и регистрирует только те ggml-*.dll, что лежат плоско рядом. Если CUDA-DLL
/// лежат в подпапке cuda12\, а рядом с exe — CPU-набор, то реально работает CPU.
/// Стейдж кладёт выбранный набор (включая ggml-cuda + cudart/cublas) плоско в один
/// каталог, и llama регистрирует именно его.
///
/// Вызывать из App.xaml.cs на старте (после выбора бэкенда) и из QwenModel.LoadAsync
/// при смене бэкенда — ДО любого обращения к API LLamaSharp.
/// </summary>
public static class NativeStaging
{
    private static readonly object Sync = new();

    /// <summary>Активный staged-каталог (null, если стейджинг не выполнялся/не удался).</summary>
    public static string? StagedDirectory { get; private set; }

    /// <summary>Бэкенд, для которого выполнен стейдж.</summary>
    public static AiBackend? StagedBackend { get; private set; }

    /// <summary>
    /// Копирует выбранный набор нативных DLL в плоский staged-каталог и настраивает
    /// окружение (GGML_BACKEND_PATH, SetDllDirectory/AddDllDirectory, резолвер LLamaSharp).
    /// Возвращает путь staged-каталога или null, если исходный набор не найден.
    /// Идемпотентно: повторный вызов для того же бэкенда пропускает копирование.
    /// </summary>
    public static string? Stage(AiBackend backend, string? explicitPath = null, ILogger? logger = null)
    {
        lock (Sync)
        {
            if (backend == AiBackend.Auto)
            {
                logger?.LogWarning("NativeStaging.Stage(Auto) невозможен — сначала выберите конкретный бэкенд (GpuProbe.SelectBackend)");
                return null;
            }

            // Если llama уже загружена из другого каталога — стейдж не сможет её подменить
            // в этом процессе (хэндлы закэшированы маршаллером). Честно предупреждаем.
            var loadedFrom = LlamaNativeLoader.GetBackendDirectory();
            if (loadedFrom != null && StagedDirectory != null &&
                !string.Equals(loadedFrom, StagedDirectory, StringComparison.OrdinalIgnoreCase) &&
                backend != StagedBackend)
            {
                logger?.LogWarning(
                    "llama уже загружена из {LoadedFrom}; смена бэкенда на {Backend} потребует чистого стейджа/перезапуска",
                    loadedFrom, backend);
            }

            if (StagedDirectory != null && StagedBackend == backend && Directory.Exists(StagedDirectory))
            {
                ApplyEnvironment(StagedDirectory, logger);
                return StagedDirectory;
            }

            // 1. Найти исходный каталог выбранного бэкенда (строгий набор в одном каталоге).
            var sourceDir = FindSourceDirectory(backend, explicitPath, logger);
            if (sourceDir == null)
            {
                logger?.LogWarning("NativeStaging: исходный набор для {Backend} не найден — стейдж пропущен", backend);
                AiLog.Backend($"стейдж {backend}: исходные DLL не найдены");
                return null;
            }

            // 2. Выбрать целевой каталог: рядом с exe, fallback %LOCALAPPDATA%.
            var stageDir = ChooseStageDirectory(backend, logger);
            if (stageDir == null)
            {
                logger?.LogWarning("NativeStaging: не удалось создать writable staged-каталог");
                return null;
            }

            // 3. Скопировать набор.
            var copied = CopyNativeSet(sourceDir, stageDir, backend, logger);
            if (!copied)
            {
                logger?.LogWarning("NativeStaging: копирование набора {Backend} не удалось", backend);
                return null;
            }

            // 4. Настроить окружение и резолвер.
            ApplyEnvironment(stageDir, logger);
            StagedDirectory = stageDir;
            StagedBackend = backend;

            AiLog.Backend($"стейдж {backend}: {stageDir} (источник {sourceDir})");
            LogStageContents(stageDir, logger);
            return stageDir;
        }
    }

    /// <summary>Сброс состояния стейджа (для тестов/чистой перезагрузки).</summary>
    public static void Reset()
    {
        lock (Sync)
        {
            StagedDirectory = null;
            StagedBackend = null;
        }
    }

    // ------------------------------------------------------------------
    // Поиск исходного набора
    // ------------------------------------------------------------------

    private static string? FindSourceDirectory(AiBackend backend, string? explicitPath, ILogger? logger)
    {
        foreach (var dir in CandidateDirectories(backend, explicitPath))
        {
            if (!Directory.Exists(dir)) continue;
            if (!HasFullSet(dir, backend)) continue;
            return dir;
        }

        logger?.LogDebug("NativeStaging: ни один каталог не содержит полный набор для {Backend}", backend);
        return null;
    }

    /// <summary>
    /// Строгая проверка (без «мягкого» fallback): llama + ggml-base + marker в ОДНОМ каталоге;
    /// для CUDA дополнительно cudart/cublas.
    /// </summary>
    private static bool HasFullSet(string dir, AiBackend backend)
    {
        if (!File.Exists(Path.Combine(dir, FileName("llama"))) ||
            !File.Exists(Path.Combine(dir, FileName("ggml-base"))))
            return false;

        switch (backend)
        {
            case AiBackend.Cuda12:
                return File.Exists(Path.Combine(dir, FileName("ggml-cuda"))) &&
                       HasCudaRuntimeFiles(dir, major: 12);
            case AiBackend.Cuda11:
                return File.Exists(Path.Combine(dir, FileName("ggml-cuda"))) &&
                       HasCudaRuntimeFiles(dir, major: 11);
            case AiBackend.Vulkan:
                return File.Exists(Path.Combine(dir, FileName("ggml-vulkan")));
            default:
                return Directory.EnumerateFiles(dir, CpuSearchPattern(), SearchOption.TopDirectoryOnly).Any();
        }
    }

    private static string CpuSearchPattern() =>
        OperatingSystem.IsWindows() ? "ggml-cpu*.dll" : "libggml-cpu*";

    private static bool HasCudaRuntimeFiles(string dir, int major)
    {
        // Проверка cudart/cublas актуальна для Windows-раскладки LLamaSharp.Backend.Cuda*.
        if (!OperatingSystem.IsWindows())
            return true;

        var cudart = major == 12 ? "cudart64_12" : "cudart64_110";
        var cublas = major == 12 ? "cublas64_12" : "cublas64_11";
        return File.Exists(Path.Combine(dir, cudart + ".dll")) &&
               File.Exists(Path.Combine(dir, cublas + ".dll"));
    }

    private static IEnumerable<string> CandidateDirectories(AiBackend backend, string? explicitPath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in Enumerate(backend, explicitPath))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string full;
            try { full = Path.GetFullPath(dir); }
            catch { continue; }
            if (seen.Add(full)) yield return full;
        }
    }

    private static IEnumerable<string> Enumerate(AiBackend backend, string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var p = explicitPath!.Trim();
            if (File.Exists(p))
            {
                var d = Path.GetDirectoryName(Path.GetFullPath(p));
                if (!string.IsNullOrEmpty(d)) yield return d;
            }
            else
            {
                yield return p;
                if (!Path.IsPathRooted(p))
                    yield return Path.Combine(AppContext.BaseDirectory, p);
            }
        }

        var baseDir = AppContext.BaseDirectory;
        var rid = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";

        foreach (var sub in BackendSubdirs(backend))
        {
            yield return Path.Combine(baseDir, sub);
            yield return Path.Combine(baseDir, "runtimes", rid, "native", sub);
        }

        yield return Path.Combine(baseDir, "runtimes", rid, "native");
        foreach (var level in new[] { "avx2", "avx512", "avx", "noavx" })
        {
            yield return Path.Combine(baseDir, "runtimes", rid, "native", level);
            yield return Path.Combine(baseDir, level);
        }

        yield return baseDir;
    }

    private static IEnumerable<string> BackendSubdirs(AiBackend backend) => backend switch
    {
        AiBackend.Cuda12 => new[] { "cuda12", "cuda", "gpu" },
        AiBackend.Cuda11 => new[] { "cuda11", "cuda", "gpu" },
        AiBackend.Vulkan => new[] { "vulkan", "gpu" },
        _ => new[] { "cpu" }
    };

    // ------------------------------------------------------------------
    // Целевой каталог
    // ------------------------------------------------------------------

    private static string? ChooseStageDirectory(AiBackend backend, ILogger? logger)
    {
        var name = backend.ToString().ToLowerInvariant();

        // Приоритет: рядом с exe (llama при GGML_BACKEND_DL сканирует и exe-каталог,
        // и GGML_BACKEND_PATH; плоский набор рядом с exe — самый надёжный вариант).
        var primary = Path.Combine(AppContext.BaseDirectory, "native-staged", name);
        if (TryEnsureWritableDirectory(primary))
            return primary;

        // Fallback: %LOCALAPPDATA%\AITradingSystem\native\<backend> (exe read-only).
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) return null;

        var fallback = Path.Combine(local, "AITradingSystem", "native", name);
        if (TryEnsureWritableDirectory(fallback))
        {
            logger?.LogInformation("NativeStaging: exe-каталог read-only, стейдж в {Dir}", fallback);
            return fallback;
        }

        return null;
    }

    private static bool TryEnsureWritableDirectory(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Копирование набора
    // ------------------------------------------------------------------

    private static bool CopyNativeSet(string sourceDir, string stageDir, AiBackend backend, ILogger? logger)
    {
        try
        {
            // Базовый набор: llama + ggml + ggml-base.
            foreach (var name in new[] { "llama", "ggml", "ggml-base" })
            {
                var src = Path.Combine(sourceDir, FileName(name));
                if (File.Exists(src))
                    CopyIfChanged(src, Path.Combine(stageDir, Path.GetFileName(src)), logger);
            }

            // CPU-варианты ggml-cpu обязательны ВСЕГДА (llama.cpp требует CPU-бэкенд
            // даже при CUDA/Vulkan). Берём все найденные варианты (avx/avx2/avx512/noavx
            // могут лежать как ggml-cpu-<variant>.dll или один ggml-cpu.dll).
            var cpuFiles = Directory.EnumerateFiles(sourceDir, CpuSearchPattern(), SearchOption.TopDirectoryOnly).ToList();
            if (cpuFiles.Count == 0)
            {
                // Для GPU-наборов CPU-DLL может лежать отдельно (плоско рядом с exe или в cpu-каталогах).
                foreach (var cpuDir in CpuFallbackDirectories())
                {
                    if (!Directory.Exists(cpuDir)) continue;
                    cpuFiles = Directory.EnumerateFiles(cpuDir, CpuSearchPattern(), SearchOption.TopDirectoryOnly).ToList();
                    if (cpuFiles.Count > 0)
                    {
                        logger?.LogInformation("NativeStaging: CPU-варианты ggml-cpu взяты из {Dir}", cpuDir);
                        break;
                    }
                }
            }

            if (cpuFiles.Count == 0)
            {
                logger?.LogWarning("NativeStaging: ggml-cpu*.dll не найдены — llama.cpp требует CPU-бэкенд даже при GPU");
                AiLog.Backend($"стейдж {backend}: ВНИМАНИЕ — нет ggml-cpu*.dll, загрузка может упасть");
            }

            foreach (var src in cpuFiles)
                CopyIfChanged(src, Path.Combine(stageDir, Path.GetFileName(src)), logger);

            // Backend-специфичные файлы.
            switch (backend)
            {
                case AiBackend.Cuda12:
                case AiBackend.Cuda11:
                    CopyRequired(sourceDir, stageDir, "ggml-cuda", logger);
                    foreach (var pattern in new[] { "cudart64_*.dll", "cublas64_*.dll", "cublasLt64_*.dll" })
                        CopyPattern(sourceDir, stageDir, pattern, logger);
                    break;

                case AiBackend.Vulkan:
                    CopyRequired(sourceDir, stageDir, "ggml-vulkan", logger);
                    break;
            }

            // llama.dll в стейдже обязана быть.
            return File.Exists(Path.Combine(stageDir, FileName("llama")));
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "NativeStaging: ошибка копирования набора {Backend}", backend);
            return false;
        }
    }

    private static IEnumerable<string> CpuFallbackDirectories()
    {
        var baseDir = AppContext.BaseDirectory;
        var rid = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";

        yield return baseDir;
        yield return Path.Combine(baseDir, "cpu");
        yield return Path.Combine(baseDir, "runtimes", rid, "native");
        foreach (var level in new[] { "avx2", "avx512", "avx", "noavx" })
        {
            yield return Path.Combine(baseDir, "runtimes", rid, "native", level);
            yield return Path.Combine(baseDir, level);
        }
    }

    private static void CopyRequired(string sourceDir, string stageDir, string baseName, ILogger? logger)
    {
        var src = Path.Combine(sourceDir, FileName(baseName));
        if (File.Exists(src))
            CopyIfChanged(src, Path.Combine(stageDir, Path.GetFileName(src)), logger);
        else
            logger?.LogWarning("NativeStaging: обязательный файл {File} не найден в {Dir}", FileName(baseName), sourceDir);
    }

    private static void CopyPattern(string sourceDir, string stageDir, string pattern, ILogger? logger)
    {
        foreach (var src in Directory.EnumerateFiles(sourceDir, pattern, SearchOption.TopDirectoryOnly))
            CopyIfChanged(src, Path.Combine(stageDir, Path.GetFileName(src)), logger);
    }

    /// <summary>Идемпотентное копирование: пропуск по длине+времени записи; занятые файлы не валят стейдж.</summary>
    private static void CopyIfChanged(string source, string destination, ILogger? logger)
    {
        try
        {
            var srcInfo = new FileInfo(source);
            var dstInfo = new FileInfo(destination);

            if (dstInfo.Exists &&
                dstInfo.Length == srcInfo.Length &&
                dstInfo.LastWriteTimeUtc == srcInfo.LastWriteTimeUtc)
                return;

            File.Copy(source, destination, overwrite: true);
            File.SetLastWriteTimeUtc(destination, srcInfo.LastWriteTimeUtc);
            logger?.LogDebug("NativeStaging: скопирован {File}", Path.GetFileName(source));
        }
        catch (IOException ex)
        {
            // Файл занят уже загруженной библиотекой — если он есть и размер совпадает, продолжаем.
            if (File.Exists(destination) && new FileInfo(destination).Length == new FileInfo(source).Length)
            {
                logger?.LogDebug("NativeStaging: {File} занят, используем существующую копию ({Reason})",
                    Path.GetFileName(source), ex.Message);
                return;
            }

            logger?.LogWarning(ex, "NativeStaging: не удалось скопировать {File}", source);
            throw;
        }
    }

    // ------------------------------------------------------------------
    // Окружение
    // ------------------------------------------------------------------

    private static void ApplyEnvironment(string stageDir, ILogger? logger)
    {
        // GGML_BACKEND_PATH: llama.cpp при GGML_BACKEND_DL сканирует этот каталог
        // и регистрирует найденные ggml-*.dll. Должно быть установлено ДО первого llama API.
        Environment.SetEnvironmentVariable("GGML_BACKEND_PATH", stageDir);

        if (OperatingSystem.IsWindows())
        {
            try
            {
                // Зависимости CUDA (cudart/cublas) ищутся загрузчиком Windows в каталоге DLL.
                SetDllDirectory(stageDir);
                AddDllDirectory(stageDir);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "NativeStaging: SetDllDirectory/AddDllDirectory недоступны");
            }
        }

        // Резолвер LLamaSharp: строгий staged-режим — поиск ТОЛЬКО в этом каталоге (A4).
        LlamaNativeLoader.UseStagedDirectory(stageDir, logger);
        LlamaNativeLoader.Initialize(stageDir, logger);

        logger?.LogInformation("NativeStaging: GGML_BACKEND_PATH={Dir}", stageDir);
    }

    private static void LogStageContents(string stageDir, ILogger? logger)
    {
        try
        {
            var files = Directory.EnumerateFiles(stageDir, "*.dll", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var message = $"стейдж-каталог {stageDir}: {string.Join(", ", files)}; " +
                          $"GGML_BACKEND_PATH={Environment.GetEnvironmentVariable("GGML_BACKEND_PATH")}";
            logger?.LogInformation("{Message}", message);
            AiLog.Backend(message);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "NativeStaging: не удалось перечислить staged-каталог");
        }
    }

    private static string FileName(string baseName)
    {
        if (OperatingSystem.IsWindows()) return baseName + ".dll";
        if (OperatingSystem.IsMacOS()) return "lib" + baseName + ".dylib";
        return "lib" + baseName + ".so";
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string lpPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string newDirectory);
}
