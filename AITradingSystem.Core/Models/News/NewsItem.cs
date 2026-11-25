using AITradingSystem.Core.Models.Enums;

namespace AITradingSystem.Core.Models.News;

/// <summary>
/// Новостная заметка (FR-2).
/// </summary>
public sealed class NewsItem
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime PublishedAt { get; set; }
    public List<string> RelatedSymbols { get; set; } = new();
    public Sentiment Sentiment { get; set; } = Sentiment.Neutral;
    public decimal SentimentScore { get; set; } // Изменено с float на decimal
    public bool IsRelevant { get; set; } = true;

    /// <summary>
    /// Краткое содержание (первые 200 символов).
    /// </summary>
    public string Summary => Text.Length > 200 ? Text.Substring(0, 200) + "..." : Text;
}