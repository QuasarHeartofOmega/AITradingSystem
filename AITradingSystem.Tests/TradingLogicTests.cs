using AITradingSystem.Common.Configuration;
using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.Trading;
using AITradingSystem.Core.Models.MarketData;
using AITradingSystem.Core.RiskManagement;
using AITradingSystem.Services.Monitoring;
using AITradingSystem.Services.Trading;
using AITradingSystem.Data.Binance;
using AITradingSystem.Data.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace AITradingSystem.Tests;

/// <summary>
/// Торговая логика: валидация сигнала, жизненный цикл позиции,
/// защита от «мгновенного» стопа и частичного закрытия.
/// </summary>
public class TradingLogicTests
{
    private static Position LongPosition(decimal entry = 100m, decimal sl = 98m, decimal tp = 106m) => new()
    {
        Symbol = "BTCUSDT",
        Side = OrderSide.Buy,
        Quantity = 1m,
        EntryPrice = entry,
        CurrentPrice = entry,
        StopLoss = sl,
        TakeProfit = tp,
        InitialStopLoss = sl,
        OpenedAt = new DateTime(2026, 11, 23, 12, 10, 0, DateTimeKind.Utc)
    };

    [Fact]
    public void Buy_Signal_Requires_Ordered_Prices()
    {
        var signal = new TradingSignal
        {
            Symbol = "BTCUSDT",
            Action = SignalAction.Buy,
            Confidence = 0.8m,
            EntryPrice = 100m,
            StopLoss = 101m, // выше входа — недопустимо для long
            TakeProfit = 106m
        };

        Assert.False(signal.IsValid(out var error));
        Assert.Contains("StopLoss", error);

        signal.StopLoss = 98m;
        Assert.True(signal.IsValid(out _));
        Assert.Equal(3m, signal.RiskRewardRatio);
    }

    [Fact]
    public void Hold_And_Close_Do_Not_Need_Prices()
    {
        var close = new TradingSignal { Action = SignalAction.Close, Confidence = 0.9m };
        Assert.True(close.IsValid(out _));
        Assert.Null(close.RiskRewardRatio);
    }

