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
        Assert.False(analyzer.IsDataFresh("BTCUSDT", now, TimeSpan.FromSeconds(30), out var awaiting));
        Assert.Equal("awaiting-live-tick", awaiting);

        analyzer.NoteKline("BTCUSDT", now);
        Assert.False(analyzer.IsDataFresh("BTCUSDT", now, TimeSpan.FromSeconds(30), out var noBook));
        Assert.Equal("no-book", noBook);

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
        Assert.Equal("awaiting-live-tick", reason);
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
        Assert.True(analyzer.TryConsumeQuarantineLift("BTCUSDT", out var lift));
        Assert.Equal("no-live-tick:stale-kline", lift);
        Assert.False(analyzer.TryConsumeQuarantineLift("BTCUSDT", out _));
    }

    [Fact]
    public void MarketAnalyzer_Live_Tick_Lifts_Quarantine_Early()
    {
        var analyzer = NewAnalyzer();
        var now = DateTime.UtcNow;
        var history = ClosedHistory("BTCUSDT", 250);
        Assert.True(analyzer.TryReloadHistory("BTCUSDT", history, Interval, 200, now, out _));
        analyzer.Quarantine("BTCUSDT", "no-live-tick:stale-kline", now, TimeSpan.FromSeconds(300));

        analyzer.NoteKline("BTCUSDT", now + TimeSpan.FromSeconds(30));
        Assert.False(analyzer.IsQuarantined("BTCUSDT", now + TimeSpan.FromSeconds(30), out _));
        Assert.True(analyzer.TryConsumeQuarantineLift("BTCUSDT", out _));
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
}
