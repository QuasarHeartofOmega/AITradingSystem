using AITradingSystem.Common.Configuration;
using AITradingSystem.Data.Binance;
using AITradingSystem.Services.Monitoring;

namespace AITradingSystem.Tests;

/// <summary>
/// P1.6: бюджет весов REST-запросов, баны 429/-1003/418, классификация критичных операций,
/// разбор заголовков и метрики. Тесты добавлены в проходе 17; не запускались
/// (сборки/запуски запрещены заказчиком).
/// </summary>
public class RequestWeightBudgetTests
{
    private static readonly DateTime T = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);

    private static RequestWeightBudget Budget(int limit = 6000, int percent = 80, bool enabled = true) =>
        new(limit, percent, enabled);

    // === Учёт веса: заголовок биржи + локальные оценки ===

    [Fact]
    public void HeaderSample_CountsTowardUsedWeight()
    {
        var b = Budget();
        b.ObserveUsedWeight(5000, T.AddSeconds(10));
        Assert.Equal(5000, b.GetUsedWeight(T.AddSeconds(11)));
    }

    [Fact]
    public void HeaderSamples_MaxWinsWithinWindow()
    {
        var b = Budget();
        b.ObserveUsedWeight(100, T);
        b.ObserveUsedWeight(50, T.AddSeconds(1)); // параллельный ответ мог прийти не по порядку
        Assert.Equal(100, b.GetUsedWeight(T.AddSeconds(2)));
    }

    [Fact]
    public void LocalEstimates_AccumulateBetweenSamples()
    {
        var b = Budget();
        b.AddEstimatedWeight(2400, T);
        b.AddEstimatedWeight(2500, T.AddSeconds(5));
        Assert.Equal(4900, b.GetUsedWeight(T.AddSeconds(6)));
        Assert.True(b.GetThrottleDelay(T.AddSeconds(6), critical: false) > TimeSpan.Zero);
    }

    [Fact]
    public void FreshHeader_ResetsLocalEstimates_NoDoubleCount()
    {
        var b = Budget();
        b.AddEstimatedWeight(3000, T);
        b.ObserveUsedWeight(100, T.AddSeconds(2)); // биржа уже учла отправленные запросы
        Assert.Equal(100, b.GetUsedWeight(T.AddSeconds(3)));
    }

    [Fact]
    public void WindowRollover_ResetsWeight()
    {
        var b = Budget();
        b.ObserveUsedWeight(5900, T);
        Assert.True(b.GetThrottleDelay(T.AddSeconds(1), critical: false) > TimeSpan.Zero);
        Assert.Equal(0, b.GetUsedWeight(T.AddSeconds(61)));
        Assert.Equal(TimeSpan.Zero, b.GetThrottleDelay(T.AddSeconds(61), critical: false));
    }

    // === Soft-limit: только некритичные запросы ===

    [Fact]
    public void SoftLimit_ThrottlesPolling_UntilWindowEnd()
    {
        var b = Budget(); // порог 80% от 6000 = 4800
        b.ObserveUsedWeight(5000, T.AddSeconds(10));

        var delay = b.GetThrottleDelay(T.AddSeconds(10), critical: false);
        Assert.Equal(TimeSpan.FromSeconds(50.25), delay); // до конца окна + guard 0.25 с
    }

    [Fact]
    public void SoftLimit_CriticalOrdersPassWithoutDelay()
    {
        var b = Budget();
        b.ObserveUsedWeight(5900, T);
        Assert.Equal(TimeSpan.Zero, b.GetThrottleDelay(T.AddSeconds(1), critical: true));
    }

    [Fact]
    public void BelowSoftLimit_NoDelay()
    {
        var b = Budget();
        b.ObserveUsedWeight(4000, T);
        Assert.Equal(TimeSpan.Zero, b.GetThrottleDelay(T.AddSeconds(1), critical: false));
    }

    [Fact]
    public void DisabledSoftThrottle_NoSoftDelay_ButBanStillApplies()
    {
        var b = Budget(enabled: false);
        b.ObserveUsedWeight(5900, T);
        Assert.Equal(TimeSpan.Zero, b.GetThrottleDelay(T.AddSeconds(1), critical: false));

        b.RegisterBan(TimeSpan.FromSeconds(10), T);
        Assert.True(b.GetThrottleDelay(T.AddSeconds(1), critical: false) > TimeSpan.Zero);
    }

    // === Бан 429/-1003/418: действует для всех ===

    [Fact]
    public void Ban_BlocksCriticalAndNonCritical()
    {
        var b = Budget();
        b.RegisterBan(TimeSpan.FromSeconds(30), T);

        Assert.True(b.IsBanned(T.AddSeconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(25), b.GetThrottleDelay(T.AddSeconds(5), critical: true));
        Assert.Equal(TimeSpan.FromSeconds(25), b.GetThrottleDelay(T.AddSeconds(5), critical: false));
        Assert.False(b.IsBanned(T.AddSeconds(31)));
        Assert.Equal(TimeSpan.Zero, b.GetThrottleDelay(T.AddSeconds(31), critical: true));
    }

    [Fact]
    public void Ban_ExtendsByMax_NotSum()
    {
        var b = Budget();
        b.RegisterBan(TimeSpan.FromSeconds(10), T);
        b.RegisterBan(TimeSpan.FromSeconds(5), T.AddSeconds(1)); // конец T+6 < T+10 — не сокращает
        Assert.Equal(TimeSpan.FromSeconds(8), b.GetBanRemaining(T.AddSeconds(2)));

        b.RegisterBan(TimeSpan.FromSeconds(20), T.AddSeconds(2)); // продление до T+22
        Assert.Equal(TimeSpan.FromSeconds(20), b.GetBanRemaining(T.AddSeconds(2)));
    }

    // === Классификация операций и веса эндпоинтов ===

    [Theory]
    [InlineData("POST", "/api/v3/order?symbol=BTCUSDT", true)]
    [InlineData("DELETE", "/api/v3/order?symbol=BTCUSDT&orderId=1", true)]
    [InlineData("POST", "/api/v3/orderList/oco?symbol=BTCUSDT", true)]
    [InlineData("DELETE", "/api/v3/orderList?symbol=BTCUSDT&orderListId=1", true)]
    [InlineData("GET", "/api/v3/order?symbol=BTCUSDT&orderId=1", false)] // polling статуса — не критичный
    [InlineData("GET", "/api/v3/openOrders", false)]
    [InlineData("GET", "/api/v3/klines?symbol=BTCUSDT", false)]
    [InlineData("DELETE", "/api/v3/userDataStream?listenKey=x", false)]
    public void IsCriticalOperation_OnlyOrderPostDelete(string method, string endpoint, bool expected)
    {
        Assert.Equal(expected, RequestWeightBudget.IsCriticalOperation(new HttpMethod(method), endpoint));
    }

    [Theory]
    [InlineData("GET", "/api/v3/time", 1)]
    [InlineData("GET", "/api/v3/klines?symbol=BTCUSDT&interval=5m&limit=200", 2)]
    [InlineData("GET", "/api/v3/exchangeInfo?symbols=x", 20)]
    [InlineData("GET", "/api/v3/account?timestamp=1", 20)]
    [InlineData("GET", "/api/v3/order?symbol=BTCUSDT&orderId=1", 4)]
    [InlineData("GET", "/api/v3/openOrders?symbol=BTCUSDT", 6)]
    [InlineData("GET", "/api/v3/openOrders", 80)]
    [InlineData("GET", "/api/v3/ticker/price", 2)]
    [InlineData("POST", "/api/v3/order?symbol=BTCUSDT", 1)]
    [InlineData("DELETE", "/api/v3/orderList?symbol=BTCUSDT", 1)]
    [InlineData("PUT", "/api/v3/userDataStream?listenKey=x", 2)]
    [InlineData("GET", "/api/v3/unknownEndpoint", 1)]
    public void EstimateWeight_MatchesBinanceSpotTable(string method, string endpoint, int expected)
    {
        Assert.Equal(expected, RequestWeightBudget.EstimateWeight(new HttpMethod(method), endpoint));
    }

    // === Разбор заголовков ответа ===

    [Fact]
    public void TryReadUsedWeight_ParsesCurrentAndLegacyHeader()
    {
        using var current = new HttpResponseMessage();
        current.Headers.TryAddWithoutValidation("X-MBX-USED-WEIGHT-1M", "123");
        Assert.True(BinanceRestClient.TryReadUsedWeight(current, out var w1));
        Assert.Equal(123, w1);

        using var legacy = new HttpResponseMessage();
        legacy.Headers.TryAddWithoutValidation("X-MBX-USED-WEIGHT", "77");
        Assert.True(BinanceRestClient.TryReadUsedWeight(legacy, out var w2));
        Assert.Equal(77, w2);

        using var both = new HttpResponseMessage();
        both.Headers.TryAddWithoutValidation("X-MBX-USED-WEIGHT", "50");
        both.Headers.TryAddWithoutValidation("X-MBX-USED-WEIGHT-1M", "60");
        Assert.True(BinanceRestClient.TryReadUsedWeight(both, out var w3));
        Assert.Equal(60, w3);
    }

    [Fact]
    public void TryReadUsedWeight_MissingOrInvalid_ReturnsFalse()
    {
        using var none = new HttpResponseMessage();
        Assert.False(BinanceRestClient.TryReadUsedWeight(none, out _));

        using var bad = new HttpResponseMessage();
        bad.Headers.TryAddWithoutValidation("X-MBX-USED-WEIGHT-1M", "abc");
        Assert.False(BinanceRestClient.TryReadUsedWeight(bad, out _));
    }

    [Fact]
    public void TryReadRetryAfterSeconds_ParsesHeader()
    {
        using var response = new HttpResponseMessage();
        response.Headers.TryAddWithoutValidation("Retry-After", "7");
        Assert.True(BinanceRestClient.TryReadRetryAfterSeconds(response, out var seconds));
        Assert.Equal(7, seconds);

        using var none = new HttpResponseMessage();
        Assert.False(BinanceRestClient.TryReadRetryAfterSeconds(none, out _));
    }

    // === Конфигурация ===

    [Fact]
    public void Config_DefaultsAndValidation()
    {
        var config = new BinanceConfig();
        Assert.Equal(6000, config.RequestWeightLimitPerMinute);
        Assert.Equal(80, config.RequestWeightSoftLimitPercent);
        Assert.True(config.WeightThrottleEnabled);
        config.Validate();

        Assert.Throws<InvalidOperationException>(() =>
            new BinanceConfig { RequestWeightSoftLimitPercent = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new BinanceConfig { RequestWeightSoftLimitPercent = 101 }.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new BinanceConfig { RequestWeightLimitPerMinute = 50 }.Validate());
    }

    // === Метрики (MetricsCollector как приёмник) ===

    [Fact]
    public void Metrics_WeightSnapshotAndRateLimitCounters()
    {
        var metrics = new MetricsCollector();
        IRestRateLimitMetrics sink = metrics;

        sink.RecordRestWeight(500, 6000);
        sink.RecordRestWeight(300, 6000);
        Assert.Equal(300, metrics.RestUsedWeight);
        Assert.Equal(500, metrics.RestMaxUsedWeight);
        Assert.Equal(6000, metrics.RestWeightLimit);

        sink.RecordRateLimitHit(429, -1003, 5000);
        sink.RecordRateLimitHit(418, null, 120_000);
        Assert.Equal(1, metrics.RateLimit429Hits);
        Assert.Equal(1, metrics.RateLimit1003Hits);
        Assert.Equal(1, metrics.IpBan418Hits);
        Assert.Equal(120_000, metrics.LastRateLimitBackoffMs);

        sink.RecordWeightThrottle(1000, critical: false);
        sink.RecordWeightThrottle(500, critical: true);
        Assert.Equal(2, metrics.WeightThrottleWaits);
        Assert.Equal(1000, metrics.MaxWeightThrottleWaitMs);
        Assert.Equal(1500, metrics.TotalWeightThrottleWaitMs);
    }
}