    [Fact]
    public void Candle_Started_Before_Entry_Is_Flagged()
    {
        var position = LongPosition();

        // Свеча 12:00 открылась до входа в 12:10 — её Low нельзя использовать для SL.
        Assert.True(position.CandleStartedBeforeEntry(new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc)));
        Assert.False(position.CandleStartedBeforeEntry(new DateTime(2026, 11, 23, 12, 15, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public void StopLoss_Hit_Uses_Low_Of_Candle()
    {
        var position = LongPosition();

        Assert.True(position.IsStopLossHit(97m, 101m));
        Assert.False(position.IsStopLossHit(99m, 101m));
        Assert.True(position.IsTakeProfitHit(99m, 107m));
    }

    [Fact]
    public void Partial_Close_Keeps_Remainder_Open()
    {
        var position = LongPosition();
        position.EntryCommission = 0.1m;

        position.ReduceQuantity(0.4m);

        Assert.Equal(0.6m, position.Quantity);
        Assert.Equal(60m, position.Notional);

        position.UpdatePrice(110m);
        Assert.Equal(6m, position.UnrealizedPnL); // 0.6 * (110 - 100)
    }

    [Fact]
    public void Trailing_Stop_Only_Moves_Up_For_Long()
    {
        var position = LongPosition();

        Assert.False(position.UpdateTrailingStop(97m));
        Assert.True(position.UpdateTrailingStop(99m));
        Assert.Equal(99m, position.StopLoss);
    }

    [Fact]
    public void PositionSizer_Never_Exceeds_Balance()
    {
        var sizer = new PositionSizer(new TradingConfig { RiskPerTrade = 0.02 });

        // Стоп в 0.1% → «по риску» объём был бы 20 BTC, но баланс позволяет только 1.
        var quantity = sizer.CalculatePositionSize(100m, 100m, 99.9m, 1m);

        Assert.Equal(1m, quantity);
    }

    [Fact]
    public void PositionSizer_Zero_Stop_Distance_Returns_Zero()
    {
        var sizer = new PositionSizer(new TradingConfig());
        Assert.Equal(0m, sizer.CalculatePositionSize(1000m, 100m, 100m, 1m));
    }

    // === P0.1: биржевая защита OCO ===

    [Fact]
    public void Oco_Prices_For_Long_Satisfy_Binance_Above_Below_Rule()
    {
        var ok = ProtectiveOrderPricing.TryBuild(
            OrderSide.Buy, quantity: 1m, stopLoss: 98m, takeProfit: 106m, referencePrice: 100m,
            tickSize: 0.01m, stepSize: 0.001m, minQty: 0.001m, minNotional: 5m,
            limitSlippage: 0.003m, out var prices, out var error);

        Assert.True(ok, error);
        Assert.NotNull(prices);

        // SELL-защита long: TP.stopPrice (above) > LastPrice > SL.stopPrice (below)
        Assert.True(prices!.TakeProfitTrigger > 100m);
        Assert.True(prices.StopTrigger < 100m);

        // Лимитные цены отступают от триггера в сторону исполнения (для SELL — вниз).
        Assert.True(prices.StopLimit < prices.StopTrigger);
        Assert.True(prices.TakeProfitLimit < prices.TakeProfitTrigger);

        // Округление к tickSize: ни одна цена не «между тиками».
        Assert.Equal(0m, prices.StopLimit % 0.01m);
        Assert.Equal(0m, prices.TakeProfitLimit % 0.01m);
    }

    [Fact]
    public void Oco_Is_Rejected_When_Stop_Already_Beyond_Market()
    {
        // Цена ушла ниже стопа: OCO поставить нельзя — нужен market-выход, а не «голая» позиция.
        var ok = ProtectiveOrderPricing.TryBuild(
            OrderSide.Buy, 1m, stopLoss: 98m, takeProfit: 106m, referencePrice: 97m,
            0.01m, 0.001m, 0.001m, 5m, 0.003m, out var prices, out var error);

        Assert.False(ok);
        Assert.Null(prices);
        Assert.Contains("market", error);
    }

    [Fact]
    public void Oco_Quantity_Rounds_To_Step_And_Checks_MinNotional()
    {
        var ok = ProtectiveOrderPricing.TryBuild(
            OrderSide.Buy, quantity: 0.123456m, stopLoss: 98m, takeProfit: 106m, referencePrice: 100m,
            tickSize: 0.01m, stepSize: 0.001m, minQty: 0.001m, minNotional: 5m,
            limitSlippage: 0.003m, out var prices, out _);

        Assert.True(ok);
        Assert.Equal(0.123m, prices!.Quantity);

        // Обе ноги должны проходить minNotional: 0.123 * ~97.7 = 12 < 50.
        var tooSmall = ProtectiveOrderPricing.TryBuild(
            OrderSide.Buy, 0.123456m, 98m, 106m, 100m,
            0.01m, 0.001m, 0.001m, minNotional: 50m, 0.003m, out _, out var error);

        Assert.False(tooSmall);
        Assert.Contains("minNotional", error);
    }

    [Fact]
    public void Trailing_Replace_Requires_Significant_Improvement()
    {
        // 0.05% улучшения — меньше порога 0.15%: биржу не дёргаем.
        Assert.False(ProtectiveOrderPricing.IsStopImprovementSignificant(OrderSide.Buy, 98m, 98.05m, 0.15m));

        // 0.51% — заменяем связку.
        Assert.True(ProtectiveOrderPricing.IsStopImprovementSignificant(OrderSide.Buy, 98m, 98.5m, 0.15m));

        // Ухудшение стопа для long недопустимо ни при каком пороге.
        Assert.False(ProtectiveOrderPricing.IsStopImprovementSignificant(OrderSide.Buy, 98m, 97m, 0m));
    }

    [Fact]
    public void Position_Without_Protection_Is_Not_Exchange_Protected()
    {
        var position = LongPosition();
        Assert.False(position.HasExchangeProtection);

        position.SetProtection(new ProtectiveOrderList { IsSimulated = true });
        Assert.False(position.HasExchangeProtection); // paper-симуляция не защищает от падения процесса

        position.SetProtection(new ProtectiveOrderList { OrderListId = 42 });
        Assert.True(position.HasExchangeProtection);
    }

    // === P0.2: независимый тик-контур сопровождения ===

    [Fact]
    public void Exit_Price_Uses_Executable_Side_Of_Book()
    {
        var tick = new PositionMonitor.MarketTick(Last: 100m, Bid: 99.5m, Ask: 100.5m, UpdatedAtUtc: DateTime.UtcNow);

        // Long закрывается SELL'ом → получим bid, а не «красивую» середину.
        Assert.Equal(99.5m, PositionMonitor.ResolveExitPrice(OrderSide.Buy, tick));
        Assert.Equal(100.5m, PositionMonitor.ResolveExitPrice(OrderSide.Sell, tick));

        // Нет стакана — последняя цена; нет и её — середина.
        Assert.Equal(100m, PositionMonitor.ResolveExitPrice(OrderSide.Buy, tick with { Bid = 0m, Ask = 0m }));
        Assert.Equal(100m, PositionMonitor.ResolveExitPrice(
            OrderSide.Buy, new PositionMonitor.MarketTick(0m, 99m, 101m, DateTime.UtcNow)));
    }

    [Fact]
    public void Tick_Below_Stop_Triggers_Exit_Without_Waiting_For_Candle()
    {
        var position = LongPosition();
        var candle = new OHLCV
        {
            Symbol = "BTCUSDT",
            OpenTime = position.OpenedAt.AddMinutes(-5), // свеча началась ДО входа
            Low = 99m, High = 101m, Close = 100m
        };

        // Тик above stop — выхода нет, экстремумы старой свечи не считаются.
        var idle = PositionMonitor.EvaluateExits(position, 99.9m, candle);
        Assert.False(idle.StopLossHit);
        Assert.False(idle.TakeProfitHit);

        // Тик пробил стоп — выход сразу, не дожидаясь закрытия свечи и цикла анализа.
        var hit = PositionMonitor.EvaluateExits(position, 97.9m, candle);
        Assert.True(hit.StopLossHit);

        var tp = PositionMonitor.EvaluateExits(position, 106.5m, candle);
        Assert.True(tp.TakeProfitHit);
    }

    [Fact]
    public void Candle_Started_After_Entry_Contributes_Extremes()
    {
        var position = LongPosition();
        var candle = new OHLCV
        {
            Symbol = "BTCUSDT",
            OpenTime = position.OpenedAt.AddMinutes(1), // свеча началась ПОСЛЕ входа
            Low = 97m, High = 100m, Close = 99m
        };

        // Последний тик 99, но внутри свечи уже был прокол 97 — стоп обязан сработать.
        var hit = PositionMonitor.EvaluateExits(position, 99m, candle);
        Assert.True(hit.StopLossHit);
    }

    [Fact]
    public void No_Tick_And_No_Candle_Does_Not_Fabricate_Exit()
    {
        var position = LongPosition();
        var result = PositionMonitor.EvaluateExits(position, 0m, null);

        Assert.False(result.StopLossHit);
        Assert.False(result.TakeProfitHit);
    }

    [Fact]
    public void PositionMonitor_Config_Validation_Rejects_Unsafe_Intervals()
    {
        var config = new TradingConfig
        {
            Symbols = new List<string> { "BTCUSDT" },
            PositionMonitorIntervalMs = 50
        };
        Assert.Throws<InvalidOperationException>(() => config.Validate());

        config.PositionMonitorIntervalMs = 500;
        config.PositionMonitorSweepSeconds = 0;
        Assert.Throws<InvalidOperationException>(() => config.Validate());

        config.PositionMonitorSweepSeconds = 5;
        config.ProtectionRetryIntervalSeconds = 0;
        Assert.Throws<InvalidOperationException>(() => config.Validate());

        config.ProtectionRetryIntervalSeconds = 15;
        config.Validate();
    }

    // === P0.3 (фундамент): очередь исполнения с приоритетами и идемпотентностью ===

    private static ExecutionQueue NewQueue(
        int maxParallel = 1,
        IExecutionJournal? journal = null,
        int retryAttempts = 3,
        int retryBaseMs = 50,
        int retryMaxMs = 60)
    {
        var config = new TradingConfig
        {
            Symbols = new List<string> { "BTCUSDT" },
            ExecutionQueueMaxParallel = maxParallel,
            ExecutionRetryMaxAttempts = retryAttempts,
            ExecutionRetryBaseDelayMs = retryBaseMs,
            ExecutionRetryMaxDelayMs = retryMaxMs
        };
        return new ExecutionQueue(config, new MetricsCollector(), NullLogger<ExecutionQueue>.Instance, journal);
    }

    private static string TempJournalDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ats-exec-journal-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static JsonExecutionJournal NewJournal(string directory) =>
        new(directory, NullLogger<JsonExecutionJournal>.Instance);

    [Fact]
    public async Task Close_Is_Executed_Before_Earlier_Queued_Entry()
    {
        await using var queue = NewQueue();
        var order = new List<string>();
        var gate = new TaskCompletionSource();
        var started = new TaskCompletionSource();

        // Первая операция занимает единственный слот, остальные копятся в очереди.
        var blocker = queue.SubmitAsync("BTCUSDT", ExecutionPriority.Entry, "block", async _ =>
        {
            started.SetResult();
            await gate.Task;
            lock (order) order.Add("block");
            return 1;
        });

        await started.Task; // слот гарантированно занят, дальше — чистая проверка приоритетов

        var entry = queue.SubmitAsync("ETHUSDT", ExecutionPriority.Entry, "entry", _ =>
        {
            lock (order) order.Add("entry");
            return Task.FromResult(2);
        });

        var close = queue.SubmitAsync("BNBUSDT", ExecutionPriority.Close, "close", _ =>
        {
            lock (order) order.Add("close");
            return Task.FromResult(3);
        });

        gate.SetResult();
        await Task.WhenAll(blocker, entry, close);

        Assert.Equal(new[] { "block", "close", "entry" }, order);
    }

    [Fact]
    public async Task Same_ClientOrderId_Does_Not_Send_Second_Order()
    {
        await using var queue = NewQueue();
        var sends = 0;

        Task<int> Send() => queue.SubmitAsync("BTCUSDT", ExecutionPriority.Entry, "ent-same", _ =>
        {
            Interlocked.Increment(ref sends);
            return Task.FromResult(42);
        });

        var first = await Send();
        var second = await Send(); // тот же ключ — результат из кэша, отправки нет

        Assert.Equal(42, first);
        Assert.Equal(42, second);
        Assert.Equal(1, sends);
        Assert.Equal(1, queue.ReplayedResults);
    }

    [Fact]
    public async Task One_Operation_Per_Symbol_In_Flight()
    {
        await using var queue = NewQueue(maxParallel: 4);
        var concurrent = 0;
        var maxConcurrent = 0;

        async Task<int> Work()
        {
            var now = Interlocked.Increment(ref concurrent);
            if (now > Volatile.Read(ref maxConcurrent))
                Interlocked.Exchange(ref maxConcurrent, now);

            await Task.Delay(20);
            Interlocked.Decrement(ref concurrent);
            return 1;
        }

        await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => queue.SubmitAsync("BTCUSDT", ExecutionPriority.Entry, null, _ => Work())));

        Assert.Equal(1, maxConcurrent);
    }

    // === P0.3: durable-журнал намерений, сверка после рестарта, ретраи операции ===

    [Fact]
    public async Task Confirmed_Key_Survives_Restart_And_Blocks_Second_Order()
    {
        var dir = TempJournalDirectory();
        try
        {
            await using (var first = NewQueue(journal: NewJournal(dir)))
            {
                var sent = await first.SubmitAsync("BTCUSDT", ExecutionPriority.Close, "cls-restart",
                    _ => Task.FromResult(7));
                Assert.Equal(7, sent);
            }

            // Новый процесс: кэш в памяти пуст, знание о ключе — только в журнале.
            await using var second = NewQueue(journal: NewJournal(dir));
            Assert.Equal(1, await second.RestoreAsync());
            second.SetIntentResolver((_, _) => Task.FromResult<object?>(99));

            var sends = 0;
            var result = await second.SubmitAsync("BTCUSDT", ExecutionPriority.Close, "cls-restart", _ =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromResult(7);
            });

            Assert.Equal(99, result); // состояние взято с биржи
            Assert.Equal(0, sends);   // второй ордер не отправлен
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Unresolved_Intent_Is_Checked_Against_Exchange_Before_Resend()
    {
        var dir = TempJournalDirectory();
        try
        {
            // Процесс умер между отправкой и ответом: в журнале Pending.
            var journal = NewJournal(dir);
            await journal.RecordAsync(new ExecutionIntent
            {
                ClientOrderId = "ent-unknown",
                Symbol = "BTCUSDT",
                Operation = nameof(ExecutionPriority.Entry),
                Status = ExecutionIntentStatus.Pending
            });

            await using var queue = NewQueue(journal: NewJournal(dir));
            await queue.RestoreAsync();
            Assert.Single(queue.UnresolvedIntents);

            // Биржа об ордере не знает → ключ снимается, отправка разрешена.
            queue.SetIntentResolver((_, _) => Task.FromResult<object?>(null));
            var sends = 0;
            var result = await queue.SubmitAsync("BTCUSDT", ExecutionPriority.Entry, "ent-unknown", _ =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromResult(5);
            });

            Assert.Equal(5, result);
            Assert.Equal(1, sends);
            Assert.Empty(queue.UnresolvedIntents);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Unresolved_Intent_Found_On_Exchange_Is_Not_Sent_Again()
    {
        var dir = TempJournalDirectory();
        try
        {
            var journal = NewJournal(dir);
            await journal.RecordAsync(new ExecutionIntent
            {
                ClientOrderId = "cls-crashed",
                Symbol = "BTCUSDT",
                Operation = nameof(ExecutionPriority.Close),
                Status = ExecutionIntentStatus.Pending
            });

            await using var queue = NewQueue(journal: NewJournal(dir));
            await queue.RestoreAsync();
            queue.SetIntentResolver((_, _) => Task.FromResult<object?>("order-on-exchange"));

            var sends = 0;
            var result = await queue.SubmitAsync("BTCUSDT", ExecutionPriority.Close, "cls-crashed", _ =>
            {
                Interlocked.Increment(ref sends);
                return Task.FromResult("duplicate");
            });

            Assert.Equal("order-on-exchange", result);
            Assert.Equal(0, sends);
            Assert.Empty(queue.UnresolvedIntents);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Transient_Failure_Is_Retried_With_Same_Key()
    {
        await using var queue = NewQueue();
        var attempts = 0;
        var keys = new List<string?>();

        var result = await queue.SubmitAsync("BTCUSDT", ExecutionPriority.Entry, "ent-retry", _ =>
        {
            keys.Add("ent-retry");
            if (Interlocked.Increment(ref attempts) == 1)
                throw new BinanceApiException(500, -1000, "Internal error", "{}");
            return Task.FromResult(11);
        });

        Assert.Equal(11, result);
        Assert.Equal(2, attempts);
        Assert.Equal(1, queue.RetriedOperations);
        Assert.Equal(new[] { "ent-retry", "ent-retry" }, keys);
    }

    [Fact]
    public async Task Retry_Stops_At_Configured_Attempt_Limit()
    {
        await using var queue = NewQueue(retryAttempts: 3);
        var attempts = 0;

        await Assert.ThrowsAsync<BinanceApiException>(() => queue.SubmitAsync<int>(
            "BTCUSDT", ExecutionPriority.Entry, "ent-limit", _ =>
            {
                Interlocked.Increment(ref attempts);
                throw new BinanceApiException(503, -1001, "Internal error", "{}");
            }));

        Assert.Equal(3, attempts);
        Assert.Equal(2, queue.RetriedOperations);
    }

    [Fact]
    public async Task NonTransient_Rejection_Is_Not_Retried()
    {
        await using var queue = NewQueue();
        var attempts = 0;

        await Assert.ThrowsAsync<BinanceApiException>(() => queue.SubmitAsync<int>(
            "BTCUSDT", ExecutionPriority.Entry, "ent-reject", _ =>
            {
                Interlocked.Increment(ref attempts);
                throw new BinanceApiException(400, -2010, "Account has insufficient balance", "{}");
            }));

        Assert.Equal(1, attempts);
        Assert.Equal(0, queue.RetriedOperations);
    }

    [Fact]
    public void Execution_Retry_Config_Is_Validated()
    {
        var config = new TradingConfig
        {
            Symbols = new List<string> { "BTCUSDT" },
            ExecutionRetryMaxAttempts = 0
        };
        Assert.Throws<InvalidOperationException>(() => config.Validate());

        config.ExecutionRetryMaxAttempts = 3;
        config.ExecutionRetryBaseDelayMs = 5000;
        config.ExecutionRetryMaxDelayMs = 1000; // потолок меньше базы
        Assert.Throws<InvalidOperationException>(() => config.Validate());

        config.ExecutionRetryBaseDelayMs = 300;
        config.ExecutionRetryMaxDelayMs = 5000;
        config.Validate();
    }

    // ===== P0.4: персист DrawdownTracker =====

    private static DrawdownTracker NewTracker(decimal initial, Func<DateTime>? clock = null) =>
        new(new TradingConfig { MaxDrawdown = 0.10, DailyLossLimit = 0.05 }, initial, clock);

    [Fact]
    public void Drawdown_Daily_Loss_Survives_Restart()
    {
        var noon = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        Func<DateTime> clock = () => noon;

        var before = NewTracker(1000m, clock);
        before.UpdateBalance(900m); // -10% за день — дневной лимит пробит
        Assert.True(before.IsDailyLossLimitHit);

        var state = before.CaptureState();

        // «Рестарт»: свежий трекер с тем же начальным балансом
        var after = NewTracker(1000m, clock);
        Assert.False(after.IsDailyLossLimitHit); // без восстановления лимит «прощал» убыток
        after.RestoreState(state);

        Assert.True(after.IsDailyLossLimitHit);
        Assert.Equal(-100m, after.DailyPnL);
        Assert.Equal(1000m, after.CaptureState().PeakBalance);
        Assert.Equal(1000m, after.CaptureState().DayStartBalance);
    }

    [Fact]
    public void Drawdown_Peak_Survives_Restart()
    {
        var noon = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        Func<DateTime> clock = () => noon;

        var before = NewTracker(1000m, clock);
        before.UpdateBalance(1200m); // пик 1200
        before.UpdateBalance(1100m); // просадка от пика
        var state = before.CaptureState();

        var after = NewTracker(1000m, clock);
        after.RestoreState(state);

        Assert.Equal(1200m, after.CaptureState().PeakBalance);
        Assert.Equal((1200m - 1100m) / 1200m, after.CurrentDrawdownPercent);
        Assert.True(after.CurrentDrawdownPercent > 0); // без восстановления пик был бы 1000 и просадка 0
    }

    [Fact]
    public void Day_Rollover_Follows_Exchange_Clock()
    {
        var exchangeNow = new DateTime(2026, 11, 24, 23, 59, 0, DateTimeKind.Utc);
        var tracker = NewTracker(1000m, () => exchangeNow);
        tracker.UpdateBalance(900m);
        Assert.Equal(-100m, tracker.DailyPnL);

        // Биржа перешла через полночь UTC — локальная дата процесса при этом ещё та же
        exchangeNow = new DateTime(2026, 11, 25, 0, 1, 0, DateTimeKind.Utc);
        tracker.UpdateBalance(890m);

        Assert.Equal(0m, tracker.DailyPnL); // новый день стартует с 890
        Assert.Equal(890m, tracker.CaptureState().DayStartBalance);
        Assert.Equal(1000m, tracker.CaptureState().PeakBalance); // пик смена дня не сбрасывает
    }

    [Fact]
    public void Restored_Stale_Day_Resets_Daily_But_Keeps_Peak()
    {
        var today = new DateTime(2026, 11, 25, 8, 0, 0, DateTimeKind.Utc);
        var state = new DrawdownState
        {
            PeakBalance = 1000m,
            DayStartBalance = 1000m,
            DayStartUtc = new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc),
            MaxDrawdownPercent = 0.10m,
            CurrentBalance = 900m,
            SavedAtUtc = new DateTime(2026, 11, 24, 23, 0, 0, DateTimeKind.Utc)
        };

        var tracker = NewTracker(1000m, () => today);
        tracker.RestoreState(state);

        Assert.Equal(0m, tracker.DailyPnL); // вчерашний убыток не переносится на новый день
        Assert.False(tracker.IsDailyLossLimitHit);
        Assert.Equal(0.10m, tracker.CurrentDrawdownPercent); // просадка от пика остаётся
        Assert.Equal(0.10m, tracker.MaxDrawdownPercent);
    }

    [Fact]
    public async Task Risk_State_Repository_Roundtrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ats-risk-state-tests", Guid.NewGuid().ToString("N"));
        var repo = new JsonStateRepository(directory, NullLogger<JsonStateRepository>.Instance);
        var state = new DrawdownState
        {
            PeakBalance = 1500m,
            DayStartBalance = 1400m,
            DayStartUtc = new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc),
            MaxDrawdownPercent = 0.07m,
            CurrentBalance = 1450m,
            SavedAtUtc = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc)
        };

        await repo.SaveRiskStateAsync(state);
        var loaded = await repo.LoadRiskStateAsync();

        Assert.NotNull(loaded);
        Assert.Equal(1500m, loaded!.PeakBalance);
        Assert.Equal(1400m, loaded.DayStartBalance);
        Assert.Equal(state.DayStartUtc, loaded.DayStartUtc);
        Assert.Equal(0.07m, loaded.MaxDrawdownPercent);
        Assert.Equal(1450m, loaded.CurrentBalance);
    }

    [Fact]
    public async Task Risk_State_Migrates_Legacy_Peak()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ats-risk-state-tests", Guid.NewGuid().ToString("N"));
        var repo = new JsonStateRepository(directory, NullLogger<JsonStateRepository>.Instance);
        await repo.SavePeakBalanceAsync(1500m);

        var loaded = await repo.LoadRiskStateAsync();
        Assert.NotNull(loaded);
        Assert.Equal(1500m, loaded!.PeakBalance);
        Assert.Equal(0m, loaded.CurrentBalance); // дневной старт неизвестен

        var tracker = NewTracker(1000m);
        tracker.RestoreState(loaded);
        tracker.UpdateBalance(1200m);

        Assert.Equal(1500m, tracker.CaptureState().PeakBalance); // пик из legacy не потерян
        Assert.Equal(0.20m, tracker.CurrentDrawdownPercent); // (1500-1200)/1500
    }

