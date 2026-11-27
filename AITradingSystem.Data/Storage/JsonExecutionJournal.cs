using System.Text.Json;
using AITradingSystem.Core.Models.Trading;
using Microsoft.Extensions.Logging;

namespace AITradingSystem.Data.Storage;

/// <summary>
/// P0.3: журнал намерений исполнения в JSON-файле (<c>execution_journal.json</c>).
///
/// Требования к записи: она должна долететь до диска ДО отправки ордера, поэтому
/// каждый <see cref="RecordAsync"/> пишет файл целиком атомарно (tmp + rename) —
/// оборванный процесс не оставит частично записанный JSON.
///
/// Незавершённые намерения (<see cref="ExecutionIntentStatus.Pending"/>) не вычищаются по TTL:
/// пока биржа не подтвердила судьбу ордера, запись нужна реконсиляции (P0.5).
/// </summary>
public sealed class JsonExecutionJournal : IExecutionJournal
{
    /// <summary>Сколько записей держим в файле — журнал не должен расти бесконечно.</summary>
    private const int MaxEntries = 500;

    private readonly string _path;
    private readonly ILogger<JsonExecutionJournal> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    private Dictionary<string, ExecutionIntent>? _cache;

    public JsonExecutionJournal(string directoryPath, ILogger<JsonExecutionJournal> logger)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
            throw new ArgumentException("Directory path cannot be empty", nameof(directoryPath));

        Directory.CreateDirectory(directoryPath);
        _path = Path.Combine(directoryPath, "execution_journal.json");
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task RecordAsync(ExecutionIntent intent, CancellationToken ct = default)
    {
        if (intent is null) throw new ArgumentNullException(nameof(intent));
        if (string.IsNullOrWhiteSpace(intent.ClientOrderId)) return;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = await LoadCoreAsync(ct).ConfigureAwait(false);
            intent.UpdatedAtUtc = DateTime.UtcNow;
            entries[intent.ClientOrderId] = intent;
            await SaveCoreAsync(entries, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // E1/P0.3: write-ahead identity must be durable before submission; fail closed.
            _logger.LogError(ex, "Не удалось записать намерение исполнения {ClientOrderId}", intent.ClientOrderId);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<List<ExecutionIntent>> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = await LoadCoreAsync(ct).ConfigureAwait(false);
            return entries.Values.OrderByDescending(e => e.UpdatedAtUtc).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PruneAsync(TimeSpan ttl, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = await LoadCoreAsync(ct).ConfigureAwait(false);
            var now = DateTime.UtcNow;

            var removable = entries.Values
                .Where(e => !e.IsUnresolved && now - e.UpdatedAtUtc > ttl)
                .Select(e => e.ClientOrderId)
                .ToList();

            if (removable.Count == 0) return;

            foreach (var key in removable)
                entries.Remove(key);

            await SaveCoreAsync(entries, ct).ConfigureAwait(false);
            _logger.LogDebug("Журнал исполнения: удалено {Count} записей старше {Ttl}", removable.Count, ttl);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось очистить журнал исполнения");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Чтение без внешнего лока — вызывается только под <see cref="_gate"/>.</summary>
    private async Task<Dictionary<string, ExecutionIntent>> LoadCoreAsync(CancellationToken ct)
    {
        if (_cache != null) return _cache;

        var map = new Dictionary<string, ExecutionIntent>(StringComparer.Ordinal);

        if (File.Exists(_path))
        {
            try
            {
                var json = await File.ReadAllTextAsync(_path, ct).ConfigureAwait(false);
                var list = JsonSerializer.Deserialize<List<ExecutionIntent>>(json, _jsonOptions);
                if (list != null)
                {
                    foreach (var intent in list)
                    {
                        if (!string.IsNullOrWhiteSpace(intent.ClientOrderId))
                            map[intent.ClientOrderId] = intent;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Журнал исполнения {Path} повреждён — начинаем пустой", _path);
            }
        }

        _cache = map;
        return map;
    }

    private async Task SaveCoreAsync(Dictionary<string, ExecutionIntent> entries, CancellationToken ct)
    {
        // Свежие сверху; незавершённые не выбрасываем даже при переполнении лимита.
        var ordered = entries.Values
            .OrderByDescending(e => e.IsUnresolved)
            .ThenByDescending(e => e.UpdatedAtUtc)
            .Take(MaxEntries)
            .ToList();

        var json = JsonSerializer.Serialize(ordered, _jsonOptions);
        var tmp = _path + ".tmp";

        await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
        File.Move(tmp, _path, overwrite: true);

        _cache = ordered.ToDictionary(e => e.ClientOrderId, StringComparer.Ordinal);
    }
}
