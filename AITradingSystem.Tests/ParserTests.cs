using System.Globalization;
using System.Text.Json;
using AITradingSystem.Common.Configuration;
using AITradingSystem.Data.Binance;
using AITradingSystem.Data.News;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITradingSystem.Tests;

/// <summary>
/// Парсеры внешних источников: cryptocurrency.cv и Binance.
/// JSON-образцы соответствуют реальным ответам API (проверено живыми запросами).
/// </summary>
public class ParserTests
{
    private static CryptocurrencyCvClient CreateClient(NewsConfig? config = null) =>
        new(config ?? new NewsConfig(), NullLogger<CryptocurrencyCvClient>.Instance);

    private static string NewsJson(string publishedAt) => $$"""
    {
      "articles": [
        {
          "title": "Bitcoin ETF inflows surge to record high",
          "link": "https://example.com/btc-etf",
          "description": "Spot bitcoin ETFs saw record inflows as institutional demand rises.",
          "pubDate": "{{publishedAt}}",
          "source": "CoinDesk",
          "sourceKey": "coindesk",
          "category": "mainstream",
          "contentType": "news",
          "credibility": 0.9,
          "reputation": 80
        },
        {
          "title": "Ethereum exploit drains protocol",
          "link": "https://example.com/eth-hack",
          "description": "An attacker exploited a contract; losses reported.",
          "pubDate": "{{publishedAt}}",
          "source": "Crypto Briefing",
          "contentType": "news",
          "credibility": 0.6
        },
        {
          "title": "Bitcoin ETF inflows surge to record high",
          "link": "https://example.com/btc-etf",
          "pubDate": "{{publishedAt}}",
          "contentType": "news"
        }
      ],
      "totalCount": 3
    }
    """;

    [Fact]
    public void CryptocurrencyCv_Parses_Articles_Deduplicates_And_Tags_Symbols()
    {
        var published = DateTime.UtcNow.AddMinutes(-10).ToString("yyyy-MM-ddTHH:mm:ss.000Z", CultureInfo.InvariantCulture);
        var client = CreateClient();

        var news = client.Parse(NewsJson(published), out var skipped);

        Assert.Equal(2, news.Count);      // третья статья — дубль по link
        Assert.Equal(1, skipped);
        Assert.Contains("BTC", news[0].RelatedSymbols);
        Assert.Contains("ETH", news[1].RelatedSymbols);
        Assert.True(news[0].SentimentScore > 0);   // inflows/surge/record high
        Assert.True(news[1].SentimentScore < 0);   // exploit
        Assert.All(news, n => Assert.StartsWith("cv:", n.Id));
    }

    [Fact]
    public void CryptocurrencyCv_Skips_Stale_And_LowCredibility()
    {
        var stale = DateTime.UtcNow.AddHours(-72).ToString("yyyy-MM-ddTHH:mm:ss.000Z", CultureInfo.InvariantCulture);
        var client = CreateClient(new NewsConfig { MaxNewsAgeHours = 24, CryptocurrencyCvMinCredibility = 0.8 });

        var news = client.Parse(NewsJson(stale), out _);
        Assert.Empty(news);

        var fresh = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.000Z", CultureInfo.InvariantCulture);
        var filtered = client.Parse(NewsJson(fresh), out _);
        Assert.Single(filtered); // остаётся только CoinDesk (credibility 0.9)
    }

    [Fact]
    public void CryptocurrencyCv_Accepts_Alternative_Wrapper_And_Field_Names()
    {
        var published = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.000Z", CultureInfo.InvariantCulture);
        var json = $$"""
        { "data": [ { "headline": "Solana rally continues", "url": "https://example.com/sol",
                      "summary": "SOL gains as adoption grows", "publishedAt": "{{published}}",
                      "sourceKey": "solana_news", "credibility": "0.7" } ] }
        """;

        var news = CreateClient().Parse(json, out _);

        Assert.Single(news);
        Assert.Equal("https://example.com/sol", news[0].Url);
        Assert.Contains("SOL", news[0].RelatedSymbols);
    }

    [Fact]
    public void NewsSymbolMapper_Maps_Names_And_Ignores_Ambiguous_Tickers()
    {
        var symbols = NewsSymbolMapper.ExtractSymbols("Bitcoin and Ethereum lead the market", null);
        Assert.Contains("BTC", symbols);
        Assert.Contains("ETH", symbols);

        // "link" как обычное слово не должно превращаться в тикер LINK
        var noFalsePositive = NewsSymbolMapper.ExtractSymbols("Click the link to read more", null);
        Assert.DoesNotContain("LINK", noFalsePositive);
    }

    [Fact]
    public void NewsSymbolMapper_Parses_Iso8601_As_Utc()
    {
        var parsed = NewsSymbolMapper.ParsePublishedAt("2026-11-23T15:38:26.000Z");
        Assert.Equal(new DateTime(2026, 11, 23, 15, 38, 26, DateTimeKind.Utc), parsed.ToUniversalTime());
    }

    [Fact]
    public void BinanceFilters_Parse_String_Numbers()
    {
        // Реальный ответ demo-api.binance.com /api/v3/exchangeInfo?symbols=["BTCUSDT"]
        const string json = """
        { "symbols": [ { "symbol": "BTCUSDT", "status": "TRADING", "baseAsset": "BTC", "quoteAsset": "USDT",
          "filters": [
            { "filterType": "PRICE_FILTER", "minPrice": "0.01000000", "tickSize": "0.01000000" },
            { "filterType": "LOT_SIZE", "minQty": "0.00001000", "maxQty": "9000.00000000", "stepSize": "0.00001000" },
            { "filterType": "NOTIONAL", "minNotional": "5.00000000", "applyMinToMarket": true }
          ] } ] }
        """;

        var info = JsonSerializer.Deserialize<BinanceExchangeInfo>(json)!.Symbols[0];

        Assert.Equal(0.00001m, info.StepSize);
        Assert.Equal(0.01m, info.TickSize);
        Assert.Equal(0.00001m, info.MinQty);
        Assert.Equal(5m, info.MinNotional);
    }

    [Fact]
    public void BinanceKlines_Mixed_Types_Are_Read_As_Invariant_Decimals()
    {
        // klines: массив массивов, long + string вперемешку (реальный ответ demo-api)
        const string json = """
        [[1795446900000,"63067.96000000","63665.78000000","62941.44000000","63393.38000000","410.59448000",
          1795447799999,"26006430.03105690",20432,"230.43107000","14599879.65098380","0"]]
        """;

        var rows = JsonSerializer.Deserialize<List<JsonElement[]>>(json)!;
        var k = rows[0];

        Assert.Equal(1795446900000L, k[0].GetInt64());
        Assert.Equal(63067.96m, decimal.Parse(k[1].GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture));
        Assert.Equal(20432, k[8].GetInt32());
        Assert.True(k.Length >= 9);
    }
}
