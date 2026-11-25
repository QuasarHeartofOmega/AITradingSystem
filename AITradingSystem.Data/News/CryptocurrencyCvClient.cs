using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AITradingSystem.Common.Configuration;
using AITradingSystem.Common.Logging;
using AITradingSystem.Core.Models.News;
using Microsoft.Extensions.Logging;

namespace AITradingSystem.Data.News;

/// <summary>
/// Бесплатный агрегатор крипто-новостей cryptocurrency.cv (ключ не требуется).
///
/// GET https://cryptocurrency.cv/api/news?limit=N[&amp;category=...]
/// Ответ: { "articles": [ { title, link, description, pubDate, source, sourceKey,
///                          category, contentType, credibility, reputation } ], "totalCount": N }
///
/// Тикеры не приходят — определяем активы по заголовку/тексту (NewsSymbolMapper),
/// тональность считаем эвристически с весом credibility источника.
/// </summary>
public sealed class CryptocurrencyCvClient : INewsProvider, IDisposable
{
    private readonly NewsConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ILogger<CryptocurrencyCvClient> _logger;

    public string Name => "cryptocurrency.cv";

    public bool IsAvailable => _config.Enabled && _config.CryptocurrencyCvEnabled;

    public CryptocurrencyCvClient(NewsConfig config, ILogger<CryptocurrencyCvClient> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var baseUrl = _config.CryptocurrencyCvBaseUrl.TrimEnd('/') + "/";
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromMilliseconds(_config.HttpTimeoutMs)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AITradingSystem/1.0");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public async Task<List<NewsItem>> GetLatestNewsAsync(CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            NewsLog.Skipped(Name, "провайдер выключен в конфигурации");
            return new List<NewsItem>();
        }

        var url = $"news?limit={_config.CryptocurrencyCvLimit}";
        if (!string.IsNullOrWhiteSpace(_config.CryptocurrencyCvCategory))
            url += $"&category={Uri.EscapeDataString(_config.CryptocurrencyCvCategory.Trim())}";

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

            var news = Parse(body, out var skipped);
            NewsLog.Response(Name, (int)response.StatusCode, news.Count, sw.Elapsed.TotalMilliseconds,
                news.Count > 0 ? $"пример: [{news[0].Source}] {news[0].Title}" : null);

            if (skipped > 0)
                _logger.LogDebug("{Provider}: пропущено {Skipped} статей (мусор/старые/низкая достоверность)", Name, skipped);

            _logger.LogInformation("Получено {Count} новостей из {Provider} за {Elapsed:F0} мс",
                news.Count, Name, sw.Elapsed.TotalMilliseconds);

            return news;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            NewsLog.Failure(Name, ex, url);
            _logger.LogWarning(ex, "Ошибка получения новостей из {Provider}", Name);
            return new List<NewsItem>();
        }
    }

    /// <summary>Разбор ответа. Публичный для юнит-тестов парсера.</summary>
    public List<NewsItem> Parse(string json, out int skipped)
    {
        skipped = 0;
        var result = new List<NewsItem>();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!TryGetArticles(root, out var articles))
        {
            _logger.LogWarning("{Provider}: в ответе нет массива статей (ожидались articles/data/items)", Name);
            return result;
        }

        var maxAge = TimeSpan.FromHours(_config.MaxNewsAgeHours);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var article in articles.EnumerateArray())
        {
            if (article.ValueKind != JsonValueKind.Object)
            {
                skipped++;
                continue;
            }

            var title = GetString(article, "title", "headline");
            if (string.IsNullOrWhiteSpace(title))
            {
                skipped++;
                continue;
            }

            var contentType = GetString(article, "contentType", "type");
            if (!string.IsNullOrEmpty(contentType) &&
                !contentType.Equals("news", StringComparison.OrdinalIgnoreCase) &&
                !contentType.Equals("article", StringComparison.OrdinalIgnoreCase))
            {
                skipped++;
                continue;
            }

            var credibility = GetDouble(article, "credibility", "credibilityScore");
            if (credibility > 0 && credibility < _config.CryptocurrencyCvMinCredibility)
            {
                skipped++;
                continue;
            }

            var published = NewsSymbolMapper.ParsePublishedAt(
                GetString(article, "pubDate", "publishedAt", "published_at", "published", "date"));
            if (DateTime.UtcNow - published > maxAge)
            {
                skipped++;
                continue;
            }

            var link = GetString(article, "link", "url");
            var id = !string.IsNullOrWhiteSpace(link) ? link : title;
            if (!seen.Add(id))
            {
                skipped++;
                continue;
            }

            var description = GetString(article, "description", "summary", "content");
            var source = GetString(article, "source", "sourceKey", "source_name");
            var category = GetString(article, "category");

            var (sentiment, score) = NewsSymbolMapper.ScoreSentiment(title, description, credibility);

            var item = new NewsItem
            {
                Id = $"cv:{id.GetHashCode(StringComparison.OrdinalIgnoreCase).ToString("X8", CultureInfo.InvariantCulture)}",
                Title = title,
                Source = string.IsNullOrWhiteSpace(source) ? "cryptocurrency.cv" : source,
                Url = link,
                Text = description,
                PublishedAt = published,
                Sentiment = sentiment,
                SentimentScore = score,
                RelatedSymbols = NewsSymbolMapper.ExtractSymbols(title, description, category),
                IsRelevant = true
            };

            result.Add(item);
        }

        return result
            .OrderByDescending(n => n.PublishedAt)
            .ToList();
    }

    /// <summary>
    /// Массив статей: cryptocurrency.cv отдаёт {"articles":[...]},
    /// но допускаем и data/items/news и «голый» массив — чтобы провайдер не сломался
    /// после смены обёртки ответа.
    /// </summary>
    private static bool TryGetArticles(JsonElement root, out JsonElement articles)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            articles = root;
            return true;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "articles", "data", "items", "news", "results" })
            {
                if (root.TryGetProperty(name, out var candidate) && candidate.ValueKind == JsonValueKind.Array)
                {
                    articles = candidate;
                    return true;
                }
            }
        }

        articles = default;
        return false;
    }

    private static string GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }

        return string.Empty;
    }

    private static double GetDouble(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
                return number;

            if (value.ValueKind == JsonValueKind.String &&
                double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }

        return 0;
    }

    public void Dispose() => _httpClient.Dispose();
}
