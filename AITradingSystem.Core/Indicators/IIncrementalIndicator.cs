using AITradingSystem.Core.Models.MarketData;

namespace AITradingSystem.Core.Indicators;

/// <summary>
/// Инкрементальный индикатор — обновляется по одной свече (FR-3.2).
/// </summary>
public interface IIncrementalIndicator : IIndicator
{
    /// <summary>
    /// Обновить индикатор новой закрытой свечой.
    /// </summary>
    void Update(OHLCV candle);

    /// <summary>
    /// Обновить индикатор незавершенной свечой (для расчета в реальном времени).
    /// Не изменяет внутреннее состояние предыдущих значений.
    /// </summary>
    decimal UpdateUnfinished(OHLCV candle);

    /// <summary>
    /// Текущее значение индикатора.
    /// </summary>
    decimal GetValue();
}