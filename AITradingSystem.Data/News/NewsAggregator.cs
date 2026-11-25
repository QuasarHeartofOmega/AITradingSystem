using AITradingSystem.Common.Configuration;
using AITradingSystem.Common.Logging;
using AITradingSystem.Core.Models.News;
using Microsoft.Extensions.Logging;

namespace AITradingSystem.Data.News;

/// <summary>
/// Агрегатор новостей с фильтрацией по релевантности (FR-2.2, FR-2.3).
/// Провайдеры: cryptocurrency.cv (без ключа) + CryptoPanic (по ключу).
/// Кэш не очищается, если все провайдеры упали — иначе модель останется без контекста.
/// </summary>
public sealed class NewsAggregator
{
    private readonly NewsConfig _config;
    private readonly List<INewsProvider> _providers;
    private readonly ILogger<NewsAggregator> _logger;
    private readonly Dictionary<string, List<NewsItem>> _cachedNews = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private DateTime _lastUpdate = DateTime.MinValue;

    public NewsAggregator(
        NewsConfig config,
        IEnumerable<INewsProvider> providers,
        ILogger<NewsAggregator> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _providers = providers?.ToList() ?? throw new ArgumentNullException(nameof(providers));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _logger.LogInformation("Новостные провайдеры: {Providers}",
            string.Join(", ", _providers.Select(p => $"{p.Name}({(p.IsAvailable ? "on" : "off")})")));
    }

    /// <summary>Последнее успешное обновление кэша (UTC).</summary>
    public DateTime LastUpdateUtc
    {
        get { lock (_cachedNews) return _lastUpdate; }
    }

    /// <summary>Получить новости для конкретной торговой пары.</summary>
    public async Task<List<NewsItem>> GetNewsForSymbolAsync(string symbol, CancellationToken ct = default)
    {
        await UpdateNewsIfNeededAsync(ct);

        lock (_cachedNews)
        {
            var result = new List<NewsItem>();

            // Провайдеры тегируют новости базовыми активами (BTC, ETH), а не парами (BTCUSDT).
            if (_cachedNews.TryGetValue(symbol, out var byPair))
                result.AddRange(byPair);

            var baseAsset = ExtractBaseAsset(symbol);
            if (baseAsset.Length > 0 &&
                !baseAsset.Equals(symbol, StringComparison.OrdinalIgnoreCase) &&
                _cachedNews.TryGetValue(baseAsset, out var byAsset))
                result.AddRange(byAsset);

            // Если по активу новостей нет — общий фон только при явном разрешении
            // (News:AllowGeneralNewsFallback). Иначе модель получает чужие заголовки.
            if (result.Count == 0 && _config.AllowGeneralNewsFallback &&
                _cachedNews.TryGetValue("GENERAL", out var general))
                result.AddRange(general.Take(_config.MaxNewsPerSymbol));

            return result
                .GroupBy(n => n.Id, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderByDescending(n => n.PublishedAt)
                .Take(_config.MaxNewsPerSymbol)
                .ToList();
        }
    }

    /// <summary>Базовый актив из торговой пары (BTCUSDT → BTC).</summary>
    public static string ExtractBaseAsset(string symbol)
    {
        string[] quotes = { "USDT", "USDC", "FDUSD", "TUSD", "BUSD", "DAI", "EUR", "TRY", "BTC", "ETH", "BNB", "USD" };
        foreach (var quote in quotes)
        {
            if (symbol.EndsWith(quote, StringComparison.OrdinalIgnoreCase) && symbol.Length > quote.Length)
                return symbol[..^quote.Length];
        }
        return symbol;
    }

    /// <summary>Обновить новости (с учётом интервала и защиты от параллельных запросов).</summary>
    public async Task UpdateNewsIfNeededAsync(CancellationToken ct = default)
    {
        if (!_config.Enabled)
            return;

        lock (_cachedNews)
        {
            if (DateTime.UtcNow - _lastUpdate < TimeSpan.FromMinutes(_config.UpdateIntervalMinutes))
                return;
        }

        if (!await _updateLock.WaitAsync(0, ct))
            return; // обновление уже идёт в другом потоке

        try
        {
            lock (_cachedNews)
            {
                if (DateTime.UtcNow - _lastUpdate < TimeSpan.FromMinutes(_config.UpdateIntervalMinutes))
                    return;
            }

            var allNews = new List<NewsItem>();
            var succeeded = 0;

            foreach (var provider in _providers)
            {
                if (!provider.IsAvailable)
                {
                    NewsLog.Skipped(provider.Name, "недоступен");
                    continue;
                }

                try
                {
                    var news = await provider.GetLatestNewsAsync(ct);
                    allNews.AddRange(news);
                    succeeded++;
                    _logger.LogInformation("Получено {Count} новостей от {Provider}", news.Count, provider.Name);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    NewsLog.Failure(provider.Name, ex, "обновление ленты");
                    _logger.LogError(ex, "Ошибка получения новостей от {Provider}", provider.Name);
                }
            }

            if (succeeded == 0)
            {
                _logger.LogWarning("Все новостные провайдеры недоступны — сохраняем предыдущий кэш");
                lock (_cachedNews) _lastUpdate = DateTime.UtcNow; // не спамим запросами каждый цикл
                return;
            }

            var grouped = FilterAndGroupBySymbol(allNews);

            lock (_cachedNews)
            {
                _cachedNews.Clear();
                foreach (var kvp in grouped)
                    _cachedNews[kvp.Key] = kvp.Value;
                _lastUpdate = DateTime.UtcNow;
            }

            _logger.LogInformation(
                "Новости обновлены: {Total} статей, символов с новостями {Count}", allNews.Count, grouped.Count);
        }
        finally
        {
            _updateLock.Release();
        }
    }

    private Dictionary<string, List<NewsItem>> FilterAndGroupBySymbol(List<NewsItem> allNews)
    {
        var result = new Dictionary<string, List<NewsItem>>(StringComparer.OrdinalIgnoreCase);
        var maxAge = TimeSpan.FromHours(_config.MaxNewsAgeHours);
        var unique = allNews
            .Where(n => n.IsRelevant && DateTime.UtcNow - n.PublishedAt <= maxAge)
            .GroupBy(n => n.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        foreach (var news in unique)
        {
            foreach (var symbol in news.RelatedSymbols.Where(s => !string.IsNullOrWhiteSpace(s)))
            {
                var key = symbol.ToUpperInvariant();
                if (!result.TryGetValue(key, out var list))
                {
                    list = new List<NewsItem>();
                    result[key] = list;
                }
                list.Add(news);
            }

            // P2.8: GENERAL — только крипто-фон (распознанный тикер или крипто-ключевые слова),
            // иначе fallback тащит BBC/Thanksgiving в контекст каждой монеты.
            if (news.RelatedSymbols.Count > 0 || NewsSymbolMapper.IsCryptoRelated(news.Title, news.Text))
            {
                if (!result.TryGetValue("GENERAL", out var general))
                {
                    general = new List<NewsItem>();
                    result["GENERAL"] = general;
                }
                general.Add(news);
            }
        }

        foreach (var kvp in result)
        {
            kvp.Value.Sort((a, b) =>
            {
                var dateCompare = b.PublishedAt.CompareTo(a.PublishedAt);
                return dateCompare != 0 ? dateCompare : Math.Abs(b.SentimentScore).CompareTo(Math.Abs(a.SentimentScore));
            });
        }

        return result;
    }
}
