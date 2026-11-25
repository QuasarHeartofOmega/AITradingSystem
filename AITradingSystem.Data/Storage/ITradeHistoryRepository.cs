using AITradingSystem.Core.Models.Trading;

namespace AITradingSystem.Data.Storage;

/// <summary>
/// Интерфейс хранилища истории сделок.
/// </summary>
public interface ITradeHistoryRepository
{
    /// <summary>
    /// Сохранить запись о сделке.
    /// </summary>
    Task SaveTradeAsync(TradeRecord trade, CancellationToken ct = default);

    /// <summary>
    /// Получить историю сделок.
    /// </summary>
    Task<List<TradeRecord>> GetTradesAsync(int limit = 100, CancellationToken ct = default);

    /// <summary>
    /// Получить историю сделок для конкретного символа.
    /// </summary>
    Task<List<TradeRecord>> GetTradesBySymbolAsync(string symbol, int limit = 100, CancellationToken ct = default);

    /// <summary>
    /// P2.4: вся история сделок без обрезки лимитом — подбор порога уверенности по expectancy
    /// должен видеть полную выборку, а не последние 100 записей.
    /// </summary>
    Task<List<TradeRecord>> GetAllTradesAsync(CancellationToken ct = default);
}