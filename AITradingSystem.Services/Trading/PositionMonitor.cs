using System.Collections.Concurrent;
using AITradingSystem.Common.Configuration;
using AITradingSystem.Common.Logging;
using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.MarketData;
using AITradingSystem.Core.Models.Trading;
using AITradingSystem.Core.RiskManagement;
using AITradingSystem.Services.Monitoring;
using Microsoft.Extensions.Logging;

namespace AITradingSystem.Services.Trading;

/// <summary>
/// P0.2 — независимый контур сопровождения позиций.
///
/// Раньше SL/TP/trailing/защита проверялись внутри цикла анализа: один последовательный
/// проход по 12 парам с инференсом LLM занимал ~14 минут, т.е. позиция пересматривалась
/// раз в ~7 минут. Здесь сопровождение живёт отдельно:
///  - тики приходят из WebSocket (kline ~2 с, partial-depth 1 с) и НЕ блокируют receive-loop:
///    обработчики только пишут последний тик в словарь (latest-wins);
///  - собственный цикл раз в <see cref="TradingConfig.PositionMonitorIntervalMs"/> мс
///    запускает обработку по каждому символу отдельной задачей (single-flight на позицию),
///    поэтому медленный REST по одной паре не задерживает остальные;
///  - в контуре НЕТ ни одного обращения к LLM/новостям — только цена, SL/TP, защита и трейлинг;
///  - закрытие идёт через тот же пайплайн движка (колбэк), а не своим путём.
///
/// Гонки с циклом анализа (ИИ решил закрыть / ручное закрытие / вход) исключены
/// посимвольным шлюзом <see cref="LockSymbolAsync"/>: критические секции короткие,
/// инференс модели выполняется ВНЕ шлюза.
/// </summary>
public sealed class PositionMonitor : IAsyncDisposable
{
    /// <summary>Последний тик по символу: цена сделки и верх стакана.</summary>
    public readonly record struct MarketTick(decimal Last, decimal Bid, decimal Ask, DateTime UpdatedAtUtc);

    private readonly TradingConfig _config;
    private readonly PositionManager _positionManager;
    private readonly IOrderExecutor _orderExecutor;
    private readonly RiskManager _riskManager;
    private readonly MetricsCollector _metrics;
    private readonly ILogger<PositionMonitor> _logger;

    private readonly ConcurrentDictionary<string, MarketTick> _ticks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _symbolGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _lastCheckUtc = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _lastProcessedTickUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _lastProtectionAttemptUtc = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _lastLatencyWarningUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<long, Task> _workers = new();

    private long _workerSeq;
    private int _running;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>Закрытие позиции через пайплайн движка: (позиция, причина, цена выхода, токен).</summary>
    private Func<Position, string, decimal?, CancellationToken, Task>? _closePosition;
    private Func<string, OHLCV?>? _candleAccessor;
    private Func<string, decimal?>? _atrAccessor;

    private long _checksProcessed;
    private double _maxLatencyMs;

    /// <summary>Сколько проверок сопровождения выполнено с момента старта.</summary>
    public long ChecksProcessed => Interlocked.Read(ref _checksProcessed);

    /// <summary>Задержка «тик пришёл → проверка началась», мс (последняя и худшая).</summary>
    public double LastLatencyMs { get; private set; }
    public double MaxLatencyMs => Volatile.Read(ref _maxLatencyMs);

