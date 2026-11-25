using System.Diagnostics;
using System.Runtime.InteropServices;
using AITradingSystem.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace AITradingSystem.Services.AI;

/// <summary>Результат разведки GPU: какие ускорители и рантаймы найдены в системе.</summary>
public sealed class GpuDetectionResult
{
    public List<string> NvidiaGpus { get; } = new();
    public List<string> AmdGpus { get; } = new();
    public List<string> IntelGpus { get; } = new();
    public bool HasCudaRuntime { get; set; }
    public bool HasVulkanRuntime { get; set; }
    public bool HasRocm { get; set; }
    public string Summary { get; set; } = string.Empty;

    /// <summary>Основной GPU (имя) для UI/логов.</summary>
    public string GpuName { get; set; } = string.Empty;

    /// <summary>Полная VRAM основного GPU, MiB (0 = неизвестно).</summary>
    public long VramTotalMb { get; set; }

    /// <summary>Свободная VRAM основного GPU, MiB (0 = неизвестно).</summary>
    public long VramFreeMb { get; set; }

    /// <summary>
    /// Старый AMD (Polaris/GCN4: RX 4xx/5xx, Vega): драйверный лимит одной аллокации ~1 GB.
    /// Требуется частичный оффлоад или CPU; полный оффлоад 14B невозможен.
    /// </summary>
    public bool IsLegacyAmd { get; set; }

    public bool HasNvidia => NvidiaGpus.Count > 0 || HasCudaRuntime;
    public bool HasAmd => AmdGpus.Count > 0 || HasRocm;
    public bool HasAnyGpu => HasNvidia || HasAmd || IntelGpus.Count > 0;
}

/// <summary>
/// Определение железа без внешних зависимостей: nvidia-smi / rocminfo / lspci,
/// наличие рантайм-библиотек (nvcuda, amdhip64, vulkan-1) и переменных окружения.
/// Используется для выбора бэкенда llama.cpp: CUDA (NVIDIA) → Vulkan (AMD/Intel) → CPU.
/// </summary>
public static class GpuProbe
{
    private static GpuDetectionResult? _cached;
    private static readonly object Sync = new();

    /// <summary>Определить GPU (результат кэшируется).</summary>
    public static GpuDetectionResult Detect(ILogger? logger = null, bool refresh = false)
    {
        lock (Sync)
        {
            if (_cached != null && !refresh)
                return _cached;

            var result = new GpuDetectionResult();

            try
            {
                ProbeNvidia(result);
                ProbeAmd(result);
                ProbeVulkan(result);
                ProbeLinuxPci(result);
                ProbeWmiVram(result);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Ошибка определения GPU — считаем, что доступен только CPU");
            }

            if (string.IsNullOrEmpty(result.GpuName))
                result.GpuName = result.NvidiaGpus.FirstOrDefault()
                    ?? result.AmdGpus.FirstOrDefault()
                    ?? result.IntelGpus.FirstOrDefault()
                    ?? string.Empty;

            if (result.IsLegacyAmd)
                logger?.LogWarning(
                    "Обнаружен старый AMD GPU (Polaris/Vega): лимит одной аллокации ~1 GB, " +
                    "рекомендуется частичный оффлоад или CPU (AutoFitGpu учтёт это)");

            result.Summary = BuildSummary(result);
            _cached = result;

            logger?.LogInformation("GPU: {Summary}", result.Summary);
            return result;
        }
    }

