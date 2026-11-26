using System.Collections.Concurrent;
using AITradingSystem.Common.Configuration;
using AITradingSystem.Common.Logging;
using AITradingSystem.Core.Models.Trading;
using AITradingSystem.Data.Binance;
using AITradingSystem.Data.Storage;
using AITradingSystem.Services.Monitoring;
using Microsoft.Extensions.Logging;

namespace AITradingSystem.Services.Trading;

/// <summary>
/// Приоритет операции исполнения. Меньше значение — раньше в очереди.
/// Защита и закрытие всегда обгоняют входы: капитал важнее новой сделки.
/// </summary>
public enum ExecutionPriority
{
    /// <summary>Постановка/снятие биржевой защиты (OCO).</summary>
    Protection = 0,

    /// <summary>Закрытие позиции, отмена ордера.</summary>
    Close = 1,

    /// <summary>Cancel/replace защитной связки при трейлинге.</summary>
    Trailing = 2,

    /// <summary>Открытие новой позиции.</summary>
    Entry = 3
}

/// <summary>
/// Маркер результата, восстановленного из журнала намерений после рестарта:
/// реального объекта ордера в памяти нет, его нужно забрать с биржи по clientOrderId.
/// </summary>
public sealed record RestoredExecution(ExecutionIntent Intent);

/// <summary>
/// P0.3 — очередь исполнения с приоритетами и идемпотентностью.
///
/// Что даёт:
///  - защита/закрытие уходят на биржу раньше входов, даже если вход был запрошен первым;
///  - по одному символу не более одной операции в полёте (нет гонок «вход vs закрытие»);
///  - идемпотентность по <c>clientOrderId</c>: повторный запрос с тем же ключом не отправляет
///    второй ордер — либо присоединяется к уже идущему, либо получает сохранённый результат;
///  - <b>durable-журнал намерений</b> (<see cref="IExecutionJournal"/>): ключ переживает рестарт,
///    неподтверждённое намерение сначала сверяется с биржей, а не отправляется вслепую;
///  - <b>ретрай на уровне операции</b>: экспоненциальный backoff с jitter и лимитом попыток,
///    тем же clientOrderId; на время backoff слот символа освобождается, чтобы закрытие
///    другой операции по этой паре не стояло за ретраем входа;
///  - ограничение параллелизма (<see cref="TradingConfig.ExecutionQueueMaxParallel"/>) —
///    задел под бюджет весов запросов (P1.6).
///
/// В очередь кладётся ТОЛЬКО сетевая операция (короткий REST), а не ожидание филла:
/// иначе 60-секундный опрос лимитного входа заблокировал бы закрытие другой позиции.
/// </summary>
public sealed class ExecutionQueue : IAsyncDisposable
{
    private readonly TradingConfig _config;
    private readonly MetricsCollector _metrics;
    private readonly ILogger<ExecutionQueue> _logger;
    private readonly IExecutionJournal? _journal;

    private readonly object _lock = new();
    private readonly List<WorkItem> _pending = new();
    private readonly Dictionary<string, WorkItem> _inFlightByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CachedResult> _completed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _busySymbols = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ExecutionIntent> _unresolved = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly ConcurrentDictionary<long, Task> _workers = new();

    private Func<ExecutionIntent, CancellationToken, Task<object?>>? _intentResolver;
    public ActiveEntryRegistry ActiveEntries { get; } = new();
    private long _seq;
    private long _workerSeq;
    private int _started;
    private int _running;
    private CancellationTokenSource? _cts;
    private Task? _dispatcher;

    /// <summary>Сколько операций ждут отправки прямо сейчас.</summary>
    public int PendingCount { get { lock (_lock) return _pending.Count; } }

    /// <summary>Сколько запросов склеено с уже идущей операцией по тому же ключу.</summary>
    public long CoalescedRequests { get; private set; }

    /// <summary>Сколько запросов получили сохранённый результат вместо повторной отправки.</summary>
    public long ReplayedResults { get; private set; }

    /// <summary>Сколько операций пришлось повторить из-за транзиентного сбоя.</summary>
    public long RetriedOperations { get; private set; }

