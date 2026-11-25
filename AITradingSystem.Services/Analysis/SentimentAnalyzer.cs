using System.Globalization;
using System.Text;
using AITradingSystem.Common.Logging;
using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.News;
using AITradingSystem.Data.News;
using Microsoft.Extensions.Logging;

namespace AITradingSystem.Services.Analysis;

/// <summary>
/// Анализатор новостей — формирует контекст для AI-модели (FR-2.3).
/// Контекст пишется в logs/news-*.log, чтобы было видно, что именно ушло в промпт.
/// </summary>
public sealed class SentimentAnalyzer
{
    private readonly NewsAggregator _newsAggregator;
    private readonly ILogger<SentimentAnalyzer> _logger;

    public SentimentAnalyzer(
        NewsAggregator newsAggregator,
        ILogger<SentimentAnalyzer> logger)
    {
        _newsAggregator = newsAggregator ?? throw new ArgumentNullException(nameof(newsAggregator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Получить новостной контекст для символа.</summary>
    public async Task<string?> GetNewsContextAsync(string symbol, CancellationToken ct = default)
    {
        try
        {
            var news = await _newsAggregator.GetNewsForSymbolAsync(symbol, ct);

            if (news.Count == 0)
            {
                NewsLog.Skipped("context", $"для {symbol} новостей нет");
                return null;
            }

            var sb = new StringBuilder();

            foreach (var item in news.Take(5))
            {
                var age = DateTime.UtcNow - item.PublishedAt;
                var ageText = age.TotalHours >= 1
                    ? $"{age.TotalHours:F0}h ago"
                    : $"{Math.Max(0, age.TotalMinutes):F0}m ago";

                var marker = item.Sentiment switch
                {
                    Sentiment.Bullish => "[+]",
                    Sentiment.Bearish => "[-]",
                    _ => "[=]"
                };

                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"{marker} ({ageText}) [{item.Source}] {item.Title}");
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"    sentiment: {item.SentimentScore.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture)}");
            }

            var avgScore = news.Average(n => n.SentimentScore);
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"Average sentiment ({news.Count} items): {avgScore.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture)}");

            var context = sb.ToString();
            NewsLog.Context(symbol, news.Count, context);
            return context;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка получения новостного контекста для {Symbol}", symbol);
            return null;
        }
    }
}
