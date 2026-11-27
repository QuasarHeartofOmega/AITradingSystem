using AITradingSystem.Common.Configuration;
using AITradingSystem.Common.Logging;
using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.MarketData;
using AITradingSystem.Core.Models.Trading;
using AITradingSystem.Core.RiskManagement;
using AITradingSystem.Data.Binance;
using AITradingSystem.Data.Storage;
using AITradingSystem.Services.AI;
using AITradingSystem.Services.Analysis;
using AITradingSystem.Services.Monitoring;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Globalization;

namespace AITradingSystem.Services.Trading;

/// <summary>
/// Центральный движок торговли — координирует все компоненты (FR-1 — FR-8).
/// </summary>
public sealed class TradingEngine : IAsyncDisposable
{
    private readonly AppConfig _config;
    private readonly IBinanceWebSocketClient _wsClient;
    private readonly IBinanceRestClient _restClient;
    private readonly IAIModel _aiModel;
    private readonly MarketAnalyzer _marketAnalyzer;
    private readonly SentimentAnalyzer _sentimentAnalyzer;
    private readonly SignalCalibrator _calibrator;
    private readonly RiskManager _riskManager;
    private readonly PortfolioRiskManager _portfolioRisk;
    private readonly PositionManager _positionManager;
    private readonly PositionMonitor _positionMonitor;
    private readonly ExecutionQueue _executionQueue;
    private readonly ReconciliationService _reconciliation;
    private readonly ClockDriftMonitor _clockDriftMonitor;
    private readonly ConfidenceThresholdTuner _confidenceTuner;
    private readonly IOrderExecutor _orderExecutor;
    private readonly ITradeHistoryRepository _tradeHistory;
    private readonly IStateRepository _stateRepository;
    private readonly MetricsCollector _metrics;
    private readonly ILogger<TradingEngine> _logger;
    private readonly AccountSnapshotService _accountSnapshots;
    private readonly LlmVetoService _llmVeto;
    private readonly E1Runtime _e1;
    private string _configHash = "unknown";
    private AccountSnapshot? _accountSnapshot;
    private decimal _paperFreeCash;
    private DateTime _nextAccountRefreshUtc = DateTime.MinValue;
    private readonly SemaphoreSlim _accountSnapshotLock = new(1, 1);

    private CancellationTokenSource? _cts;
    private Task? _mainLoop;
    private BotStatus _status = BotStatus.Stopped;

    private readonly Dictionary<string, OHLCV> _currentCandles = new();
    private readonly object _candleLock = new();

    private readonly Dictionary<string, OrderBook> _currentOrderBooks = new();
    private readonly object _orderBookLock = new();

    /// <summary>P1.4: символы, ожидающие REST-дозагрузки истории и повторного прогрева.</summary>
    private readonly ConcurrentDictionary<string, byte> _pendingMarketDataRecovery = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _nextMarketDataRecoveryAttemptUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _marketDataRecoveryLock = new(1, 1);
    /// <summary>P0.3: причина ухода символа в восстановление и момент начала ожидания живого WS-тика.</summary>
    private readonly ConcurrentDictionary<string, string> _marketDataRecoveryReason = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastMarketDataForcedReconnectUtc = DateTime.MinValue;
    private TimeSpan MarketDataLiveConfirmWindow => TimeSpan.FromSeconds(_config.Trading.MarketDataLiveConfirmSeconds);
    private readonly TimeSpan _candleInterval;
    private int RequiredClosedCandles => Math.Min(200, _config.Trading.HistoryCandlesCount);

    /// <summary>
    /// P0.2: абсолютный ATR по символу для трейлинга в тик-контуре.
    /// Обновляется на закрытых свечах — считать индикаторы на каждом тике незачем.
    /// </summary>
    private readonly ConcurrentDictionary<string, decimal> _atrAbsolute = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Когда последний раз спрашивали ИИ об уже открытой позиции (закрыть/держать).</summary>
    private readonly Dictionary<string, DateTime> _lastPositionReview = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _reviewLock = new();

    /// <summary>
    /// Phase 1.5 (Blocker 5): lifecycle, по которым терминальное закрытие уже применено.
    /// Второй sell-order по тому же positionId не отправляется ни reconcile, ни монитором,
    /// ни ручным закрытием — даже если positions.json/очередь ещё не отражают факт.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTime> _terminalCloseLifecycles =
        new(StringComparer.OrdinalIgnoreCase);
    private const int MaxTerminalCloseLifecycles = 4096;

    private void MarkLifecycleClosed(string positionId)
    {
        if (string.IsNullOrWhiteSpace(positionId)) return;
        _terminalCloseLifecycles[positionId] = DateTime.UtcNow;
        if (_terminalCloseLifecycles.Count <= MaxTerminalCloseLifecycles) return;
        foreach (var old in _terminalCloseLifecycles.OrderBy(x => x.Value).Take(512).ToList())
            _terminalCloseLifecycles.TryRemove(old.Key, out _);
    }

    private bool IsLifecycleClosed(string positionId) =>
        !string.IsNullOrWhiteSpace(positionId) && _terminalCloseLifecycles.ContainsKey(positionId);

    /// <summary>
    /// Phase 1.5: принудительное закрытие (нет биржевой защиты / trailing-fail) не должно
    /// повторяться бесконечным циклом по уже проданному объёму. Ограничиваем частоту и число
    /// попыток; после исчерпания — алерт оператору и freeze входов, позиция остаётся под наблюдением.
    /// </summary>
    private bool ForcedCloseAllowed(Position position, string reason)
    {
        var forced = reason is "NoExchangeProtection" or "TrailingProtectionFailed" or "ProtectionLost";
        if (!forced) return true;

        var now = DateTime.UtcNow;
        var cooldown = TimeSpan.FromSeconds(Math.Max(1, _config.Trading.ForcedCloseCooldownSeconds));
        if (position.LastForcedCloseAttemptUtc is { } last && now - last < cooldown)
        {
            TradeLog.Info("CLOSE-DEFERRED", position.Symbol,
                $"принудительное закрытие ({reason}) отложено: cooldown {(cooldown - (now - last)).TotalSeconds:F0} c, попыток {position.ForcedCloseAttempts}");
            return false;
        }

        var max = Math.Max(1, _config.Trading.ForcedCloseMaxAttempts);
        if (position.ForcedCloseAttempts >= max)
        {
            TradeLog.Error("CLOSE-FORCED-EXHAUSTED", position.Symbol,
                $"lifecycle={position.Id} reason={reason} attempts={position.ForcedCloseAttempts}/{max} — повторные MARKET запрещены; " +
                "требуется разбор оператора (биржевая история/баланс), входы заблокированы");
            _reconciliation.FlagUnresolvedState($"forced-close-exhausted:{position.Symbol}");
            return false;
        }

        position.ForcedCloseAttempts++;
        position.LastForcedCloseAttemptUtc = now;
        return true;
    }

    /// <summary>
    /// Phase 1.5: зарезервировать объём к закрытию по lifetime-учёту и поднять аномалию
    /// «продано больше, чем куплено» (в прогоне 2026-11-27: 20.522 против 1.267 DASH).
    /// </summary>
    private decimal ReserveExitQuantity(Position position, decimal requested, string reason)
    {
        position.TryReserveExitQuantity(requested, out var allowed, out var oversell);
        if (oversell && !position.CloseOversellAlerted)
        {
            position.CloseOversellAlerted = true;
            TradeLog.Error("CLOSE-OVERSELL-ANOMALY", position.Symbol,
                $"lifecycle={position.Id} reason={reason} запрошено={LogOptions.N(requested)} разрешено={LogOptions.N(allowed)} " +
                $"куплено={LogOptions.N(position.LifetimeBoughtQuantity)} продано={LogOptions.N(position.LifetimeSoldQuantity)} — " +
                "лишний объём НЕ отправляется на биржу");
            _reconciliation.FlagUnresolvedState($"close-oversell:{position.Symbol}");
        }
        return allowed;
    }

    /// <summary>Cooldown анализа символа (B11): последний запрос к ИИ на вход.</summary>
    private readonly Dictionary<string, DateTime> _lastSymbolAnalysis = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Троттлинг WAITING-сообщений (не чаще раза в минуту на символ).</summary>
    private readonly Dictionary<string, DateTime> _lastWaitingLog = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _throttleLock = new();

    private sealed record SymbolPrecision(int PriceDecimals, decimal? TickSize, decimal? StepSize);
    private readonly ConcurrentDictionary<string, SymbolPrecision> _symbolPrecision = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Фильтры лотов/минимальной суммы ордера — чтобы сайзинг знал про минимум биржи заранее.</summary>
    private sealed record SymbolEntryFilters(
        decimal StepSize, decimal MinQty, decimal MarketStepSize, decimal MarketMinQty,
        decimal MinNotional, bool ApplyMinNotionalToMarket);
    private readonly ConcurrentDictionary<string, SymbolEntryFilters> _symbolEntryFilters = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> UnavailableSymbols { get; private set; } = Array.Empty<string>();
    /// <summary>P0.6: все исключённые пары и причины, включая неторговые классы и ошибки exchangeInfo.</summary>
    public IReadOnlyDictionary<string, string> ExcludedSymbols { get; private set; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private List<string> _activeSymbols = new();
    private DateTime _nextCorrelationBenchmarkRefreshUtc = DateTime.MinValue;
    public BotStatus Status => _status;
    public event Action<BotStatus>? OnStatusChanged;
    public event Action<TradingSignal>? OnSignalGenerated;
    public event Action<Position>? OnPositionOpened;
    public event Action<TradeRecord>? OnPositionClosed;
    /// <summary>Частичное закрытие: история/UI обновляются, но позиция остаётся открытой.</summary>
    public event Action<TradeRecord>? OnPartialPositionClosed;

    /// <summary>P0.6/P0.2: kill-switch взведён — новые входы заблокированы до явного сброса.</summary>
    private int _killSwitchEngaged;
    private string _killSwitchReason = string.Empty;
    private DateTime _killSwitchEngagedAtUtc;
    private decimal _killSwitchEquityAtEngage;
    private bool _killSwitchRequiresManualReset = true;
    private string _killSwitchCloseStatus = string.Empty;
    public bool IsKillSwitchEngaged => Volatile.Read(ref _killSwitchEngaged) == 1;
    public string KillSwitchReason => _killSwitchReason;
    public string KillSwitchCloseStatus => _killSwitchCloseStatus;
    public event Action<string>? OnKillSwitchEngaged;

    public TradingEngine(
        AppConfig config,
        IBinanceWebSocketClient wsClient,
        IBinanceRestClient restClient,
        IAIModel aiModel,
        MarketAnalyzer marketAnalyzer,
        SentimentAnalyzer sentimentAnalyzer,
        SignalCalibrator calibrator,
        RiskManager riskManager,
        PortfolioRiskManager portfolioRisk,
        PositionManager positionManager,
        PositionMonitor positionMonitor,
        ExecutionQueue executionQueue,
        ReconciliationService reconciliation,
        ClockDriftMonitor clockDriftMonitor,
        ConfidenceThresholdTuner confidenceTuner,
        IOrderExecutor orderExecutor,
        ITradeHistoryRepository tradeHistory,
        IStateRepository stateRepository,
        MetricsCollector metrics,
        LlmVetoService llmVeto,
        E1Runtime e1,
        ILogger<TradingEngine> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _wsClient = wsClient ?? throw new ArgumentNullException(nameof(wsClient));
        _restClient = restClient ?? throw new ArgumentNullException(nameof(restClient));
        _aiModel = aiModel ?? throw new ArgumentNullException(nameof(aiModel));
        _marketAnalyzer = marketAnalyzer ?? throw new ArgumentNullException(nameof(marketAnalyzer));
        _sentimentAnalyzer = sentimentAnalyzer ?? throw new ArgumentNullException(nameof(sentimentAnalyzer));
        _calibrator = calibrator ?? throw new ArgumentNullException(nameof(calibrator));
        _riskManager = riskManager ?? throw new ArgumentNullException(nameof(riskManager));
        _portfolioRisk = portfolioRisk ?? throw new ArgumentNullException(nameof(portfolioRisk));
        _positionManager = positionManager ?? throw new ArgumentNullException(nameof(positionManager));
        _positionMonitor = positionMonitor ?? throw new ArgumentNullException(nameof(positionMonitor));
        _executionQueue = executionQueue ?? throw new ArgumentNullException(nameof(executionQueue));
        _reconciliation = reconciliation ?? throw new ArgumentNullException(nameof(reconciliation));
        _clockDriftMonitor = clockDriftMonitor ?? throw new ArgumentNullException(nameof(clockDriftMonitor));
        _confidenceTuner = confidenceTuner ?? throw new ArgumentNullException(nameof(confidenceTuner));
        _orderExecutor = orderExecutor ?? throw new ArgumentNullException(nameof(orderExecutor));
        _tradeHistory = tradeHistory ?? throw new ArgumentNullException(nameof(tradeHistory));
        _stateRepository = stateRepository ?? throw new ArgumentNullException(nameof(stateRepository));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _llmVeto = llmVeto ?? throw new ArgumentNullException(nameof(llmVeto));
        _e1 = e1 ?? throw new ArgumentNullException(nameof(e1));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _accountSnapshots = new AccountSnapshotService(_config.Trading, _restClient, _wsClient, _positionManager);

        if (!CandleSchedule.TryParseInterval(_config.Trading.CandleInterval, out _candleInterval))
            throw new InvalidOperationException($"Недопустимый интервал свечей: {_config.Trading.CandleInterval}");
    }

    /// <summary>
    /// Запустить торговый движок.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_status is BotStatus.Running or BotStatus.Starting or BotStatus.Stopping or BotStatus.Paused)
        {
            _logger.LogWarning("Движок уже запущен или в переходном состоянии: {Status}", _status);
            return;
        }

        SetStatus(BotStatus.Starting);
        _logger.LogInformation("Запуск торгового движка");

        try
        {
            // P0.5/P0.6: эффективный config hash до первого торгового действия.
            _configHash = ComputeConfigHash();
            // Проверка классов и доступности выбранной биржи до дорогой загрузки модели.
            var exclusions = await ValidateSymbolsAsync(ct);
            TradeLog.SymbolUniverse(_restClient.EnvironmentName, _configHash, _activeSymbols, exclusions,
                _config.Trading.VolatileSymbols, _config.Trading.StablecoinPairs, _config.Trading.BenchmarkSymbols);
            var aiRole = _config.AI.ResolvedRole;
            TradeLog.LlmRole(aiRole.ToString(), _config.AI.Enabled, _config.AI.VetoBudgetSeconds);

            // P0.5: модель загружается только если роль не Off — детерминированный путь работает без неё.
            if (aiRole != AiRole.Off)
                await _aiModel.LoadAsync(ct);
            else
                TradeLog.Info("LLM-ROLE-OFF", null, "модель не загружается, вход только из engine setup");

            // P0.3: ключи идемпотентности из журнала намерений — ДО первой торговой операции.
            // Иначе после рестарта повторное закрытие/защита ушли бы вторым ордером.
            var restoredIntents = await _executionQueue.RestoreAsync(ct);
            var unresolved = _executionQueue.UnresolvedIntents;
            if (unresolved.Count > 0)
            {
                TradeLog.Warn("EXEC-INTENTS-UNRESOLVED", null,
                    $"count={unresolved.Count} keys={string.Join(",", unresolved.Select(i => i.ClientOrderId))} — судьба ордеров сверяется с биржей при повторе");
            }
            else if (restoredIntents > 0)
            {
                TradeLog.Info("EXEC-KEYS-RESTORED", null, $"count={restoredIntents}");
            }

            // Восстановление позиций
            await _positionManager.LoadPositionsAsync(ct);
            await _e1.InitializeAsync(_configHash, ct);
            foreach (var restored in _positionManager.GetOpenPositions())
            {
                OnPositionOpened?.Invoke(restored); // показываем восстановленные позиции в UI
            }

            // P0.5: реконсиляция с биржей + user-data stream (в paper/без ключей — тихий пропуск).
            // Колбэки применения филлов/подхвата сирот регистрируются до старта контура.
            _reconciliation.Configure(ApplyExchangeFillAsync, AdoptOrphanPositionAsync);
            await _reconciliation.StartAsync(ct);

            // P0.4: граница торгового дня — по часам биржи, а не локального процесса.
            await ConfigureExchangeClockAsync(ct);

            // P0.4: восстанавливаем пик/дневной старт/макс. просадку ДО первого UpdateBalance —
            // иначе рестарт «прощает» дневной убыток и обнуляет пик.
            var riskState = await _stateRepository.LoadRiskStateAsync(RiskEnvironment, RiskAccountId, RiskQuoteAsset, ct);
            if (riskState != null)
            {
                _riskManager.DrawdownTracker.RestoreState(riskState);
                TradeLog.Info("RISK-STATE-RESTORED", null,
                    $"peak={LogOptions.N(riskState.PeakBalance, "F2")} day_start={LogOptions.N(riskState.DayStartBalance, "F2")} " +
                    $"day={riskState.DayStartUtc:yyyy-MM-dd} max_drawdown_pct={LogOptions.N(riskState.MaxDrawdownPercent * 100m, "F2")}");

                // P0.2: авария переживает рестарт; входы остаются заблокированными до явного reset.
                if (riskState.KillSwitchEngaged)
                {
                    Interlocked.Exchange(ref _killSwitchEngaged, 1);
                    _killSwitchReason = riskState.KillSwitchReason;
                    _killSwitchEngagedAtUtc = riskState.KillSwitchEngagedAtUtc;
                    _killSwitchEquityAtEngage = riskState.KillSwitchEquityAtEngage;
                    _killSwitchRequiresManualReset = riskState.KillSwitchRequiresManualReset;
                    _killSwitchCloseStatus = string.IsNullOrWhiteSpace(riskState.KillSwitchCloseStatus)
                        ? "UNPROTECTED_OR_OPEN" : riskState.KillSwitchCloseStatus;
                    TradeLog.KillSwitchRestored(_killSwitchReason, _killSwitchCloseStatus);
                    OnKillSwitchEngaged?.Invoke(_killSwitchReason);
                }
            }

            // Получение начального баланса
            if (_config.Trading.PaperTradingMode)
            {
                // P0.4: виртуальный баланс переживает рестарт — иначе дневной PnL считался бы с нуля.
                _paperFreeCash = riskState?.FreeCashQuote
                    ?? (riskState?.CurrentBalance > 0 ? riskState.CurrentBalance : _config.Trading.PaperTradingInitialBalance);
                TradeLog.Info("PAPER-BALANCE", null,
                    $"free_cash={LogOptions.N(_paperFreeCash, "F2")} source=" +
                    (riskState?.FreeCashQuote.HasValue == true ? "scoped-risk-state" :
                     riskState?.CurrentBalance > 0 ? "legacy-equity" : "config"));
                await UpdateRiskSnapshotAsync(_accountSnapshots.CreatePaper(_paperFreeCash), ct);
            }
            else
            {
                // В реальном режиме запрашиваем баланс с биржи
                _logger.LogInformation("Режим Live: запрос баланса с Binance...");
                var snapshot = await _accountSnapshots.CreateLiveAsync(ct);
                if (!snapshot.IsValuationComplete)
                    throw new InvalidOperationException("Не удалось оценить все активы счёта в USDT; запуск fail-closed.");
                await UpdateRiskSnapshotAsync(snapshot, ct);
            }

            // P1.7: портфельный риск восстанавливается из полной истории сделок (день/серия/пауза).
            var allTrades = await _tradeHistory.GetAllTradesAsync(ct);
            _portfolioRisk.RebuildFromHistory(allTrades);
            _metrics.RecordPortfolioLossStreak(_portfolioRisk.ConsecutiveLosses, _portfolioRisk.PauseUntilUtc);
            TradeLog.Info("PORTFOLIO-RISK-RESTORED", null,
                $"trades={allTrades.Count} loss_streak={_portfolioRisk.ConsecutiveLosses} pause_until={_portfolioRisk.PauseUntilUtc?.ToString("O") ?? "-"}");

            // Прогрев индикаторов историческими свечами с REST.
            // P1.4: история проверяется на непрерывность/свежесть; при отказе символ уходит в REST-восстановление,
            // а входы блокируются до успешного повторного прогрева.
            foreach (var symbol in _activeSymbols)
            {
                try
                {
                    var history = await _restClient.GetKlinesAsync(
                        symbol, _config.Trading.CandleInterval, _config.Trading.HistoryCandlesCount, ct);

                    if (history.Count > 0)
                    {
                        lock (_candleLock)
                        {
                            _currentCandles[symbol] = history[^1];
                        }
                    }

                    if (_marketAnalyzer.TryReloadHistory(
                            symbol, history, _candleInterval, RequiredClosedCandles, DateTime.UtcNow, out var reloadReason,
                            MarketDataLiveConfirmWindow))
                    {
                        if (history.Count > 0)
                            RefreshAtrCache(symbol, history[^1]);

                        // P0.3: прогрев ≠ живой стрим. Символ остаётся в контуре подтверждения live.
                        _marketDataRecoveryReason[symbol] = "startup-rest-reload";
                        _pendingMarketDataRecovery[symbol] = 0;
                        _nextMarketDataRecoveryAttemptUtc[symbol] = DateTime.UtcNow + MarketDataLiveConfirmWindow;
                        _metrics.RecordMarketDataRecovery(success: true);
                        TradeLog.DataRestReload(symbol, history.Count(k => k.IsClosed), MarketDataLiveConfirmWindow.TotalSeconds);
                    }
                    else
                    {
                        _marketAnalyzer.MarkRecoveryRequired(symbol, reloadReason);
                        EnqueueMarketDataRecovery(symbol, reloadReason, immediate: true);
                        TradeLog.Warn("DATA-INTEGRITY-FAIL", symbol,
                            $"{reloadReason}; входы заблокированы до восстановления");
                    }
                }
                catch (Exception ex)
                {
                    _marketAnalyzer.MarkRecoveryRequired(symbol, "rest-startup-failed");
                    EnqueueMarketDataRecovery(symbol, "rest-startup-failed", immediate: true);
                    TradeLog.Warn("DATA-LOAD-FAIL", symbol, "входы заблокированы до REST-восстановления", ex);
                }
            }

            // Подписка на события ДО подключения, чтобы не потерять первые сообщения
            _wsClient.OnKline += OnKlineReceived;
            _wsClient.OnOrderBook += OnOrderBookReceived;
            _wsClient.OnError += OnWebSocketError;
            _wsClient.OnDisconnected += OnWebSocketDisconnected;
            _wsClient.OnReconnected += OnWebSocketReconnected;

            // Подключение к WebSocket
            await _wsClient.ConnectAsync(
                _activeSymbols,
                _config.Trading.CandleInterval,
                ct);

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // P1.8: мониторинг дрейфа часов живёт отдельно от анализа и подписанных запросов.
            _clockDriftMonitor.Start(_cts.Token);

            // P2.4: подбор порога уверенности по истории сделок — тоже вне цикла анализа.
            _confidenceTuner.Start(_cts.Token);

            // P0.2: независимый контур сопровождения позиций стартует ДО цикла анализа
            // и работает даже когда анализ занят инференсом или стоит на Pause.
            _positionMonitor.Configure(
                (position, reason, exitPrice, token) => ClosePositionAsync(position, reason, token, exitPrice),
                GetCurrentCandle,
                GetAtrAbsolute);
            _positionMonitor.Start(_cts.Token);

            // Запуск основного цикла (только входы и пересмотр позиции моделью)
            _mainLoop = Task.Run(() => MainLoopAsync(_cts.Token), _cts.Token);

            SetStatus(BotStatus.Running);
            _logger.LogInformation("Торговый движок запущен");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка запуска движка");

            // Откатываем частично выполненный запуск: иначе остаются подписки на WS,
            // живой receive-loop и загруженная модель, а повторный "Старт" плодит дубли.
            await CleanupAfterFailedStartAsync();

            SetStatus(BotStatus.Error);
            throw;
        }
    }