    /// <summary>Сколько ключей идемпотентности восстановлено из журнала при старте.</summary>
    public long RestoredIntents { get; private set; }

    /// <summary>
    /// Намерения, судьба которых неизвестна (процесс умер между отправкой и ответом).
    /// Список для реконсиляции P0.5 и для алерта оператору.
    /// </summary>
    public IReadOnlyList<ExecutionIntent> UnresolvedIntents
    {
        get { lock (_lock) return _unresolved.ToList(); }
    }

    public ExecutionQueue(
        TradingConfig config,
        MetricsCollector metrics,
        ILogger<ExecutionQueue> logger,
        IExecutionJournal? journal = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _journal = journal;
    }

    /// <summary>
    /// Зарегистрировать сверку восстановленного намерения с биржей.
    /// Возвращает актуальный объект ордера/связки либо null, если биржа о нём не знает
    /// (тогда ключ снимается и ордер можно отправить). Бросить исключение = «судьба неизвестна»,
    /// повторная отправка в этом случае запрещена (fail-closed).
    /// </summary>
    public void SetIntentResolver(Func<ExecutionIntent, CancellationToken, Task<object?>> resolver)
        => _intentResolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    /// <summary>
    /// Восстановить ключи идемпотентности из журнала (вызывается на старте движка,
    /// до первой торговой операции). Подтверждённые ключи в окне TTL блокируют повторную
    /// отправку; неподтверждённые помечаются как требующие сверки с биржей.
    /// </summary>
    public async Task<int> RestoreAsync(CancellationToken ct = default)
    {
        if (_journal is null)
            return 0;

        var intents = await _journal.LoadAsync(ct).ConfigureAwait(false);
        var ttl = TimeSpan.FromSeconds(_config.ExecutionIdempotencyTtlSeconds);
        var now = DateTime.UtcNow;
        var restored = 0;
        var unresolved = 0;

        lock (_lock)
        {
            foreach (var intent in intents)
            {
                if (string.IsNullOrWhiteSpace(intent.ClientOrderId))
                    continue;

                // Отклонённые биржей ключи можно переиспользовать — их не восстанавливаем.
                if (intent.Status == ExecutionIntentStatus.Failed)
                    continue;

                // Подтверждённые старше TTL уже неинтересны; незавершённые ждём вечно.
                if (!intent.IsUnresolved && now - intent.UpdatedAtUtc > ttl)
                    continue;

                _completed[intent.ClientOrderId] = new CachedResult(
                    new RestoredExecution(intent),
                    intent.IsUnresolved ? now : intent.UpdatedAtUtc,
                    Failed: false);

                if (intent.IsUnresolved && _unresolved.All(i => i.ClientOrderId != intent.ClientOrderId))
                {
                    _unresolved.Add(intent);
                    unresolved++;
                    TradeLog.Decision(intent.Symbol, "EXEC-INTENT-UNRESOLVED",
                        $"{intent.ClientOrderId} ({intent.Operation}): отправка не подтверждена до рестарта — сверка с биржей перед повтором");
                }

                restored++;
            }

            RestoredIntents = restored;
        }

        if (restored > 0)
        {
            TradeLog.Info("EXEC-JOURNAL-RESTORED", null, $"keys={restored} unresolved={unresolved}");
        }

        await _journal.PruneAsync(ttl, ct).ConfigureAwait(false);
        return restored;
    }

