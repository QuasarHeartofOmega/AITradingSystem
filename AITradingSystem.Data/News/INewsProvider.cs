using AITradingSystem.Core.Models.News;

namespace AITradingSystem.Data.News;

/// <summary>
/// Интерфейс провайдера новостей (FR-2.1).
/// Закладывает основу для будущих расширений (Telegram, RSS, etc).
/// </summary>
public interface INewsProvider
{
    /// <summary>
    /// Имя провайдера (для логирования).
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Получить последние новости.
    /// </summary>
    Task<List<NewsItem>> GetLatestNewsAsync(CancellationToken ct = default);

    /// <summary>
    /// Провайдер доступен.
    /// </summary>
    bool IsAvailable { get; }
}