using System.Text.Json;
using AITradingSystem.Core.Models.Trading;
using Microsoft.Extensions.Logging;

namespace AITradingSystem.Data.Storage;

/// <summary>
/// Хранилище истории сделок на основе JSON-файла.
/// </summary>
public sealed class JsonTradeHistoryRepository : ITradeHistoryRepository
{
    private readonly string _filePath;
    private readonly ILogger<JsonTradeHistoryRepository> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };
    private readonly object _lock = new();

    public JsonTradeHistoryRepository(string filePath, ILogger<JsonTradeHistoryRepository> logger)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public async Task SaveTradeAsync(TradeRecord trade, CancellationToken ct = default)
    {
        var trades = await LoadTradesAsync(ct);
        trades.Add(trade);

        await SaveTradesAsync(trades, ct);
        _logger.LogInformation("Сохранена сделка: {Symbol} {Side} PnL: {PnL:F2}",
            trade.Symbol, trade.Side, trade.PnL);
    }

    public async Task<List<TradeRecord>> GetTradesAsync(int limit = 100, CancellationToken ct = default)
    {
        var trades = await LoadTradesAsync(ct);
        return trades
            .OrderByDescending(t => t.ClosedAt)
            .Take(limit)
            .ToList();
    }

    public async Task<List<TradeRecord>> GetTradesBySymbolAsync(string symbol, int limit = 100, CancellationToken ct = default)
    {
        var trades = await LoadTradesAsync(ct);
        return trades
            .Where(t => t.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.ClosedAt)
            .Take(limit)
            .ToList();
    }

    /// <summary>
    /// P2.4: полная история (сортировка по времени закрытия, старые первыми).
    /// GetTradesAsync обрезает выборку лимитом и для подбора порога не годится.
    /// </summary>
    public async Task<List<TradeRecord>> GetAllTradesAsync(CancellationToken ct = default)
    {
        var trades = await LoadTradesAsync(ct);
        return trades.OrderBy(t => t.ClosedAt).ToList();
    }

    private async Task<List<TradeRecord>> LoadTradesAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath))
            {
                return new List<TradeRecord>();
            }
        }

        try
        {
            var json = await File.ReadAllTextAsync(_filePath, ct);
            return JsonSerializer.Deserialize<List<TradeRecord>>(json, _jsonOptions)
                ?? new List<TradeRecord>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка загрузки истории сделок из {Path}", _filePath);
            return new List<TradeRecord>();
        }
    }

    private async Task SaveTradesAsync(List<TradeRecord> trades, CancellationToken ct)
    {
        lock (_lock)
        {
            try
            {
                var json = JsonSerializer.Serialize(trades, _jsonOptions);
                // P1.7: история питает портфельный риск после рестарта — запись атомарная.
                var tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _filePath, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка сохранения истории сделок в {Path}", _filePath);
            }
        }

        await Task.CompletedTask;
    }
}