    private async Task CleanupAfterFailedStartAsync()
    {
        _wsClient.OnKline -= OnKlineReceived;
        _wsClient.OnOrderBook -= OnOrderBookReceived;
        _wsClient.OnError -= OnWebSocketError;
        _wsClient.OnDisconnected -= OnWebSocketDisconnected;
        _wsClient.OnReconnected -= OnWebSocketReconnected;

        // P0.5: если реконсиляция успела стартовать до падения запуска — гасим и её.
        try
        {
            await _reconciliation.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Не удалось остановить контур реконсиляции при откате запуска");
        }

        try
        {
            await _clockDriftMonitor.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Не удалось остановить мониторинг дрейфа часов при откате запуска");
        }

        try
        {
            await _confidenceTuner.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Не удалось остановить подбор порога уверенности при откате запуска");
        }

        try
        {
            _cts?.Cancel();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Не удалось отменить токен основного цикла");
        }

        if (_mainLoop != null)
        {
            try
            {
                await _mainLoop;
            }
            catch (OperationCanceledException)
            {
                // Ожидаемо
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Основной цикл завершился с ошибкой при откате запуска");
            }
        }

        try
        {
            await _positionMonitor.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Контур сопровождения завершился с ошибкой при откате запуска");
        }

        try
        {
            await _wsClient.DisconnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось отключить WebSocket при откате запуска");
        }

        try
        {
            await _aiModel.UnloadAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось выгрузить модель при откате запуска");
        }

        _cts?.Dispose();
        _cts = null;
        _mainLoop = null;
    }

    /// <summary>
    /// Остановить торговый движок.
    /// </summary>
    public async Task StopAsync()
    {
        if (_status == BotStatus.Stopped)
            return;

        SetStatus(BotStatus.Stopping);
        _logger.LogInformation("Остановка торгового движка");

        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Токен уже освобождён предыдущей остановкой
        }

        if (_mainLoop != null)
        {
            try
            {
                await _mainLoop;
            }
            catch (OperationCanceledException)
            {
                // Ожидаемое поведение
            }
        }

        // P0.2: гасим тик-контур раньше отключения WS — иначе он продолжит закрывать позиции.
        try
        {
            await _positionMonitor.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка остановки контура сопровождения позиций");
        }

        // P0.5: user-data stream и keepalive гасятся до отключения рыночного WS.
        try
        {
            await _reconciliation.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка остановки контура реконсиляции");
        }

        // P1.8: гасим периодическую проверку часов вместе с остальными фоновыми контурами.
        try
        {
            await _clockDriftMonitor.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка остановки мониторинга дрейфа часов");
        }

        // P2.4: контур подбора порога уверенности гасится вместе с фоновыми контурами.
        try
        {
            await _confidenceTuner.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка остановки подбора порога уверенности");
        }

        _wsClient.OnKline -= OnKlineReceived;
        _wsClient.OnOrderBook -= OnOrderBookReceived;
        _wsClient.OnError -= OnWebSocketError;
        _wsClient.OnDisconnected -= OnWebSocketDisconnected;
        _wsClient.OnReconnected -= OnWebSocketReconnected;

        try
        {
            await _wsClient.DisconnectAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка отключения WebSocket при остановке");
        }

        try
        {
            await _aiModel.UnloadAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ошибка выгрузки модели при остановке");
        }

        try
        {
            await _positionManager.SavePositionsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось сохранить позиции при остановке");
        }

        _cts?.Dispose();
        _cts = null;
        _mainLoop = null;