    /// <summary>
    /// Забыть ключ: ордер/связка отменены локально, повторная постановка с тем же ключом легитимна
    /// (cancel/replace трейлинга, снятие защиты перед market-закрытием).
    /// </summary>
    public async Task ForgetKeyAsync(string symbol, string? key, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        lock (_lock)
        {
            _completed.Remove(key);
            _unresolved.RemoveAll(i => i.ClientOrderId == key);
        }

        if (_journal != null)
        {
            await _journal.RecordAsync(new ExecutionIntent
            {
                ClientOrderId = key,
                Symbol = symbol,
                Operation = "Canceled",
                Status = ExecutionIntentStatus.Failed,
                Error = reason
            }, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Поставить операцию в очередь. <paramref name="idempotencyKey"/> — clientOrderId ордера
    /// (или listClientOrderId связки): null означает операцию без дедупликации (чтение, cancel).
    /// </summary>
    public async Task<T> SubmitAsync<T>(
        string symbol,
        ExecutionPriority priority,
        string? idempotencyKey,
        Func<CancellationToken, Task<T>> work,
        CancellationToken ct = default)
    {
        if (work is null) throw new ArgumentNullException(nameof(work));
        ct.ThrowIfCancellationRequested();

        var result = await SubmitCoreAsync(
            symbol, priority, idempotencyKey,
            async token => (object?)await work(token).ConfigureAwait(false),
            ct).ConfigureAwait(false);

        return result is T typed ? typed : default!;
    }

    private async Task<object?> SubmitCoreAsync(
        string symbol,
        ExecutionPriority priority,
        string? key,
        Func<CancellationToken, Task<object?>> work,
        CancellationToken ct)
    {
        EnsureStarted();
        PurgeExpired();

        // Ключ восстановлен из журнала: сначала выясняем судьбу ордера на бирже.
        if (key != null)
        {
            ExecutionIntent? restoredIntent = null;
            lock (_lock)
            {
                if (_completed.TryGetValue(key, out var restored) &&
                    !restored.Failed &&
                    restored.Result is RestoredExecution marker)
                {
                    restoredIntent = marker.Intent;
                }
            }

            if (restoredIntent != null)
            {
                var (found, result) = await ResolveRestoredIntentAsync(restoredIntent, ct).ConfigureAwait(false);
                if (found)
                {
                    return result;
                }

                lock (_lock)
                {
                    _completed.Remove(key);
                    _unresolved.RemoveAll(i => i.ClientOrderId == key);
                }
            }
        }

        // Дедупликация под локом: готовый результат, ожидание уже идущей операции либо новая постановка.
        Task<object?>? joined = null;
        object? replay = null;
        var hasReplay = false;
        WorkItem item;

        lock (_lock)
        {
            if (key != null)
            {
                // Уже отправлено и успешно завершено — второй ордер не шлём.
                if (_completed.TryGetValue(key, out var cached) && !cached.Failed)
                {
                    ReplayedResults++;
                    TradeLog.Decision(symbol, "IDEMPOTENT-REPLAY",
                        $"{key}: результат взят из кэша, ордер повторно не отправляется");
                    hasReplay = true;
                    replay = cached.Result;
                }
                // Ещё в полёте (в т.ч. на backoff между попытками) — присоединяемся к тому же результату.
                else if (_inFlightByKey.TryGetValue(key, out var running))
                {
                    CoalescedRequests++;
                    TradeLog.Decision(symbol, "IDEMPOTENT-JOIN", $"{key}: запрос склеен с уже идущей отправкой");
                    joined = running.Completion.Task;
                }
            }

            item = new WorkItem(
                symbol, priority, key, work,
                Interlocked.Increment(ref _seq),
                DateTime.UtcNow,
                new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously))
            {
                CallerToken = ct
            };

            if (!hasReplay && joined is null)
            {
                if (key != null)
                    _inFlightByKey[key] = item;

                if (priority == ExecutionPriority.Entry && key != null)
                    ActiveEntries.Track(symbol, key, _config, item.EnqueuedAtUtc);
                InsertByPriority(item);
            }
        }

        if (hasReplay)
            return replay;

        if (joined != null)
            return await joined.ConfigureAwait(false);

        // Отмена вызывающего (стоп движка) снимает ещё не отправленный ордер с очереди.
        if (ct.CanBeCanceled)
        {
            ct.Register(() =>
            {
                lock (_lock)
                {
                    if (_pending.Remove(item))
                    {
                        if (key != null) ActiveEntries.Remove(key);
                        item.Completion.TrySetCanceled(ct);
                    }
                }
            });
        }

        _signal.Release();
        return await item.Completion.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Сверка восстановленного намерения с биржей. Неизвестный бирже ордер снимает ключ,
    /// известный — превращается в обычный кэш результата (повтор не отправляется).
    /// </summary>
    private async Task<(bool Found, object? Result)> ResolveRestoredIntentAsync(
        ExecutionIntent intent, CancellationToken ct)
    {
        if (_intentResolver is null)
        {
            throw new InvalidOperationException(
                $"Намерение {intent.ClientOrderId} восстановлено из журнала, но сверка с биржей не зарегистрирована — " +
                "повторная отправка запрещена (риск дубля)");
        }

        // Исключения намеренно не глотаем: пока судьба ордера неизвестна, слать второй нельзя.
        var result = await _intentResolver(intent, ct).ConfigureAwait(false);

        if (result is null)
        {
            TradeLog.Decision(intent.Symbol, "EXEC-INTENT-CLEARED",
                $"{intent.ClientOrderId}: биржа об ордере не знает — ключ идемпотентности снят");
            return (false, null);
        }

        lock (_lock)
        {
            _completed[intent.ClientOrderId] = new CachedResult(result, DateTime.UtcNow, Failed: false);
            _unresolved.RemoveAll(i => i.ClientOrderId == intent.ClientOrderId);
            ReplayedResults++;
        }

        TradeLog.Decision(intent.Symbol, "EXEC-INTENT-RESOLVED",
            $"{intent.ClientOrderId}: ордер найден на бирже, берём его состояние вместо повторной отправки");

        if (_journal != null && intent.Status != ExecutionIntentStatus.Confirmed)
        {
            intent.Status = ExecutionIntentStatus.Confirmed;
            intent.Error = null;
            await _journal.RecordAsync(intent, ct).ConfigureAwait(false);
        }

        return (true, result);
    }

    /// <summary>Вставка с сохранением порядка (приоритет, затем FIFO внутри приоритета).</summary>
    private void InsertByPriority(WorkItem item)
    {
        var index = _pending.Count;
        while (index > 0 &&
               (_pending[index - 1].Priority > item.Priority ||
                (_pending[index - 1].Priority == item.Priority && _pending[index - 1].Seq > item.Seq)))
        {
            index--;
        }

        _pending.Insert(index, item);

        _metrics.RecordExecutionQueueDepth(_pending.Count);
    }

    private void EnsureStarted()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            return;

        _cts = new CancellationTokenSource();
        _dispatcher = Task.Run(() => DispatchLoopAsync(_cts.Token));
        TradeLog.Info("EXEC-QUEUE-START", null,
            $"parallel={_config.ExecutionQueueMaxParallel} idempotency_ttl_s={_config.ExecutionIdempotencyTtlSeconds} " +
            $"attempts={_config.ExecutionRetryMaxAttempts} journal={(_journal != null ? "on" : "off")}");
    }

    private async Task DispatchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(ct).ConfigureAwait(false);
                Dispatch(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка диспетчера очереди исполнения");
            }
        }

        CancelPending();
    }