    /// <summary>
    /// Выбирает бэкенд под железо. requested=Auto → автовыбор, иначе уважаем явный выбор,
    /// но откатываемся на CPU, если нужных нативных библиотек нет.
    /// </summary>
    public static AiBackend SelectBackend(
        AiBackend requested,
        GpuDetectionResult gpu,
        Func<AiBackend, bool> backendFilesAvailable,
        ILogger? logger = null)
    {
        var order = requested switch
        {
            AiBackend.Cpu => new[] { AiBackend.Cpu },
            AiBackend.Cuda12 => new[] { AiBackend.Cuda12, AiBackend.Vulkan, AiBackend.Cpu },
            // LLamaSharp 0.30 ships CUDA 12 builds only: CUDA 11 запрос честно
            // переводим на CUDA 12 (тот же драйвер NVIDIA), затем Vulkan/CPU.
            AiBackend.Cuda11 => new[] { AiBackend.Cuda12, AiBackend.Vulkan, AiBackend.Cpu },
            AiBackend.Vulkan => new[] { AiBackend.Vulkan, AiBackend.Cpu },
            _ => AutoOrder(gpu)
        };

        if (requested == AiBackend.Cuda11)
            logger?.LogWarning("Backend=Cuda11: для LLamaSharp 0.30 есть только CUDA 12 — используем Cuda12/Vulkan/CPU");

        foreach (var candidate in order)
        {
            if (candidate != AiBackend.Cpu && !backendFilesAvailable(candidate))
            {
                logger?.LogWarning("Бэкенд {Backend} пропущен: нативные библиотеки не найдены", candidate);
                continue;
            }

            if (candidate is AiBackend.Cuda12 or AiBackend.Cuda11 &&
                (!gpu.HasNvidia || !gpu.HasCudaRuntime))
            {
                // A3: строго — нужен и GPU, и драйверный CUDA-рантайм (nvcuda/libcuda).
                // Без драйвера CUDA-DLL грузятся (cudart идёт в пакете), но устройств нет.
                logger?.LogWarning(
                    "Бэкенд {Backend} пропущен: NVIDIA GPU/драйверный CUDA-рантайм не обнаружен (nvcuda/libcuda)", candidate);
                continue;
            }

            if (candidate == AiBackend.Vulkan && !gpu.HasVulkanRuntime && !gpu.HasAnyGpu)
            {
                logger?.LogWarning("Vulkan пропущен: рантайм Vulkan не найден");
                continue;
            }

            if (requested == AiBackend.Auto && candidate != AiBackend.Cpu)
                logger?.LogInformation("Автовыбор бэкенда: {Backend} ({Summary})", candidate, gpu.Summary);

            return candidate;
        }

        return AiBackend.Cpu;
    }

    private static AiBackend[] AutoOrder(GpuDetectionResult gpu)
    {
        // NVIDIA → CUDA 12 (самый быстрый путь), затем Vulkan.
        if (gpu.HasNvidia)
            return new[]
            {
                AiBackend.Cuda12,
                AiBackend.Vulkan,
                AiBackend.Cpu
            };

        // AMD/Intel → Vulkan (ROCm на Windows у llama.cpp поддерживается хуже и требует HIP SDK).
        if (gpu.HasAnyGpu)
            return new[]
            {
                AiBackend.Vulkan,
                AiBackend.Cpu
            };

        return new[] { AiBackend.Cpu };
    }

    private static void ProbeNvidia(GpuDetectionResult result)
    {
        // Точные имя + VRAM (total/free) одним запросом.
        var query = RunTool("nvidia-smi",
            "--query-gpu=name,memory.total,memory.free --format=csv,noheader,nounits", 4000);
        if (query != null)
        {
            foreach (var line in query.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(',');
                if (parts.Length < 3) continue;
                var name = parts[0].Trim();
                if (name.Length == 0) continue;
                result.NvidiaGpus.Add(name);

                if (result.VramTotalMb == 0 &&
                    long.TryParse(parts[1].Trim(), out var total) &&
                    long.TryParse(parts[2].Trim(), out var free))
                {
                    result.GpuName = name;
                    result.VramTotalMb = total;
                    result.VramFreeMb = free;
                }
            }
        }

        // Fallback: просто список GPU, если query недоступен (старый драйвер).
        if (result.NvidiaGpus.Count == 0)
        {
            var smi = RunTool("nvidia-smi", "--list-gpus", 4000);
            if (smi != null)
            {
                foreach (var line in smi.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length > 0 && trimmed.Contains("GPU", StringComparison.OrdinalIgnoreCase))
                        result.NvidiaGpus.Add(trimmed);
                }
            }
        }

        // A3: CUDA_PATH — это тулкит, а не драйвер: сам по себе он не даёт CUDA-рантайм.
        // Драйвер определяем по nvcuda/libcuda/proc (или успешному nvidia-smi ниже).
        if (FileExistsInSystem("nvcuda.dll") ||
            FileExistsInSystem("libcuda.so.1") ||
            File.Exists("/proc/driver/nvidia/version"))
            result.HasCudaRuntime = true;

