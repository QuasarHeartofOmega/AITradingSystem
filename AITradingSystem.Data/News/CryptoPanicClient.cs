using System.Diagnostics;
using System.Text.Json;
using AITradingSystem.Common.Configuration;
using AITradingSystem.Common.Logging;
using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.News;
using Microsoft.Extensions.Logging;

namespace AITradingSystem.Data.News;

/// <summary>
/// Клиент CryptoPanic API (FR-2.1). Требует API-ключ, поэтому по факту опционален:
/// без ключа провайдер помечается недоступным, а новости идут из cryptocurrency.cv.
/// Парсер терпим к отсутствующим полям (votes/currencies/source бывают не у всех постов).
/// </summary>
public sealed class CryptoPanicClient : INewsProvider, IDisposable
{
    private readonly NewsConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ILogger<CryptoPanicClient> _logger;

    public string Name => "CryptoPanic";

    public bool IsAvailable =>
        _config.Enabled && _config.CryptoPanicEnabled && !string.IsNullOrWhiteSpace(_config.CryptoPanicApiKey);

    public CryptoPanicClient(NewsConfig config, ILogger<CryptoPanicClient> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var baseUrl = string.IsNullOrWhiteSpace(_config.CryptoPanicBaseUrl)
            ? "https://cryptopanic.com/api/developer/v2/"
            : _config.CryptoPanicBaseUrl.TrimEnd('/') + "/";

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromMilliseconds(_config.HttpTimeoutMs)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AITradingSystem/1.0");
    }

    public async Task<List<NewsItem>> GetLatestNewsAsync(CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            NewsLog.Skipped(Name, "API-ключ не задан или провайдер выключен");
            return new List<NewsItem>();
        }

        var url = $"posts/?auth_token={Uri.EscapeDataString(_config.CryptoPanicApiKey)}&kind=news&public=true";
        var sw = Stopwatch.StartNew();
        NewsLog.Request(Name, _httpClient.BaseAddress + url);

        try
        {
            using var response = await _httpClient.GetAsync(url, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                NewsLog.Response(Name, (int)response.StatusCode, 0, sw.Elapsed.TotalMilliseconds, body);
                _logger.LogWarning("{Provider} вернул {Status}", Name, response.StatusCode);
                return new List<NewsItem>();
            }

            var news = Parse(body);
            NewsLog.Response(Name, (int)response.StatusCode, news.Count, sw.Elapsed.TotalMilliseconds);
            _logger.LogInformation("Получено {Count} новостей из {Provider}", news.Count, Name);
            return news;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            NewsLog.Failure(Name, ex, "posts/");
            _logger.LogWarning(ex, "Ошибка получения новостей из {Provider}", Name);
            return new List<NewsItem>();
        }
    }

    /// <summary>Разбор ответа (публичный для юнит-тестов).</summary>
    public List<NewsItem> Parse(string json)
    {
        var news = new List<NewsItem>();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!TryGetArray(root, out var results))
        {
            _logger.LogWarning("{Provider}: в ответе нет массива results/posts", Name);
            return news;
        }

        var maxAge = TimeSpan.FromHours(_config.MaxNewsAgeHours);
        var limit = Math.Max(1, _config.MaxNewsPerSymbol * 10);

        foreach (var item in results.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var title = GetString(item, "title");
            if (string.IsNullOrWhiteSpace(title)) continue;

            var published = NewsSymbolMapper.ParsePublishedAt(GetString(item, "published_at"));
            if (DateTime.UtcNow - published > maxAge) continue;

            var text = GetString(item, "text");
            if (string.IsNullOrEmpty(text)) text = GetString(item, "description");

            var newsItem = new NewsItem
            {
                Id = "cp:" + (item.TryGetProperty("id", out var id) ? id.ToString() : title),
                Title = title,
                Source = item.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.Object
                    ? GetString(src, "title")
                    : GetString(item, "source"),
                Url = GetString(item, "url"),
                Text = text,
                PublishedAt = published,
                Sentiment = MapSentiment(item, title, text),
                SentimentScore = CalculateSentimentScore(item, title, text)
            };

            if (string.IsNullOrWhiteSpace(newsItem.Source))
                newsItem.Source = "CryptoPanic";

            if (item.TryGetProperty("currencies", out var currencies) && currencies.ValueKind == JsonValueKind.Array)
            {
                foreach (var curr in currencies.EnumerateArray())
                {
                    var code = GetString(curr, "code");
                    if (!string.IsNullOrWhiteSpace(code))
                        newsItem.RelatedSymbols.Add(code.ToUpperInvariant());
                }
            }

            if (newsItem.RelatedSymbols.Count == 0)
                newsItem.RelatedSymbols.AddRange(NewsSymbolMapper.ExtractSymbols(title, text));

            news.Add(newsItem);

            if (news.Count >= limit) break;
        }

        return news;
    }

    private static bool TryGetArray(JsonElement root, out JsonElement array)
    {
        foreach (var name in new[] { "results", "posts", "data" })
        {
            if (root.TryGetProperty(name, out array) && array.ValueKind == JsonValueKind.Array)
                return true;
        }

        array = default;
        return false;
    }

    private Sentiment MapSentiment(JsonElement item, string title, string text)
    {
        var score = CalculateSentimentScore(item, title, text);

        return score switch
        {
            > 0.2m => Sentiment.Bullish,
            < -0.2m => Sentiment.Bearish,
            _ => Sentiment.Neutral
        };
    }

    private static decimal CalculateSentimentScore(JsonElement item, string title, string text)
    {
        if (item.TryGetProperty("votes", out var votes) && votes.ValueKind == JsonValueKind.Object)
        {
            var positive = GetInt(votes, "positive") + GetInt(votes, "liked") + GetInt(votes, "bullish");
            var negative = GetInt(votes, "negative") + GetInt(votes, "disliked") + GetInt(votes, "bearish");
            var total = positive + negative;

            if (total > 0)
                return Math.Clamp((decimal)(positive - negative) / total, -1m, 1m);
        }

        // Голосов нет (обычно у большинства постов) — эвристика по тексту.
        return NewsSymbolMapper.ScoreSentiment(title, text).Score;
    }

    private static int GetInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out var i) ? i : 0,
            JsonValueKind.String => int.TryParse(value.GetString(), out var i) ? i : 0,
            _ => 0
        };
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    public void Dispose() => _httpClient.Dispose();
}