    private void Dispatch(CancellationToken ct)
    {
        while (true)
        {
            WorkItem? next = null;
            lock (_lock)
            {
                if (_running >= Math.Max(1, _config.ExecutionQueueMaxParallel))
                    return;

                var now = DateTime.UtcNow;
                for (var i = 0; i < _pending.Count; i++)
                {
                    var candidate = _pending[i];
                    if (candidate.Completion.Task.IsCompleted)
                    {
                        _pending.RemoveAt(i--);
                        continue;
                    }

                    // По символу — строго одна операция в полёте.
                    if (_busySymbols.Contains(candidate.Symbol))
                        continue;

                    // Ретрай ждёт свой backoff — слот символа при этом свободен.
                    if (candidate.NotBeforeUtc > now)
                        continue;

                    _pending.RemoveAt(i);
                    _busySymbols.Add(candidate.Symbol);
                    _running++;
                    next = candidate;
                    break;
                }
            }

            if (next is null)
                return;

            var item = next;
            var task = Task.Run(() => RunAsync(item, ct), CancellationToken.None);
            // Уникальный id на каждую попытку: ретрай не должен сниматься с учёта чужим continuation.
            var id = Interlocked.Increment(ref _workerSeq);
            _workers[id] = task;
            _ = task.ContinueWith(
                t => _workers.TryRemove(id, out _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task RunAsync(WorkItem item, CancellationToken queueToken)
    {
        var waitMs = (DateTime.UtcNow - item.EnqueuedAtUtc).TotalMilliseconds;
        _metrics.RecordExecutionWait(waitMs);

        if (waitMs > _config.ExecutionQueueWarnWaitMs)
        {
            TradeLog.Decision(item.Symbol, "QUEUE-WAIT",
                $"{item.Priority} ждал {LogOptions.N(waitMs)} мс при пороге {_config.ExecutionQueueWarnWaitMs} мс", alarm: true);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(queueToken, item.CallerToken);
        item.Attempts++;

        // Write-ahead: намерение фиксируется ДО сетевого вызова, иначе рестарт между
        // отправкой и ответом оставил бы ордер-невидимку.
        var intent = item.Intent;
        if (item.Key != null && _journal != null)
        {
            intent ??= new ExecutionIntent
            {
                ClientOrderId = item.Key,
                Symbol = item.Symbol,
                Operation = item.Priority.ToString(),
                CreatedAtUtc = item.EnqueuedAtUtc
            };
            item.Intent = intent;
            intent.Attempts = item.Attempts;
            intent.Status = ExecutionIntentStatus.Pending;
            if (item.Priority == ExecutionPriority.Entry && item.Key != null) ActiveEntries.Update(item.Key, intent.Status);
            await TryJournalAsync(intent).ConfigureAwait(false);
        }

        object? result = null;
        Exception? error = null;

        try
        {
            result = await item.Work(linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        var maxAttempts = Math.Max(1, _config.ExecutionRetryMaxAttempts);
        if (error != null && item.Attempts < maxAttempts && IsTransient(error) && !linked.IsCancellationRequested)
        {
            var delay = RetryDelay(item.Attempts);
            item.NotBeforeUtc = DateTime.UtcNow + delay;

            lock (_lock)
            {
                // Слот символа и общий слот освобождаем: закрытие не должно ждать backoff входа.
                _busySymbols.Remove(item.Symbol);
                _running--;
                InsertByPriority(item); // Seq прежний — место в FIFO не теряется
            }

            RetriedOperations++;
            _metrics.RecordExecutionRetry();

            if (intent != null)
            {
                intent.Error = error.Message;
                await TryJournalAsync(intent).ConfigureAwait(false);
            }

            TradeLog.Decision(item.Symbol, "QUEUE-RETRY",
                $"{item.Priority}: попытка {item.Attempts}/{maxAttempts} — {error.Message}; " +
                $"повтор через {LogOptions.N(delay)} мс, ключ {item.Key ?? "-"}", alarm: true);

            // Разбудить диспетчер, когда backoff истечёт (и сразу — освободились слоты).
            _ = Task.Delay(delay, CancellationToken.None).ContinueWith(
                _ => { try { _signal.Release(); } catch (ObjectDisposedException) { /* очередь остановлена */ } },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            _signal.Release();
            return; // Completion не завершён — вызывающий продолжает ждать
        }

        lock (_lock)
        {
            _busySymbols.Remove(item.Symbol);
            _running--;

            if (item.Key != null)
            {
                _inFlightByKey.Remove(item.Key);

                // Кэшируем только успех: отказ можно повторить тем же clientOrderId.
                if (error is null)
                    _completed[item.Key] = new CachedResult(result, DateTime.UtcNow, Failed: false);
            }
        }

        if (intent != null)
        {
            intent.Status = error is null ? ExecutionIntentStatus.Confirmed : ExecutionIntentStatus.Failed;
            if (item.Priority == ExecutionPriority.Entry && item.Key != null) ActiveEntries.Update(item.Key, intent.Status);
            intent.Error = error?.Message;
            await TryJournalAsync(intent).ConfigureAwait(false);
        }

        if (item.Priority == ExecutionPriority.Entry && item.Key != null && result is Order entryOrder)
            ActiveEntries.Observe(item.Key, entryOrder);

        _signal.Release(); // освободившийся слот/символ — повод пересмотреть очередь

        if (error is not null)
            item.Completion.TrySetException(error);
        else
            item.Completion.TrySetResult(result);
    }

    private async Task TryJournalAsync(ExecutionIntent intent)
    {
        if (_journal is null) return;

        try
        {
            await _journal.RecordAsync(intent, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            TradeLog.Error("EXEC-JOURNAL-WRITE-FAIL", intent.Symbol, $"не удалось зафиксировать {intent.ClientOrderId}", ex);
        }
    }

    /// <summary>Экспоненциальный backoff с полным jitter — против синхронных ретраев всех пар.</summary>
    private TimeSpan RetryDelay(int attempt)
    {
        var baseMs = Math.Max(1, _config.ExecutionRetryBaseDelayMs);
        var maxMs = Math.Max(baseMs, _config.ExecutionRetryMaxDelayMs);
        var ceiling = Math.Min(maxMs, baseMs * Math.Pow(2, attempt - 1));
        return TimeSpan.FromMilliseconds(Math.Max(1, Random.Shared.NextDouble() * ceiling));
    }

    /// <summary>
    /// Транзиентный сбой — есть смысл повторить с тем же clientOrderId.
    /// Отказ по существу (недостаточно средств, post-only, фильтры) не ретраится.
    /// </summary>
    private static bool IsTransient(Exception error) => error switch
    {
        BinanceApiException api => api.IsTransient,
        OperationCanceledException oce => oce.InnerException is TimeoutException,
        HttpRequestException => true,
        TimeoutException => true,
        _ => false
    };

    private void PurgeExpired()
    {
        var ttl = TimeSpan.FromSeconds(_config.ExecutionIdempotencyTtlSeconds);
        var now = DateTime.UtcNow;

        lock (_lock)
        {
            if (_completed.Count == 0)
                return;

            var expired = _completed
                .Where(kv => now - kv.Value.CompletedAtUtc > ttl &&
                             !(kv.Value.Result is RestoredExecution restored && restored.Intent.IsUnresolved))
                .Select(kv => kv.Key)
                .ToList();

            foreach (var key in expired)
                _completed.Remove(key);
        }
    }

    private void CancelPending()
    {
        lock (_lock)
        {
            foreach (var item in _pending)
                item.Completion.TrySetCanceled();

            _pending.Clear();
        }
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
            return;

        _cts?.Cancel();

        if (_dispatcher != null)
        {
            try { await _dispatcher.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* ожидаемо */ }
        }

        var pending = _workers.Values.ToArray();
        if (pending.Length > 0)
        {
            try { await Task.WhenAll(pending).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* ожидаемо */ }
            catch (Exception ex) { _logger.LogWarning(ex, "Ошибка завершения операций очереди исполнения"); }
        }

        _workers.Clear();
        CancelPending();
        _cts?.Dispose();
        _cts = null;
        _dispatcher = null;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private sealed record CachedResult(object? Result, DateTime CompletedAtUtc, bool Failed);

    private sealed class WorkItem
    {
        public WorkItem(
            string symbol,
            ExecutionPriority priority,
            string? key,
            Func<CancellationToken, Task<object?>> work,
            long seq,
            DateTime enqueuedAtUtc,
            TaskCompletionSource<object?> completion)
        {
            Symbol = symbol;
            Priority = priority;
            Key = key;
            Work = work;
            Seq = seq;
            EnqueuedAtUtc = enqueuedAtUtc;
            Completion = completion;
        }

        public string Symbol { get; }
        public ExecutionPriority Priority { get; }
        public string? Key { get; }
        public Func<CancellationToken, Task<object?>> Work { get; }
        public long Seq { get; }
        public DateTime EnqueuedAtUtc { get; }
        public TaskCompletionSource<object?> Completion { get; }
        public CancellationToken CallerToken { get; set; }

        /// <summary>Номер текущей попытки (1 = первая отправка).</summary>
        public int Attempts { get; set; }

        /// <summary>Раньше этого момента операция не отправляется (backoff ретрая).</summary>
        public DateTime NotBeforeUtc { get; set; }

        /// <summary>Запись журнала, привязанная к операции (переиспользуется между попытками).</summary>
        public ExecutionIntent? Intent { get; set; }
    }
}