        SetStatus(BotStatus.Stopped);
        _logger.LogInformation("Торговый движок остановлен");
    }

    /// <summary>
    /// Приостановить торговлю.
    /// </summary>
    public void Pause()
    {
        if (_status != BotStatus.Running)
            return;

        SetStatus(BotStatus.Paused);
        _logger.LogInformation("Торговля приостановлена");
    }

    /// <summary>
    /// Возобновить торговлю.
    /// </summary>
    public void Resume()
    {
        if (_status != BotStatus.Paused)
            return;

        SetStatus(BotStatus.Running);
        _logger.LogInformation("Торговля возобновлена");
    }

    private async Task MainLoopAsync(CancellationToken ct)
    {
        _logger.LogInformation("Основной цикл запущен");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_status == BotStatus.Running)
                {
                    if (DateTime.UtcNow >= _nextAccountRefreshUtc)
                    {
                        await RunGuardedPhaseAsync("account-refresh", RefreshAccountSnapshotAsync, ct);
                        _nextAccountRefreshUtc = DateTime.UtcNow.AddSeconds(_config.Trading.AccountRefreshIntervalSeconds);
                    }
                    await RunGuardedPhaseAsync("market-data-recovery", RecoverMarketDataAsync, ct);
                    await RunGuardedPhaseAsync("correlation-benchmark", EnsureCorrelationBenchmarkAsync, ct);
                    await ProcessSymbolsGuardedAsync(ct);
                }

                await Task.Delay(TimeSpan.FromSeconds(_config.Trading.AnalysisIntervalSeconds), ct);
            }
            // P0.8: break только по реальной остановке бота. Таймаут транспорта тоже бросает
            // OperationCanceledException — раньше он молча завершал цикл («цикл завершен»
            // в логе при живом процессе), теперь это ошибка итерации, а не конец торговли.
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка в основном цикле");
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
        }

        _logger.LogInformation("Основной цикл завершен");
    }

    /// <summary>P0.8: бюджет фазы цикла. Залипшая фаза не имеет права остановить торговлю навсегда.</summary>
    private static readonly TimeSpan MainLoopPhaseBudget = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MainLoopStallWarning = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RecoveryLockWait = TimeSpan.FromSeconds(15);

    /// <summary>P0.3a шаг 3: столько карантинов подряд без live-подтверждения — операторский алерт.</summary>
    private const int QuarantineEscalationStreak = 3;

    /// <summary>
    /// P0.8: фаза без побочных торговых эффектов под жёстким бюджетом. Просрочка — громкий
    /// LOOP-PHASE-TIMEOUT и продолжение цикла (результат фазы игнорируется), а не тихая смерть.
    /// </summary>
    private async Task RunGuardedPhaseAsync(string phase, Func<CancellationToken, Task> body, CancellationToken ct)
    {
        var task = body(ct);
        var completed = await Task.WhenAny(task, Task.Delay(MainLoopPhaseBudget, ct));
        if (completed == task)
        {
            await task; // исключения фазы уходят в catch основного цикла
            return;
        }

        ct.ThrowIfCancellationRequested();
        TradeLog.Error("LOOP-PHASE-TIMEOUT", null,
            $"phase={phase} budget_s={LogOptions.N(MainLoopPhaseBudget.TotalSeconds)} — результат фазы игнорируется, цикл продолжает работу");
        _ = task.ContinueWith(
            t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// P0.8: обход символов ставит ордера — бросать его на полпути нельзя (риск дубля входа).
    /// Поэтому здесь только сигнал LOOP-STALL о зависании, ожидание продолжается.
    /// </summary>
    private async Task ProcessSymbolsGuardedAsync(CancellationToken ct)
    {
        var task = ProcessSymbolsAsync(ct);
        var completed = await Task.WhenAny(task, Task.Delay(MainLoopStallWarning, ct));
        if (completed != task && !ct.IsCancellationRequested)
            TradeLog.Warn("LOOP-STALL", null,
                $"обход символов длится дольше {LogOptions.N(MainLoopStallWarning.TotalSeconds)} с — торговый контур ждёт (проверить REST/биржу)",
                gateKey: "loop-stall");

        await task;
    }

    private async Task ProcessSymbolsAsync(CancellationToken ct)
    {
        foreach (var symbol in _activeSymbols)
        {
            if (ct.IsCancellationRequested)
                break;

            await ProcessSymbolAsync(symbol, ct);
        }
    }

    private async Task ProcessSymbolAsync(string symbol, CancellationToken ct)
    {
        var ctx = new E1DecisionContext(_e1, symbol);
        ctx.LlmRole = _config.AI.ResolvedRole.ToString();
        try
        {
            await ProcessSymbolCoreAsync(symbol, ctx, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await ctx.ErrorAsync("exception", "exception", ex.Message, CancellationToken.None);
            throw;
        }
    }

    private async Task ProcessSymbolCoreAsync(string symbol, E1DecisionContext ctx, CancellationToken ct)
    {
        // P0.6: kill-switch блокирует новые входы независимо от статуса (Pause может не примениться
        // во время Starting); сопровождение открытых позиций продолжает PositionMonitor.
        if (IsKillSwitchEngaged)
        {
            TradeLog.Rejected(symbol, "kill-switch", "входы заблокированы kill-switch (пробит лимит риска или ручная паника)");
            await ctx.RejectAsync("kill-switch", "kill_switch_engaged", "входы заблокированы kill-switch", ct);
            return;
        }

        // P0.4: неизвестное состояние ордеров/позиций — freeze entries, не угадывать.
        if (!_reconciliation.IsExecutionStateTrusted)
        {
            var blockReason = _reconciliation.ExecutionStateBlockReason;
            _metrics.RecordExecutionStateBlock();
            TradeLog.Rejected(symbol, "execution-state",
                $"состояние исполнения недостоверно ({blockReason}); режим {_reconciliation.ExecutionMode}");
            await ctx.RejectAsync("execution-state", "execution_state_untrusted", blockReason, ct);
            return;
        }

        // P0.1: неполная оценка капитала не допускает входы.
        if (_accountSnapshot?.IsValuationComplete != true)
        {
            TradeLog.Rejected(symbol, "risk", "снимок капитала неполон или отсутствует (account valuation fail-closed)");
            await ctx.RejectAsync("risk", "account_valuation_incomplete", "снимок капитала неполон", ct);
            return;
        }

        // 1. Позиция уже открыта — здесь только вопрос модели «держать/закрыть».
        //    P0.2: SL/TP, трейлинг и биржевая защита ушли в PositionMonitor (тик-контур),
        //    чтобы сопровождение не ждало инференса LLM по всем остальным парам.
        if (_positionManager.GetPositionBySymbol(symbol) != null)
        {
            await ctx.HoldAsync("position-review", "position_already_open", "открытая позиция сопровождается отдельным контуром", ct);
            await ReviewOpenPositionAsync(symbol, ct);
            return;
        }

        // 2. Лимит одновременно открытых позиций.
        var openCount = _positionManager.GetOpenPositions().Count;
        if (openCount >= _config.Trading.MaxOpenPositions)
        {
            TradeLog.Rejected(symbol, "portfolio",
                $"достигнут лимит открытых позиций {openCount}/{_config.Trading.MaxOpenPositions}");
            await ctx.RejectAsync("portfolio", "max_open_positions", $"{openCount}/{_config.Trading.MaxOpenPositions}", ct);
            return;
        }

        // P1.7: дешёвый портфельный гейт до AI — пауза серии убытков / дневной лимит символа.
        if (_portfolioRisk.IsEntryBlocked(symbol, _riskManager.DrawdownTracker.DayStartBalance, out var portfolioBlock))
        {
            _metrics.RecordPortfolioRejection("daily-or-pause");
            TradeLog.Rejected(symbol, "portfolio", portfolioBlock);
            await ctx.RejectAsync("portfolio", "portfolio_daily_or_pause", portfolioBlock, ct);
            return;
        }

        // 3. Индикаторы.
        var currentCandle = GetCurrentCandle(symbol);
        if (currentCandle == null)
        {
            LogWaiting(symbol, "нет текущей свечи (ожидание данных WebSocket/REST)");
            await ctx.RejectAsync("market-data", "no_current_candle", "нет текущей свечи", ct);
            return;
        }

        // P1.4: fail-closed до AI — протухшие/разорванные данные после реконнекта WS не допускаются ко входу.
        if (!PassesMarketDataFreshness(symbol, "pre-ai", ctx))
        {
            await ctx.RejectAsync("market-data", "market_data_freshness", "pre-ai freshness fail", ct);
            return;
        }

        var indicators = _marketAnalyzer.GetIndicatorSnapshot(symbol, currentCandle);
        if (indicators == null)
        {
            // Раньше здесь был молчаливый return — в логах было пусто, казалось что бот не работает.
            var warmup = _marketAnalyzer.GetWarmupInfo(symbol);
            LogWaiting(symbol, $"прогрев индикаторов: {warmup.Candles}/{warmup.Required} свечей");
            await ctx.RejectAsync("warmup", "indicator_warmup", $"{warmup.Candles}/{warmup.Required}", ct);
            return;
        }

        // 3a. Cooldown на символ: при 12+ парах и медленном инференсе не забиваем очередь одним символом.
        if (!TryEnterAnalysisCooldown(symbol))
        {
            await ctx.RejectAsync("cooldown", "analysis_cooldown", "symbol analysis cooldown", ct);
            return;
        }

        // 3b. Детерминированный pre-score (B9): математика до ИИ, якорь для рубрики уверенности.
        var preScore = _calibrator.PreScore(indicators);
        ctx.PreScore = preScore;
        foreach (var kv in indicators) ctx.Features[kv.Key] = kv.Value;
        ctx.CandleCloseUtc = currentCandle.OpenTime;
        ctx.DataSnapshotHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(indicators))))[..16].ToLowerInvariant();
        TradeLog.Decision(symbol, "PRE-SCORE", $"{preScore.Summary}; {preScore.ReasonsText}");

        // 4. P0.5: детерминированный сигнал БЕЗ ожидания LLM. Направление/prior — PreScore,
        //    цены — математика движка. LLM может только запретить (VetoOnly) или наблюдаться
        //    (Shadow/Explainer); её BUY никогда не создаёт ордер.
        var signal = _calibrator.BuildEngineSignal(symbol, preScore,
            BuildCalibrationContext(symbol, indicators, currentCandle.Close, hasPosition: false));

        // P0.6: защита от противоположного/неинициализированного направления даже если
        // сигнал в будущем придёт из другого источника. Reversal setup пока не реализован.
        if (signal.Action is SignalAction.Buy or SignalAction.Sell &&
            !SignalCalibrator.IsEntryDirectionAllowed(signal.Action, preScore.Direction))
        {
            TradeLog.Rejected(symbol, "symbol-policy",
                $"direction_mismatch: engine={preScore.DirectionName}, proposed={signal.Action}; вход запрещён без отдельного reversal setup");
            await ctx.RejectAsync("symbol-policy", "direction_mismatch", $"{preScore.DirectionName}/{signal.Action}", ct);
            return;
        }

        ctx.Signal = signal;
        _metrics.RecordSignal(signal);
        OnSignalGenerated?.Invoke(signal);

        // 4a. P0.5: роль LLM. Verdict кэшируется по (symbol, закрытая свеча, config_hash);
        //     закрытая свеча закрывается в момент открытия текущей (currentCandle.OpenTime).
        var aiRole = _config.AI.ResolvedRole;
        if (aiRole != AiRole.Off && signal.Action is SignalAction.Buy or SignalAction.Sell)
        {
            var candleCloseUtc = currentCandle.OpenTime;
            var requestContext = BuildAiContext(symbol, position: null, preScore, indicators, currentCandle.Close);

            if (!_llmVeto.TryGetVerdict(symbol, candleCloseUtc, _configHash, out var verdict))
            {
                // Асинхронный запрос: цикл не ждёт инференс.
                var newsContext = await _sentimentAnalyzer.GetNewsContextAsync(symbol, ct);
                _llmVeto.RequestOpinion(symbol, candleCloseUtc, _configHash, indicators, newsContext, requestContext);

                if (aiRole == AiRole.VetoOnly)
                {
                    // Fail-closed: без готового вердикта вход не разрешён, но и не ждём —
                    // вердикт будет готов к следующему проходу по символу.
                    _metrics.RecordLlmVetoPendingBlock();
                    TradeLog.LlmVetoPending(symbol);
                    TradeLog.Rejected(symbol, "llm-veto", "вердикт LLM по закрытой свече ещё не готов (VetoOnly, fail-closed)");
                    ctx.AiDisposition = AiDisposition.NotInvoked;
                    await ctx.RejectAsync("llm-veto", "llm_verdict_pending", "VetoOnly fail-closed", ct);
                    return;
                }
            }
            else if (aiRole == AiRole.VetoOnly && verdict != null)
            {
                if (!verdict.WithinBudget)
                {
                    // Опоздавший вердикт не применяется постфактум, но и не разрешает вход:
                    // для этой свечи валидного в-бюджет вердикта нет → fail-closed.
                    _metrics.RecordLlmVetoPendingBlock();
                    TradeLog.Rejected(symbol, "llm-veto",
                        $"вердикт LLM опоздал ({verdict.LatencyMs:F0} ms > бюджета {_config.AI.VetoBudgetSeconds}s) — вход не разрешён");
                    ctx.AiDisposition = AiDisposition.TimedOut;
                    await ctx.RejectAsync("llm-veto", "llm_verdict_late", verdict.LatencyMs.ToString("F0"), ct);
                    return;
                }

                if (verdict.Veto)
                {
                    _metrics.RecordLlmVetoApplied();
                    TradeLog.LlmVetoApplied(symbol, verdict.Reason);
                    TradeLog.Rejected(symbol, "llm-veto", verdict.Reason);
                    ctx.AiDisposition = AiDisposition.Completed;
                    await ctx.RejectAsync("llm-veto", "llm_veto", verdict.Reason, ct);
                    return;
                }
            }
            // Shadow/Explainer: вердикт только в журнале (LLM-VETO-RESULT), на вход не влияет.
        }

        if (signal.Action is SignalAction.Hold or SignalAction.Close)
        {
            TradeLog.Outcome(symbol, "HOLD",
                signal.Action == SignalAction.Close
                    ? "ИИ просит закрыть, но открытой позиции нет"
                    : $"conf={signal.Confidence:P0}: {signal.Reasoning}");
            await ctx.HoldAsync("signal", "hold", signal.Reasoning, ct);
            return;
        }

        if (signal.Action == SignalAction.Sell && !_config.Trading.AllowShort)
        {
            TradeLog.Rejected(symbol, "spot",
                "SELL без открытой позиции = шорт, на споте Binance невозможен (Trading:AllowShort=false)");
            await ctx.RejectAsync("spot", "short_disabled", "AllowShort=false", ct);
            return;
        }

        // 5. Порог уверенности применяется к FINAL confidence (после калибратора B9),
        //    а не к «сырому» числу модели — иначе якорь 0.45 убивал все сигналы.
        if (signal.Confidence < _config.AI.MinConfidence)
        {
            TradeLog.Rejected(symbol, "confidence",
                $"final={signal.Confidence:P0} < порога {_config.AI.MinConfidence:P0} " +
                $"(model={signal.ModelConfidence:P0}, engine={signal.EngineConfidence:P0}, {signal.ConfidenceSource})");
            await ctx.RejectAsync("confidence", "below_min_confidence", $"{signal.Confidence}<{_config.AI.MinConfidence}", ct);
            return;
        }

        // 5a. P2.7: модель систематически ошибается в R/R (симметричные SL/TP → net ≈ 0.7
        //     при пороге 2.0). Вместо вечного отказа валидный по остальным параметрам вход
        //     получает детерминированно исправленный TP — той же формулой, что и post-exec.
        TryFixSignalTakeProfit(symbol, signal, indicators, currentCandle.Close);

        // 6. Валидация риск-менеджером (просадка, дневной лимит, R/R, корректность цен).
        var balance = _accountSnapshot?.EquityQuote ?? _riskManager.DrawdownTracker.CurrentEquity;
        if (!_riskManager.ValidateSignal(signal, balance, out var reason))
        {
            TradeLog.Rejected(symbol, "risk", reason);
            await ctx.RejectAsync("risk", "risk_manager_reject", reason, ct);
            return;
        }

        if (!signal.EntryPrice.HasValue || signal.EntryPrice.Value <= 0 ||
            !signal.StopLoss.HasValue || !signal.TakeProfit.HasValue)
        {
            TradeLog.Rejected(symbol, "signal", "в сигнале нет корректных entry/SL/TP");
            await ctx.RejectAsync("signal", "invalid_signal_prices", "entry/SL/TP missing", ct);
            return;
        }

        // 7. Размер позиции.
        var atr = indicators.TryGetValue("atr_pct", out var atrPct)
            ? atrPct * currentCandle.Close
            : currentCandle.Close * 0.02m; // Fallback: 2% от цены

        var side = signal.Action == SignalAction.Buy ? OrderSide.Buy : OrderSide.Sell;
        var preliminaryStopLoss = _riskManager.CalculateStopLoss(side, signal.EntryPrice.Value, atr);

        var quantity = _riskManager.CalculatePositionSize(
            balance,
            signal.EntryPrice.Value,
            preliminaryStopLoss,
            atr);

        // 7a. Спот без плеча: свободные деньги = баланс минус капитал в открытых позициях.
        //     PositionSizer ограничивает объём всем балансом, поэтому при MaxOpenPositions=3
        //     бот набирал до 3x баланса (в live Binance отклонял ордер по -2010).
        var freeCash = GetFreeCash();
        var feeBuffer = 1m + FeeRate;
        var maxAffordable = freeCash > 0 ? freeCash / (signal.EntryPrice.Value * feeBuffer) : 0m;

        if (maxAffordable <= 0)
        {
            TradeLog.Rejected(symbol, "cash",
                $"нет свободных средств: free cash {freeCash:F2}, equity {balance:F2} USDT");
            await ctx.RejectAsync("cash", "no_free_cash", $"free={freeCash}", ct);
            return;
        }

        // Лимит конкретной пары из symbols.json, в дополнение к риск-сайзингу и свободным средствам.
        if (_config.Trading.SymbolPositionLimits.TryGetValue(symbol, out var positionPercent))
        {
            var symbolCap = balance * positionPercent / 100m / (signal.EntryPrice.Value * feeBuffer);
            if (quantity > symbolCap)
            {
                TradeLog.Decision(symbol, "SYMBOL-CAP", $"объём ограничен лимитом пары {positionPercent}%: {quantity:F8} → {symbolCap:F8}");
                quantity = symbolCap;
            }
        }

        if (quantity > maxAffordable)
        {
            TradeLog.Decision(symbol, "SIZE-CAP",
                $"объём урезан по свободным средствам: {quantity:F8} → {maxAffordable:F8} " +
                $"(свободно {freeCash:F2} USDT из {balance:F2})");
            quantity = maxAffordable;
        }

        if (quantity <= 0)
        {
            TradeLog.Rejected(symbol, "sizing", "рассчитанный объём <= 0");
            await ctx.RejectAsync("sizing", "zero_quantity", "quantity<=0", ct);
            return;
        }

        // P1.7: полный портфельный гейт после сайзинга — экспозиция и BTC-корреляция.
        quantity = ApplyPortfolioRiskCap(symbol, quantity, signal.EntryPrice.Value, balance, "sizing");
        if (quantity <= 0)
        {
            await ctx.RejectAsync("portfolio", "portfolio_cap_zero", "sizing", ct);
            return;
        }

        // P1.4: повторная проверка свежести перед отправкой ордера — пока шёл инференс, WS мог оборваться.
        if (!PassesMarketDataFreshness(symbol, "pre-exec"))
        {
            await ctx.RejectAsync("market-data", "market_data_freshness", "pre-exec freshness fail", ct);
            return;
        }

        // P0.4: пока шёл инференс, user-data stream мог упасть, а реконсиляция — не пройти.
        if (!_reconciliation.IsExecutionStateTrusted)
        {
            _metrics.RecordExecutionStateBlock();
            TradeLog.Rejected(symbol, "execution-state",
                $"[pre-exec] состояние исполнения недостоверно ({_reconciliation.ExecutionStateBlockReason})");
            await ctx.RejectAsync("execution-state", "execution_state_untrusted", _reconciliation.ExecutionStateBlockReason, ct);
            return;
        }

        var currentOrderBook = GetCurrentOrderBook(symbol);

        // 7b. P1.5: ликвидность перед входом — спред / глубина стороны входа / объём свечей.
        //     Раньше широкий спред только логировался, вход не блокировался.
        if (!PassesLiquidityFilter(symbol, side, quantity, signal.EntryPrice.Value, currentOrderBook, currentCandle.Close))
        {
            await ctx.RejectAsync("liquidity", "liquidity_filter", "spread/depth/volume gate", ct);
            return;
        }

        // P1.7: повторный портфельный гейт перед отправкой — позиции/цены могли измениться во время проверок.
        quantity = ApplyPortfolioRiskCap(symbol, quantity, signal.EntryPrice.Value, balance, "pre-exec");
        if (quantity <= 0)
        {
            await ctx.RejectAsync("portfolio", "portfolio_cap_zero", "pre-exec", ct);
            return;
        }

        // 7c. Минимум биржи (NOTIONAL + лоты) учитываем ДО отправки: иначе риск-сайзинг даёт
        //     объём на 2 USDT при минимуме 5, и ордер отклоняется уже после инференса и проверок.
        //     Либо подъём до минимума в пределах риска/денег, либо явный пропуск пары.
        if (_symbolEntryFilters.TryGetValue(symbol, out var entryFilters))
        {
            var notionalPrice = _config.Trading.UseMarketEntry
                ? currentOrderBook?.MidPrice ?? signal.EntryPrice.Value
                : signal.EntryPrice.Value;

            var bumpCeiling = maxAffordable;
            if (_config.Trading.SymbolPositionLimits.TryGetValue(symbol, out var bumpCapPercent))
                bumpCeiling = Math.Min(bumpCeiling,
                    balance * bumpCapPercent / 100m / (signal.EntryPrice.Value * feeBuffer));

            var entryPlan = EntryFilterPolicy.PlanEntry(
                quantity, notionalPrice, _config.Trading.UseMarketEntry,
                entryFilters.StepSize, entryFilters.MinQty,
                entryFilters.MarketStepSize, entryFilters.MarketMinQty,
                entryFilters.MinNotional, entryFilters.ApplyMinNotionalToMarket,
                _config.Trading.MinNotionalBufferPercent,
                _config.Trading.MinNotionalBumpMaxPercent,
                bumpCeiling);

            if (entryPlan.Route == EntryFilterRoute.Reject)
            {
                TradeLog.Rejected(symbol, "min-notional", entryPlan.Reason ?? "объём ниже фильтров биржи");
                await ctx.RejectAsync("min-notional", "entry_filter_reject", entryPlan.Reason ?? string.Empty, ct);
                return;
            }

            if (entryPlan.Route == EntryFilterRoute.Bump)
            {
                // Подъём не должен обходить портфельные лимиты: если они режут объём ниже
                // минимума биржи — вход пропускается, а не отправляется заведомо невалидным.
                var allowed = ApplyPortfolioRiskCap(symbol, entryPlan.Quantity, signal.EntryPrice.Value, balance, "min-notional-bump");
                if (allowed < entryPlan.Quantity)
                {
                    TradeLog.Rejected(symbol, "min-notional",
                        $"минимум биржи требует {entryPlan.Quantity}, портфельный лимит разрешает {allowed} — вход пропущен");
                    await ctx.RejectAsync("min-notional", "bump_exceeds_portfolio_cap", $"{entryPlan.Quantity}>{allowed}", ct);
                    return;
                }

                TradeLog.Decision(symbol, "MIN-NOTIONAL-BUMP", entryPlan.Reason ?? string.Empty);
            }

            quantity = entryPlan.Quantity;
        }

        // 8. Исполнение. Ордер возвращается только после реального исполнения.
        await ctx.AllowAsync("execution", "entry_allowed", $"qty={quantity}", ct);
        var entryIntent = await _e1.PrepareEntryAsync(ctx.DecisionId, signal, quantity, side, ct);
        var order = await _orderExecutor.ExecuteSignalAsync(signal, quantity, currentOrderBook, ct, entryIntent);
        await _e1.RecordEntryResultAsync(entryIntent, order, currentOrderBook, ct);
        if (order == null)
            return;

        // 9. Позиция открывается на ФАКТИЧЕСКИ исполненный объём и цену.
        var executedQuantity = order.ExecutedQuantity > 0 ? order.ExecutedQuantity : quantity;

        // Spot: комиссия может удерживаться в базовом активе (например XRP).
        // ExecutedQuantity — gross fill; продавать/защищать можно только net после комиссии.
        var sellableQuantity = executedQuantity;
        if (!_config.Trading.PaperTradingMode &&
            side == OrderSide.Buy &&
            SymbolNaming.TrySplit(symbol, _config.Trading.QuoteAssets, out var baseAsset, out _))
        {
            var baseFee = order.GetCommission(baseAsset);
            if (baseFee > 0m)
            {
                sellableQuantity = Math.Max(0m, executedQuantity - baseFee);
                TradeLog.Decision(symbol, "ENTRY-FEE-NET",
                    $"комиссия в базовом активе: fill={executedQuantity}, fee={baseFee} {baseAsset}, sellable={sellableQuantity}");
            }
        }

        var actualEntry = order.AverageFillPrice ?? order.Price;
        if (actualEntry <= 0)
            actualEntry = signal.EntryPrice.Value;

        var stopLoss = _riskManager.CalculateStopLoss(side, actualEntry, atr);

        var takeProfit = signal.TakeProfit.Value;
        var tpDirectionOk = side == OrderSide.Buy ? takeProfit > actualEntry : takeProfit < actualEntry;
        var netRR = _riskManager.CalculateNetRiskReward(side, actualEntry, stopLoss, takeProfit);
        if (!tpDirectionOk || !netRR.HasValue || netRR.Value < (decimal)_config.Trading.MinRiskRewardRatio)
        {
            takeProfit = _riskManager.CalculateTakeProfit(side, actualEntry, stopLoss);
        }

        // Цена-ориентир для защитных ног: серединa стакана актуальнее закрытия свечи —
        // Binance проверяет правило OCO относительно последней цены, а не нашей свечи.
        var referencePrice = currentOrderBook?.MidPrice is { } mid && mid > 0m ? mid : currentCandle.Close;

        var position = new Position
        {
            Id = entryIntent.EntryIntentId!,
            E1DecisionId = ctx.DecisionId,
            E1EntryIntentId = entryIntent.EntryIntentId!,
            Symbol = symbol,
            Side = side,
            Quantity = sellableQuantity,
            // Phase 1.5: lifetime-база «куплено» — потолок суммарных продаж по этой позиции.
            LifetimeBoughtQuantity = sellableQuantity,
            EntryPrice = actualEntry,
            CurrentPrice = referencePrice,
            StopLoss = stopLoss,
            TakeProfit = takeProfit,
            InitialStopLoss = stopLoss,
            OpenedAt = DateTime.UtcNow,
            OpeningOrderId = order.Id,
            EntryCommission = order.Commission,
            SignalId = signal.Timestamp.ToString("yyyyMMddHHmmss"),
            // P2.4: метка уверенности живёт в позиции и персистится — история сделок
            // должна знать, на каком confidence вход был разрешён.
            SignalConfidence = signal.Confidence,
            ModelConfidence = signal.ModelConfidence,
            EngineConfidence = signal.EngineConfidence ?? 0m,
            ConfidenceSource = signal.ConfidenceSource ?? string.Empty,
            // P2.6: фичи скоринга на входе + направление движка — материал для валидации
            // весов на истории (персистятся в positions.json, доживают до TradeRecord).
            ScoreFeatures = ScoringMath.CaptureFeatures(indicators),
            EngineDirection = preScore.Direction
        };

        // P0.2: вход и постановка защиты — под посимвольным шлюзом, чтобы тик-контур
        // не начал сопровождать позицию раньше, чем она защищена на бирже.
        var protectionFailed = false;
        using (await _positionMonitor.LockSymbolAsync(symbol, ct))
        {
            await _positionManager.AddPositionAsync(position, ct);
            await _e1.AttachPositionAsync(position, ct);

            // P0.1: биржевая защита ставится СРАЗУ после филла входа.
            if (await _orderExecutor.EnsureProtectionAsync(position, ct))
            {
                if (position.Protection != null)
                    await _positionManager.SavePositionsAsync(ct);
            }
            else
            {
                protectionFailed = true;
            }
        }

        // Не встала — позиция закрывается market'ом (правило «нет защиты → нет позиции»).
        // Вне шлюза: ClosePositionAsync берёт его сам.
        if (protectionFailed)
        {
            if (!_config.Trading.ClosePositionIfProtectionFails ||
                !_config.Trading.ExchangeProtectionEnabled ||
                _config.Trading.PaperTradingMode)
            {
                TradeLog.Warn("PROTECTION-LOCAL-ONLY", symbol,
                    "биржевой защиты нет, работает только локальный SL (Trading:ClosePositionIfProtectionFails=false)",
                    gateKey: "protection-local-only:" + symbol);
            }
            else
            {
                TradeLog.Rejected(symbol, "protection",
                    "OCO не поставлена: закрываем позицию, чтобы капитал не зависел от живого процесса");
                await ClosePositionAsync(position, "NoExchangeProtection", ct);
                await _e1.RefreshOutcomeAsync(position.Id, "NoExchangeProtection", position.DustExchangeQuantity, ct);
                return;
            }
        }

        OnPositionOpened?.Invoke(position);

        TradeLog.Opened(symbol, side.ToString(), executedQuantity, actualEntry, stopLoss, takeProfit,
            _config.Trading.PaperTradingMode ? "PAPER" : _restClient.EnvironmentName, order.Id);

        // P0.1: paper cash-ledger: покупка уменьшает free cash на notional + комиссию.
        if (_config.Trading.PaperTradingMode)
        {
            _paperFreeCash -= executedQuantity * actualEntry + order.Commission;
            await RefreshAccountSnapshotAsync(ct);
        }
        else
        {
            await RefreshAccountSnapshotAsync(ct);
        }

        lock (_reviewLock)
        {
            _lastPositionReview[symbol] = DateTime.UtcNow;
        }

    }

    /// <summary>
    /// P1.7: применить портфельный кап к объёму. Возвращает допустимый объём (0 = отказ).
    /// Equity = баланс бота + нереализованный PnL открытых позиций (mark-to-market).
    /// </summary>
    private decimal ApplyPortfolioRiskCap(string symbol, decimal quantity, decimal entryPrice, decimal balance, string stage)
    {
        if (quantity <= 0m || entryPrice <= 0m) return 0m;

        var open = _positionManager.GetOpenPositions();
        var equity = balance + open.Sum(p => p.UnrealizedPnL);
        var proposed = quantity * entryPrice;
        var decision = _portfolioRisk.EvaluateEntry(
            symbol, proposed, open, equity,
            _riskManager.DrawdownTracker.DayStartBalance,
            s => _marketAnalyzer.GetClosedCandles(s, _config.Trading.CorrelationLookbackCandles + 1));

        _metrics.RecordPortfolioDecision(decision, equity);

        if (!decision.Allowed)
        {
            _metrics.RecordPortfolioRejection(decision.Reason);
            TradeLog.Rejected(symbol, "portfolio", $"[{stage}] {decision.Reason}");
            return 0m;
        }

        if (decision.MaxAdditionalNotional < proposed)
        {
            var capped = decision.MaxAdditionalNotional / entryPrice;
            TradeLog.Decision(symbol, "PORTFOLIO-CAP",
                $"[{stage}] объём урезан портфельным риском: {quantity:F8} → {capped:F8} " +
                $"(экспозиция {decision.PortfolioExposure:F2}, корр. кластер {decision.CorrelatedExposure:F2}, maxρ {decision.MaxCorrelation:F2})");
            return capped;
        }

        return quantity;
    }

    /// <summary>P1.7: бенчмарк корреляции может не входить в торговые пары — держим его REST-историю свежей.</summary>
    private async Task EnsureCorrelationBenchmarkAsync(CancellationToken ct)
    {
        var cfg = _config.Trading;
        if (!cfg.PortfolioRiskEnabled || !cfg.CorrelationRiskEnabled) return;

        var benchmark = cfg.CorrelationBenchmarkSymbol;
        var now = DateTime.UtcNow;
        if (now < _nextCorrelationBenchmarkRefreshUtc) return;

        _nextCorrelationBenchmarkRefreshUtc = now + _candleInterval;
        try
        {
            var history = await _restClient.GetKlinesAsync(benchmark, cfg.CandleInterval, cfg.HistoryCandlesCount, ct);
            if (!_marketAnalyzer.TryReloadHistory(benchmark, history, _candleInterval, RequiredClosedCandles, now, out var reason))
                TradeLog.Warn("PORTFOLIO-CORRELATION", benchmark,
                    $"история бенчмарка не прошла проверку: {reason}; входы fail-closed",
                    gateKey: "portfolio-correlation:" + benchmark);
        }
        catch (Exception ex)
        {
            TradeLog.Warn("PORTFOLIO-CORRELATION", benchmark,
                "не удалось обновить бенчмарк; входы fail-closed до восстановления", ex,
                gateKey: "portfolio-correlation-fail:" + benchmark);
        }
    }

    /// <summary>
    /// Ставка комиссии для расчёта «сколько денег реально нужно под ордер».
    /// В paper-режиме берётся из конфига, в live/demo — консервативные 0.1% (spot).
    /// </summary>
    private decimal FeeRate => TradeCostModel.EntryFeeRate(_config.Trading);

    /// <summary>P0.1: свободный quote-cash берётся из снимка счёта, не вычитается из equity.</summary>
    private decimal GetFreeCash() => Math.Max(0m, _accountSnapshot?.FreeCashQuote ?? 0m);

    /// <summary>
    /// Контекст для модели: открытая позиция, спот-ограничения, минимальные R/R и уверенность.
    /// </summary>
    private AiRequestContext BuildAiContext(
        string symbol,
        Position? position,
        EngineScore? preScore = null,
        Dictionary<string, decimal>? indicators = null,
        decimal price = 0m) => new()
    {
        Role = _config.AI.ResolvedRole,
        SymbolClass = SymbolUniversePolicy.Classify(symbol, _config.Trading.VolatileSymbols,
            _config.Trading.StablecoinPairs, _config.Trading.BenchmarkSymbols),
        SymbolExclusionReason = _config.Trading.SymbolExclusions.TryGetValue(symbol, out var exclusion) ? exclusion : null,
        HasSeparateReversalSetup = false,
        HasPosition = position != null,
        PositionSide = position == null ? null : (position.Side == OrderSide.Buy ? "LONG" : "SHORT"),
        EntryPrice = position?.EntryPrice,
        Quantity = position?.Quantity,
        UnrealizedPnlPercent = position?.UnrealizedPnLPercent,
        StopLoss = position?.StopLoss,
        TakeProfit = position?.TakeProfit,
        MinutesInPosition = position == null
            ? null
            : (DateTime.UtcNow - position.OpenedAt).TotalMinutes,
        AllowShort = _config.Trading.AllowShort,
        MinRiskReward = (decimal)_config.Trading.MinRiskRewardRatio,
        MinConfidence = _config.AI.MinConfidence,
        StopLossAtrMultiplier = (decimal)_config.Trading.StopLossAtrMultiplier,
        EngineScore = preScore?.Direction,
        EngineDirection = preScore?.DirectionName,
        EnginePriorConfidence = preScore?.PriorConfidence,
        EngineReasons = preScore?.Reasons,
        EngineFacts = preScore?.Facts,
        PriceDecimals = GetPriceDecimals(symbol),
        TickSize = GetTickSize(symbol)
    };

    /// <summary>
    /// P2.7: детерминированная починка R/R сигнала до ValidateSignal. Если net R/R
    /// после комиссий ниже Trading:MinRiskRewardRatio, TP пересчитывается формулой
    /// TakeProfitForNetRiskReward (как post-exec и ENGINE-ENTRY) и округляется к тику
    /// строго «от входа», чтобы округление не уронило R/R обратно ниже порога.
    /// SL модели сохраняется; при неположительном риске после комиссий (SL внутри
    /// комиссий) берётся ATR-стоп движка — тот же, что используется при открытии позиции.
    /// </summary>
    private void TryFixSignalTakeProfit(
        string symbol, TradingSignal signal, Dictionary<string, decimal> indicators, decimal price)
    {
        if (!_config.Trading.AutoFixSignalTakeProfit)
            return;
        if (signal.Action is not (SignalAction.Buy or SignalAction.Sell))
            return;
        if (signal.EntryPrice is not > 0m || signal.StopLoss is not > 0m || signal.TakeProfit is not > 0m)
            return; // некорректные цены отклонит IsValid — починка не про это

        var side = signal.Action == SignalAction.Buy ? OrderSide.Buy : OrderSide.Sell;
        var entry = signal.EntryPrice.Value;
        var stop = signal.StopLoss.Value;
        var minRr = (decimal)_config.Trading.MinRiskRewardRatio;
        var decimals = Math.Clamp(GetPriceDecimals(symbol), 0, 18);

        var rr = _riskManager.CalculateNetRiskReward(side, entry, stop, signal.TakeProfit.Value);
        if (rr.HasValue && rr.Value >= minRr)
            return; // порог и так пройден — сигнал не трогаем

        var originalStop = stop;
        var originalTp = signal.TakeProfit.Value;

        if (!rr.HasValue)
        {
            // Риск после комиссий неположителен — SL модели бесполезен, берём ATR-стоп.
            var atr = indicators.TryGetValue("atr_abs", out var atrAbs) && atrAbs > 0m
                ? atrAbs
                : indicators.TryGetValue("atr_pct", out var atrPct) && atrPct > 0m
                    ? atrPct * price
                    : price * 0.02m; // тот же fallback, что в сайзинге
            stop = Math.Round(_riskManager.CalculateStopLoss(side, entry, atr), decimals, MidpointRounding.AwayFromZero);
            if (stop <= 0m)
                return;
        }

        var rawTp = _riskManager.CalculateTakeProfit(side, entry, stop);
        if (rawTp <= 0m)
            return; // риск неположителен даже с ATR-стопом — пусть отклоняется как раньше

        // Округление к тику строго «от входа»: ceil для long, floor для short.
        var tick = GetTickSize(symbol);
        decimal step;
        if (tick is > 0m)
        {
            step = tick.Value;
        }
        else
        {
            step = 1m;
            for (var i = 0; i < decimals; i++) step /= 10m;
        }
        var fixedTp = side == OrderSide.Buy
            ? Math.Ceiling(rawTp / step) * step
            : Math.Floor(rawTp / step) * step;
        fixedTp = Math.Round(fixedTp, decimals, MidpointRounding.AwayFromZero);
        if (fixedTp <= 0m)
            return;

        signal.StopLoss = stop;
        signal.TakeProfit = fixedTp;

        var newRr = _riskManager.CalculateNetRiskReward(side, entry, stop, fixedTp);
        signal.Reasoning = string.IsNullOrWhiteSpace(signal.Reasoning)
            ? $"TP исправлен движком до net R/R >= {minRr:0.##} после комиссий."
            : signal.Reasoning + $" [TP-FIX: движок довёл net R/R до порога {minRr:0.##} после комиссий]";

        TradeLog.Decision(symbol, "TP-FIX",
            rr.HasValue
                ? $"net R/R {rr.Value:0.##} < {minRr:0.##}: TP {originalTp} → {fixedTp}" +
                  (stop != originalStop ? $", SL {originalStop} → {stop} (ATR-стоп движка)" : string.Empty) +
                  $" (net R/R теперь {newRr:0.##})"
                : $"риск после комиссий <= 0: SL {originalStop} → {stop} (ATR-стоп движка), TP {originalTp} → {fixedTp} (net R/R {newRr:0.##})");
    }

    /// <summary>Контекст калибровки: пороги, спот-ограничения, цена и ATR для детерминированного входа.</summary>
    private CalibrationContext BuildCalibrationContext(
        string symbol, Dictionary<string, decimal> indicators, decimal price, bool hasPosition) => new()
    {
        MinConfidence = _config.AI.MinConfidence,
        HasPosition = hasPosition,
        AllowShort = _config.Trading.AllowShort,
        Price = price,
        AtrAbsolute = indicators.TryGetValue("atr_abs", out var atrAbs) && atrAbs > 0m
            ? atrAbs
            : indicators.TryGetValue("atr_pct", out var atrPct) ? atrPct * price : price * 0.01m,
        StopLossAtrMultiplier = (decimal)_config.Trading.StopLossAtrMultiplier,
        MinRiskReward = (decimal)_config.Trading.MinRiskRewardRatio,
        PriceDecimals = GetPriceDecimals(symbol)
    };

    private async Task<Dictionary<string, string>> ValidateSymbolsAsync(CancellationToken ct)
    {
        _activeSymbols = new List<string>();
        _symbolPrecision.Clear();
        var excluded = new Dictionary<string, string>(_config.Trading.SymbolExclusions, StringComparer.OrdinalIgnoreCase);
        var valid = (await _restClient.GetValidSymbolsAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var available = new List<string>();
        var unavailable = new List<string>();
        foreach (var symbol in _config.Trading.Symbols)
        {
            ct.ThrowIfCancellationRequested();
            // Runtime не доверяет только ConfigLoader: прямой AppConfig тоже проходит политику.
            var policyReason = SymbolUniversePolicy.EntryExclusionReason(symbol,
                _config.Trading.VolatileSymbols, _config.Trading.StablecoinPairs, _config.Trading.BenchmarkSymbols);
            if (policyReason != null)
            {
                excluded[symbol] = policyReason;
                continue;
            }
            if (!valid.Contains(symbol))
            {
                excluded[symbol] = "exchange_not_trading";
                unavailable.Add(symbol);
                continue;
            }
            try
            {
                var info = await _restClient.GetSymbolInfoAsync(symbol, ct);
                if (info is null)
                {
                    excluded[symbol] = "exchange_info_missing";
                    unavailable.Add(symbol);
                    continue;
                }
                if (!info.IsSpotTradingAllowed ||
                    !string.Equals(info.Status, "TRADING", StringComparison.OrdinalIgnoreCase))
                {
                    excluded[symbol] = !info.IsSpotTradingAllowed
                        ? "spot_trading_disabled" : "exchange_status_not_trading";
                    unavailable.Add(symbol);
                    continue;
                }
                var tick = info.TickSize > 0 ? info.TickSize : (decimal?)null;
                var configured = _config.Trading.SymbolPriceDecimals.TryGetValue(symbol, out var decimals);
                var precision = tick.HasValue ? TickDecimals(tick.Value) : configured ? decimals : 8;
                _symbolPrecision[symbol] = new SymbolPrecision(precision, tick, info.StepSize > 0 ? info.StepSize : null);
                _symbolEntryFilters[symbol] = new SymbolEntryFilters(
                    info.StepSize, info.MinQty, info.MarketStepSize, info.MarketMinQty,
                    info.MinNotional, info.ApplyMinNotionalToMarket);
                available.Add(symbol);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                excluded[symbol] = "exchange_info_error:" + ex.GetType().Name;
                unavailable.Add(symbol);
                TradeLog.Warn("SYMBOL-FILTERS-FAIL", symbol, "фильтры недоступны; пара отключена до следующего старта", ex);
            }
        }
        UnavailableSymbols = unavailable;
        ExcludedSymbols = excluded;
        foreach (var symbol in unavailable)
            TradeLog.Warn("SYMBOL-UNAVAILABLE", symbol,
                $"reason={excluded[symbol]} env={_restClient.EnvironmentName}");
        _activeSymbols = available;
        if (available.Count == 0)
            throw new InvalidOperationException("Нет доступных торговых пар: " + SymbolUniversePolicy.FormatReasons(excluded));
        return excluded;
    }

    /// <summary>
    /// Знаков после запятой у шага цены. Считаем арифметически, а не через строку:
    /// decimal.ToString("G29") для tick &lt;= 1e-5 даёт научную нотацию ("1E-05"), точки в ней нет,
    /// и старый разбор возвращал 0 знаков. Дальше entry округлялся до целого: DOGE 0.15 → 0,
    /// TryUpgrade молча выходил, и пара вечно печатала HOLD при prior выше порога.
    /// </summary>
    private static int TickDecimals(decimal tick)
    {
        if (tick <= 0m) return 0;
        var decimals = 0;
        var scaled = tick;
        while (decimals < 12 && scaled != decimal.Truncate(scaled))
        {
            scaled *= 10m;
            decimals++;
        }
        return decimals;
    }
    private int GetPriceDecimals(string symbol) =>
        _symbolPrecision.TryGetValue(symbol, out var precision) ? precision.PriceDecimals
        : _config.Trading.SymbolPriceDecimals.TryGetValue(symbol, out var decimals) ? decimals : 8;
    private decimal? GetTickSize(string symbol) =>
        _symbolPrecision.TryGetValue(symbol, out var precision) ? precision.TickSize : null;

    /// <summary>
    /// Троттлинг WAITING-лога: не чаще раза в минуту на символ, чтобы не залить logs/trades-*.log.
    /// </summary>
    private void LogWaiting(string symbol, string reason)
    {
        lock (_throttleLock)
        {
            if (_lastWaitingLog.TryGetValue(symbol, out var last) &&
                DateTime.UtcNow - last < TimeSpan.FromMinutes(1))
                return;

            _lastWaitingLog[symbol] = DateTime.UtcNow;
        }

        TradeLog.Waiting(symbol, reason);
    }

    /// <summary>Cooldown анализа символа (B11). true — можно спрашивать ИИ.</summary>
    private bool TryEnterAnalysisCooldown(string symbol)
    {
        var cooldown = _config.Trading.SymbolAnalysisCooldownSeconds;
        if (cooldown <= 0)
            return true;

        lock (_throttleLock)
        {
            if (_lastSymbolAnalysis.TryGetValue(symbol, out var last) &&
                DateTime.UtcNow - last < TimeSpan.FromSeconds(cooldown))
                return false;

            _lastSymbolAnalysis[symbol] = DateTime.UtcNow;
        }

        return true;
    }

    /// <summary>
    /// Периодический вопрос модели об открытой позиции: держать или закрыть.
    /// Без этого шага ИИ умел только открывать сделки, а выход был лишь по SL/TP.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _positionReviewInFlight = new(StringComparer.OrdinalIgnoreCase);

    private async Task ReviewOpenPositionAsync(string symbol, CancellationToken ct)
    {
        // P0.6a: only the explainer audits an existing deterministic position decision.
        // The legacy AiCanClosePositions switch no longer grants LLM exit authority.
        if (_config.AI.ResolvedRole != AiRole.Explainer)
            return;

        // P0.5: LLM-пересмотр позиции не входит в последовательный критический путь.
        // Off — пересмотра нет (защита в PositionMonitor); остальные роли — асинхронно,
        // один in-flight на символ, с жёстким бюджетом. Цикл анализа не ждёт инференс.
        if (_config.AI.ResolvedRole == AiRole.Off)
            return;

        if (!_positionReviewInFlight.TryAdd(symbol, 0))
            return;

        _metrics.RecordLlmReviewCloseRequest();
        _ = Task.Run(async () =>
        {
            try
            {
                using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budgetCts.CancelAfter(TimeSpan.FromSeconds(_config.AI.VetoBudgetSeconds));
                await ReviewOpenPositionCoreAsync(symbol, budgetCts.Token);
            }
            catch (OperationCanceledException)
            {
                TradeLog.Decision(symbol, "LLM-REVIEW-TIMEOUT",
                    $"пересмотр позиции не уложился в бюджет {_config.AI.VetoBudgetSeconds}s — позиция держится, защита в PositionMonitor");
            }
            catch (Exception ex)
            {
                TradeLog.Warn("LLM-REVIEW-FAIL", symbol, "асинхронный пересмотр позиции завершился ошибкой", ex);
            }
            finally
            {
                _positionReviewInFlight.TryRemove(symbol, out _);
            }
        }, ct);
    }

    private async Task ReviewOpenPositionCoreAsync(string symbol, CancellationToken ct)
    {

        lock (_reviewLock)
        {
            if (_lastPositionReview.TryGetValue(symbol, out var last) &&
                DateTime.UtcNow - last < TimeSpan.FromSeconds(_config.Trading.PositionReviewIntervalSeconds))
                return;

            _lastPositionReview[symbol] = DateTime.UtcNow;
        }

        var position = _positionManager.GetPositionBySymbol(symbol);
        if (position == null)
            return;

        var candle = GetCurrentCandle(symbol);
        if (candle == null)
            return;

        var indicators = _marketAnalyzer.GetIndicatorSnapshot(symbol, candle);
        if (indicators == null)
        {
            var warmup = _marketAnalyzer.GetWarmupInfo(symbol);
            LogWaiting(symbol, $"прогрев индикаторов: {warmup.Candles}/{warmup.Required} свечей (позиция открыта)");
            return;
        }

        var preScore = _calibrator.PreScore(indicators);
        TradeLog.Decision(symbol, "PRE-SCORE", $"{preScore.Summary}; {preScore.ReasonsText} (сопровождение позиции)");

        var newsContext = await _sentimentAnalyzer.GetNewsContextAsync(symbol, ct);
        var signal = await _aiModel.GetTradingSignalAsync(
            symbol, indicators, newsContext, BuildAiContext(symbol, position, preScore, indicators, candle.Close), ct);

        signal = _calibrator.Calibrate(signal, preScore, _config.AI.ResolvedConfidencePolicy,
            BuildCalibrationContext(symbol, indicators, candle.Close, hasPosition: true),
            blendModelWeight: _config.AI.BlendModelWeight);

        // P0.6a: LLM review is audit/explanation only. It cannot close a position;
        // deterministic SL/TP/trailing/emergency protection remains the sole exit path here.
        TradeLog.Decision(symbol, "LLM-REVIEW-AUDIT",
            $"role={_config.AI.ResolvedRole}; action_authority=false; {signal.Reasoning}");
    }

    /// <summary>
    /// Ручное закрытие позиции из UI — проходит через тот же пайплайн,
    /// что и автоматическое (ордер, история, баланс, события).
    /// </summary>
    public Task ClosePositionManuallyAsync(Position position, CancellationToken ct = default)
    {
        return ClosePositionAsync(position, "Manual", ct);
    }

    private async Task ClosePositionAsync(Position position, string reason, CancellationToken ct, decimal? exitPriceOverride = null)
    {
        // P0.2: закрытие может прийти одновременно из тик-контура, из ИИ-пересмотра и из UI.
        // Шлюз + повторная проверка «позиция ещё открыта» исключают двойной MARKET-выход.
        using (await _positionMonitor.LockSymbolAsync(position.Symbol, ct))
        {
            // Phase 1.5: работаем с живой записью менеджера, а не с устаревшей копией из вызывающего контура.
            var live = _positionManager.GetPositionById(position.Id);
            if (live is null || IsLifecycleClosed(position.Id))
            {
                TradeLog.Info("CLOSE-SKIPPED", position.Symbol,
                    $"позиция уже закрыта другим контуром — повторное закрытие ({reason}) пропущено");
                return;
            }

            if (live.CloseInFlight)
            {
                var stale = TimeSpan.FromSeconds(Math.Max(5, _config.Trading.CloseInFlightStaleSeconds));
                if (DateTime.UtcNow - live.CloseInFlightSinceUtc < stale)
                {
                    TradeLog.Info("CLOSE-SKIPPED", live.Symbol,
                        $"close_in_flight client_order_id={live.CloseClientOrderId} — второй sell-order ({reason}) не отправляется");
                    return;
                }
                TradeLog.Warn("CLOSE-INFLIGHT-STALE", live.Symbol,
                    $"close_in_flight висит {(DateTime.UtcNow - live.CloseInFlightSinceUtc).TotalSeconds:F0} c — флаг снят, решение по биржевой истории");
                live.CloseInFlight = false;
            }

            await ClosePositionCoreAsync(live, reason, ct, exitPriceOverride);
        }
    }

    private async Task ClosePositionCoreAsync(Position position, string reason, CancellationToken ct, decimal? exitPriceOverride)
    {
        // Phase 1.5: терминальное закрытие уже применено — новый sell-order запрещён.
        if (IsLifecycleClosed(position.Id))
        {
            TradeLog.Info("CLOSE-SKIPPED", position.Symbol,
                $"lifecycle={position.Id} уже терминально закрыт — повторный {reason} пропущен");
            return;
        }

        if (!ForcedCloseAllowed(position, reason)) return;

        TradeLog.Info("CLOSE-TRIGGER", position.Symbol, reason);

        // Фиксируем цену выхода: для SL/TP — уровень срабатывания, для ручного — текущая
        if (exitPriceOverride.HasValue && exitPriceOverride.Value > 0)
        {
            position.UpdatePrice(exitPriceOverride.Value);
        }

        var emergency = reason.StartsWith("KillSwitch", StringComparison.OrdinalIgnoreCase)
                        || reason is "NoExchangeProtection" or "Stop-Loss";

        // Phase 1.5: один terminal close на lifecycle. Флаг взводится до отправки и переживает
        // гонку с reconcile/монитором; снимается только если ордер не состоялся.
        position.CloseInFlight = true;
        position.CloseInFlightSinceUtc = DateTime.UtcNow;
        Order? order;
        try
        {
            order = await _orderExecutor.ClosePositionAsync(
                position, ct, GetCurrentOrderBook(position.Symbol), emergency);
        }
        finally
        {
            position.CloseInFlight = false;
        }

        if (order == null)
        {
            TradeLog.Rejected(position.Symbol, "close", "ордер на закрытие не исполнен — позиция остаётся открытой");
            await _positionManager.SavePositionsAsync(ct);
            return;
        }

        if (!string.IsNullOrWhiteSpace(order.ClientOrderId))
            position.CloseClientOrderId = order.ClientOrderId;

        // Для live используем фактическую цену исполнения, если она есть
        var fillPrice = order.AverageFillPrice;
        if (fillPrice.HasValue && fillPrice.Value > 0)
        {
            position.UpdatePrice(fillPrice.Value);
        }

        // Закрываем ровно тот объём, который реально продан/куплен биржей.
        var closedQuantity = order.ExecutedQuantity > 0 ? order.ExecutedQuantity : position.Quantity;
        if (closedQuantity > position.Quantity)
            closedQuantity = position.Quantity;

        // Phase 1.5: lifetime-учёт «куплено/продано» — суммарно нельзя продать больше купленного.
        closedQuantity = ReserveExitQuantity(position, closedQuantity, reason);
        if (closedQuantity <= 0m)
        {
            TradeLog.Error("CLOSE-NO-ALLOWANCE", position.Symbol,
                $"lifecycle={position.Id} reason={reason} — lifetime-объём исчерпан, локальная позиция снимается без нового ордера");
            MarkLifecycleClosed(position.Id);
            await _positionManager.RemovePositionAsync(position.Id, ct);
            await _e1.RefreshOutcomeAsync(position.Id, reason + "-oversell-guard", position.DustExchangeQuantity, ct);
            _reconciliation.FlagUnresolvedState($"close-no-allowance:{position.Symbol}");
            return;
        }

        var remaining = position.Quantity - closedQuantity;
        var isPartial = remaining > 0m;
        var entryCommissionPart = position.Quantity > 0m
            ? position.EntryCommission * closedQuantity / position.Quantity
            : 0m;

        // Создание записи о сделке (на фактически закрытый объём)
        var trade = new TradeRecord
        {
            Symbol = position.Symbol,
            Side = position.Side,
            Quantity = closedQuantity,
            EntryPrice = position.EntryPrice,
            ExitPrice = position.CurrentPrice,
            PnL = (position.Side == OrderSide.Buy
                ? position.CurrentPrice - position.EntryPrice
                : position.EntryPrice - position.CurrentPrice) * closedQuantity,
            PnLPercent = position.UnrealizedPnLPercent,
            Commission = entryCommissionPart + order.Commission,
            ExitReason = isPartial ? reason + "-partial" : reason,
            OpenedAt = position.OpenedAt,
            ClosedAt = DateTime.UtcNow,
            SignalId = position.SignalId,
            // P2.4: уверенность входа + начальный риск для expectancy после издержек.
            InitialStopLoss = position.InitialStopLoss,
            SignalConfidence = position.SignalConfidence,
            ModelConfidence = position.ModelConfidence,
            EngineConfidence = position.EngineConfidence,
            ConfidenceSource = position.ConfidenceSource,
            // P2.6: фичи скоринга и направление входа — в историю для валидации весов.
            ScoreFeatures = position.ScoreFeatures,
            EngineDirection = position.EngineDirection
        };

        await _tradeHistory.SaveTradeAsync(trade, ct);
        _portfolioRisk.RecordClosedTrade(trade);
        _metrics.RecordPortfolioLossStreak(_portfolioRisk.ConsecutiveLosses, _portfolioRisk.PauseUntilUtc);

        // P0.5: тот же close-ордер может прийти позже как executionReport/запись журнала —
        // кумулятив уже применён штатным закрытием, повтор не должен уменьшить позицию ещё раз.
        if (!string.IsNullOrWhiteSpace(order.ClientOrderId))
            position.RecordExchangeFill(order.ClientOrderId, order.ExecutedQuantity);

        if (isPartial)
        {
            // Биржа исполнила не всё (тонкий стакан / лимиты): остаток остаётся открытой
            // позицией и будет дозакрыт на следующем цикле, а не «исчезает» из учёта.
            position.ReduceQuantity(closedQuantity);
            position.EntryCommission = Math.Max(0m, position.EntryCommission - entryCommissionPart);
            await _positionManager.SavePositionsAsync(ct);
            await _e1.RefreshOutcomeAsync(position.Id, reason + "-partial", position.DustExchangeQuantity, ct);

            // P1.2: остаток после частичного выхода не должен остаться без биржевой защиты.
            if (await _orderExecutor.EnsureProtectionAsync(position, ct))
                await _positionManager.SavePositionsAsync(ct);

            TradeLog.Rejected(position.Symbol, "close-partial",
                $"закрыто {closedQuantity} из {closedQuantity + remaining}, остаток {remaining} остаётся открытым");
        }
        else
        {
            // Phase 1.5: сначала фиксируем терминальный факт lifecycle, затем пишем evidence.
            // Сбой E1 больше не откатывает закрытие и не порождает повторную продажу.
            MarkLifecycleClosed(position.Id);
            await _positionManager.RemovePositionAsync(position.Id, ct);
            await _e1.RefreshOutcomeAsync(position.Id, reason, position.DustExchangeQuantity, ct);

            lock (_reviewLock)
            {
                _lastPositionReview.Remove(position.Symbol);
            }
        }

        var mode = _config.Trading.PaperTradingMode ? "PAPER" : _restClient.EnvironmentName;
        TradeLog.Closed(position.Symbol, trade.ExitReason, closedQuantity, position.EntryPrice, position.CurrentPrice,
            trade.NetPnL, trade.PnLPercent, trade.Commission, mode);

        // P0.1: обновление капитала от mark-to-market equity; paper cash-ledger возвращает proceeds.
        if (_config.Trading.PaperTradingMode)
        {
            _paperFreeCash += closedQuantity * position.CurrentPrice - order.Commission;
        }
        await RefreshAccountSnapshotAsync(ct);

        if (isPartial)
        {
            // Позиция ещё открыта — событие закрытия не шлём (UI иначе уберёт её из списка).
            // Количество в UI обновится само: Position реализует INotifyPropertyChanged.
            _metrics.RecordTrade(trade);
            OnPartialPositionClosed?.Invoke(trade);
        }
        else
        {
            OnPositionClosed?.Invoke(trade);
        }

    }

    /// <summary>
    /// P0.4: часы биржи для границы торгового дня. Смещение оценивается один раз при старте
    /// (полудлина RTT, как в синхронизации подписи BinanceRestClient); при недоступности /time
    /// остаются локальные UTC-часы.
    /// </summary>
    private async Task ConfigureExchangeClockAsync(CancellationToken ct)
    {
        try
        {
            var localBefore = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var serverTime = await _restClient.GetServerTimeAsync(ct);
            var localAfter = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var offsetMs = serverTime - localBefore - (localAfter - localBefore) / 2;

            _riskManager.DrawdownTracker.SetExchangeClock(() => DateTime.UtcNow.AddMilliseconds(offsetMs));
            _portfolioRisk.SetExchangeClock(() => DateTime.UtcNow.AddMilliseconds(offsetMs));
            TradeLog.Info("RISK-DAY-CLOCK", null, $"exchange_time_offset_ms={LogOptions.N(offsetMs)}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось получить время биржи — граница дня по локальным UTC-часам");
        }
    }

    private string RiskEnvironment => _config.Trading.PaperTradingMode ? "Paper" : _restClient.EnvironmentName;
    private string RiskAccountId => _config.Trading.PaperTradingMode
        ? (string.IsNullOrWhiteSpace(_config.Trading.PaperTradingAccountId) ? "default" : _config.Trading.PaperTradingAccountId)
        : _restClient.AccountId;
    private const string RiskQuoteAsset = "USDT";

    /// <summary>P0.1: снимок риск-состояния на диск, изолированный по окружению/счёту/quote.</summary>
    private Task PersistRiskStateAsync(CancellationToken ct)
    {
        var state = _riskManager.DrawdownTracker.CaptureState() with
        {
            FreeCashQuote = _accountSnapshot?.FreeCashQuote,
            KillSwitchEngaged = IsKillSwitchEngaged,
            KillSwitchReason = _killSwitchReason,
            KillSwitchEngagedAtUtc = _killSwitchEngagedAtUtc,
            KillSwitchEquityAtEngage = _killSwitchEquityAtEngage,
            KillSwitchRequiresManualReset = _killSwitchRequiresManualReset,
            KillSwitchCloseStatus = _killSwitchCloseStatus
        };
        return _stateRepository.SaveRiskStateAsync(state, RiskEnvironment, RiskAccountId, RiskQuoteAsset, ct);
    }

    /// <summary>P0.1: обновить equity/free-cash снимок, персист и kill-switch.</summary>
    private async Task UpdateRiskSnapshotAsync(AccountSnapshot snapshot, CancellationToken ct)
    {
        var engageReason = string.Empty;
        await _accountSnapshotLock.WaitAsync(ct);
        try
        {
            _accountSnapshot = snapshot;
            var tracker = _riskManager.DrawdownTracker;
            if (snapshot.IsValuationComplete)
                _riskManager.UpdateEquity(snapshot.EquityQuote);

            await PersistRiskStateAsync(ct);
            TradeLog.RiskSnapshot(snapshot.Environment, snapshot.AccountId, snapshot.QuoteAsset,
                snapshot.EquityQuote, snapshot.FreeCashQuote, snapshot.LockedCashQuote,
                tracker.CaptureState().PeakBalance, tracker.DayStartBalance,
                tracker.CurrentDrawdownPercent * 100m, tracker.DailyPnL,
                snapshot.Source, snapshot.IsValuationComplete);

            if (!snapshot.IsValuationComplete)
            {
                TradeLog.Warn("RISK-VALUATION-INCOMPLETE", null,
                    "оценка капитала неполна — входы fail-closed, equity не обновлён",
                    gateKey: "risk-valuation-incomplete");
                return;
            }

            if (_riskManager.ShouldEngageKillSwitch && !IsKillSwitchEngaged)
                engageReason = tracker.IsMaxDrawdownHit ? "MaxDrawdown" : "DailyLoss";
        }
        finally
        {
            _accountSnapshotLock.Release();
        }

        // P0.2: закрытие позиций обновляет снимок капитала; не держим snapshot-lock во избежание deadlock.
        if (engageReason.Length > 0)
            await EngageKillSwitchAsync(engageReason, ct);
    }

    private async Task RefreshAccountSnapshotAsync(CancellationToken ct)
    {
        var snapshot = _config.Trading.PaperTradingMode
            ? _accountSnapshots.CreatePaper(_paperFreeCash)
            : await _accountSnapshots.CreateLiveAsync(ct);
        await UpdateRiskSnapshotAsync(snapshot, ct);
    }

    /// <summary>P0.1: явная регистрация депозита/вывода/трансфера корректирует baseline, не торговый PnL.</summary>
    public async Task RegisterExternalCashFlowAsync(decimal amount, string reason, CancellationToken ct = default)
    {
        if (amount == 0m) return;
        _riskManager.DrawdownTracker.RegisterExternalCashFlow(amount);
        if (_config.Trading.PaperTradingMode)
            _paperFreeCash += amount;
        TradeLog.Warn("RISK-CASH-FLOW", null,
            $"amount={LogOptions.N(amount, "F2")} reason={reason} cumulative={LogOptions.N(_riskManager.DrawdownTracker.CumulativeExternalCashFlow, "F2")}");
        await RefreshAccountSnapshotAsync(ct);
    }

    /// <summary>
    /// P0.5: применить филл, уже случившийся на бирзе (защитная нога / close из журнала),
    /// к позиции, истории и балансу — БЕЗ повторного MARKET. Дедуп по кумулятиву ордера
    /// в Position.AppliedExchangeFills переживает рестарт и повторные executionReport.
    /// </summary>
    private async Task<bool> ApplyExchangeFillAsync(Position position, ExecutionReport report, string reason, CancellationToken ct)
    {
        decimal closedQuantity;
        TradeRecord trade;
        bool fullyClosed;

        using (await _positionMonitor.LockSymbolAsync(position.Symbol, ct))
        {
            var live = _positionManager.GetPositionById(position.Id);
            if (live is null || IsLifecycleClosed(position.Id)) return false;

            var unapplied = live.GetUnappliedExchangeFill(report.ClientOrderId, report.CumulativeQuantity);
            if (unapplied <= 0) return false;

            var e1Role = reason is "ExchangeStopLoss" or "ExchangeTakeProfit" ? FillRole.ProtectiveExit : FillRole.Exit;
            var protIdentity = live.Protection != null
                ? (live.Protection.OrderListId > 0 ? $"oco:{live.Protection.OrderListId}" : $"oco-client:{live.Protection.ListClientOrderId}")
                : null;
            await _e1.RecordReportAsync(live, report, e1Role, protIdentity, ct);
            if (e1Role == FillRole.ProtectiveExit)
                await _e1.RecordProtectionAsync(live, reason, true, ct);

            // Phase 1.5: биржевой филл применяется к позиции независимо от того, легла ли
            // evidence в E1 (запись выше уже fail-safe). Lifetime-учёт не даёт продать дважды.
            closedQuantity = ReserveExitQuantity(live, Math.Min(unapplied, live.Quantity), reason);
            if (closedQuantity <= 0)
            {
                TradeLog.Warn("EXCHANGE-FILL-NO-ALLOWANCE", live.Symbol,
                    $"lifecycle={live.Id} reason={reason} — lifetime-объём исчерпан, филл не уменьшает позицию повторно");
                return false;
            }

            var exitPrice = report.LastPrice > 0
                ? report.LastPrice
                : report.CumulativeQuantity > 0 && report.CumulativeQuoteQuantity > 0
                    ? report.CumulativeQuoteQuantity / report.CumulativeQuantity
                    : live.CurrentPrice;
            if (exitPrice <= 0) exitPrice = live.EntryPrice;

            live.UpdatePrice(exitPrice);

            var entryCommissionPart = live.Quantity > 0
                ? live.EntryCommission * closedQuantity / live.Quantity
                : 0m;

            trade = new TradeRecord
            {
                Symbol = live.Symbol,
                Side = live.Side,
                Quantity = closedQuantity,
                EntryPrice = live.EntryPrice,
                ExitPrice = exitPrice,
                PnL = (live.Side == OrderSide.Buy ? exitPrice - live.EntryPrice : live.EntryPrice - exitPrice) * closedQuantity,
                PnLPercent = live.EntryPrice > 0
                    ? (live.Side == OrderSide.Buy ? exitPrice - live.EntryPrice : live.EntryPrice - exitPrice) / live.EntryPrice * 100m
                    : 0m,
                Commission = entryCommissionPart + report.Commission,
                ExitReason = reason,
                OpenedAt = live.OpenedAt,
                ClosedAt = report.TransactionTimeUtc != default ? report.TransactionTimeUtc : DateTime.UtcNow,
                SignalId = live.SignalId,
                // P2.4: биржевой филл (защитная нога/close) — та же метка уверенности и риск.
                InitialStopLoss = live.InitialStopLoss,
                SignalConfidence = live.SignalConfidence,
                ModelConfidence = live.ModelConfidence,
                EngineConfidence = live.EngineConfidence,
                ConfidenceSource = live.ConfidenceSource,
                // P2.6: биржевой филл — те же фичи скоринга и направление входа.
                ScoreFeatures = live.ScoreFeatures,
                EngineDirection = live.EngineDirection
            };

            await _tradeHistory.SaveTradeAsync(trade, ct);
            _portfolioRisk.RecordClosedTrade(trade);
            _metrics.RecordPortfolioLossStreak(_portfolioRisk.ConsecutiveLosses, _portfolioRisk.PauseUntilUtc);

            // Дедуп-леджер фиксируем до удаления/уменьшения позиции.
            live.RecordExchangeFill(report.ClientOrderId, report.CumulativeQuantity);
            live.EntryCommission = Math.Max(0m, live.EntryCommission - entryCommissionPart);

            var remaining = live.Quantity - closedQuantity;
            fullyClosed = remaining <= 0m;
            if (fullyClosed)
            {
                MarkLifecycleClosed(live.Id);
                await _positionManager.RemovePositionAsync(live.Id, ct);
                await _e1.RefreshOutcomeAsync(live.Id, reason, live.DustExchangeQuantity, ct);
                lock (_reviewLock) _lastPositionReview.Remove(live.Symbol);
            }
            else
            {
                live.ReduceQuantity(closedQuantity);
                await _positionManager.SavePositionsAsync(ct);
                await _e1.RefreshOutcomeAsync(live.Id, reason + "-partial", live.DustExchangeQuantity, ct);
            }
        }

        // Частичный филл защитной ноги мог оставить объём защиты больше объёма позиции —
        // снимаем старую OCO, контур сопровождения поставит новую на остаток.
        if (!fullyClosed && reason is "ExchangeStopLoss" or "ExchangeTakeProfit")
        {
            var live = _positionManager.GetPositionById(position.Id);
            if (live?.Protection != null)
            {
                await _orderExecutor.CancelProtectionAsync(live, ct);
                await _positionManager.SavePositionsAsync(ct);
            }
        }

        var mode = _config.Trading.PaperTradingMode ? "PAPER" : _restClient.EnvironmentName;
        TradeLog.Closed(position.Symbol, reason, closedQuantity, trade.EntryPrice, trade.ExitPrice,
            trade.NetPnL, trade.PnLPercent, trade.Commission, mode);

        await RefreshAccountSnapshotAsync(ct);

        _metrics.RecordReconciliationFill();
        if (fullyClosed)
            OnPositionClosed?.Invoke(trade);
        else
        {
            _metrics.RecordTrade(trade);
            OnPartialPositionClosed?.Invoke(trade);
        }

        TradeLog.Warn("EXCHANGE-FILL-APPLIED", position.Symbol,
            $"reason={reason} qty={LogOptions.N(closedQuantity)}@{LogOptions.N(trade.ExitPrice)} " +
            $"remaining={LogOptions.N(fullyClosed ? 0m : position.Quantity)} — без повторного MARKET");
        return true;
    }

    /// <summary>
    /// P0.5: подхватить сиротскую позицию бота (entry исполнился, пока процесс был мёртв,
    /// либо positions.json потерял запись). Ставим защиту сразу; fail-closed сохраняет правило
    /// «нет биржевой защиты → нет позиции».
    /// </summary>
    private async Task AdoptOrphanPositionAsync(Position orphan, CancellationToken ct)
    {
        using (await _positionMonitor.LockSymbolAsync(orphan.Symbol, ct))
        {
            if (_positionManager.GetPositionBySymbol(orphan.Symbol) != null) return;

            var atr = GetAtrAbsolute(orphan.Symbol) ?? orphan.EntryPrice * 0.02m;
            orphan.StopLoss = _riskManager.CalculateStopLoss(orphan.Side, orphan.EntryPrice, atr);
            orphan.TakeProfit = _riskManager.CalculateTakeProfit(orphan.Side, orphan.EntryPrice, orphan.StopLoss);
            orphan.InitialStopLoss = orphan.StopLoss;
            orphan.SignalId = "reconciled";
            if (orphan.LifetimeBoughtQuantity <= 0m)
                orphan.LifetimeBoughtQuantity = orphan.Quantity;

            // Phase 1.5: подхват без E1-идентичности не должен бросать «E1 identity is required»
            // и ронять reconcile. Восстанавливаем identity из журнала намерений, иначе external.
            await _e1.RestoreIdentityAsync(orphan, ct);

            await _positionManager.AddPositionAsync(orphan, ct);
            await _e1.AttachPositionAsync(orphan, ct);
            var protectedOk = await _orderExecutor.EnsureProtectionAsync(orphan, ct);
            if (protectedOk && orphan.Protection != null)
                await _positionManager.SavePositionsAsync(ct);

            OnPositionOpened?.Invoke(orphan);
            TradeLog.Opened(orphan.Symbol, orphan.Side.ToString(), orphan.Quantity, orphan.EntryPrice,
                orphan.StopLoss, orphan.TakeProfit,
                _config.Trading.PaperTradingMode ? "PAPER" : _restClient.EnvironmentName, orphan.OpeningOrderId);

            if (!protectedOk && _config.Trading.ClosePositionIfProtectionFails &&
                _config.Trading.ExchangeProtectionEnabled && !_config.Trading.PaperTradingMode)
            {
                TradeLog.Error("RECON-ADOPT-UNPROTECTED", orphan.Symbol, "сиротская позиция без защиты — аварийное закрытие");
                await ClosePositionCoreAsync(orphan, "NoExchangeProtection", ct, null);
            }
        }
    }

    /// <summary>
    /// P0.6: взвести kill-switch (авто по лимиту риска или ручная паника из UI):
    /// блокировка новых входов + закрытие всех позиций штатным пайплайном
    /// (снятие OCO → market → история → баланс → события). Идемпотентно.
    /// </summary>
    public async Task<int> EngageKillSwitchAsync(string reason, CancellationToken ct = default)
    {
        var first = Interlocked.CompareExchange(ref _killSwitchEngaged, 1, 0) == 0;
        if (first)
        {
            _killSwitchReason = reason;
            _killSwitchEngagedAtUtc = DateTime.UtcNow;
            _killSwitchEquityAtEngage = _accountSnapshot?.EquityQuote ?? _riskManager.DrawdownTracker.CurrentEquity;
            _killSwitchRequiresManualReset = true;
            _killSwitchCloseStatus = "CLOSING";
            _metrics.RecordKillSwitch();
            TradeLog.KillSwitchEngaged(reason, _killSwitchEquityAtEngage, _killSwitchRequiresManualReset);
            Pause(); // если уже Running; в Starting флаг выше всё равно блокирует входы
            OnKillSwitchEngaged?.Invoke(reason);
            await PersistRiskStateAsync(ct);
        }

        var total = _positionManager.GetOpenPositions().Count;
        var closed = await CloseAllPositionsAsync($"KillSwitch:{reason}", ct);
        var remaining = _positionManager.GetOpenPositions().Count;
        _killSwitchCloseStatus = remaining > 0 ? "UNPROTECTED_OR_OPEN" : "ALL_CLOSED";
        TradeLog.KillSwitchCloseResult(closed, total, remaining, _killSwitchCloseStatus);
        await PersistRiskStateAsync(ct);
        return closed;
    }

    /// <summary>
    /// P0.6: закрыть все открытые позиции. Каждое закрытие проходит посимвольный шлюз
    /// и повторную проверку «ещё открыта» — гонка с тик-контуром/ИИ-пересмотром исключена.
    /// Возвращает число реально закрытых позиций; сбой одной не прерывает остальные.
    /// </summary>
    public async Task<int> CloseAllPositionsAsync(string reason, CancellationToken ct = default)
    {
        var open = _positionManager.GetOpenPositions().ToList();
        if (open.Count == 0) return 0;

        TradeLog.Warn("KILL-SWITCH-CLOSING", null, $"positions={open.Count} reason={reason}");
        var closed = 0;
        foreach (var position in open)
        {
            try
            {
                await ClosePositionAsync(position, reason, ct);
                if (_positionManager.GetPositionById(position.Id) is null)
                    closed++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                TradeLog.Error("KILL-SWITCH-CLOSE-FAIL", position.Symbol, "позиция остаётся под сопровождением", ex);
            }
        }

        TradeLog.Warn("KILL-SWITCH-CLOSED", null, $"closed={closed}/{open.Count} reason={reason}");
        return closed;
    }

    /// <summary>
    /// P0.2: явный сброс kill-switch. Не обнуляет peak/day_start.
    /// Запрещён, если лимит всё ещё пробит или остались открытые позиции.
    /// </summary>
    public async Task<bool> ResetKillSwitchAsync(string operatorReason, CancellationToken ct = default)
    {
        if (!IsKillSwitchEngaged)
        {
            TradeLog.KillSwitchResetDenied("not-engaged");
            return false;
        }

        var openCount = _positionManager.GetOpenPositions().Count;
        if (openCount > 0 || _killSwitchCloseStatus == "UNPROTECTED_OR_OPEN")
        {
            TradeLog.KillSwitchResetDenied($"UNPROTECTED_OR_OPEN open={openCount}");
            return false;
        }

        if (!_riskManager.DrawdownTracker.IsTradingAllowed)
        {
            var reason = _riskManager.DrawdownTracker.IsMaxDrawdownHit ? "MaxDrawdown" : "DailyLoss";
            TradeLog.KillSwitchResetDenied($"risk-limit-still-breached:{reason}");
            return false;
        }

        if (Interlocked.Exchange(ref _killSwitchEngaged, 0) == 1)
        {
            _killSwitchCloseStatus = "RESET";
            TradeLog.KillSwitchReset(operatorReason);
            await PersistRiskStateAsync(ct);
            return true;
        }
        return false;
    }

    /// <summary>Пишет срез баланса/просадки в logs/trades-*.log.</summary>
    private void LogBalance()
    {
        var tracker = _riskManager.DrawdownTracker;
        var snapshot = _accountSnapshot;
        TradeLog.Balance(
            _config.Trading.PaperTradingMode ? "PAPER" : _restClient.EnvironmentName,
            snapshot?.FreeCashQuote ?? 0m,
            snapshot?.EquityQuote ?? tracker.CurrentEquity,
            tracker.CurrentDrawdownPercent * 100m);
    }

    private void OnKlineReceived(OHLCV candle)
    {
        // 1. Сохраняем текущую свечу в кэш для актуальной цены. Внутриинтервальные
        //    тики не участвуют в volume_ratio: снапшот берёт объём последней закрытой
        //    свечи из IndicatorCalculator, обновляемого ниже только при IsClosed.
        lock (_candleLock)
        {
            _currentCandles[candle.Symbol] = candle;
        }

        // 2/3. P1.4: отмечаем свежесть kline и добавляем закрытую свечу с детектом разрыва.
        //     При разрыве индикаторы не маскируют дыру: символ уходит в REST-восстановление.
        if (candle.IsClosed)
        {
            var append = _marketAnalyzer.UpdateIndicators(candle.Symbol, candle, _candleInterval, DateTime.UtcNow);
            if (append == CandleAppendResult.Gap)
            {
                _metrics.RecordMarketDataGap();
                EnqueueMarketDataRecovery(candle.Symbol, "candle-gap", immediate: true);
            }
        }
        else
        {
            _marketAnalyzer.NoteKline(candle.Symbol, DateTime.UtcNow);
        }

        // 4. Обновляем текущую цену для расчета PnL открытых позиций
        _positionManager.UpdatePrices(candle.Symbol, candle.Close);

        // 5. ATR для трейлинга пересчитываем только на закрытой свече (индикаторы всё равно
        //    обновляются лишь по закрытым свечам) — тик-контур берёт готовое значение.
        if (candle.IsClosed)
            RefreshAtrCache(candle.Symbol, candle);

        // 6. P0.2: тик в независимый контур сопровождения. Только запись в словарь:
        //    WebSocket receive-loop не должен ждать REST/SL-проверок.
        _positionMonitor.OnKlineTick(candle);
    }

    private void RefreshAtrCache(string symbol, OHLCV candle)
    {
        try
        {
            var indicators = _marketAnalyzer.GetIndicatorSnapshot(symbol, candle);
            if (indicators == null)
                return;

            var atr = indicators.TryGetValue("atr_abs", out var abs) && abs > 0m
                ? abs
                : indicators.TryGetValue("atr_pct", out var pct) ? pct * candle.Close : 0m;

            if (atr > 0m)
                _atrAbsolute[symbol] = atr;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Не удалось обновить ATR для {Symbol}", symbol);
        }
    }

    /// <summary>Абсолютный ATR символа для тик-контура (null — ещё не прогрет).</summary>
    private decimal? GetAtrAbsolute(string symbol) =>
        _atrAbsolute.TryGetValue(symbol, out var atr) && atr > 0m ? atr : null;

    private void OnOrderBookReceived(OrderBook orderBook)
    {
        // P1.4: отметка свежести стакана; после реконнекта старый стакан был сброшен.
        _marketAnalyzer.NoteOrderBook(orderBook.Symbol, DateTime.UtcNow);

        // Кэшируем актуальный стакан для каждого символа
        lock (_orderBookLock)
        {
            _currentOrderBooks[orderBook.Symbol] = orderBook;
        }

        // P0.2: стакан — самый частый тик (partial-depth 1 с) и лучшая цена выхода
        // (bid для long, ask для short). Обработчик не блокируется.
        _positionMonitor.OnOrderBookTick(orderBook);

        // Логирование аномально широкого спреда (защита от манипуляций или низкой ликвидности)
        if (orderBook.Spread.HasValue && orderBook.MidPrice.HasValue)
        {
            var spreadPercent = (orderBook.Spread.Value / orderBook.MidPrice.Value) * 100m;
            if (spreadPercent > 0.5m) // Спред более 0.5%
            {
                TradeLog.Warn("WIDE-SPREAD", orderBook.Symbol,
                    $"spread_pct={LogOptions.N(spreadPercent, "F3")} bid={LogOptions.N(orderBook.BestBid)} ask={LogOptions.N(orderBook.BestAsk)}",
                    gateKey: "wide-spread:" + orderBook.Symbol);
            }
        }
    }

    private OrderBook? GetCurrentOrderBook(string symbol)
    {
        lock (_orderBookLock)
        {
            return _currentOrderBooks.TryGetValue(symbol, out var ob) ? ob : null;
        }
    }

    /// <summary>
    /// P1.5: фильтр ликвидности перед входом. Пороги — глобальные из TradingConfig,
    /// точечные — из symbols.json (SymbolLiquidityOverrides: BTTC/STRAX/FLR и прочие тонкие пары).
    /// Отказ попадает в воронку как Rejected/liquidity и агрегируется в метрику;
    /// применяется и в paper-режиме, чтобы «бумажная» статистика не врала.
    /// </summary>
    private bool PassesLiquidityFilter(
        string symbol,
        OrderSide side,
        decimal quantity,
        decimal fallbackPrice,
        OrderBook? orderBook,
        decimal lastPrice)
    {
        var cfg = _config.Trading;
        if (!cfg.LiquidityFilterEnabled)
            return true;

        cfg.SymbolLiquidityOverrides.TryGetValue(symbol, out var ov);
        var thresholds = new LiquidityThresholds
        {
            MaxSpreadPercent = ov?.MaxSpreadPercent ?? cfg.MaxEntrySpreadPercent,
            MinDepthQuote = ov?.MinDepthQuote ?? cfg.MinEntryDepthQuote,
            MinDepthNotionalRatio = cfg.MinDepthNotionalRatio,
            DepthLevels = cfg.LiquidityDepthLevels,
            MinCandleQuoteVolume = ov?.MinCandleQuoteVolume ?? cfg.MinCandleQuoteVolume
        };

        // Средний объём закрытой свечи в котировке: SMA20 объёма закрытых свечей × текущая цена.
        // Незакрытая WS-свеча в оценку не попадает (контракт фикса volume_ratio, проход 11).
        var avgCandleQuoteVolume = _marketAnalyzer.GetVolumeSma20(symbol) * lastPrice;

        var result = LiquidityFilter.Evaluate(orderBook, side, quantity, fallbackPrice, avgCandleQuoteVolume, thresholds);
        if (result.Passed)
            return true;

        _metrics.RecordLiquidityRejection(result.Reason);
        TradeLog.Rejected(symbol, "liquidity", $"[{result.Reason}] {result.Detail}");
        return false;
    }

    private void OnWebSocketError(Exception ex)
    {
        _logger.LogError(ex, "Ошибка WebSocket");
    }

    /// <summary>P1.4: потеря рыночного WS — сразу блокируем входы и сбрасываем стакан.</summary>
    private void OnWebSocketDisconnected()
    {
        _metrics.RecordMarketDataDisconnect();
        lock (_orderBookLock)
        {
            _currentOrderBooks.Clear();
        }

        foreach (var symbol in _activeSymbols)
        {
            _marketAnalyzer.MarkRecoveryRequired(symbol, "ws-disconnected");
            EnqueueMarketDataRecovery(symbol, "ws-disconnected", immediate: true);
        }

        TradeLog.Warn("MARKET-DATA-WS-DOWN", null,
            "входы заблокированы, стакан сброшен, ожидается REST-восстановление после реконнекта");
    }

    /// <summary>P1.4: рыночный WS переподключился — история могла потерять свечи, требуется REST-дозагрузка.</summary>
    private void OnWebSocketReconnected()
    {
        _metrics.RecordMarketDataReconnect();
        lock (_orderBookLock)
        {
            _currentOrderBooks.Clear();
        }

        // P0.3a шаг 3: один молчащий символ не должен стирать прогресс здоровых пар.
        // Пара с непрерывной закрытой сеткой свечей сохраняется без REST-перезагрузки,
        // но входы блокируются до свежего kline И стакана, полученных после реконнекта.
        var kept = 0;
        var reloaded = 0;
        foreach (var symbol in _activeSymbols)
        {
            if (_marketAnalyzer.TryResumeAfterReconnect(symbol, DateTime.UtcNow, _candleInterval, MarketDataLiveConfirmWindow))
            {
                kept++;
                _metrics.RecordMarketDataReconnectKept();
                continue;
            }

            reloaded++;
            _metrics.RecordMarketDataReconnectReload();
            _marketAnalyzer.MarkRecoveryRequired(symbol, "ws-reconnected");
            EnqueueMarketDataRecovery(symbol, "ws-reconnected", immediate: true);
        }

        TradeLog.Warn("MARKET-DATA-WS-UP", null,
            $"входы заблокированы до live-подтверждения; сохранено без REST-перезагрузки: {kept}, отправлено на REST-восстановление: {reloaded}");
    }

    private void EnqueueMarketDataRecovery(string symbol, string reason, bool immediate = false)
    {
        _marketAnalyzer.MarkRecoveryRequired(symbol, reason);
        _marketDataRecoveryReason[symbol] = reason;
        _pendingMarketDataRecovery[symbol] = 0;
        _nextMarketDataRecoveryAttemptUtc.AddOrUpdate(
            symbol,
            immediate ? DateTime.MinValue : DateTime.UtcNow,
            (_, existing) => immediate ? DateTime.MinValue : existing);
    }

    /// <summary>
    /// P1.4/P0.3: watchdog протухания + REST-дозагрузка свечей и повторный прогрев.
    /// P0.3: REST-прогрев (history_reloaded) не разрешает входы. После прогрева требуется живой WS-тик
    /// (stream_live) в окне MarketDataLiveConfirmSeconds; иначе resubscribe → reconnect → карантин.
    /// Сопровождение открытых позиций не блокируется: PositionMonitor и биржевые OCO продолжают работать.
    /// </summary>
    private async Task RecoverMarketDataAsync(CancellationToken ct)
    {
        var maxAge = TimeSpan.FromSeconds(_config.Trading.MarketDataMaxAgeSeconds);
        var now = DateTime.UtcNow;

        // 0. Карантин истёк — символ возвращается в восстановление (не сразу в торговлю).
        foreach (var symbol in _activeSymbols)
        {
            if (_marketAnalyzer.TryLiftExpiredQuarantine(symbol, now))
                EnqueueMarketDataRecovery(symbol, "quarantine-expired", immediate: true);

            if (_marketAnalyzer.TryConsumeQuarantineLift(symbol, out var liftReason, out var servedSeconds))
            {
                _metrics.RecordMarketDataQuarantineLift();
                _metrics.RecordMarketDataQuarantineServed(servedSeconds);
                // P0.3a шаг 4: снятие карантина не разрешает вход само по себе — итоговый data-гейт.
                var liftEntriesAllowed = _wsClient.IsConnected
                    && _marketAnalyzer.IsDataFresh(symbol, now, maxAge, out _);
                TradeLog.DataQuarantineLifted(symbol, liftReason, liftEntriesAllowed);
            }
        }

        // 1. Живой WS-тик подтверждён — только теперь символ считается delivering.
        foreach (var symbol in _activeSymbols)
        {
            if (!_marketAnalyzer.TryConsumeLiveConfirmation(symbol, out var secondsToLive))
                continue;

            _metrics.RecordMarketDataLiveConfirmed(secondsToLive);
            _metrics.RecordMarketDataRecoveryCycle(symbol, success: true);
            // P0.3a шаг 4: итоговый вердикт data-гейта (WS + свежесть обоих стримов), а не промежуточный флаг.
            var entriesAllowed = _wsClient.IsConnected
                && _marketAnalyzer.IsDataFresh(symbol, now, maxAge, out _);
            TradeLog.DataLiveConfirmed(symbol, secondsToLive, _marketAnalyzer.GetResubscribeAttempts(symbol), entriesAllowed);
            _pendingMarketDataRecovery.TryRemove(symbol, out _);
            _nextMarketDataRecoveryAttemptUtc.TryRemove(symbol, out _);
            _marketDataRecoveryReason.TryRemove(symbol, out _);
        }

        // 2. Протухание: REST лечит только историю; awaiting-live-tick лечится resubscribe/reconnect.
        foreach (var symbol in _activeSymbols)
        {
            if (_marketAnalyzer.IsQuarantined(symbol, now, out _))
                continue;

            if (_marketAnalyzer.IsDataFresh(symbol, now, maxAge, out var reason))
                continue;

            // no-book сразу после старта/реконнекта сам по себе не лечится REST — ждём WS-стакан.
            // P0.3a: awaiting-live-tick теперь имеет суффикс :kline/:book/:both — матч по префиксу.
            if (reason is "no-book" || reason.StartsWith("awaiting-live-tick", StringComparison.Ordinal))
                continue;

            if (reason is "stale-book" or "stale-kline" or "no-kline" or "warmup-incomplete" or "candle-gap" or "recovery-required")
            {
                TradeLog.DataStale(symbol, reason, maxAge.TotalSeconds);
                EnqueueMarketDataRecovery(symbol, reason);
            }
        }

        if (!_wsClient.IsConnected || _pendingMarketDataRecovery.IsEmpty)
            return;

        foreach (var symbol in _pendingMarketDataRecovery.Keys.ToArray())
        {
            ct.ThrowIfCancellationRequested();
            if (_nextMarketDataRecoveryAttemptUtc.TryGetValue(symbol, out var next) && next > DateTime.UtcNow)
                continue;

            if (_marketAnalyzer.IsQuarantined(symbol, DateTime.UtcNow, out _))
            {
                ForgetRecovery(symbol);
                continue;
            }

            // 3. История ещё недостоверна — REST-дозагрузка.
            if (_marketAnalyzer.IsRecoveryRequired(symbol))
            {
                await ReloadHistoryAsync(symbol, ct);
                continue;
            }

            // 4. История прогрета, но WS молчит — эскалация вместо повторного REST-цикла.
            if (_marketAnalyzer.IsLiveTickOverdue(symbol, DateTime.UtcNow))
                await EscalateSilentStreamAsync(symbol, ct);
        }
    }

    private void ForgetRecovery(string symbol)
    {
        _pendingMarketDataRecovery.TryRemove(symbol, out _);
        _nextMarketDataRecoveryAttemptUtc.TryRemove(symbol, out _);
        _marketDataRecoveryReason.TryRemove(symbol, out _);
    }

    /// <summary>P0.3: REST-прогрев истории. Успех пишет DATA-REST-RELOAD, а не «входы разрешены».</summary>
    private async Task ReloadHistoryAsync(string symbol, CancellationToken ct)
    {
        // P0.8: ожидание замка восстановления тоже ограничено — иначе одна залипшая фаза
        // держит замок и основной цикл стоит вечно.
        if (!await _marketDataRecoveryLock.WaitAsync(RecoveryLockWait, ct))
        {
            _nextMarketDataRecoveryAttemptUtc[symbol] = DateTime.UtcNow.AddSeconds(_config.Trading.MarketDataRecoveryRetrySeconds);
            TradeLog.Warn("DATA-RECOVERY-LOCK-BUSY", symbol,
                $"замок восстановления занят дольше {LogOptions.N(RecoveryLockWait.TotalSeconds)} с; повтор через {_config.Trading.MarketDataRecoveryRetrySeconds} с",
                gateKey: "data-recovery-lock-busy:" + symbol);
            return;
        }

        try
        {
            if (!_pendingMarketDataRecovery.ContainsKey(symbol) || !_wsClient.IsConnected)
                return;

            _metrics.RecordMarketDataRecoveryAttempt();
            var history = await _restClient.GetKlinesAsync(
                symbol, _config.Trading.CandleInterval, _config.Trading.HistoryCandlesCount, ct);

            if (_marketAnalyzer.TryReloadHistory(
                    symbol, history, _candleInterval, RequiredClosedCandles, DateTime.UtcNow, out var reason,
                    MarketDataLiveConfirmWindow))
            {
                if (history.Count > 0)
                {
                    lock (_candleLock)
                    {
                        _currentCandles[symbol] = history[^1];
                    }
                    RefreshAtrCache(symbol, history[^1]);
                }

                _metrics.RecordMarketDataRecovery(success: true);
                TradeLog.DataRestReload(symbol, history.Count(k => k.IsClosed), MarketDataLiveConfirmWindow.TotalSeconds);
                // Символ остаётся в контуре: следующий шаг — подтверждение живого WS-тика.
                _nextMarketDataRecoveryAttemptUtc[symbol] = DateTime.UtcNow + MarketDataLiveConfirmWindow;
            }
            else
            {
                _metrics.RecordMarketDataRecovery(success: false);
                _nextMarketDataRecoveryAttemptUtc[symbol] = DateTime.UtcNow.AddSeconds(_config.Trading.MarketDataRecoveryRetrySeconds);
                TradeLog.Warn("DATA-RECOVERY-REJECTED", symbol,
                    $"{reason}; повтор через {_config.Trading.MarketDataRecoveryRetrySeconds} с",
                    gateKey: "data-recovery-rejected:" + symbol);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _metrics.RecordMarketDataRecovery(success: false);
            _nextMarketDataRecoveryAttemptUtc[symbol] = DateTime.UtcNow.AddSeconds(_config.Trading.MarketDataRecoveryRetrySeconds);
            TradeLog.Warn("DATA-RECOVERY-FAILED", symbol,
                $"повтор через {_config.Trading.MarketDataRecoveryRetrySeconds} с", ex);
        }
        finally
        {
            _marketDataRecoveryLock.Release();
        }
    }

    /// <summary>
    /// P0.3: молчащий стрим — resubscribe symbol/stream, затем полный реконнект, затем карантин.
    /// Бесконечный цикл recovered → stale здесь невозможен: попытки конечны и завершаются явным карантином.
    /// </summary>
    private async Task EscalateSilentStreamAsync(string symbol, CancellationToken ct)
    {
        var cfg = _config.Trading;
        var reason = _marketDataRecoveryReason.TryGetValue(symbol, out var r) ? r : "silent-stream";

        // P0.3a: до любой эскалации фиксируем per-stream факты — какой именно стрим молчит
        // (kline/depth/оба), сколько сообщений принято, возраст последнего, подтверждена ли подписка.
        var silentTarget = LogStreamDiagnostics(symbol, reason);

        var attempts = _marketAnalyzer.GetResubscribeAttempts(symbol);

        if (attempts < cfg.MarketDataMaxResubscribeAttempts)
        {
            try
            {
                await _wsClient.ResubscribeAsync(new[] { symbol }, cfg.CandleInterval, ct);
                var attempt = _marketAnalyzer.NoteResubscribeAttempt(symbol, DateTime.UtcNow, MarketDataLiveConfirmWindow);
                _metrics.RecordMarketDataResubscribe();
                TradeLog.DataWsResubscribe(symbol, attempt, cfg.MarketDataMaxResubscribeAttempts, reason);
                _nextMarketDataRecoveryAttemptUtc[symbol] = DateTime.UtcNow + MarketDataLiveConfirmWindow;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _marketAnalyzer.NoteResubscribeAttempt(symbol, DateTime.UtcNow, MarketDataLiveConfirmWindow);
                _nextMarketDataRecoveryAttemptUtc[symbol] = DateTime.UtcNow + MarketDataLiveConfirmWindow;
                TradeLog.Warn("DATA-WS-RESUBSCRIBE-FAILED", symbol, "переподписка на стрим не удалась", ex);
            }
            return;
        }

        // P0.3a шаг 3: cooldown + jitter против синхронных reconnect-штормов;
        // single-flight самого разрыва сокета — внутри WS-клиента.
        var baseCooldown = cfg.MarketDataReconnectCooldownSeconds;
        var cooldown = TimeSpan.FromSeconds(baseCooldown + Random.Shared.Next(0, Math.Max(1, baseCooldown / 4)));
        if (DateTime.UtcNow - _lastMarketDataForcedReconnectUtc >= cooldown)
        {
            _lastMarketDataForcedReconnectUtc = DateTime.UtcNow;
            try
            {
                TradeLog.DataWsReconnect(symbol, reason);
                _metrics.RecordMarketDataReconnectRequest(reason);
                await _wsClient.ReconnectMarketStreamsAsync(ct);
                // Обработчики OnDisconnected/OnReconnected заново отправят все символы на REST-прогрев.
                _nextMarketDataRecoveryAttemptUtc[symbol] = DateTime.UtcNow + MarketDataLiveConfirmWindow;
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                TradeLog.Warn("DATA-WS-RECONNECT-FAILED", symbol, "принудительный реконнект не удался", ex);
            }
        }

        // Реконнект уже был недавно или не удался — символ карантинирован, цикл по нему остановлен.
        // P0.3a: в причину карантина попадает и то, какой стрим молчал — иначе 30 одинаковых циклов
        // quarantine/lift не объясняют, что именно не доставляется по символу.
        // P0.3a шаг 3: persistently silent символ получает экспоненциальный backoff карантина
        // (серия сбрасывается только live-подтверждением обоих стримов) и операторский алерт.
        var streakBefore = _marketAnalyzer.GetQuarantineStreak(symbol);
        var backoffSeconds = Math.Min(
            cfg.MarketDataQuarantineSeconds * Math.Pow(2, streakBefore),
            cfg.MarketDataQuarantineMaxSeconds);
        var quarantine = TimeSpan.FromSeconds(backoffSeconds);
        var quarantineReason = $"no-live-tick:{reason}:silent-{silentTarget}";
        var streak = _marketAnalyzer.Quarantine(symbol, quarantineReason, DateTime.UtcNow, quarantine);
        _metrics.RecordMarketDataQuarantine(streak);
        _metrics.RecordMarketDataRecoveryCycle(symbol, success: false);
        TradeLog.DataQuarantine(symbol, quarantineReason, quarantine.TotalSeconds);
        if (streak >= QuarantineEscalationStreak)
        {
            _metrics.RecordMarketDataQuarantineEscalation();
            TradeLog.DataQuarantineEscalated(symbol, quarantineReason, streak, quarantine.TotalSeconds);
        }
        ForgetRecovery(symbol);
    }

    /// <summary>
    /// P0.3a: записать per-stream диагностику символа и вернуть, какой стрим молчит:
    /// "kline", "depth", "both", "none" (доставка есть, проблема не в стриме) или "unknown" (диагностика недоступна).
    /// Это факты из WebSocket-клиента, а не интерпретация: причина молчания по ним всё ещё требует расследования.
    /// </summary>
    private string LogStreamDiagnostics(string symbol, string reason)
    {
        IReadOnlyList<MarketStreamDiagnostics> diagnostics;
        try
        {
            diagnostics = _wsClient.GetStreamDiagnostics(DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            TradeLog.Warn("DATA-STREAM-DIAG-FAILED", symbol,
                "per-stream диагностика недоступна; причина молчания не зафиксирована", ex);
            return "unknown";
        }

        var kline = diagnostics.FirstOrDefault(d => IsStreamOf(d, symbol, MarketStreamKind.Kline));
        var depth = diagnostics.FirstOrDefault(d => IsStreamOf(d, symbol, MarketStreamKind.Depth));

        // Молчанием считаем отсутствие сообщений дольше окна свежести/подтверждения live.
        var silenceThreshold = TimeSpan.FromSeconds(Math.Max(
            _config.Trading.MarketDataMaxAgeSeconds,
            _config.Trading.MarketDataLiveConfirmSeconds));
        var silentTarget = ClassifySilentTarget(kline, depth, silenceThreshold);

        var (indicatorsReady, acceptedCandles, requiredCandles) = _marketAnalyzer.GetWarmupInfo(symbol);

        TradeLog.DataStreamDiag(
            symbol, reason, silentTarget,
            kline?.MessageCount ?? 0, kline?.LastMessageAgeSeconds, kline?.EmptyPayloadCount ?? 0,
            kline?.Subscribed ?? false, kline?.SubscribeRequestCount ?? 0, DescribeSubscribeAck(kline),
            depth?.MessageCount ?? 0, depth?.LastMessageAgeSeconds, depth?.EmptyPayloadCount ?? 0,
            depth?.Subscribed ?? false, depth?.SubscribeRequestCount ?? 0, DescribeSubscribeAck(depth),
            acceptedCandles, requiredCandles, indicatorsReady);

        return silentTarget;
    }

    private static bool IsStreamOf(MarketStreamDiagnostics diagnostics, string symbol, MarketStreamKind kind) =>
        diagnostics.Kind == kind && string.Equals(diagnostics.Symbol, symbol, StringComparison.OrdinalIgnoreCase);

    private static string ClassifySilentTarget(
        MarketStreamDiagnostics? kline,
        MarketStreamDiagnostics? depth,
        TimeSpan silenceThreshold) =>
        (IsStreamSilent(kline, silenceThreshold), IsStreamSilent(depth, silenceThreshold)) switch
        {
            (true, true) => "both",
            (true, false) => "kline",
            (false, true) => "depth",
            (false, false) => "none"
        };

    private static bool IsStreamSilent(MarketStreamDiagnostics? diagnostics, TimeSpan silenceThreshold) =>
        diagnostics is null ||
        diagnostics.MessageCount == 0 ||
        diagnostics.LastMessageAgeSeconds is null ||
        diagnostics.LastMessageAgeSeconds.Value > silenceThreshold.TotalSeconds;

    /// <summary>P0.3a: состояние подписки стрима одним токеном: url / id{N}:acked / id{N}:pending / id{N}:error / none.</summary>
    private static string DescribeSubscribeAck(MarketStreamDiagnostics? diagnostics)
    {
        if (diagnostics is null)
            return "no-such-stream";
        if (diagnostics.LastSubscribeError is not null)
            return $"id{diagnostics.LastSubscribeRequestId}:error";
        if (diagnostics.LastSubscribeRequestId is null)
            return diagnostics.Subscribed ? "url" : "none";
        return diagnostics.LastSubscribeAcked
            ? $"id{diagnostics.LastSubscribeRequestId.Value}:acked"
            : $"id{diagnostics.LastSubscribeRequestId.Value}:pending";
    }

    /// <summary>P1.4: fail-closed гейт свежести рыночных данных перед входом.</summary>
    private bool PassesMarketDataFreshness(string symbol, string stage, E1DecisionContext? ctx = null)
    {
        if (!_wsClient.IsConnected)
        {
            _metrics.RecordMarketDataStaleEntryBlock();
            TradeLog.Rejected(symbol, "market-data", $"[{stage}] WS не подключён; вход заблокирован до восстановления данных");
            EnqueueMarketDataRecovery(symbol, "ws-disconnected");
            return false;
        }

        var maxAge = TimeSpan.FromSeconds(_config.Trading.MarketDataMaxAgeSeconds);
        if (_marketAnalyzer.IsDataFresh(symbol, DateTime.UtcNow, maxAge, out var reason))
            return true;

        _metrics.RecordMarketDataStaleEntryBlock();
        TradeLog.Rejected(symbol, "market-data", $"[{stage}] данные непригодны: {reason}; вход заблокирован");

        // P0.3: карантин не лечится новым REST-прогревом; awaiting-live-tick обслуживает эскалация.
        if (reason is not "no-book" and not "stale-book"
            && !reason.StartsWith("awaiting-live-tick", StringComparison.Ordinal)
            && !reason.StartsWith("quarantined", StringComparison.Ordinal))
            EnqueueMarketDataRecovery(symbol, reason);

        return false;
    }

    /// <summary>P0.5: хэш эффективного конфига для кэша veto и журнала решений.</summary>
    private string ComputeConfigHash()
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(_config);
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json));
            return Convert.ToHexString(hash)[..16].ToLowerInvariant();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось вычислить config hash настроек");
            return "unknown";
        }
    }

    private OHLCV? GetCurrentCandle(string symbol)
    {
        lock (_candleLock)
        {
            return _currentCandles.TryGetValue(symbol, out var candle) ? candle : null;
        }
    }

    private void SetStatus(BotStatus status)
    {
        _status = status;
        OnStatusChanged?.Invoke(status);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _llmVeto.DisposeAsync();
    }
}