        if (result.NvidiaGpus.Count > 0)
            result.HasCudaRuntime = true;
    }

    private static void ProbeAmd(GpuDetectionResult result)
    {
        if (FileExistsInSystem("amdhip64.dll") ||
            FileExistsInSystem("amdhip64_6.dll") ||
            File.Exists("/dev/kfd") ||
            Directory.Exists("/opt/rocm") ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ROCM_PATH")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HIP_PATH")))
            result.HasRocm = true;

        // Драйвер AMD Radeon (Windows): наличие user-mode драйверных DLL.
        if (FileExistsInSystem("atidxx64.dll") || FileExistsInSystem("amdxc64.dll"))
            result.AmdGpus.Add("AMD Radeon (драйвер обнаружен)");

        var rocm = RunTool("rocminfo", string.Empty, 4000);
        if (rocm != null)
        {
            result.HasRocm = true;
            foreach (var line in rocm.Split('\n'))
            {
                if (line.Contains("Marketing Name", StringComparison.OrdinalIgnoreCase) &&
                    !line.Contains("CPU", StringComparison.OrdinalIgnoreCase))
                    result.AmdGpus.Add(line.Trim());
            }
        }
    }

    private static void ProbeVulkan(GpuDetectionResult result)
    {
        if (FileExistsInSystem("vulkan-1.dll") ||
            FileExistsInSystem("libvulkan.so.1") ||
            File.Exists("/usr/lib/x86_64-linux-gnu/libvulkan.so.1") ||
            File.Exists("/usr/lib64/libvulkan.so.1"))
            result.HasVulkanRuntime = true;

        var info = RunTool("vulkaninfo", "--summary", 4000);
        if (info != null)
        {
            result.HasVulkanRuntime = true;
            foreach (var line in info.Split('\n'))
            {
                if (!line.Contains("deviceName", StringComparison.OrdinalIgnoreCase)) continue;
                var name = line.Split('=').Last().Trim();
                if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) result.NvidiaGpus.Add(name);
                else if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                         name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
                {
                    result.AmdGpus.Add(name);
                    if (IsLegacyAmdName(name)) result.IsLegacyAmd = true;
                }
                else if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase)) result.IntelGpus.Add(name);

                if (string.IsNullOrEmpty(result.GpuName))
                    result.GpuName = name;
            }
        }

        // Имя GPU из Windows-драйвера тоже может указывать на legacy AMD.
        foreach (var amd in result.AmdGpus)
            if (IsLegacyAmdName(amd)) result.IsLegacyAmd = true;
    }

    /// <summary>Polaris/GCN4 (RX 4xx/5xx) и Vega: лимит одной аллокации ~1 GB.</summary>
    private static bool IsLegacyAmdName(string name)
    {
        var n = name.ToUpperInvariant();
        // RX 460/470/480/560/570/580/590, RX Vega, Vega 56/64, Radeon Pro Vega и т.п.
        if (n.Contains("VEGA")) return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(n, @"RX\s?-?\s?[45]\d{2}")) return true;
        if (n.Contains("POLARIS")) return true;
        return false;
    }

    /// <summary>
    /// Фолбэк VRAM через WMI Win32_VideoController.AdapterRAM (Windows).
    /// Грубо: для >4 GB значение врёт (uint32), поэтому используем только как последний резерв
    /// и только если nvidia-smi/vulkaninfo не дали VRAM.
    /// </summary>
    private static void ProbeWmiVram(GpuDetectionResult result)
    {
        if (!OperatingSystem.IsWindows() || result.VramTotalMb > 0)
            return;

        var wmi = RunTool("wmic", "path Win32_VideoController get AdapterRAM,Name /format:list", 5000);
        if (wmi == null) return;

        string? name = null;
        foreach (var line in wmi.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Name=", StringComparison.OrdinalIgnoreCase))
                name = trimmed[5..].Trim();
            else if (trimmed.StartsWith("AdapterRAM=", StringComparison.OrdinalIgnoreCase) &&
                     long.TryParse(trimmed[11..].Trim(), out var bytes) && bytes > 0)
            {
                var mb = bytes / (1024 * 1024);
                // AdapterRAM — uint32, >4095 MB недостоверно; берём только скромные значения.
                if (mb is > 0 and <= 4095 && result.VramTotalMb == 0)
                {
                    result.VramTotalMb = mb;
                    result.VramFreeMb = mb; // free неизвестен — консервативно считаем всё свободным
                    if (!string.IsNullOrEmpty(name)) result.GpuName = name;
                }
            }
        }
    }

    /// <summary>
    /// Уточняет VRAM изнутри llama (GgmlNative.GetDevices) — точнее nvidia-smi/WMI.
    /// Вызывать ПОСЛЕ загрузки нативных библиотек. Обновляет кэшированный результат.
    /// </summary>
    public static void EnrichFromNative(ILogger? logger = null)
    {
        lock (Sync)
        {
            if (_cached == null) return;

            try
            {
                var devices = GgmlNative.GetDevices();
                // Берём первое устройство с ненулевой VRAM (дискретный GPU).
                var gpu = devices.FirstOrDefault(d => d.VramTotalMb > 0);
                if (gpu == null) return;

                _cached.GpuName = gpu.Name;
                _cached.VramTotalMb = gpu.VramTotalMb;
                _cached.VramFreeMb = gpu.VramFreeMb;
                _cached.Summary = BuildSummary(_cached);
                logger?.LogInformation("GPU VRAM уточнён из llama: {Device}", gpu);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Не удалось уточнить VRAM из GgmlNative");
            }
        }
    }

    private static void ProbeLinuxPci(GpuDetectionResult result)
    {
        if (!OperatingSystem.IsLinux()) return;

        var lspci = RunTool("lspci", string.Empty, 4000);
        if (lspci == null) return;

        foreach (var line in lspci.Split('\n'))
        {
            if (!line.Contains("VGA", StringComparison.OrdinalIgnoreCase) &&
                !line.Contains("3D controller", StringComparison.OrdinalIgnoreCase))
                continue;

            if (line.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) result.NvidiaGpus.Add(line.Trim());
            else if (line.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                     line.Contains("ATI", StringComparison.OrdinalIgnoreCase)) result.AmdGpus.Add(line.Trim());
            else if (line.Contains("Intel", StringComparison.OrdinalIgnoreCase)) result.IntelGpus.Add(line.Trim());
        }
    }

    private static bool FileExistsInSystem(string fileName)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
                return File.Exists(Path.Combine(system, fileName));
            }

            string[] dirs = { "/usr/lib/x86_64-linux-gnu", "/usr/lib64", "/usr/lib", "/lib/x86_64-linux-gnu" };
            return dirs.Any(d => File.Exists(Path.Combine(d, fileName)));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? RunTool(string fileName, string arguments, int timeoutMs)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            if (!process.Start())
                return null;

            // Читаем с таймаутом: зависшая утилита не должна блокировать запуск бота.
            var readTask = process.StandardOutput.ReadToEndAsync();
            if (!readTask.Wait(timeoutMs))
            {
                try { process.Kill(true); } catch (Exception) { /* игнорируем */ }
                return null;
            }

            if (!process.WaitForExit(500))
            {
                try { process.Kill(true); } catch (Exception) { /* игнорируем */ }
                return null;
            }

            var output = readTask.Result;
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) ? output : null;
        }
        catch (Exception)
        {
            // Утилиты нет в PATH — это нормально, пробуем другие признаки.
            return null;
        }
    }

    private static string BuildSummary(GpuDetectionResult g)
    {
        var parts = new List<string>
        {
            $"OS={RuntimeInformation.OSDescription}",
            $"arch={RuntimeInformation.OSArchitecture}"
        };

        if (g.NvidiaGpus.Count > 0) parts.Add("NVIDIA: " + string.Join("; ", g.NvidiaGpus.Distinct().Take(3)));
        if (g.AmdGpus.Count > 0) parts.Add("AMD: " + string.Join("; ", g.AmdGpus.Distinct().Take(3)));
        if (g.IntelGpus.Count > 0) parts.Add("Intel: " + string.Join("; ", g.IntelGpus.Distinct().Take(2)));
        if (!g.HasAnyGpu) parts.Add("GPU не обнаружен");

        parts.Add($"CUDA={(g.HasCudaRuntime ? "yes" : "no")}");
        parts.Add($"Vulkan={(g.HasVulkanRuntime ? "yes" : "no")}");
        parts.Add($"ROCm={(g.HasRocm ? "yes" : "no")}");

        if (g.VramTotalMb > 0)
            parts.Add($"VRAM={g.VramFreeMb}/{g.VramTotalMb} MB");
        if (g.IsLegacyAmd)
            parts.Add("LegacyAMD(~1GB alloc limit)");

        return string.Join(", ", parts);
    }
}
