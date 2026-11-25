using AITradingSystem.Core.Models.MarketData;

namespace AITradingSystem.Core.Indicators;

/// <summary>
/// Базовый интерфейс индикатора.
/// </summary>
public interface IIndicator
{
    /// <summary>
    /// Имя индикатора (например, "EMA_20").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Требуемое количество исторических свечей для прогрева (warm-up).
    /// </summary>
    int WarmupPeriod { get; }

    /// <summary>
    /// Индикатор готов к выдаче значений (прогрев пройден).
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Сбросить состояние индикатора.
    /// </summary>
    void Reset();
}