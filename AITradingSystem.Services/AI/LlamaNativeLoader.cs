using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;
using AITradingSystem.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace AITradingSystem.Services.AI;

/// <summary>
/// Загрузчик нативных библиотек llama.cpp для LLamaSharp с выбором бэкенда.
///
/// Поддерживаются наборы: CPU (avx/avx2/avx512/noavx), CUDA 11/12 (NVIDIA), Vulkan (AMD/Intel/NVIDIA).
/// Библиотеки ищутся рядом с exe, в runtimes/&lt;rid&gt;/native/&lt;backend&gt; и по пути AI:NativeLibraryPath.
/// Резолвер регистрируется ДО первого вызова API LLamaSharp (иначе NativeApi..cctor падает).
/// </summary>
public static class LlamaNativeLoader
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, IntPtr> LoadedHandles = new(StringComparer.OrdinalIgnoreCase);
    private static bool _resolverRegistered;
    private static string? _explicitPath;
    private static ILogger? _logger;

    // A4: true, когда резолвер вернул хэндл реальному P/Invoke LLamaSharp.
    // После этого NativeLibrary.Free опасен: маршаллер закэшировал указатели (DllImport cache).
    private static bool _marshalerResolved;
    private static bool _requiresCleanStage;
    private static string? _stagedDirectory;

    /// <summary>Активный бэкенд (влияет на порядок поиска каталогов и предзагрузку зависимостей).</summary>
    public static AiBackend ActiveBackend { get; private set; } = AiBackend.Cpu;

    /// <summary>Каталог, из которого реально загружена llama (для диагностики).</summary>
    public static string? LoadedFromDirectory { get; private set; }

    /// <summary>Явный уровень набора инструкций CPU ("avx2", "avx512", "avx", "noavx").</summary>
    public static string? PreferredAvxLevel { get; set; }

    /// <summary>
    /// Выбирает бэкенд до регистрации резолвера. Можно вызывать повторно до загрузки модели.
    /// </summary>
    public static void UseBackend(AiBackend backend, ILogger? logger = null)
    {
        lock (Sync)
        {
            if (logger != null) _logger = logger;

            if (LoadedHandles.ContainsKey("llama") && ActiveBackend != backend)
            {
                if (_marshalerResolved)
                {
                    // Хэндлы уже закэшированы P/Invoke-маршаллером — Free даст висячие указатели
                    // и AccessViolation на следующей загрузке (диагноз 0.4). Не освобождаем.
                    _requiresCleanStage = true;
                    _logger?.LogWarning(
                        "Бэкенд {Active} уже использован LLamaSharp; переключение на {Requested} " +
                        "возможно только через чистый стейдж (требуется перезапуск загрузки)",
                        ActiveBackend, backend);
                    return;
                }

                // Были только пробные загрузки (TryProbeBackend), маршаллер ещё не кэшировал
                // указатели — безопасно освобождаем и переключаемся без перезапуска приложения.
                _logger?.LogInformation(
                    "Бэкенд {Active} был только пробным; освобождаю хэндлы и переключаюсь на {Requested}",
                    ActiveBackend, backend);
                FreeLoadedHandlesUnsafe();
            }

            ActiveBackend = backend;
            _logger?.LogInformation("Нативный бэкенд llama.cpp: {Backend}", backend);
        }
    }

    /// <summary>
    /// Есть ли на диске ПОЛНЫЙ набор нативных файлов выбранного бэкенда в ОДНОМ каталоге:
    /// llama + ggml-base + marker (ggml-cuda/ggml-vulkan/ggml-cpu).
    /// Для CUDA дополнительно cudart/cublas нужной версии (Windows-раскладка).
    /// «Мягкий» fallback (marker без llama) убран: он давал ложный выбор CUDA,
    /// а реально грузился плоский CPU llama.dll (диагноз 0.1/F1).
    /// </summary>
    public static bool HasBackendFiles(AiBackend backend)
    {
        var marker = backend switch
        {
            AiBackend.Cuda11 or AiBackend.Cuda12 => "ggml-cuda",
            AiBackend.Vulkan => "ggml-vulkan",
            _ => "ggml-cpu"
        };

        foreach (var dir in GetCandidateDirectories(backend))
        {
            if (!Directory.Exists(dir))
                continue;

            if (!File.Exists(Path.Combine(dir, GetFileName("llama"))) ||
                !File.Exists(Path.Combine(dir, GetFileName("ggml-base"))))
                continue;

            var hasMarker = marker == "ggml-cpu"
                ? Directory.EnumerateFiles(dir, OperatingSystem.IsWindows() ? "ggml-cpu*.dll" : "libggml-cpu*").Any()
                : File.Exists(Path.Combine(dir, GetFileName(marker)));
            if (!hasMarker)
                continue;

            if (backend is AiBackend.Cuda11 or AiBackend.Cuda12 && OperatingSystem.IsWindows())
            {
                var cudart = backend == AiBackend.Cuda12 ? "cudart64_12.dll" : "cudart64_110.dll";
                var cublas = backend == AiBackend.Cuda12 ? "cublas64_12.dll" : "cublas64_11.dll";
                if (!File.Exists(Path.Combine(dir, cudart)) || !File.Exists(Path.Combine(dir, cublas)))
                    continue;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Активирует строгий staged-режим резолвера: поиск ТОЛЬКО в этом каталоге.
    /// Вызывается из NativeStaging.Stage после копирования набора.
    /// </summary>
    public static void UseStagedDirectory(string directory, ILogger? logger = null)
    {
        lock (Sync)
        {
            if (logger != null) _logger = logger;
            _stagedDirectory = Path.GetFullPath(directory);
            _explicitPath = _stagedDirectory;
            _logger?.LogInformation("Резолвер llama: строгий staged-режим, каталог {Dir}", _stagedDirectory);
        }
    }

    /// <summary>Требуется ли чистый стейдж/перезапуск загрузки для смены бэкенда.</summary>
    public static bool RequiresCleanStage => _requiresCleanStage;

    /// <summary>
    /// Регистрирует DllImportResolver для сборки LLamaSharp. Идемпотентно и потокобезопасно.
    /// </summary>
    public static void Initialize(string? explicitNativePath = null, ILogger? logger = null)
    {
        lock (Sync)
        {
            if (!string.IsNullOrWhiteSpace(explicitNativePath))
                _explicitPath = explicitNativePath.Trim();

            if (logger != null)
                _logger = logger;

            if (_resolverRegistered)
                return;

            try
            {
                var assembly = FindLlamaSharpAssembly();
                NativeLibrary.SetDllImportResolver(assembly, ImportResolver);
                _resolverRegistered = true;
                _logger?.LogDebug("DllImportResolver для LLamaSharp зарегистрирован");
            }
            catch (InvalidOperationException ex)
            {
                _resolverRegistered = true;
                _logger?.LogDebug(ex, "DllImportResolver для LLamaSharp уже зарегистрирован");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Не удалось зарегистрировать DllImportResolver для LLamaSharp");
            }
        }
    }

    /// <summary>
    /// Проверяет, что llama доступна, и возвращает путь к найденной библиотеке.
    /// </summary>
    public static string EnsureAvailable(string? explicitNativePath = null, ILogger? logger = null)
    {
        Initialize(explicitNativePath, logger);

        if (TryFindLibraryFile("llama", out var path))
        {
            _logger?.LogInformation("Нативная библиотека llama найдена: {Path} (бэкенд {Backend})", path, ActiveBackend);
            return path;
        }

        throw new FileNotFoundException(BuildNotFoundMessage());
    }

    /// <summary>
    /// Пробная загрузка нативных библиотек выбранного бэкенда БЕЗ обращения к API LLamaSharp.
    /// Важно: если сначала позвать LLamaSharp и библиотека не подгрузится, статический
    /// конструктор NativeApi «сломается» до конца процесса и откат на CPU станет невозможен.
    /// Поэтому проверяем GPU сами через NativeLibrary и только потом грузим модель.
    /// </summary>
    public static bool TryProbeBackend(AiBackend backend, ILogger? logger = null)
    {
        lock (Sync)
        {
            if (logger != null) _logger = logger;

            ActiveBackend = backend;

            if (!HasBackendFiles(backend))
            {
                _logger?.LogWarning("Бэкенд {Backend}: нативные файлы не найдены на диске", backend);
                return false;
            }

            var handle = ResolveHandle("llama");
            if (handle != IntPtr.Zero)
            {
                _logger?.LogInformation("Бэкенд {Backend} прошёл пробную загрузку ({Dir})", backend, LoadedFromDirectory);
                return true;
            }

            _logger?.LogWarning("Бэкенд {Backend} не загрузился (драйвер GPU/рантайм/зависимости)", backend);

            // A4: FreeLoadedHandles безопасен только ДО первого P/Invoke LLamaSharp.
            // Если маршаллер уже закэшировал указатели — не освобождаем (иначе AccessViolation),
            // помечаем «требуется чистый стейдж» и грузим следующий кандидат изолированно.
            if (_marshalerResolved)
            {
                _requiresCleanStage = true;
                _logger?.LogWarning(
                    "Хэндлы уже использованы LLamaSharp — не освобождаю их; следующий кандидат " +
                    "будет загружен из изолированного staged-каталога (требуется чистый стейдж)");
            }
            else
            {
                FreeLoadedHandlesUnsafe();
            }

            return false;
        }
    }

    /// <summary>
    /// Каталог фактической загрузки нативных библиотек (стейдж или найденный каталог).
    /// null, если ничего ещё не загружено.
    /// </summary>
    public static string? GetBackendDirectory() => LoadedFromDirectory;

    /// <summary>
    /// Пытается получить адрес экспорта из уже загруженной (или загружаемой через резолвер)
    /// нативной библиотеки: "llama", "ggml", "ggml-base", "ggml-cuda" и т.п.
    /// Используется GgmlNative для безопасных P/Invoke-обёрток. Не бросает исключений.
    /// </summary>
    public static bool TryGetExport(string library, string export, out IntPtr ptr)
    {
        ptr = IntPtr.Zero;
        var baseName = NormalizeLibraryName(library);
        if (baseName == null)
            return false;

        lock (Sync)
        {
            try
            {
                if (!LoadedHandles.TryGetValue(baseName, out var handle) || handle == IntPtr.Zero)
                {
                    handle = ResolveHandle(baseName);
                    if (handle == IntPtr.Zero)
                        return false;
                }

                return NativeLibrary.TryGetExport(handle, export, out ptr);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "TryGetExport({Lib}, {Export}) не удался", baseName, export);
                return false;
            }
        }
    }

    /// <summary>
    /// Освобождает предзагруженные хэндлы. Безопасно ТОЛЬКО до первого P/Invoke LLamaSharp;
    /// после него маршаллер держит закэшированные указатели и Free даст AccessViolation.
    /// </summary>
    public static void FreeLoadedHandles()
    {
        lock (Sync)
        {
            if (_marshalerResolved)
            {
                _requiresCleanStage = true;
                _logger?.LogWarning(
                    "FreeLoadedHandles пропущен: LLamaSharp уже закэшировал нативные указатели. " +
                    "Смена бэкенда потребует чистого стейджа/перезапуска загрузки");
                return;
            }

            FreeLoadedHandlesUnsafe();
        }
    }

    /// <summary>Внутреннее освобождение без проверок — вызывать только под Sync и до первого P/Invoke.</summary>
    private static void FreeLoadedHandlesUnsafe()
    {
        foreach (var handle in LoadedHandles.Values)
        {
            try { NativeLibrary.Free(handle); }
            catch (Exception ex) { _logger?.LogDebug(ex, "Не удалось освободить нативный хэндл"); }
        }

        LoadedHandles.Clear();
        LoadedFromDirectory = null;

        // Перехват нативного лога указывал в выгруженную библиотеку — переустановить на новой.
        GgmlNative.ResetLogCapture();
    }

    private static Assembly FindLlamaSharpAssemblySafe()
    {
        try { return FindLlamaSharpAssembly(); }
        catch (Exception) { return typeof(LlamaNativeLoader).Assembly; }
    }

    /// <summary>Диагностика: где искали и что нашли.</summary>
    public static string GetDiagnostics()
    {
        var sb = new StringBuilder();
        sb.Append("Backend: ").Append(ActiveBackend)
          .Append(", RID: ").Append(GetRuntimeIdentifier())
          .Append(", OS: ").Append(RuntimeInformation.OSDescription)
          .Append(", Arch: ").Append(RuntimeInformation.OSArchitecture)
          .Append(", AVX2: ").Append(Avx2.IsSupported)
          .Append(", AVX512F: ").Append(Avx512F.IsSupported);

        sb.AppendLine();
        sb.Append("  Staged: ").Append(_stagedDirectory ?? "нет")
          .Append(", LoadedFrom: ").Append(LoadedFromDirectory ?? "ещё не загружена")
          .Append(", MarshalerUsed: ").Append(_marshalerResolved)
          .Append(", RequiresCleanStage: ").Append(_requiresCleanStage)
          .Append(", GGML_BACKEND_PATH: ").Append(Environment.GetEnvironmentVariable("GGML_BACKEND_PATH") ?? "не задан");

        foreach (var dir in GetCandidateDirectories(ActiveBackend))
        {
            sb.AppendLine();
            sb.Append("  ").Append(dir).Append(" -> ")
              .Append(Directory.Exists(dir) ? "есть каталог" : "нет каталога");

            if (!Directory.Exists(dir)) continue;

            sb.Append(", llama: ").Append(File.Exists(Path.Combine(dir, GetFileName("llama"))) ? "найден" : "НЕТ");

            foreach (var marker in new[] { "ggml-cpu", "ggml-cuda", "ggml-vulkan" })
            {
                if (File.Exists(Path.Combine(dir, GetFileName(marker))))
                    sb.Append(", ").Append(marker).Append(": есть");
            }

            // A4: полный состав каталога — видно, какие именно ggml-бэкенды зарегистрирует llama.
            try
            {
                var pattern = OperatingSystem.IsWindows() ? "*.dll" : "*.so*";
                var files = Directory.EnumerateFiles(dir, pattern)
                    .Select(Path.GetFileName)
                    .Where(n => n != null &&
                                (n.StartsWith("ggml", StringComparison.OrdinalIgnoreCase) ||
                                 n.StartsWith("llama", StringComparison.OrdinalIgnoreCase) ||
                                 n.StartsWith("cudart", StringComparison.OrdinalIgnoreCase) ||
                                 n.StartsWith("cublas", StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (files.Count > 0)
                    sb.Append(", файлы: [").Append(string.Join(", ", files)).Append(']');
            }
            catch (Exception)
            {
                // Каталог недоступен для чтения — диагностика не должна падать.
            }
        }

        return sb.ToString();
    }

    private static Assembly FindLlamaSharpAssembly()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (string.Equals(asm.GetName().Name, "LLamaSharp", StringComparison.OrdinalIgnoreCase))
                return asm;
        }

        return Assembly.Load(new AssemblyName("LLamaSharp"));
    }

    /// <summary>
    /// Вход для DllImportResolver LLamaSharp. Успешный резолв здесь означает, что маршаллер
    /// закэширует указатели — после этого NativeLibrary.Free запрещён.
    /// </summary>
    private static IntPtr ImportResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        var handle = ResolveCore(libraryName);
        if (handle != IntPtr.Zero)
            _marshalerResolved = true;
        return handle;
    }

    /// <summary>Пробный резолв (TryProbeBackend/TryGetExport) — не помечает маршаллер как использованный.</summary>
    private static IntPtr ResolveHandle(string baseName) => ResolveCore(baseName);

    private static IntPtr ResolveCore(string libraryName)
    {
        var baseName = NormalizeLibraryName(libraryName);
        if (baseName == null)
            return IntPtr.Zero;

        lock (Sync)
        {
            // A4: стейдж сменился на другой каталог. Пока P/Invoke-маршаллер не закэшировал
            // указатели, освобождаем хэндлы прежнего набора — иначе резолвер вернёт закэшированную
            // llama прошлого бэкенда и переключение будет невозможно даже между пробами.
            if (_stagedDirectory != null && LoadedHandles.Count > 0 &&
                !string.Equals(LoadedFromDirectory, _stagedDirectory, StringComparison.OrdinalIgnoreCase))
            {
                if (_marshalerResolved)
                {
                    // Free после первого P/Invoke даст висячие указатели (AccessViolation).
                    _requiresCleanStage = true;
                    _logger?.LogWarning(
                        "Стейдж {Stage} отличается от каталога загрузки {Loaded}, но LLamaSharp уже закэшировал " +
                        "указатели — смена бэкенда потребует чистого стейджа/перезапуска загрузки",
                        _stagedDirectory, LoadedFromDirectory ?? "?");
                }
                else
                {
                    _logger?.LogInformation(
                        "Стейдж сменился на {Stage}: освобождаю пробные хэндлы из {Loaded}",
                        _stagedDirectory, LoadedFromDirectory ?? "?");
                    FreeLoadedHandlesUnsafe();
                }
            }

            if (LoadedHandles.TryGetValue(baseName, out var cached) && cached != IntPtr.Zero)
                return cached;

            var fileName = GetFileName(baseName);

            foreach (var dir in GetCandidateDirectories(ActiveBackend))
            {
                var fullPath = Path.Combine(dir, fileName);
                if (!File.Exists(fullPath))
                    continue;

                if (baseName is "llama" or "mtmd")
                    PreloadDependencies(dir);

                if (NativeLibrary.TryLoad(fullPath, out var handle) && handle != IntPtr.Zero)
                {
                    LoadedHandles[baseName] = handle;
                    LoadedFromDirectory = dir;
                    _logger?.LogInformation("Нативная библиотека {Name} загружена из {Path}", baseName, fullPath);
                    return handle;
                }

                _logger?.LogWarning(
                    "Не удалось загрузить {Path}. Частые причины: нет Microsoft Visual C++ Redistributable x64, " +
                    "нет драйвера GPU/CUDA-рантайма, либо сборка не под этот CPU.", fullPath);
            }

            return IntPtr.Zero;
        }
    }

    private static void PreloadDependencies(string directory)
    {
        // Порядок важен: ggml-base <- ggml <- ggml-cpu <- backend <- llama.
        var deps = new List<string> { "ggml-base", "ggml", "ggml-cpu" };

        switch (ActiveBackend)
        {
            case AiBackend.Cuda12:
                deps.AddRange(new[] { "cudart64_12", "cublasLt64_12", "cublas64_12", "ggml-cuda" });
                break;
            case AiBackend.Cuda11:
                deps.AddRange(new[] { "cudart64_110", "cublasLt64_11", "cublas64_11", "ggml-cuda" });
                break;
            case AiBackend.Vulkan:
                deps.Add("ggml-vulkan");
                break;
        }

        foreach (var dep in deps)
        {
            if (LoadedHandles.ContainsKey(dep))
                continue;

            var path = Path.Combine(directory, GetFileName(dep));
            if (!File.Exists(path))
                continue;

            if (NativeLibrary.TryLoad(path, out var handle) && handle != IntPtr.Zero)
            {
                LoadedHandles[dep] = handle;
                _logger?.LogDebug("Предзагружена зависимость {Dep} из {Path}", dep, path);
            }
            else
            {
                _logger?.LogDebug("Не удалось предзагрузить {Path}", path);
            }
        }
    }

    private static bool TryFindLibraryFile(string baseName, out string path)
    {
        var fileName = GetFileName(baseName);

        foreach (var dir in GetCandidateDirectories(ActiveBackend))
        {
            var candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate))
            {
                path = candidate;
                return true;
            }
        }

        path = string.Empty;
        return false;
    }

    private static IEnumerable<string> GetCandidateDirectories(AiBackend backend)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in EnumerateCandidateDirectories(backend))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;

            string full;
            try
            {
                full = Path.GetFullPath(dir);
            }
            catch (Exception)
            {
                continue;
            }

            if (seen.Add(full))
                yield return full;
        }
    }

    private static IEnumerable<string> EnumerateCandidateDirectories(AiBackend backend)
    {
        // 0. A4: активный стейдж — ищем ТОЛЬКО в нём (изолированный плоский набор,
        //    никакого конфликта с плоскими CPU-DLL рядом с exe).
        if (!string.IsNullOrWhiteSpace(_stagedDirectory))
        {
            yield return _stagedDirectory!;
            yield break;
        }

        // 1. Явный путь из конфигурации (файл или каталог).
        if (!string.IsNullOrWhiteSpace(_explicitPath))
        {
            var explicitPath = _explicitPath!;

            if (File.Exists(explicitPath))
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(explicitPath));
                if (!string.IsNullOrEmpty(dir))
                    yield return dir;
            }
            else
            {
                yield return explicitPath;

                if (!Path.IsPathRooted(explicitPath))
                    yield return Path.Combine(AppContext.BaseDirectory, explicitPath);
            }
        }

        var baseDir = AppContext.BaseDirectory;
        var cwd = Directory.GetCurrentDirectory();
        var rid = GetRuntimeIdentifier();
        var backendDirs = BackendSubdirs(backend);

        foreach (var root in new[] { baseDir, cwd })
        {
            // 2. Подкаталоги бэкенда рядом с exe (сюда их кладёт сборка проекта UI).
            foreach (var sub in backendDirs)
            {
                yield return Path.Combine(root, sub);
                yield return Path.Combine(root, "runtimes", rid, "native", sub);
            }

            // 3. Стандартная раскладка NuGet-пакета LLamaSharp.Backend.*.
            yield return Path.Combine(root, "runtimes", rid, "native");

            foreach (var level in GetAvxLevels())
                yield return Path.Combine(root, "runtimes", rid, "native", level);

            // 4. Плоская раскладка (DLL прямо рядом с exe).
            yield return root;

            foreach (var level in GetAvxLevels())
                yield return Path.Combine(root, level);
        }
    }

    private static IEnumerable<string> BackendSubdirs(AiBackend backend) => backend switch
    {
        AiBackend.Cuda12 => new[] { "cuda12", "cuda", "gpu" },
        AiBackend.Cuda11 => new[] { "cuda11", "cuda", "gpu" },
        AiBackend.Vulkan => new[] { "vulkan", "gpu" },
        _ => new[] { "cpu" }
    };

    private static IEnumerable<string> GetAvxLevels()
    {
        if (!string.IsNullOrWhiteSpace(PreferredAvxLevel))
        {
            yield return PreferredAvxLevel!.Trim();
            yield break;
        }

        if (Avx2.IsSupported)
            yield return "avx2";

        if (Avx512F.IsSupported)
            yield return "avx512";

        if (Avx.IsSupported)
            yield return "avx";

        yield return "noavx";
    }

    private static string GetRuntimeIdentifier()
    {
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()
        };

        if (OperatingSystem.IsWindows()) return $"win-{arch}";
        if (OperatingSystem.IsLinux()) return $"linux-{arch}";
        if (OperatingSystem.IsMacOS()) return $"osx-{arch}";
        return $"unknown-{arch}";
    }

    private static string GetFileName(string baseName)
    {
        if (OperatingSystem.IsWindows()) return baseName + ".dll";
        if (OperatingSystem.IsMacOS()) return "lib" + baseName + ".dylib";
        return "lib" + baseName + ".so";
    }

    private static string? NormalizeLibraryName(string libraryName)
    {
        var name = libraryName.Trim().ToLowerInvariant();

        if (name.EndsWith(".dll", StringComparison.Ordinal) ||
            name.EndsWith(".so", StringComparison.Ordinal) ||
            name.EndsWith(".dylib", StringComparison.Ordinal))
        {
            name = Path.GetFileNameWithoutExtension(name);
        }

        if (name.StartsWith("lib", StringComparison.Ordinal))
            name = name[3..];

        return name switch
        {
            "llama" or "ggml" or "ggml-base" or "ggml-cpu" or "ggml-cuda" or
            "ggml-metal" or "ggml-blas" or "ggml-rpc" or "ggml-vulkan" or
            "mtmd" or "llava" => name,
            _ => null
        };
    }

    private static string BuildNotFoundMessage()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Нативная библиотека llama.cpp не найдена (бэкенд {ActiveBackend}).");
        sb.AppendLine("Что сделать:");
        sb.AppendLine("  1) Пересобрать проект AITradingSystem.UI — нативные DLL копируются в каталог exe целевой сборкой UI.");
        sb.AppendLine("  2) Убедиться, что рядом с AITradingSystem.UI.exe есть llama.dll, ggml.dll, ggml-base.dll, ggml-cpu.dll");
        sb.AppendLine("     (для GPU дополнительно ggml-cuda.dll + cudart/cublas или ggml-vulkan.dll).");
        sb.AppendLine("  3) Для NVIDIA: пакет LLamaSharp.Backend.Cuda12 и свежий драйвер NVIDIA (CUDA 12 runtime идёт в пакете).");
        sb.AppendLine("  4) Для AMD/Intel: пакет LLamaSharp.Backend.Vulkan и драйвер с поддержкой Vulkan 1.2+.");
        sb.AppendLine("  5) Либо указать путь к DLL в appsettings.json: AI:NativeLibraryPath.");
        sb.AppendLine("  6) Установить Microsoft Visual C++ Redistributable 2015-2022 x64.");
        sb.AppendLine("Диагностика поиска:");
        sb.AppendLine(GetDiagnostics());
        return sb.ToString();
    }
}