    [Fact]
    public void Reconciliation_Classifies_Orphans_External_And_Missing_Protection()
    {
        var pos = LongPosition();
        pos.SetProtection(new ProtectiveOrderList
        {
            OrderListId = 10, Symbol = "BTCUSDT", Side = OrderSide.Sell, Quantity = 1m,
            ListClientOrderId = "oco-known", StopOrderId = 11, StopClientOrderId = "sl-known",
            TakeProfitOrderId = 12, TakeProfitClientOrderId = "tp-known"
        });
        var openOrders = new List<Order>
        {
            new() { Symbol = "BTCUSDT", BinanceOrderId = 99, ClientOrderId = "ent-orphan" },
            new() { Symbol = "ETHUSDT", BinanceOrderId = 100, ClientOrderId = "manual-order" }
        };
        var openLists = new List<ProtectiveOrderList>
        {
            new() { OrderListId = 20, Symbol = "ETHUSDT" }
        };

        var report = ReconciliationService.Classify(new[] { pos }, openOrders, openLists);

        Assert.Equal(1, report.OrphanOcoLists);
        Assert.Equal(1, report.OrphanOrders);
        Assert.Equal(1, report.ExternalOrders);
        Assert.Equal(1, report.ProtectionMissing);
        Assert.Equal(3, report.TotalIssues);
    }

