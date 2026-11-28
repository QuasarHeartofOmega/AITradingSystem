using System.Text.Json;
using AITradingSystem.Core.Models.Trading;
using Microsoft.Extensions.Logging;

namespace AITradingSystem.Data.Storage;

/// <summary>
/// Phase 1.7: отдельный durable-леджер external-решений (Release и базовые объёмы).
/// Не смешивается с positions.json: отпущенный остаток не является позицией бота.
/// </summary>
public sealed class JsonExternalPositionLedger
{
    private readonly string _path;
    private readonly ILogger<JsonExternalPositionLedger> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public JsonExternalPositionLedger(string directoryPath, ILogger<JsonExternalPositionLedger> logger)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
            throw new ArgumentException("Directory path cannot be empty", nameof(directoryPath));
        Directory.CreateDirectory(directoryPath);
        _path = Path.Combine(directoryPath, "external_positions.json");
        _logger = logger;
    }

    public async Task<ExternalPositionLedgerState> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(_path)) return new ExternalPositionLedgerState();
            var json = await File.ReadAllTextAsync(_path, ct);
            return JsonSerializer.Deserialize<ExternalPositionLedgerState>(json, Json) ?? new ExternalPositionLedgerState();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось загрузить external-position ledger {Path}", _path);
            // Fail-safe: не теряем молча операторские Release-решения; сервис увидит ошибку и не будет автоподхватывать вслепую.
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(ExternalPositionLedgerState state, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var tmp = _path + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(state, Json), ct);
            File.Move(tmp, _path, overwrite: true);
        }
        finally { _gate.Release(); }
    }
}
