using AITradingSystem.Common.Configuration;
using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.Trading;
using AITradingSystem.Data.Storage;
using Microsoft.Extensions.Logging;

namespace AITradingSystem.Services.Trading;

/// <summary>
/// Менеджер открытых позиций (FR-5.5, NFR-2.2).
/// </summary>
public sealed class PositionManager
{
    private readonly TradingConfig _config;
    private readonly IStateRepository _stateRepository;
    private readonly ILogger<PositionManager> _logger;
    private readonly Dictionary<string, Position> _positions = new();
    private readonly object _lock = new();

    public PositionManager(
        TradingConfig config,
        IStateRepository stateRepository,
        ILogger<PositionManager> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _stateRepository = stateRepository ?? throw new ArgumentNullException(nameof(stateRepository));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Загрузить позиции из хранилища (восстановление после сбоя).
    /// </summary>
    public async Task LoadPositionsAsync(CancellationToken ct = default)
    {
        var positions = await _stateRepository.LoadPositionsAsync(ct);

        lock (_lock)
        {
            _positions.Clear();
            foreach (var pos in positions)
            {
                _positions[pos.Id] = pos;
            }
        }

        _logger.LogInformation("Загружено {Count} позиций из хранилища", positions.Count);
    }

    /// <summary>
    /// Сохранить позиции в хранилище.
    /// </summary>
    public async Task SavePositionsAsync(CancellationToken ct = default)
    {
        List<Position> snapshot;
        lock (_lock)
        {
            snapshot = _positions.Values.ToList();
        }

        await _stateRepository.SavePositionsAsync(snapshot, ct);
    }

    /// <summary>
    /// Добавить новую позицию.
    /// </summary>
    public async Task AddPositionAsync(Position position, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _positions[position.Id] = position;
        }

        await SavePositionsAsync(ct);
        _logger.LogInformation(
            "Добавлена позиция: {Symbol} {Side} {Quantity} @ {Price}",
            position.Symbol, position.Side, position.Quantity, position.EntryPrice);
    }

    /// <summary>
    /// Удалить позицию.
    /// </summary>
    public async Task RemovePositionAsync(string positionId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _positions.Remove(positionId);
        }

        await SavePositionsAsync(ct);
        _logger.LogInformation("Удалена позиция: {PositionId}", positionId);
    }

    /// <summary>
    /// Получить все открытые позиции.
    /// </summary>
    public List<Position> GetOpenPositions()
    {
        lock (_lock)
        {
            return _positions.Values.ToList();
        }
    }

    /// <summary>
    /// Получить позицию по символу.
    /// </summary>
    public Position? GetPositionBySymbol(string symbol)
    {
        lock (_lock)
        {
            return _positions.Values.FirstOrDefault(p =>
                p.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Получить позицию по идентификатору (P0.2: тик-контур перечитывает позицию
    /// после захвата шлюза — её могли закрыть, пока он ждал).
    /// </summary>
    public Position? GetPositionById(string positionId)
    {
        lock (_lock)
        {
            return _positions.TryGetValue(positionId, out var position) ? position : null;
        }
    }

    /// <summary>
    /// Обновить цену для всех позиций (для расчета PnL).
    /// </summary>
    public void UpdatePrices(string symbol, decimal currentPrice)
    {
        lock (_lock)
        {
            foreach (var pos in _positions.Values.Where(p => p.Symbol == symbol))
            {
                pos.UpdatePrice(currentPrice);
            }
        }
    }

    /// <summary>
    /// Обновить Trailing Stop для позиции (FR-5.5).
    /// </summary>
    public bool UpdateTrailingStop(string positionId, decimal newStopLoss)
    {
        if (!_config.EnableTrailingStop)
            return false;

        lock (_lock)
        {
            if (!_positions.TryGetValue(positionId, out var position))
                return false;

            return position.UpdateTrailingStop(newStopLoss);
        }
    }

    /// <summary>
    /// Проверить, сработал ли SL/TP для позиции.
    /// </summary>
    public (bool StopLossHit, bool TakeProfitHit) CheckStopLossTakeProfit(
        string positionId,
        decimal low,
        decimal high)
    {
        lock (_lock)
        {
            if (!_positions.TryGetValue(positionId, out var position))
                return (false, false);

            var slHit = position.IsStopLossHit(low, high);
            var tpHit = position.IsTakeProfitHit(low, high);

            return (slHit, tpHit);
        }
    }
}