    [Fact]
    public void Binance_ExecutionReport_Maps_Fill_Fields()
    {
        var report = new BinanceExecutionReportStream
        {
            Symbol = "btcusdt", Side = "SELL", OrderId = 7, ClientOrderId = "sl-abc",
            OrderListId = 3, ExecutionType = "TRADE", OrderStatus = "FILLED",
            LastQuantity = "0.25", LastPrice = "97.5", CumulativeQuantity = "0.25",
            Commission = "0.01", CommissionAsset = "USDT", TransactionTime = 1763942400000
        }.ToExecutionReport();

        Assert.Equal("BTCUSDT", report.Symbol);
        Assert.Equal(OrderSide.Sell, report.Side);
        Assert.True(report.IsFill);
        Assert.True(report.IsTerminal);
        Assert.Equal(0.25m, report.LastQuantity);
        Assert.Equal(97.5m, report.LastPrice);
        Assert.Equal(3, report.OrderListId);
    }

    [Fact]
    public void RiskManager_KillSwitch_Predicate_Follows_Config_And_Limits()
    {
        var config = new TradingConfig
        {
            Symbols = new List<string> { "BTCUSDT" }, MaxDrawdown = 0.10, DailyLossLimit = 0.05,
            KillSwitchAutoCloseOnRiskBreach = true
        };
        var risk = new RiskManager(config, 1000m);
        Assert.False(risk.ShouldEngageKillSwitch);
        risk.UpdateBalance(890m);
        Assert.True(risk.ShouldEngageKillSwitch);
        config.KillSwitchAutoCloseOnRiskBreach = false;
        Assert.False(risk.ShouldEngageKillSwitch);
    }