    public PositionMonitor(
        TradingConfig config,
        PositionManager positionManager,
        IOrderExecutor orderExecutor,
        RiskManager riskManager,
        MetricsCollector metrics,
        ILogger<PositionMonitor> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _positionManager = positionManager ?? throw new ArgumentNullException(nameof(positionManager));
        _orderExecutor = orderExecutor ?? throw new ArgumentNullException(nameof(orderExecutor));
        _riskManager = riskManager ?? throw new ArgumentNullException(nameof(riskManager));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Подключить колбэки движка. Вызывается до <see cref="Start"/>.
    /// Колбэк закрытия обязан сам брать посимвольный шлюз (движок это делает).
    /// </summary>
    public void Configure(
        Func<Position, string, decimal?, CancellationToken, Task> closePosition,
        Func<string, OHLCV?> candleAccessor,
        Func<string, decimal?> atrAccessor)
    {
        _closePosition = closePosition ?? throw new ArgumentNullException(nameof(closePosition));
        _candleAccessor = candleAccessor ?? throw new ArgumentNullException(nameof(candleAccessor));
        _atrAccessor = atrAccessor ?? throw new ArgumentNullException(nameof(atrAccessor));
    }

    // === Приём тиков: вызываются из WebSocket-потока, поэтому только запись в словарь ===

    public void OnKlineTick(OHLCV candle)
    {
        if (candle is null || candle.Close <= 0 || string.IsNullOrEmpty(candle.Symbol))
            return;

        var now = DateTime.UtcNow;
        _ticks.AddOrUpdate(
            candle.Symbol,
            _ => new MarketTick(candle.Close, 0m, 0m, now),
            (_, prev) => prev with { Last = candle.Close, UpdatedAtUtc = now });
    }

    public void OnOrderBookTick(OrderBook orderBook)
    {
        if (orderBook is null || string.IsNullOrEmpty(orderBook.Symbol))
            return;

        var bid = orderBook.BestBid ?? 0m;
        var ask = orderBook.BestAsk ?? 0m;
        if (bid <= 0 && ask <= 0)
            return;

        var now = DateTime.UtcNow;
        _ticks.AddOrUpdate(
            orderBook.Symbol,
            _ => new MarketTick(0m, bid, ask, now),
            (_, prev) => prev with { Bid = bid, Ask = ask, UpdatedAtUtc = now });
    }

    /// <summary>
    /// Посимвольный шлюз: короткие критические секции с позицией (закрытие, вход+защита).
    /// Инференс LLM под шлюзом держать нельзя — иначе контур снова встанет на минуты.
    /// </summary>
    public async Task<IDisposable> LockSymbolAsync(string symbol, CancellationToken ct = default)
    {
        var gate = _symbolGates.GetOrAdd(symbol, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new GateReleaser(gate);
    }

    public void Start(CancellationToken ct)
    {
        if (_closePosition is null)
            throw new InvalidOperationException("PositionMonitor.Configure не вызван до Start.");

        if (Interlocked.Exchange(ref _running, 1) == 1)
            return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => LoopAsync(_cts.Token));

        TradeLog.Info("MONITOR-START", null,
            $"tick_interval_ms={_config.PositionMonitorIntervalMs} sweep_s={_config.PositionMonitorSweepSeconds} " +
            $"max_latency_ms={_config.PositionMonitorMaxLatencyMs}");
    }

    public async Task StopAsync()
    {
        Interlocked.Exchange(ref _running, 0);

        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { /* уже освобождён предыдущей остановкой */ }

        if (_loop != null)
        {
            try { await _loop; }
            catch (OperationCanceledException) { /* ожидаемо */ }
            catch (Exception ex) { _logger.LogWarning(ex, "Контур сопровождения завершился с ошибкой"); }
        }

        var pending = _workers.Values.ToArray();
        if (pending.Length > 0)
        {
            try { await Task.WhenAll(pending); }
            catch (OperationCanceledException) { /* ожидаемо */ }
            catch (Exception ex) { _logger.LogWarning(ex, "Ошибка завершения задач сопровождения"); }
        }

        _workers.Clear();
        _cts?.Dispose();
        _cts = null;
        _loop = null;

        TradeLog.Info("MONITOR-STOP", null,
            $"checks={ChecksProcessed} latency_last_ms={LogOptions.N(LastLatencyMs)} latency_max_ms={LogOptions.N(MaxLatencyMs)}");
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMilliseconds(_config.PositionMonitorIntervalMs);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                DispatchPendingPositions(ct);
                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка цикла сопровождения позиций");
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>
    /// Раздаёт работу по позициям. Сама не делает сетевых вызовов и не ждёт исполнения:
    /// по каждой позиции не более одной задачи в полёте (single-flight).
    /// </summary>
    private void DispatchPendingPositions(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var sweep = TimeSpan.FromSeconds(_config.PositionMonitorSweepSeconds);

        foreach (var position in _positionManager.GetOpenPositions())
        {
            if (ct.IsCancellationRequested)
                break;

            if (position.NeedsManualDustReview)
                continue;

            var hasTick = _ticks.TryGetValue(position.Symbol, out var tick);
            var freshTick = hasTick &&
                            (!_lastProcessedTickUtc.TryGetValue(position.Symbol, out var processed) ||
                             tick.UpdatedAtUtc > processed);

            var sweepDue = !_lastCheckUtc.TryGetValue(position.Id, out var lastCheck) ||
                           now - lastCheck >= sweep;

            if (!freshTick && !sweepDue)
                continue;

            if (!_inFlight.TryAdd(position.Id, 0))
                continue; // предыдущая проверка этой позиции ещё идёт

            var captured = position;
            var capturedTick = tick;
            var task = Task.Run(() => ProcessPositionAsync(captured, capturedTick, hasTick, ct), CancellationToken.None);

            var id = Interlocked.Increment(ref _workerSeq);
            _workers[id] = task;
            _ = task.ContinueWith(
                t => _workers.TryRemove(id, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task ProcessPositionAsync(Position position, MarketTick tick, bool hasTick, CancellationToken ct)
    {
        string? closeReason = null;
        decimal? exitPrice = null;

        try
        {
            var gate = _symbolGates.GetOrAdd(position.Symbol, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                // Позицию могли закрыть, пока ждали шлюз (ИИ/ручное закрытие/биржевой стоп).
                var live = _positionManager.GetPositionById(position.Id);
                if (live is null)
                    return;

                position = live;
                if (position.NeedsManualDustReview)
                    return;
                _lastCheckUtc[position.Id] = DateTime.UtcNow;
                Interlocked.Increment(ref _checksProcessed);

                var price = hasTick ? ResolveExitPrice(position.Side, tick) : 0m;
                if (price > 0m)
                {
                    position.UpdatePrice(price);
                    _lastProcessedTickUtc[position.Symbol] = tick.UpdatedAtUtc;
                }

                if (hasTick)
                    RecordLatency(position.Symbol, tick.UpdatedAtUtc);

                // 1. SL/TP — по текущему тику (и по экстремумам свечи, если она началась после входа).
                var candle = _candleAccessor?.Invoke(position.Symbol);
                var (slHit, tpHit) = EvaluateExits(position, price, candle);

                if (slHit)
                {
                    closeReason = "Stop-Loss";
                    exitPrice = position.StopLoss;
                }
                else if (tpHit)
                {
                    closeReason = "Take-Profit";
                    exitPrice = position.TakeProfit;
                }

                // 2. Биржевая защита (P0.1): позиция из positions.json без OCO должна её получить.
                if (closeReason is null && position.Protection is null && ProtectionAttemptDue(position.Id))
                {
                    _lastProtectionAttemptUtc[position.Id] = DateTime.UtcNow;

                    if (await _orderExecutor.EnsureProtectionAsync(position, ct))
                    {
                        await _positionManager.SavePositionsAsync(ct);
                    }
                    else if (FailClosedWithoutProtection)
                    {
                        TradeLog.Rejected(position.Symbol, "protection",
                            "OCO не поставлена: закрываем позицию, чтобы капитал не зависел от живого процесса");
                        closeReason = "NoExchangeProtection";
                    }
                    else
                    {
                        TradeLog.Warn("PROTECTION-LOCAL-ONLY", position.Symbol,
                            "биржевой защиты нет, работает только локальный SL (Trading:ClosePositionIfProtectionFails=false)",
                            gateKey: "protection-local-only:" + position.Symbol);
                    }
                }

                // 3. Трейлинг — cancel/replace биржевой связки; частоту режет сам executor
                //    (TrailingReplaceMinIntervalSeconds / TrailingReplaceMinStepPercent).
                if (closeReason is null && (_config.EnableTrailingStop || _config.BreakEvenEnabled))
                    await TryTrailAsync(position, price, ct);
            }
            finally
            {
                gate.Release();
            }

            // Закрытие — ВНЕ шлюза: колбэк движка берёт его сам (иначе самоблокировка).
            if (closeReason != null && _closePosition != null)
                await _closePosition(position, closeReason, exitPrice, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Остановка движка
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка сопровождения позиции {Symbol}", position.Symbol);
        }
        finally
        {
            _inFlight.TryRemove(position.Id, out _);
        }
    }

    private async Task TryTrailAsync(Position position, decimal tickPrice, CancellationToken ct)
    {
        var reference = position.CurrentPrice > 0 ? position.CurrentPrice : tickPrice;
        if (reference <= 0)
            return;

        var atr = _atrAccessor?.Invoke(position.Symbol);
        var atrAbsolute = atr is > 0m ? atr.Value : reference * 0.02m; // fallback как в цикле анализа

        var newStopLoss = _config.EnableTrailingStop
            ? _riskManager.CalculateTrailingStop(position.Side, reference, atrAbsolute)
            : position.StopLoss;

        // P1.2: безубыток переносит стоп на цену, покрывающую комиссии обеих ног (+буфер),
        // только после достижения порога net прибыли относительно начального net риска.
        if (_config.BreakEvenEnabled && _riskManager.IsBreakEvenActivated(position, reference))
        {
            var breakEven = _riskManager.CalculateBreakEvenPrice(position.Side, position.EntryPrice);
            var buffer = position.EntryPrice * _config.BreakEvenBufferPercent / 100m;
            var breakEvenStop = position.Side == OrderSide.Buy ? breakEven + buffer : breakEven - buffer;

            newStopLoss = position.Side == OrderSide.Buy
                ? Math.Max(newStopLoss, breakEvenStop)
                : Math.Min(newStopLoss, breakEvenStop);

            // Не ставим стоп хуже/равный текущей цене для long или выше/равный для short.
            if ((position.Side == OrderSide.Buy && newStopLoss >= reference) ||
                (position.Side == OrderSide.Sell && newStopLoss <= reference))
                newStopLoss = position.StopLoss;
        }

        if (!await _orderExecutor.UpdateStopLossAsync(position, newStopLoss, ct))
            return;

        TradeLog.Info("TRAILING-STOP", position.Symbol,
            $"sl={LogOptions.N(newStopLoss)} protection={position.Protection?.ToString() ?? "локальный SL"}");

        // Персистим сразу: подтянутый стоп и id ног должны пережить рестарт (NFR-2.2).
        await _positionManager.SavePositionsAsync(ct);
    }

    private bool FailClosedWithoutProtection =>
        _config.ClosePositionIfProtectionFails &&
        _config.ExchangeProtectionEnabled &&
        !_config.PaperTradingMode;

    /// <summary>Повторная попытка поставить OCO не чаще раза в ProtectionRetryIntervalSeconds.</summary>
    private bool ProtectionAttemptDue(string positionId) =>
        !_lastProtectionAttemptUtc.TryGetValue(positionId, out var last) ||
        DateTime.UtcNow - last >= TimeSpan.FromSeconds(_config.ProtectionRetryIntervalSeconds);

    private void RecordLatency(string symbol, DateTime tickTimestampUtc)
    {
        var latencyMs = Math.Max(0d, (DateTime.UtcNow - tickTimestampUtc).TotalMilliseconds);
        LastLatencyMs = latencyMs;
        var max = Volatile.Read(ref _maxLatencyMs);
        while (latencyMs > max)
        {
            var prev = Interlocked.CompareExchange(ref _maxLatencyMs, latencyMs, max);
            if (prev == max) break;
            max = prev;
        }

        _metrics.RecordPositionCheck(latencyMs);

        if (latencyMs <= _config.PositionMonitorMaxLatencyMs)
            return;

        // Не чаще раза в 30 с на символ: иначе лог зальёт при тонком рынке.
        if (_lastLatencyWarningUtc.TryGetValue(symbol, out var last) &&
            DateTime.UtcNow - last < TimeSpan.FromSeconds(30))
            return;

        _lastLatencyWarningUtc[symbol] = DateTime.UtcNow;
        TradeLog.Decision(symbol, "MONITOR-LAG",
            $"задержка сопровождения {LogOptions.N(latencyMs)} мс > {_config.PositionMonitorMaxLatencyMs} мс", alarm: true);
    }

    /// <summary>
    /// Исполнимая цена выхода: long закрывается SELL'ом → best bid, short — BUY'ем → best ask.
    /// Нет стакана — берём последнюю цену, нет её — середину.
    /// </summary>
    public static decimal ResolveExitPrice(OrderSide side, MarketTick tick)
    {
        var executable = side == OrderSide.Buy ? tick.Bid : tick.Ask;
        if (executable > 0m)
            return executable;

        if (tick.Last > 0m)
            return tick.Last;

        if (tick.Bid > 0m && tick.Ask > 0m)
            return (tick.Bid + tick.Ask) / 2m;

        return Math.Max(tick.Bid, tick.Ask);
    }

    /// <summary>
    /// Проверка SL/TP по тику. Экстремумы текущей свечи учитываются только если она
    /// началась ПОСЛЕ входа: иначе Low/High содержат движения, которых при входе уже не было.
    /// </summary>
    public static (bool StopLossHit, bool TakeProfitHit) EvaluateExits(
        Position position, decimal tickPrice, OHLCV? candle)
    {
        var low = tickPrice > 0m ? tickPrice : decimal.MaxValue;
        var high = tickPrice > 0m ? tickPrice : 0m;

        if (candle != null && !position.CandleStartedBeforeEntry(candle.OpenTime))
        {
            if (candle.Low > 0m) low = Math.Min(low, candle.Low);
            if (candle.High > 0m) high = Math.Max(high, candle.High);
        }

        if (low <= 0m || high <= 0m || low == decimal.MaxValue)
            return (false, false);

        return (position.IsStopLossHit(low, high), position.IsTakeProfitHit(low, high));
    }

    private sealed class GateReleaser : IDisposable
    {
        private SemaphoreSlim? _gate;
        public GateReleaser(SemaphoreSlim gate) => _gate = gate;

        public void Dispose()
        {
            var gate = Interlocked.Exchange(ref _gate, null);
            gate?.Release();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
