using System.Text.Json;
using AITradingSystem.Core.Models.Trading;
using AITradingSystem.Core.RiskManagement;
using Microsoft.Extensions.Logging;

namespace AITradingSystem.Data.Storage;

/// <summary>
/// Хранилище состояния на основе JSON-файлов.
/// </summary>
public sealed class JsonStateRepository : IStateRepository
{
    private readonly string _positionsPath;
    private readonly string _peakBalancePath;
    private readonly string _riskStatePath;
    private readonly ILogger<JsonStateRepository> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public JsonStateRepository(string directoryPath, ILogger<JsonStateRepository> logger)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
            throw new ArgumentException("Directory path cannot be empty", nameof(directoryPath));

        Directory.CreateDirectory(directoryPath);

        _positionsPath = Path.Combine(directoryPath, "positions.json");
        _peakBalancePath = Path.Combine(directoryPath, "peak_balance.json");
        _riskStatePath = Path.Combine(directoryPath, "risk_state.json");
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task SavePositionsAsync(IEnumerable<Position> positions, CancellationToken ct = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(positions.ToList(), _jsonOptions);
            await File.WriteAllTextAsync(_positionsPath, json, ct);
            _logger.LogDebug("Сохранено {Count} позиций", positions.Count());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка сохранения позиций");
        }
    }

    public async Task<List<Position>> LoadPositionsAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_positionsPath))
        {
            return new List<Position>();
        }

        try
        {
            var json = await File.ReadAllTextAsync(_positionsPath, ct);
            return JsonSerializer.Deserialize<List<Position>>(json, _jsonOptions)
                ?? new List<Position>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка загрузки позиций");
            return new List<Position>();
        }
    }

    public async Task SavePeakBalanceAsync(decimal peakBalance, CancellationToken ct = default)
    {
        try
        {
            var data = new { PeakBalance = peakBalance, UpdatedAt = DateTime.UtcNow };
            var json = JsonSerializer.Serialize(data, _jsonOptions);
            await File.WriteAllTextAsync(_peakBalancePath, json, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка сохранения пикового баланса");
        }
    }

    public async Task<decimal> LoadPeakBalanceAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_peakBalancePath))
        {
            return 0;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_peakBalancePath, ct);
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("PeakBalance").GetDecimal();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка загрузки пикового баланса");
            return 0;
        }
    }

    /// <summary>
    /// P0.4: состояние риск-трекера целиком. Запись атомарная (tmp + rename),
    /// чтобы падение процесса посередине не оставило битый risk_state.json.
    /// </summary>
    public async Task SaveRiskStateAsync(DrawdownState state, CancellationToken ct = default)
    {
        try
        {
            var json = JsonSerializer.Serialize(state, _jsonOptions);
            var tmp = _riskStatePath + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct);
            File.Move(tmp, _riskStatePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка сохранения состояния риск-трекера");
        }
    }

    public async Task<DrawdownState?> LoadRiskStateAsync(CancellationToken ct = default)
    {
        if (File.Exists(_riskStatePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(_riskStatePath, ct);
                return JsonSerializer.Deserialize<DrawdownState>(json, _jsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка загрузки состояния риск-трекера");
                return null;
            }
        }

        // Миграция: до P0.4 сохранялся только пик (peak_balance.json).
        // Дневной старт неизвестен — снимок «только пик», день начнётся с первого фактического баланса.
        var legacyPeak = await LoadPeakBalanceAsync(ct);
        if (legacyPeak > 0)
        {
            _logger.LogInformation("risk_state.json нет — переношу пик {Peak:F2} из legacy peak_balance.json", legacyPeak);
            return new DrawdownState { PeakBalance = legacyPeak };
        }

        return null;
    }
}