    [Fact]
    public void BinanceConfig_Live_Uses_Separate_Encrypted_Keys()
    {
        var config = new BinanceConfig
        {
            Environment = "Live", EncryptedApiKey = "shared-key", EncryptedApiSecret = "shared-secret",
            EncryptedLiveApiKey = "live-key", EncryptedLiveApiSecret = "live-secret"
        };
        Assert.Equal("live-key", config.EffectiveEncryptedApiKey);
        Assert.Equal("live-secret", config.EffectiveEncryptedApiSecret);
        config.Environment = "Demo";
        Assert.Equal("shared-key", config.EffectiveEncryptedApiKey);
        Assert.Equal("shared-secret", config.EffectiveEncryptedApiSecret);
    }


    [Fact]
    public void Position_Exchange_Fill_Ledger_Dedups_By_Cumulative()
    {
        var pos = LongPosition();
        pos.RecordExchangeFill("sl-abc", 0.4m);

        Assert.Equal(0m, pos.GetUnappliedExchangeFill("sl-abc", 0.4m)); // повторный report
        Assert.Equal(0.3m, pos.GetUnappliedExchangeFill("sl-abc", 0.7m)); // новый частичный филл
        pos.RecordExchangeFill("sl-abc", 0.7m);
        Assert.Equal(0m, pos.GetUnappliedExchangeFill("sl-abc", 0.6m)); // устаревший кумулятив
        Assert.Equal(0.3m, pos.GetUnappliedExchangeFill("sl-abc", 1.0m));
    }

