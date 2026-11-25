using AITradingSystem.Core.Models.Trading;

namespace AITradingSystem.Services.AI;

/// <summary>
/// Интерфейс AI-модели для принятия торговых решений (FR-4).
/// </summary>
public interface IAIModel : IAsyncDisposable
{
    /// <summary>Модель загружена и готова к работе.</summary>
    bool IsLoaded { get; }

    /// <summary>Загрузить модель в память (FR-4.1).</summary>
    Task LoadAsync(CancellationToken ct = default);

    /// <summary>Выгрузить модель из памяти.</summary>
    Task UnloadAsync();

    /// <summary>
    /// Получить торговый сигнал по рыночным данным, новостям и состоянию позиции (FR-4.2, FR-4.3).
    /// </summary>
    Task<TradingSignal> GetTradingSignalAsync(
        string symbol,
        Dictionary<string, decimal> indicators,
        string? newsContext,
        AiRequestContext? context = null,
        CancellationToken ct = default);

    /// <summary>Последнее время инференса в мс (для метрик, NFR-1.1).</summary>
    double LastInferenceTimeMs { get; }
}
