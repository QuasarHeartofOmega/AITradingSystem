using AITradingSystem.Common.Configuration;
using AITradingSystem.Core.Indicators;
using AITradingSystem.Core.Models.MarketData;
using AITradingSystem.Services.Analysis;
using AITradingSystem.Services.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITradingSystem.Tests;

/// <summary>
/// P1.4: целостность данных после реконнекта WS — сетка свечей, REST-перезагрузка,
/// повторный прогрев и fail-closed запрет входов на протухших данных.
/// Тесты добавлены, но не запускались (запрет сборок/проверок в проходе 21).
/// </summary>
public class MarketDataIntegrityTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private static OHLCV Candle(string symbol, DateTime openTime, decimal close = 100m, decimal volume = 10m) => new()
    {
        Symbol = symbol,
        OpenTime = openTime,
        CloseTime = openTime + Interval - TimeSpan.FromMilliseconds(1),
        Open = close,
        High = close + 1m,
        Low = close - 1m,
        Close = close,
        Volume = volume,
        QuoteVolume = volume * close,
        TradesCount = 1
    };

    private static List<OHLCV> ClosedHistory(string symbol, int count, DateTime? lastClosedOpenTime = null)
    {
        var last = lastClosedOpenTime ?? CandleSchedule.GetLastClosedOpenTime(DateTime.UtcNow, Interval);
        var list = new List<OHLCV>(count);
        for (var i = count - 1; i >= 0; i--)
            list.Add(Candle(symbol, last - TimeSpan.FromTicks(Interval.Ticks * i), 100m + i % 7, 10m + i % 5));
        return list;
    }

    private static MarketAnalyzer NewAnalyzer() => new(NullLogger<MarketAnalyzer>.Instance);

    [Fact]
    public void CandleSchedule_Parses_Valid_Intervals()
    {
        Assert.True(CandleSchedule.TryParseInterval("1m", out var m1));
        Assert.Equal(TimeSpan.FromMinutes(1), m1);
        Assert.True(CandleSchedule.TryParseInterval("15m", out var m15));
        Assert.Equal(TimeSpan.FromMinutes(15), m15);
        Assert.True(CandleSchedule.TryParseInterval("4h", out var h4));
        Assert.Equal(TimeSpan.FromHours(4), h4);
        Assert.True(CandleSchedule.TryParseInterval("1d", out var d1));
        Assert.Equal(TimeSpan.FromDays(1), d1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0m")]
    [InlineData("-1m")]
    [InlineData("15x")]
    [InlineData("m")]
    [InlineData("1.5m")]
    public void CandleSchedule_Rejects_Invalid_Intervals(string text)
    {
        Assert.False(CandleSchedule.TryParseInterval(text, out _));
    }

    [Fact]
    public void CandleSchedule_Computes_Current_And_LastClosed_OpenTime()
    {
        var now = new DateTime(2026, 11, 24, 12, 7, 30, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc), CandleSchedule.GetCurrentOpenTime(now, Interval));
        Assert.Equal(new DateTime(2026, 11, 24, 11, 45, 0, DateTimeKind.Utc), CandleSchedule.GetLastClosedOpenTime(now, Interval));
    }

    [Fact]
    public void IndicatorCalculator_Accepts_Contiguous_Closed_Candles()
    {
        var calc = new IndicatorCalculator("BTCUSDT");
        var start = new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(CandleAppendResult.Accepted, calc.TryAppendClosed(Candle("BTCUSDT", start), Interval));
        Assert.Equal(CandleAppendResult.Accepted, calc.TryAppendClosed(Candle("BTCUSDT", start + Interval), Interval));
        Assert.Equal(2, calc.ClosedCandles);
        Assert.Equal(start + Interval, calc.LastClosedOpenTime);
    }

    [Fact]
    public void IndicatorCalculator_Detects_Duplicate_OutOfOrder_And_Gap()
    {
        var calc = new IndicatorCalculator("BTCUSDT");
        var start = new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc);
        calc.TryAppendClosed(Candle("BTCUSDT", start), Interval);
        calc.TryAppendClosed(Candle("BTCUSDT", start + Interval), Interval);

        Assert.Equal(CandleAppendResult.Duplicate, calc.TryAppendClosed(Candle("BTCUSDT", start + Interval), Interval));
        Assert.Equal(CandleAppendResult.OutOfOrder, calc.TryAppendClosed(Candle("BTCUSDT", start), Interval));
        Assert.Equal(CandleAppendResult.Gap, calc.TryAppendClosed(Candle("BTCUSDT", start + 3 * Interval), Interval));

        // Разрыв не должен маскироваться: свеча после дыры не попала в индикаторы.
        Assert.Equal(2, calc.ClosedCandles);
        Assert.Equal(start + Interval, calc.LastClosedOpenTime);
    }

    [Fact]
    public void IndicatorCalculator_Reset_Clears_Candle_Sequence()
    {
        var calc = new IndicatorCalculator("BTCUSDT");
        var start = new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc);
        calc.TryAppendClosed(Candle("BTCUSDT", start), Interval);
        calc.Reset();

        Assert.Null(calc.LastClosedOpenTime);
        Assert.Equal(0, calc.ClosedCandles);
        Assert.Equal(CandleAppendResult.Accepted, calc.TryAppendClosed(Candle("BTCUSDT", start + 10 * Interval), Interval));
    }

    [Fact]
    public void MarketAnalyzer_Reloads_Continuous_History_And_Warms_Indicators()
    {
        var analyzer = NewAnalyzer();
        var now = DateTime.UtcNow;
        var history = ClosedHistory("BTCUSDT", 250);

        Assert.True(analyzer.TryReloadHistory("BTCUSDT", history, Interval, 200, now, out var reason));
        Assert.Equal(string.Empty, reason);
        Assert.False(analyzer.IsRecoveryRequired("BTCUSDT"));

        var warmup = analyzer.GetWarmupInfo("BTCUSDT");
        Assert.True(warmup.Ready);
        Assert.Equal(250, warmup.Candles);
    }

    [Fact]
    public void MarketAnalyzer_Rejects_Gapped_Rest_History()
    {
        var analyzer = NewAnalyzer();
        var history = ClosedHistory("BTCUSDT", 250);
        history.RemoveAt(100);

        Assert.False(analyzer.TryReloadHistory("BTCUSDT", history, Interval, 200, DateTime.UtcNow, out var reason));
        Assert.Equal("history-gap", reason);
    }

    [Fact]
    public void MarketAnalyzer_Rejects_Duplicate_Rest_History()
    {
        var analyzer = NewAnalyzer();
        var history = ClosedHistory("BTCUSDT", 250);
        history.Add(history[^1]);

        Assert.False(analyzer.TryReloadHistory("BTCUSDT", history, Interval, 200, DateTime.UtcNow, out var reason));
        Assert.Equal("duplicate-candle", reason);
    }

    [Fact]
    public void MarketAnalyzer_Rejects_Insufficient_History()
    {
        var analyzer = NewAnalyzer();
        var history = ClosedHistory("BTCUSDT", 50);

        Assert.False(analyzer.TryReloadHistory("BTCUSDT", history, Interval, 200, DateTime.UtcNow, out var reason));
        Assert.StartsWith("insufficient-history", reason);
    }

    [Fact]
    public void MarketAnalyzer_Rejects_Stale_Rest_History()
    {
        var analyzer = NewAnalyzer();
        var now = DateTime.UtcNow;
        var expectedLast = CandleSchedule.GetLastClosedOpenTime(now, Interval);
        var history = ClosedHistory("BTCUSDT", 250, expectedLast - 2 * Interval);

        Assert.False(analyzer.TryReloadHistory("BTCUSDT", history, Interval, 200, now, out var reason));
        Assert.Equal("stale-history", reason);
    }

    [Fact]
    public void MarketAnalyzer_Freshness_Requires_Kline_And_Book()
    {
        var analyzer = NewAnalyzer();
        var now = DateTime.UtcNow;
        var history = ClosedHistory("BTCUSDT", 250);
        Assert.True(analyzer.TryReloadHistory("BTCUSDT", history, Interval, 200, now, out _));

        // P0.3: REST-прогрев не подтверждает живой стрим.
        // P0.3a шаг 2: суффикс awaiting-live-tick указывает, каких сообщений не хватает.
        Assert.False(analyzer.IsDataFresh("BTCUSDT", now, TimeSpan.FromSeconds(30), out var awaiting));
        Assert.Equal("awaiting-live-tick:both", awaiting);

        // Один kline без стакана больше не объявляет символ живым.
        analyzer.NoteKline("BTCUSDT", now);
        Assert.False(analyzer.IsDataFresh("BTCUSDT", now, TimeSpan.FromSeconds(30), out var noBook));
        Assert.Equal("awaiting-live-tick:book", noBook);

        analyzer.NoteOrderBook("BTCUSDT", now);
        Assert.True(analyzer.IsDataFresh("BTCUSDT", now, TimeSpan.FromSeconds(30), out _));

        var later = now + TimeSpan.FromSeconds(31);
        Assert.False(analyzer.IsDataFresh("BTCUSDT", later, TimeSpan.FromSeconds(30), out var stale));
        Assert.Equal("stale-kline", stale);

        analyzer.NoteKline("BTCUSDT", later);
        Assert.False(analyzer.IsDataFresh("BTCUSDT", later, TimeSpan.FromSeconds(30), out var staleBook));
        Assert.Equal("stale-book", staleBook);

        analyzer.NoteOrderBook("BTCUSDT", later);
        Assert.True(analyzer.IsDataFresh("BTCUSDT", later, TimeSpan.FromSeconds(30), out _));
    }

    [Fact]
    public void MarketAnalyzer_RecoveryRequired_Blocks_Snapshot_And_Freshness()
    {
        var analyzer = NewAnalyzer();
        var now = DateTime.UtcNow;
        var history = ClosedHistory("BTCUSDT", 250);
        Assert.True(analyzer.TryReloadHistory("BTCUSDT", history, Interval, 200, now, out _));
        analyzer.NoteKline("BTCUSDT", now);
        analyzer.NoteOrderBook("BTCUSDT", now);
        Assert.NotNull(analyzer.GetIndicatorSnapshot("BTCUSDT", history[^1]));

        analyzer.MarkRecoveryRequired("BTCUSDT", "ws-reconnected");
        Assert.True(analyzer.IsRecoveryRequired("BTCUSDT"));
        Assert.Null(analyzer.GetIndicatorSnapshot("BTCUSDT", history[^1]));
        Assert.False(analyzer.IsDataFresh("BTCUSDT", now, TimeSpan.FromSeconds(30), out var reason));
        Assert.Equal("ws-reconnected", reason);

        Assert.True(analyzer.TryReloadHistory("BTCUSDT", history, Interval, 200, now, out _));
        analyzer.NoteKline("BTCUSDT", now);
        analyzer.NoteOrderBook("BTCUSDT", now);
        Assert.True(analyzer.IsDataFresh("BTCUSDT", now, TimeSpan.FromSeconds(30), out _));
    }

    [Fact]
    public void MarketAnalyzer_Closed_Candle_Gap_Marks_Recovery()
    {
        var analyzer = NewAnalyzer();
        var now = DateTime.UtcNow;
        var history = ClosedHistory("BTCUSDT", 250);
        Assert.True(analyzer.TryReloadHistory("BTCUSDT", history, Interval, 200, now, out _));

        var lastClosed = history[^1].OpenTime;
        // Пропущена одна закрытая свеча: следующая закрытая приходит с дырой.
        var gapped = Candle("BTCUSDT", lastClosed + 2 * Interval);
        // Делаем свечу уже закрытой для контракта UpdateIndicators.
        gapped.CloseTime = now - TimeSpan.FromMilliseconds(1);

        var result = analyzer.UpdateIndicators("BTCUSDT", gapped, Interval, now);
        Assert.Equal(CandleAppendResult.Gap, result);
        Assert.True(analyzer.IsRecoveryRequired("BTCUSDT"));
        Assert.False(analyzer.IsDataFresh("BTCUSDT", now, TimeSpan.FromSeconds(30), out var reason));
        Assert.Equal("candle-gap", reason);
    }

    [Fact]
    public void MarketAnalyzer_Duplicate_Closed_Candle_Does_Not_Advance_History()
    {
        var analyzer = NewAnalyzer();
        var now = DateTime.UtcNow;
        var history = ClosedHistory("BTCUSDT", 250);
        Assert.True(analyzer.TryReloadHistory("BTCUSDT", history, Interval, 200, now, out _));

        var result = analyzer.UpdateIndicators("BTCUSDT", history[^1], Interval, now);
        Assert.Equal(CandleAppendResult.Duplicate, result);
        Assert.Equal(250, analyzer.GetWarmupInfo("BTCUSDT").Candles);
        Assert.False(analyzer.IsRecoveryRequired("BTCUSDT"));
    }

    [Fact]
    public void Config_Validates_MarketData_Integrity_Settings()
    {
        var config = new TradingConfig { MarketDataMaxAgeSeconds = 4 };
        Assert.Throws<InvalidOperationException>(() => config.Validate());

        config = new TradingConfig { MarketDataRecoveryRetrySeconds = 4 };
        Assert.Throws<InvalidOperationException>(() => config.Validate());

        config = new TradingConfig { MarketDataMaxAgeSeconds = 30, MarketDataRecoveryRetrySeconds = 15 };
        config.Validate();
    }

    [Fact]
    public void Metrics_Record_MarketData_Integrity_Events()
    {
        var metrics = new MetricsCollector();
        metrics.RecordMarketDataDisconnect();
        metrics.RecordMarketDataReconnect();
        metrics.RecordMarketDataGap();
        metrics.RecordMarketDataRecovery(success: false);
        metrics.RecordMarketDataRecovery(success: true);
        metrics.RecordMarketDataStaleEntryBlock();

        Assert.Equal(1, metrics.MarketDataDisconnects);
        Assert.Equal(1, metrics.MarketDataReconnects);
        Assert.Equal(1, metrics.MarketDataGaps);
        Assert.Equal(1, metrics.MarketDataRecoveries);
        Assert.Equal(1, metrics.MarketDataRecoveryFailures);
        Assert.Equal(1, metrics.MarketDataStaleEntryBlocks);
        Assert.Contains("market-data disconnects 1", metrics.GetSummary());
        Assert.Contains("stale-entry blocks 1", metrics.GetSummary());
    }

    /// <summary>
    /// P0.3: REST-прогрев — это history_reloaded, а не stream_live. Входы разрешает только живой WS-тик.
    /// Тесты добавлены, но не запускались (запрет сборок/прогонов в этом проходе).
    /// </summary>
    [Fact]
    public void MarketAnalyzer_RestReload_Does_Not_Fake_Live_Tick()
    {
        var analyzer = NewAnalyzer();
        var now = DateTime.UtcNow;
        var history = ClosedHistory("BTCUSDT", 250);

        Assert.True(analyzer.TryReloadHistory("BTCUSDT", history, Interval, 200, now, out _, TimeSpan.FromSeconds(20)));
        analyzer.NoteOrderBook("BTCUSDT", now);

        Assert.False(analyzer.IsRecoveryRequired("BTCUSDT"));
        Assert.True(analyzer.IsAwaitingLiveTick("BTCUSDT", out var deadline));
        Assert.Equal(now + TimeSpan.FromSeconds(20), deadline);
        Assert.False(analyzer.IsDataFresh("BTCUSDT", now, TimeSpan.FromSeconds(30), out var reason));
        Assert.Equal("awaiting-live-tick:kline", reason);
        Assert.False(analyzer.IsLiveTickOverdue("BTCUSDT", now));
        Assert.True(analyzer.IsLiveTickOverdue("BTCUSDT", now + TimeSpan.FromSeconds(21)));

        analyzer.NoteKline("BTCUSDT", now + TimeSpan.FromSeconds(5));
        Assert.False(analyzer.IsAwaitingLiveTick("BTCUSDT", out _));
        Assert.True(analyzer.TryConsumeLiveConfirmation("BTCUSDT", out var ttl));
        Assert.Equal(5d, ttl, 3);
        Assert.False(analyzer.TryConsumeLiveConfirmation("BTCUSDT", out _));
        Assert.True(analyzer.IsDataFresh("BTCUSDT", now + TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), out _));
    }

    [Fact]
    public void MarketAnalyzer_Resubscribe_Attempts_Extend_Window_And_Reset_On_Live_Tick()
    {
        var analyzer = NewAnalyzer();
        var now = DateTime.UtcNow;
        var history = ClosedHistory("BTCUSDT", 250);
        Assert.True(analyzer.TryReloadHistory("BTCUSDT", history, Interval, 200, now, out _, TimeSpan.FromSeconds(20)));

        var window = TimeSpan.FromSeconds(20);
        Assert.Equal(1, analyzer.NoteResubscribeAttempt("BTCUSDT", now + TimeSpan.FromSeconds(21), window));
        Assert.Equal(2, analyzer.NoteResubscribeAttempt("BTCUSDT", now + TimeSpan.FromSeconds(42), window));
        Assert.True(analyzer.IsLiveTickOverdue("BTCUSDT", now + TimeSpan.FromSeconds(63)));

        analyzer.NoteKline("BTCUSDT", now + TimeSpan.FromSeconds(63));
        // P0.3a шаг 2: сброс попыток требует оба типа сообщений, kline в одиночку не подтверждает live.
        Assert.Equal(2, analyzer.GetResubscribeAttempts("BTCUSDT"));
        analyzer.NoteOrderBook("BTCUSDT", now + TimeSpan.FromSeconds(63));
        Assert.Equal(0, analyzer.GetResubscribeAttempts("BTCUSDT"));
        Assert.False(analyzer.IsLiveTickOverdue("BTCUSDT", now + TimeSpan.FromSeconds(100)));
    }

    [Fact]
    public void MarketAnalyzer_Quarantine_Blocks_Entries_And_Expires_Into_Recovery()
    {
        var analyzer = NewAnalyzer();
        var now = DateTime.UtcNow;
        var history = ClosedHistory("BTCUSDT", 250);
        Assert.True(analyzer.TryReloadHistory("BTCUSDT", history, Interval, 200, now, out _));
        analyzer.NoteKline("BTCUSDT", now);
        analyzer.NoteOrderBook("BTCUSDT", now);
        Assert.True(analyzer.IsDataFresh("BTCUSDT", now, TimeSpan.FromSeconds(30), out _));

        analyzer.Quarantine("BTCUSDT", "no-live-tick:stale-kline", now, TimeSpan.FromSeconds(300));
        Assert.True(analyzer.IsQuarantined("BTCUSDT", now + TimeSpan.FromSeconds(10), out var qReason));
        Assert.Equal("no-live-tick:stale-kline", qReason);
        Assert.False(analyzer.IsDataFresh("BTCUSDT", now + TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), out var blocked));
        Assert.StartsWith("quarantined", blocked);
        Assert.Null(analyzer.GetIndicatorSnapshot("BTCUSDT", history[^1]));
        Assert.False(analyzer.TryLiftExpiredQuarantine("BTCUSDT", now + TimeSpan.FromSeconds(10)));

        Assert.True(analyzer.TryLiftExpiredQuarantine("BTCUSDT", now + TimeSpan.FromSeconds(301)));
        Assert.True(analyzer.IsRecoveryRequired("BTCUSDT"));
        // Снятие срока не равно разрешению входа: символ вернулся в recovery, данные непригодны.
        Assert.False(analyzer.IsDataFresh("BTCUSDT", now + TimeSpan.FromSeconds(301), TimeSpan.FromSeconds(30), out var afterLift));
        Assert.Equal("quarantine-expired", afterLift);
        Assert.True(analyzer.TryConsumeQuarantineLift("BTCUSDT", out var lift, out var served));
        Assert.Equal("no-live-tick:stale-kline", lift);
        Assert.Equal(301d, served, 3);
        Assert.False(analyzer.TryConsumeQuarantineLift("BTCUSDT", out _, out _));
    }

    [Fact]
    public void MarketAnalyzer_Live_Ticks_Lift_Quarantine_Early_Only_With_Both_Streams()
    {
        var analyzer = NewAnalyzer();
        var now = DateTime.UtcNow;
        var history = ClosedHistory("BTCUSDT", 250);
        Assert.True(analyzer.TryReloadHistory("BTCUSDT", history, Interval, 200, now, out _));
        analyzer.Quarantine("BTCUSDT", "no-live-tick:stale-kline", now, TimeSpan.FromSeconds(300));

        // P0.3a шаг 2: один kline после карантина не снимает её — стакан тоже должен вернуться.
        analyzer.NoteKline("BTCUSDT", now + TimeSpan.FromSeconds(30));
        Assert.True(analyzer.IsQuarantined("BTCUSDT", now + TimeSpan.FromSeconds(30), out _));
        Assert.False(analyzer.TryConsumeQuarantineLift("BTCUSDT", out _, out _));

        analyzer.NoteOrderBook("BTCUSDT", now + TimeSpan.FromSeconds(31));
        Assert.False(analyzer.IsQuarantined("BTCUSDT", now + TimeSpan.FromSeconds(31), out _));
        Assert.True(analyzer.TryConsumeQuarantineLift("BTCUSDT", out _, out var served));
        Assert.Equal(31d, served, 3);
    }

    /// <summary>
    /// P0.3a шаг 2: live-подтверждение требует kline И стакан после начала ожидания.
    /// Тесты добавлены, но не запускались (запрет сборок/прогонов в этом проходе).
    /// </summary>
    [Fact]
    public void MarketAnalyzer_Kline_Only_Does_Not_Confirm_Live()
    {
        var analyzer = NewAnalyzer();
        var now = DateTime.UtcNow;
        var history = ClosedHistory("BTCUSDT", 250);
        Assert.True(analyzer.TryReloadHistory("BTCUSDT", history, Interval, 200, now, out _, TimeSpan.FromSeconds(20)));

        analyzer.NoteKline("BTCUSDT", now + TimeSpan.FromSeconds(5));
        analyzer.NoteKline("BTCUSDT", now + TimeSpan.FromSeconds(10));

        Assert.True(analyzer.IsAwaitingLiveTick("BTCUSDT", out _));
        Assert.False(analyzer.TryConsumeLiveConfirmation("BTCUSDT", out _));
        Assert.False(analyzer.IsDataFresh("BTCUSDT", now + TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), out var reason));
        Assert.Equal("awaiting-live-tick:book", reason);
        // Молчание стакана продолжает эскалацию recovery, а не маскируется живым kline.
        Assert.True(analyzer.IsLiveTickOverdue("BTCUSDT", now + TimeSpan.FromSeconds(21)));
    }

    [Fact]
    public void MarketAnalyzer_Book_Only_Does_Not_Confirm_Live()
    {
        var analyzer = NewAnalyzer();
        var now = DateTime.UtcNow;
        var history = ClosedHistory("BTCUSDT", 250);
        Assert.True(analyzer.TryReloadHistory("BTCUSDT", history, Interval, 200, now, out _, TimeSpan.FromSeconds(20)));

        analyzer.NoteOrderBook("BTCUSDT", now + TimeSpan.FromSeconds(5));

        Assert.True(analyzer.IsAwaitingLiveTick("BTCUSDT", out _));
        Assert.False(analyzer.TryConsumeLiveConfirmation("BTCUSDT", out _));
        Assert.False(analyzer.IsDataFresh("BTCUSDT", now + TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), out var reason));
        Assert.Equal("awaiting-live-tick:kline", reason);
        Assert.True(analyzer.IsLiveTickOverdue("BTCUSDT", now + TimeSpan.FromSeconds(21)));
    }

    [Fact]
    public void MarketAnalyzer_Pre_Reload_Book_Does_Not_Count_For_Live_Confirmation()
    {
        var analyzer = NewAnalyzer();
        var now = DateTime.UtcNow;
        var history = ClosedHistory("BTCUSDT", 250);
        Assert.True(analyzer.TryReloadHistory("BTCUSDT", history, Interval, 200, now, out _, TimeSpan.FromSeconds(20)));
        analyzer.NoteKline("BTCUSDT", now);
        analyzer.NoteOrderBook("BTCUSDT", now);
        Assert.True(analyzer.TryConsumeLiveConfirmation("BTCUSDT", out _));

        // Реконнект: старый стакан до нового REST-прогрева не подтверждает новый live-цикл.
        var later = now + TimeSpan.FromSeconds(60);
        analyzer.MarkRecoveryRequired("BTCUSDT", "ws-reconnected");
        Assert.True(analyzer.TryReloadHistory("BTCUSDT", history, Interval, 200, later, out _, TimeSpan.FromSeconds(20)));

        analyzer.NoteKline("BTCUSDT", later + TimeSpan.FromSeconds(5));
        Assert.True(analyzer.IsAwaitingLiveTick("BTCUSDT", out _));
        Assert.False(analyzer.IsDataFresh("BTCUSDT", later + TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), out var reason));
        Assert.Equal("awaiting-live-tick:book", reason);

        analyzer.NoteOrderBook("BTCUSDT", later + TimeSpan.FromSeconds(6));
        Assert.False(analyzer.IsAwaitingLiveTick("BTCUSDT", out _));
        Assert.True(analyzer.TryConsumeLiveConfirmation("BTCUSDT", out var ttl));
        Assert.Equal(6d, ttl, 3);
    }

    /// <summary>
    /// P0.3a шаг 3: серия карантинов растёт до live-подтверждения обоих стримов;
    /// истечение срока серию не сбрасывает — backoff должен переживать expiry-lift.
    /// Тесты добавлены, но не запускались (запрет сборок/прогонов в этом проходе).
    /// </summary>
    [Fact]
    public void MarketAnalyzer_Quarantine_Streak_Resets_Only_On_Live_Confirmation()
    {
        var analyzer = NewAnalyzer();
        var now = DateTime.UtcNow;
        var history = ClosedHistory("BTCUSDT", 250);
        Assert.True(analyzer.TryReloadHistory("BTCUSDT", history, Interval, 200, now, out _, TimeSpan.FromSeconds(20)));

        Assert.Equal(1, analyzer.Quarantine("BTCUSDT", "no-live-tick", now, TimeSpan.FromSeconds(300)));
        Assert.True(analyzer.TryLiftExpiredQuarantine("BTCUSDT", now + TimeSpan.FromSeconds(301)));
        Assert.Equal(1, analyzer.GetQuarantineStreak("BTCUSDT"));

        var second = now + TimeSpan.FromSeconds(301);
        Assert.True(analyzer.TryReloadHistory("BTCUSDT", history, Interval, 200, second, out _, TimeSpan.FromSeconds(20)));
        Assert.Equal(2, analyzer.Quarantine("BTCUSDT", "no-live-tick", second, TimeSpan.FromSeconds(600)));

        // Live-подтверждение обоих стримов во время карантина сбрасывает серию и снимает карантин досрочно.
        var third = second + TimeSpan.FromSeconds(1);
        Assert.True(analyzer.TryReloadHistory("BTCUSDT", history, Interval, 200, third, out _, TimeSpan.FromSeconds(20)));
        analyzer.NoteKline("BTCUSDT", third + TimeSpan.FromSeconds(2));
        analyzer.NoteOrderBook("BTCUSDT", third + TimeSpan.FromSeconds(2));
        Assert.True(analyzer.TryConsumeLiveConfirmation("BTCUSDT", out _));
        Assert.False(analyzer.IsQuarantined("BTCUSDT", third + TimeSpan.FromSeconds(2), out _));
        Assert.Equal(0, analyzer.GetQuarantineStreak("BTCUSDT"));
        Assert.Equal(1, analyzer.Quarantine("BTCUSDT", "no-live-tick", third + TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(300)));
    }

    /// <summary>
    /// P0.3a шаг 3: общий реконнект сохраняет прогресс здоровой пары с непрерывной сеткой свечей
    /// без REST-перезагрузки, но вход требует свежие kline И стакан после реконнекта.
    /// </summary>
    [Fact]
    public void MarketAnalyzer_Reconnect_Keeps_Continuous_History_Without_Rest_Reload()
    {
        var analyzer = NewAnalyzer();
        // now — середина текущей незакрытой свечи: малые смещения не пересекают границу интервала.
        var lastClosed = CandleSchedule.GetLastClosedOpenTime(DateTime.UtcNow, Interval);
        var now = lastClosed + Interval + TimeSpan.FromSeconds(Interval.TotalSeconds / 2);
        var history = ClosedHistory("BTCUSDT", 250, lastClosed);
        Assert.True(analyzer.TryReloadHistory("BTCUSDT", history, Interval, 200, now, out _, TimeSpan.FromSeconds(20)));
        analyzer.NoteKline("BTCUSDT", now);
        analyzer.NoteOrderBook("BTCUSDT", now);
        Assert.True(analyzer.TryConsumeLiveConfirmation("BTCUSDT", out _));

        // Здоровая пара на момент реконнекта: индикаторы сохраняются, REST не нужен.
        var up = now + TimeSpan.FromSeconds(5);
        Assert.True(analyzer.TryResumeAfterReconnect("BTCUSDT", up, Interval, TimeSpan.FromSeconds(20)));
        Assert.False(analyzer.IsRecoveryRequired("BTCUSDT"));
        Assert.False(analyzer.IsDataFresh("BTCUSDT", up, TimeSpan.FromSeconds(30), out var awaiting));
        Assert.Equal("awaiting-live-tick:both", awaiting);

        // Старые (до реконнекта) сообщения не подтверждают новый live-цикл.
        analyzer.NoteKline("BTCUSDT", up + TimeSpan.FromSeconds(1));
        Assert.False(analyzer.IsDataFresh("BTCUSDT", up + TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), out var noBook));
        Assert.Equal("awaiting-live-tick:book", noBook);
        analyzer.NoteOrderBook("BTCUSDT", up + TimeSpan.FromSeconds(2));
        Assert.True(analyzer.IsDataFresh("BTCUSDT", up + TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30), out _));
        Assert.True(analyzer.TryConsumeLiveConfirmation("BTCUSDT", out _));

        // После ws-disconnected пара с непрерывной сеткой тоже возвращается без REST.
        analyzer.MarkRecoveryRequired("BTCUSDT", "ws-disconnected");
        Assert.True(analyzer.TryResumeAfterReconnect("BTCUSDT", up + TimeSpan.FromSeconds(3), Interval, TimeSpan.FromSeconds(20)));
        Assert.False(analyzer.IsRecoveryRequired("BTCUSDT"));
    }

    [Fact]
    public void MarketAnalyzer_Reconnect_Requires_Rest_When_Closed_Candle_Missed_Or_Quarantined()
    {
        var analyzer = NewAnalyzer();
        var lastClosed = CandleSchedule.GetLastClosedOpenTime(DateTime.UtcNow, Interval);
        var now = lastClosed + Interval + TimeSpan.FromSeconds(Interval.TotalSeconds / 2);
        var history = ClosedHistory("BTCUSDT", 250, lastClosed);
        Assert.True(analyzer.TryReloadHistory("BTCUSDT", history, Interval, 200, now, out _, TimeSpan.FromSeconds(20)));
        analyzer.NoteKline("BTCUSDT", now);
        analyzer.NoteOrderBook("BTCUSDT", now);
        Assert.True(analyzer.TryConsumeLiveConfirmation("BTCUSDT", out _));

        // Пропущена закрытая свеча (даун длиннее интервала) — сетка разорвана, нужен REST.
        analyzer.MarkRecoveryRequired("BTCUSDT", "ws-disconnected");
        var late = now + Interval + TimeSpan.FromSeconds(1);
        Assert.False(analyzer.TryResumeAfterReconnect("BTCUSDT", late, Interval, TimeSpan.FromSeconds(20)));
        Assert.True(analyzer.IsRecoveryRequired("BTCUSDT"));

        // Реальная проблема данных (candle-gap) не лечится resume — только REST.
        Assert.True(analyzer.TryReloadHistory("BTCUSDT", history, Interval, 200, now, out _, TimeSpan.FromSeconds(20)));
        analyzer.MarkRecoveryRequired("BTCUSDT", "candle-gap");
        Assert.False(analyzer.TryResumeAfterReconnect("BTCUSDT", now + TimeSpan.FromSeconds(1), Interval, TimeSpan.FromSeconds(20)));

        // Карантин не снимается реконнектом.
        Assert.True(analyzer.TryReloadHistory("BTCUSDT", history, Interval, 200, now, out _, TimeSpan.FromSeconds(20)));
        analyzer.Quarantine("BTCUSDT", "no-live-tick", now, TimeSpan.FromSeconds(300));
        Assert.False(analyzer.TryResumeAfterReconnect("BTCUSDT", now + TimeSpan.FromSeconds(10), Interval, TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void Config_Validates_Quarantine_Backoff_Cap()
    {
        var config = new TradingConfig { MarketDataQuarantineMaxSeconds = 30 };
        Assert.Throws<InvalidOperationException>(() => config.Validate());

        config = new TradingConfig { MarketDataQuarantineSeconds = 300, MarketDataQuarantineMaxSeconds = 120 };
        Assert.Throws<InvalidOperationException>(() => config.Validate());

        config = new TradingConfig();
        config.Validate();
    }

    [Fact]
    public void Config_Validates_MarketData_Live_Confirm_Settings()
    {
        var config = new TradingConfig { MarketDataLiveConfirmSeconds = 4 };
        Assert.Throws<InvalidOperationException>(() => config.Validate());

        config = new TradingConfig { MarketDataMaxResubscribeAttempts = 6 };
        Assert.Throws<InvalidOperationException>(() => config.Validate());

        config = new TradingConfig { MarketDataQuarantineSeconds = 10 };
        Assert.Throws<InvalidOperationException>(() => config.Validate());

        config = new TradingConfig { MarketDataReconnectCooldownSeconds = 5 };
        Assert.Throws<InvalidOperationException>(() => config.Validate());

        config = new TradingConfig();
        config.Validate();
    }

    [Fact]
    public void Metrics_Record_Live_Confirm_And_Quarantine_Events()
    {
        var metrics = new MetricsCollector();
        metrics.RecordMarketDataRecoveryAttempt();
        metrics.RecordMarketDataRecoveryAttempt();
        metrics.RecordMarketDataLiveConfirmed(4d);
        metrics.RecordMarketDataLiveConfirmed(8d);
        metrics.RecordMarketDataResubscribe();
        metrics.RecordMarketDataReconnectRequest();
        metrics.RecordMarketDataQuarantine();
        metrics.RecordMarketDataQuarantineLift();

        Assert.Equal(2, metrics.MarketDataRecoveryAttempts);
        Assert.Equal(2, metrics.MarketDataLiveConfirmations);
        Assert.Equal(6d, metrics.MarketDataMeanTimeToLiveSeconds, 3);
        Assert.Equal(8d, metrics.MarketDataMaxTimeToLiveSeconds, 3);
        Assert.Equal(1, metrics.MarketDataResubscribes);
        Assert.Equal(1, metrics.MarketDataReconnectRequests);
        Assert.Equal(1, metrics.MarketDataQuarantines);
        Assert.Equal(1, metrics.MarketDataQuarantineLifts);
        Assert.Contains("live confirmations 2", metrics.GetSummary());
        Assert.Contains("quarantines 1", metrics.GetSummary());
    }

    /// <summary>
    /// P0.3a шаги 3–4: backoff-серия/эскалация, фактические секунды карантина, причины реконнекта,
    /// collateral-эффект общего реконнекта и recovery-циклы по символам.
    /// Тесты добавлены, но не запускались (запрет сборок/прогонов в этом проходе).
    /// </summary>
    [Fact]
    public void Metrics_Record_Reconnect_Isolation_And_Quarantine_Backoff()
    {
        var metrics = new MetricsCollector();
        metrics.RecordMarketDataReconnectRequest("no-live-tick:stale-kline");
        metrics.RecordMarketDataReconnectRequest("no-live-tick:stale-kline");
        metrics.RecordMarketDataReconnectRequest("awaiting-live-tick:book");
        metrics.RecordMarketDataQuarantine(1);
        metrics.RecordMarketDataQuarantine(3);
        metrics.RecordMarketDataQuarantineEscalation();
        metrics.RecordMarketDataQuarantineServed(300d);
        metrics.RecordMarketDataQuarantineServed(600d);
        metrics.RecordMarketDataQuarantineLift();
        metrics.RecordMarketDataReconnectKept();
        metrics.RecordMarketDataReconnectKept();
        metrics.RecordMarketDataReconnectReload();
        metrics.RecordMarketDataRecoveryCycle("ENJUSDT", success: false);
        metrics.RecordMarketDataRecoveryCycle("ENJUSDT", success: true);
        metrics.RecordMarketDataRecoveryCycle("DOGEUSDT", success: true);

        Assert.Equal(3, metrics.MarketDataReconnectRequests);
        Assert.Equal(2, metrics.MarketDataReconnectCauses["no-live-tick:stale-kline"]);
        Assert.Equal(1, metrics.MarketDataReconnectCauses["awaiting-live-tick:book"]);
        Assert.Equal(2, metrics.MarketDataQuarantines);
        Assert.Equal(3, metrics.MarketDataQuarantineMaxStreak);
        Assert.Equal(1, metrics.MarketDataQuarantineEscalations);
        Assert.Equal(900d, metrics.MarketDataQuarantinedSeconds, 3);
        Assert.Equal(2, metrics.MarketDataReconnectKept);
        Assert.Equal(1, metrics.MarketDataReconnectReloads);
        Assert.Equal(2, metrics.MarketDataRecoveryCyclesBySymbol["ENJUSDT"]);
        Assert.Equal(1, metrics.MarketDataRecoveryFailedCyclesBySymbol["ENJUSDT"]);
        Assert.Equal(1, metrics.MarketDataRecoveryCyclesBySymbol["DOGEUSDT"]);

        var summary = metrics.GetSummary();
        Assert.Contains("max streak 3", summary);
        Assert.Contains("escalations 1", summary);
        Assert.Contains("served 900s", summary);
        Assert.Contains("reconnect kept 2/reloads 1", summary);
        Assert.Contains("no-live-tick:stale-kline=2", summary);
        Assert.Contains("ENJUSDT=2(failed 1)", summary);
        Assert.Contains("DOGEUSDT=1", summary);
    }
}