    [Fact]
    public void Reconciliation_ToReport_Uses_Cumulative_Not_LastTick()
    {
        var order = new Order
        {
            Symbol = "BTCUSDT",
            Side = OrderSide.Sell,
            ClientOrderId = "sl-known",
            BinanceOrderId = 11,
            Status = OrderStatus.PartiallyFilled,
            ExecutedQuantity = 0.7m,
            FilledQuoteQuantity = 68.25m,
            Price = 98m,
            Commission = 0.02m
        };

        var report = ReconciliationService.ToReport(order, "ExchangeStopLoss");

        Assert.True(report.IsFill);
        Assert.Equal(0.7m, report.CumulativeQuantity);
        Assert.Equal(68.25m, report.CumulativeQuoteQuantity);
        Assert.Equal("PARTIALLY_FILLED", report.OrderStatus);
        Assert.Equal(97.5m, report.LastPrice); // average fill, not trigger price
    }

    [Fact]
    public void TradingConfig_Validates_UserDataStream_HealthCheck()
    {
        var config = new TradingConfig { Symbols = new List<string> { "BTCUSDT" } };
        config.UserDataStreamHealthCheckSeconds = 15;
        config.Validate();

        config.UserDataStreamHealthCheckSeconds = 1;
        Assert.Throws<InvalidOperationException>(() => config.Validate());
    }

}
