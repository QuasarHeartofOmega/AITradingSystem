using AITradingSystem.Core.Models.Trading;
using AITradingSystem.Core.RiskManagement;

namespace AITradingSystem.Data.Storage;

/// <summary>
/// Интерфейс хранилища состояния бота (для восстановления после сбоя, NFR-2.2).
/// </summary>
public interface IStateRepository
{
    /// <summary>
    /// Сохранить открытые позиции.
    /// </summary>
    Task SavePositionsAsync(IEnumerable<Position> positions, CancellationToken ct = default);

    /// <summary>
    /// Загрузить открытые позиции.
    /// </summary>
    Task<List<Position>> LoadPositionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Сохранить пиковый баланс (для расчета просадки).
    /// </summary>
    Task SavePeakBalanceAsync(decimal peakBalance, CancellationToken ct = default);

    /// <summary>
    /// Загрузить пиковый баланс.
    /// </summary>
    Task<decimal> LoadPeakBalanceAsync(CancellationToken ct = default);

    /// <summary>
    /// P0.4: сохранить состояние риск-трекера (пик, дневной старт, дата дня, макс. просадка, баланс).
    /// </summary>
    Task SaveRiskStateAsync(DrawdownState state, CancellationToken ct = default);

    /// <summary>
    /// P0.4: загрузить состояние риск-трекера. Нет файла — null;
    /// есть только legacy peak_balance.json — снимок «только пик» (CurrentBalance = 0).
    /// </summary>
    Task<DrawdownState?> LoadRiskStateAsync(CancellationToken ct = default);

    /// <summary>P0.1: risk-state изолирован по (environment, account_id, quote_asset).</summary>
    Task SaveRiskStateAsync(DrawdownState state, string environment, string accountId, string quoteAsset, CancellationToken ct = default);

    /// <summary>P0.1: scoped-загрузка не наследует legacy unscoped peak между окружениями.</summary>
    Task<DrawdownState?> LoadRiskStateAsync(string environment, string accountId, string quoteAsset, CancellationToken ct = default);
}