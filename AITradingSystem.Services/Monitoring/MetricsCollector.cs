using AITradingSystem.Core.Models.Trading;
using AITradingSystem.Core.RiskManagement;
using AITradingSystem.Data.Binance;

namespace AITradingSystem.Services.Monitoring;

/// <summary>
/// Сборщик метрик производительности (FR-8.3).
/// P1.6: реализует <see cref="IRestRateLimitMetrics"/> — приёмник метрик весов/rate-limit
/// REST-клиента (Data не ссылается на Services, поэтому интерфейс объявлен в Data).
/// </summary>
public sealed class MetricsCollector : IRestRateLimitMetrics
{
    private readonly Queue<double> _inferenceTimes = new();
    private readonly Queue<double> _positionCheckLatencies = new();
    private readonly Queue<double> _executionWaits = new();
    private int _totalSignals;
    private int _totalTrades;
    private decimal _totalPnL;
    private readonly object _lock = new();

    public int TotalSignals => _totalSignals;
    public int TotalTrades => _totalTrades;
    public decimal TotalPnL => _totalPnL;
    public double AverageInferenceTimeMs { get; private set; }

    /// <summary>P0.2: сколько проверок сопровождения позиций выполнил тик-контур.</summary>
    public long PositionChecks { get; private set; }

    /// <summary>Средняя и худшая задержка «тик → проверка позиции», мс.</summary>
    public double AveragePositionCheckLatencyMs { get; private set; }
    public double MaxPositionCheckLatencyMs { get; private set; }

    /// <summary>P0.3: ожидание операции в очереди исполнения (среднее/худшее), мс.</summary>
    public double AverageExecutionWaitMs { get; private set; }
    public double MaxExecutionWaitMs { get; private set; }

    /// <summary>Максимальная глубина очереди исполнения за сессию.</summary>
    public int ExecutionQueueMaxDepth { get; private set; }

    /// <summary>Текущая глубина очереди исполнения (для дашборда).</summary>
    public int ExecutionQueueDepth { get; private set; }

    /// <summary>P0.3: сколько операций очередь повторила из-за транзиентных сбоев.</summary>
    public long ExecutionRetries { get; private set; }

    /// <summary>
    /// Зафиксировать время инференса.
    /// </summary>
    public void RecordInferenceTime(double timeMs)
    {
        // Под тем же локом, что и остальные метрики: иначе гонка
        // "очередь опустела между Dequeue и Average" роняла цикл анализа.
        lock (_lock)
        {
            _inferenceTimes.Enqueue(timeMs);

            // Храним только последние 100 значений
            while (_inferenceTimes.Count > 100)
            {
                _inferenceTimes.Dequeue();
            }

            AverageInferenceTimeMs = _inferenceTimes.Count > 0 ? _inferenceTimes.Average() : 0d;
        }
    }

    /// <summary>
    /// P0.2: зафиксировать задержку сопровождения позиции (тик пришёл → проверка началась).
    /// Критерий независимого контура: ≤1–2 с, иначе позиция снова защищена только «на бумаге».
    /// </summary>
    public void RecordPositionCheck(double latencyMs)
    {
        lock (_lock)
        {
            PositionChecks++;
            _positionCheckLatencies.Enqueue(latencyMs);

            while (_positionCheckLatencies.Count > 200)
                _positionCheckLatencies.Dequeue();

            AveragePositionCheckLatencyMs = _positionCheckLatencies.Average();
            if (latencyMs > MaxPositionCheckLatencyMs)
                MaxPositionCheckLatencyMs = latencyMs;
        }
    }

    /// <summary>
    /// P0.3: зафиксировать ожидание операции в очереди исполнения.
    /// Закрытие/защита не должны стоять за входами — рост этой метрики означает регресс.
    /// </summary>
    public void RecordExecutionWait(double waitMs)
    {
        lock (_lock)
        {
            _executionWaits.Enqueue(waitMs);
            while (_executionWaits.Count > 200)
                _executionWaits.Dequeue();

            AverageExecutionWaitMs = _executionWaits.Average();
            if (waitMs > MaxExecutionWaitMs)
                MaxExecutionWaitMs = waitMs;
        }
    }

    /// <summary>Зафиксировать текущую глубину очереди исполнения.</summary>
    public void RecordExecutionQueueDepth(int depth)
    {
        lock (_lock)
        {
            ExecutionQueueDepth = depth;
            if (depth > ExecutionQueueMaxDepth)
                ExecutionQueueMaxDepth = depth;
        }
    }

    /// <summary>
    /// P0.3: зафиксировать повтор операции исполнения (backoff на транзиентном сбое).
    /// Рост метрики при неизменном числе сделок = деградация сети/биржи.
    /// </summary>
    public void RecordExecutionRetry()
    {
        lock (_lock)
        {
            ExecutionRetries++;
        }
    }

    /// <summary>P0.5: получено executionReport из user-data stream.</summary>
    public long ExecutionReportsReceived { get; private set; }

    /// <summary>P0.5: число проблем последней реконсиляции (сироты + пропавшая защита).</summary>
    public int ReconciliationIssues { get; private set; }

    /// <summary>P0.6: сколько раз взводился kill-switch (авто + ручной).</summary>
    public int KillSwitchActivations { get; private set; }

    /// <summary>P1.8: мониторинг дрейфа часов относительно Binance.</summary>
    public long ClockDriftChecks { get; private set; }
    public long ClockDriftFailures { get; private set; }
    public long ClockDriftAlerts { get; private set; }
    public long? LastClockDriftMs { get; private set; }
    public long LastClockRttMs { get; private set; }
    public long MaxAbsClockDriftMs { get; private set; }
    public bool IsClockDriftWarning { get; private set; }
    public DateTime? LastClockCheckUtc { get; private set; }
    public string? ClockDriftError { get; private set; }

    public void RecordClockDrift(long driftMs, long rttMs, bool warning, DateTime checkedAtUtc)
    {
        lock (_lock)
        {
            ClockDriftChecks++;
            LastClockDriftMs = driftMs;
            LastClockRttMs = rttMs;
            var abs = Math.Abs(driftMs);
            if (abs > MaxAbsClockDriftMs)
                MaxAbsClockDriftMs = abs;
            IsClockDriftWarning = warning;
            LastClockCheckUtc = checkedAtUtc;
            ClockDriftError = null;
        }
    }

    public void RecordClockDriftFailure(string error, DateTime checkedAtUtc)
    {
        lock (_lock)
        {
            ClockDriftFailures++;
            LastClockCheckUtc = checkedAtUtc;
            ClockDriftError = error;
            IsClockDriftWarning = false;
        }
    }

    public void RecordClockDriftAlert()
    {
        lock (_lock)
        {
            ClockDriftAlerts++;
        }
    }

    /// <summary>P0.5: применённые биржевые филлы / подхваченные сироты / снятые сиротские ордера / пересоздания listenKey.</summary>
    public long ReconciliationFillsApplied { get; private set; }
    public long OrphanPositionsAdopted { get; private set; }
    public long OrphanOrdersCanceled { get; private set; }
    public long ListenKeyRecreations { get; private set; }

    public void RecordExecutionReport()
    {
        lock (_lock)
        {
            ExecutionReportsReceived++;
        }
    }

    public void RecordReconciliation(int issues)
    {
        lock (_lock)
        {
            ReconciliationIssues = issues;
        }
    }

    public void RecordKillSwitch()
    {
        lock (_lock)
        {
            KillSwitchActivations++;
        }
    }

    public void RecordReconciliationFill() { lock (_lock) ReconciliationFillsApplied++; }
    public void RecordOrphanPositionAdopted() { lock (_lock) OrphanPositionsAdopted++; }
    public void RecordOrphanOrderCanceled() { lock (_lock) OrphanOrdersCanceled++; }
    public void RecordListenKeyRecreation() { lock (_lock) ListenKeyRecreations++; }

    /// <summary>P0.4: поддержка user-data stream, polling-fallback и fail-closed по неизвестному состоянию исполнения.</summary>
    public long UserDataStreamProbes { get; private set; }
    public long UserDataStreamProbeFailures { get; private set; }
    public long PollingFallbackReconciles { get; private set; }
    public long ExecutionStateBlocks { get; private set; }
    public long ReconciliationFailures { get; private set; }

    public void RecordUserDataStreamProbe(bool supported)
    {
        lock (_lock)
        {
            UserDataStreamProbes++;
            if (!supported) UserDataStreamProbeFailures++;
        }
    }
    public void RecordPollingFallbackReconcile() { lock (_lock) PollingFallbackReconciles++; }
    public void RecordExecutionStateBlock() { lock (_lock) ExecutionStateBlocks++; }
    public void RecordReconciliationFailure() { lock (_lock) ReconciliationFailures++; }

    /// <summary>P1.5: отказы входа по фильтру ликвидности — всего и по причинам.</summary>
    public long LiquidityRejections { get; private set; }
    public long LiquiditySpreadRejections { get; private set; }
    public long LiquidityDepthRejections { get; private set; }
    public long LiquidityVolumeRejections { get; private set; }
    public long LiquidityNoBookRejections { get; private set; }

    /// <summary>
    /// P1.5: post-only-отказы биржи (цена пересекла спред, ордер был бы taker).
    /// Раньше признак IsPostOnlyRejection только писался в лог сделки и не агрегировался.
    /// </summary>
    public long PostOnlyRejections { get; private set; }

    /// <summary>P1.5: зафиксировать отказ входа по ликвидности (spread | depth | volume | no-book).</summary>
    public void RecordLiquidityRejection(string reason)
    {
        lock (_lock)
        {
            LiquidityRejections++;
            switch (reason)
            {
                case "spread": LiquiditySpreadRejections++; break;
                case "depth": LiquidityDepthRejections++; break;
                case "volume": LiquidityVolumeRejections++; break;
                default: LiquidityNoBookRejections++; break;
            }
        }
    }

    /// <summary>P1.5: зафиксировать post-only-отказ биржи на входе.</summary>
    public void RecordPostOnlyRejection()
    {
        lock (_lock)
        {
            PostOnlyRejections++;
        }
    }

    /// <summary>P1.3: аудиты отмены неисполненного остатка — чистые и расхождения.</summary>
    public long CancelAudits { get; private set; }
    public long CancelAuditMismatches { get; private set; }

    /// <summary>P1.3: суммарный отменённый остаток по чистым аудитам (в базовом активе, все символы).</summary>
    public decimal TotalCanceledResidual { get; private set; }

    /// <summary>P1.3: dust-закрытия MARKET SELL всего остатка (успех) и отказы биржи.</summary>
    public long DustMarketCloses { get; private set; }
    public long DustCloseFailures { get; private set; }

    /// <summary>P1.3: «хвосты» защиты — OCO покрыла меньше объёма позиции после округления к stepSize.</summary>
    public long ProtectionTails { get; private set; }
    public decimal ProtectionTailQuantity { get; private set; }

    /// <summary>P1.4: целостность рыночных данных после реконнекта WS / тихого обрыва.</summary>
    public long MarketDataDisconnects { get; private set; }
    public long MarketDataReconnects { get; private set; }
    public long MarketDataGaps { get; private set; }
    public long MarketDataRecoveries { get; private set; }
    public long MarketDataRecoveryFailures { get; private set; }
    public long MarketDataStaleEntryBlocks { get; private set; }

    public void RecordMarketDataDisconnect() { lock (_lock) MarketDataDisconnects++; }
    public void RecordMarketDataReconnect() { lock (_lock) MarketDataReconnects++; }
    public void RecordMarketDataGap() { lock (_lock) MarketDataGaps++; }
    public void RecordMarketDataRecovery(bool success)
    {
        lock (_lock)
        {
            if (success) MarketDataRecoveries++;
            else MarketDataRecoveryFailures++;
        }
    }
    public void RecordMarketDataStaleEntryBlock() { lock (_lock) MarketDataStaleEntryBlocks++; }

    /// <summary>P0.3: recovery_attempts / recovery_success / mean_time_to_live / resubscribe / quarantine.</summary>
    public long MarketDataRecoveryAttempts { get; private set; }
    public long MarketDataLiveConfirmations { get; private set; }
    public double MarketDataMeanTimeToLiveSeconds { get; private set; }
    public double MarketDataMaxTimeToLiveSeconds { get; private set; }
    public long MarketDataResubscribes { get; private set; }
    public long MarketDataReconnectRequests { get; private set; }
    public long MarketDataQuarantines { get; private set; }
    public long MarketDataQuarantineLifts { get; private set; }

    public void RecordMarketDataRecoveryAttempt() { lock (_lock) MarketDataRecoveryAttempts++; }

    public void RecordMarketDataLiveConfirmed(double secondsToLive)
    {
        lock (_lock)
        {
            MarketDataLiveConfirmations++;
            if (secondsToLive < 0) secondsToLive = 0;
            MarketDataMeanTimeToLiveSeconds += (secondsToLive - MarketDataMeanTimeToLiveSeconds) / MarketDataLiveConfirmations;
            if (secondsToLive > MarketDataMaxTimeToLiveSeconds) MarketDataMaxTimeToLiveSeconds = secondsToLive;
        }
    }

    public void RecordMarketDataResubscribe() { lock (_lock) MarketDataResubscribes++; }
    public void RecordMarketDataReconnectRequest() { lock (_lock) MarketDataReconnectRequests++; }
    public void RecordMarketDataQuarantine() { lock (_lock) MarketDataQuarantines++; }
    public void RecordMarketDataQuarantineLift() { lock (_lock) MarketDataQuarantineLifts++; }

    /// <summary>P2.4: подбор порога MinConfidence по expectancy после издержек.</summary>
    public long ConfidenceTuneRuns { get; private set; }
    public long ConfidenceTuneApplications { get; private set; }
    public long ConfidenceTuneNoRecommendation { get; private set; }

    /// <summary>Рекомендованный порог последнего разбора (null — данных не хватает).</summary>
    public decimal? LastRecommendedMinConfidence { get; private set; }

    /// <summary>Expectancy (net R) в рекомендованной точке и размер пригодной выборки.</summary>
    public decimal LastConfidenceExpectancyR { get; private set; }
    public int LastConfidenceSampleTrades { get; private set; }

    /// <summary>Действующий порог после разбора (изменён только при авто-режиме).</summary>
    public decimal ActiveMinConfidence { get; private set; }

    public DateTime? LastConfidenceTuneUtc { get; private set; }
    public string? ConfidenceTuneReason { get; private set; }

    /// <summary>
    /// P2.4: зафиксировать разбор истории сделок для подбора порога уверенности.
    /// applied=true — порог реально изменён (авто-режим), иначе только рекомендация.
    /// </summary>
    public void RecordConfidenceTune(
        ConfidenceExpectancyReport report, decimal activeThreshold, bool applied, DateTime atUtc)
    {
        if (report is null) return;
        lock (_lock)
        {
            ConfidenceTuneRuns++;
            if (applied) ConfidenceTuneApplications++;
            if (!report.HasRecommendation) ConfidenceTuneNoRecommendation++;
            LastRecommendedMinConfidence = report.RecommendedThreshold;
            LastConfidenceExpectancyR = report.RecommendedExpectancyR;
            LastConfidenceSampleTrades = report.EvaluatedTrades;
            ActiveMinConfidence = activeThreshold;
            LastConfidenceTuneUtc = atUtc;
            ConfidenceTuneReason = report.Reason;
        }
    }

    /// <summary>P2.6: валидация весов скоринга / reliability diagram (только диагностика).</summary>
    public long ScoringCalibrationRuns { get; private set; }
    public DateTime? LastScoringCalibrationUtc { get; private set; }

    /// <summary>Калибровка последнего разбора: ECE, Brier, max |разрыв| значимых корзин.</summary>
    public decimal LastCalibrationEce { get; private set; }
    public decimal LastCalibrationBrier { get; private set; }
    public decimal LastCalibrationMaxGap { get; private set; }

    /// <summary>Выборка: пригодные сделки и из них с фичами скоринга.</summary>
    public int LastCalibrationSampleTrades { get; private set; }
    public int LastCalibrationFeatureTrades { get; private set; }

    /// <summary>Компоненты с вердиктом Dead (вес не contributes) / Contradicts (согласованные сделки убыточны).</summary>
    public int LastDeadScoreComponents { get; private set; }
    public int LastContradictedScoreComponents { get; private set; }

    /// <summary>insufficient-data | calibrated | overconfident | underconfident | miscalibrated.</summary>
    public string? ScoringCalibrationVerdict { get; private set; }

    /// <summary>P2.6: зафиксировать разбор калибровки. Веса/пороги не меняются — только отчёт.</summary>
    public void RecordScoringCalibration(ScoringCalibrationReport report, DateTime atUtc)
    {
        if (report is null) return;
        lock (_lock)
        {
            ScoringCalibrationRuns++;
            LastScoringCalibrationUtc = atUtc;
            LastCalibrationEce = report.Ece;
            LastCalibrationBrier = report.BrierScore;
            LastCalibrationMaxGap = report.MaxAbsGap;
            LastCalibrationSampleTrades = report.EvaluatedTrades;
            LastCalibrationFeatureTrades = report.FeatureTrades;
            LastDeadScoreComponents = report.DeadComponents;
            LastContradictedScoreComponents = report.ContradictedComponents;
            ScoringCalibrationVerdict = report.CalibrationVerdict;
        }
    }

    /// <summary>
    /// P1.3: зафиксировать аудит остатка после отмены неисполненной части ордера.
    /// Mismatch — ордер не дошёл до терминального статуса или объёмы не сходятся:
    /// остаток мог остаться живым на бирже (ловит реконсиляция P0.5).
    /// </summary>
    public void RecordCancelAudit(decimal executed, decimal canceledResidual, bool mismatch)
    {
        lock (_lock)
        {
            if (mismatch)
            {
                CancelAuditMismatches++;
            }
            else
            {
                CancelAudits++;
                TotalCanceledResidual += canceledResidual;
            }
        }
    }

    /// <summary>P1.3: зафиксировать dust-закрытие (MARKET SELL остатка ниже фильтров) или его отказ.</summary>
    public void RecordDustClose(bool rejected)
    {
        lock (_lock)
        {
            if (rejected) DustCloseFailures++;
            else DustMarketCloses++;
        }
    }

    /// <summary>P1.3: зафиксировать «хвост» защиты — объём позиции, не покрытый OCO после округления.</summary>
    public void RecordProtectionTail(decimal tailQuantity)
    {
        lock (_lock)
        {
            ProtectionTails++;
            ProtectionTailQuantity += tailQuantity;
        }
    }

    /// <summary>P1.6: бюджет весов REST — последний/максимальный вес окна и лимит.</summary>
    public int RestUsedWeight { get; private set; }
    public int RestMaxUsedWeight { get; private set; }
    public int RestWeightLimit { get; private set; }

    /// <summary>P1.6: ответы биржи 429 / -1003 / 418 (IP-бан). 429 c -1003 учитывается в обоих счётчиках.</summary>
    public long RateLimit429Hits { get; private set; }
    public long RateLimit1003Hits { get; private set; }
    public long IpBan418Hits { get; private set; }
    public double LastRateLimitBackoffMs { get; private set; }

    /// <summary>P1.6: притормаживания запросов бюджетом до отправки (soft-limit/бан).</summary>
    public long WeightThrottleWaits { get; private set; }
    public double MaxWeightThrottleWaitMs { get; private set; }
    public double TotalWeightThrottleWaitMs { get; private set; }

    /// <summary>P1.6: снимок веса из заголовка X-MBX-USED-WEIGHT-1M.</summary>
    public void RecordRestWeight(int usedWeight, int limitPerMinute)
    {
        lock (_lock)
        {
            RestUsedWeight = usedWeight;
            RestWeightLimit = limitPerMinute;
            if (usedWeight > RestMaxUsedWeight)
                RestMaxUsedWeight = usedWeight;
        }
    }

    /// <summary>P1.6: ответ 429/-1003/418 — биржа ограничила запросы.</summary>
    public void RecordRateLimitHit(int httpStatus, int? apiCode, double backoffMs)
    {
        lock (_lock)
        {
            if (httpStatus == 429) RateLimit429Hits++;
            if (apiCode == -1003) RateLimit1003Hits++;
            if (httpStatus == 418) IpBan418Hits++;
            LastRateLimitBackoffMs = backoffMs;
        }
    }

    /// <summary>P1.6: запрос ожидает окончания окна/бана до отправки.</summary>
    public void RecordWeightThrottle(double delayMs, bool critical)
    {
        lock (_lock)
        {
            WeightThrottleWaits++;
            TotalWeightThrottleWaitMs += delayMs;
            if (delayMs > MaxWeightThrottleWaitMs)
                MaxWeightThrottleWaitMs = delayMs;
        }
    }

    /// <summary>P1.7: портфельный риск — экспозиция, корреляция, отказы, серия убытков.</summary>
    public long PortfolioRiskRejections { get; private set; }
    public decimal LastPortfolioExposure { get; private set; }
    public decimal LastPortfolioEquity { get; private set; }
    public decimal LastCorrelatedExposure { get; private set; }
    public decimal LastMaxCorrelation { get; private set; }
    public int PortfolioConsecutiveLosses { get; private set; }
    public DateTime? PortfolioPauseUntilUtc { get; private set; }
    public string? LastPortfolioRejectionReason { get; private set; }

    public void RecordPortfolioDecision(PortfolioEntryDecision decision, decimal equity)
    {
        lock (_lock)
        {
            LastPortfolioExposure = decision.PortfolioExposure;
            LastPortfolioEquity = equity;
            LastCorrelatedExposure = decision.CorrelatedExposure;
            LastMaxCorrelation = decision.MaxCorrelation;
        }
    }

    public void RecordPortfolioRejection(string reason)
    {
        lock (_lock)
        {
            PortfolioRiskRejections++;
            LastPortfolioRejectionReason = reason;
        }
    }

    public void RecordPortfolioLossStreak(int consecutiveLosses, DateTime? pauseUntilUtc)
    {
        lock (_lock)
        {
            PortfolioConsecutiveLosses = consecutiveLosses;
            PortfolioPauseUntilUtc = pauseUntilUtc;
        }
    }

    /// <summary>
    /// Зафиксировать сигнал.
    /// </summary>
    public void RecordSignal(TradingSignal signal)
    {
        Interlocked.Increment(ref _totalSignals);
    }

    /// <summary>
    /// Зафиксировать сделку.
    /// </summary>
    public void RecordTrade(TradeRecord trade)
    {
        lock (_lock)
        {
            _totalTrades++;
            // P1.2: агрегируем чистый результат после комиссий.
            _totalPnL += trade.NetPnL;
        }
    }

    /// <summary>
    /// Получить сводку метрик.
    /// </summary>
    public string GetSummary()
    {
        return $"Signals: {_totalSignals}, Trades: {_totalTrades}, " +
               $"PnL: {_totalPnL:F2} USDT, Avg Inference: {AverageInferenceTimeMs:F0} ms, " +
               $"Position checks: {PositionChecks} (avg {AveragePositionCheckLatencyMs:F0} ms, max {MaxPositionCheckLatencyMs:F0} ms), " +
               $"Exec queue wait: avg {AverageExecutionWaitMs:F0} ms / max {MaxExecutionWaitMs:F0} ms, " +
               $"depth {ExecutionQueueDepth}/{ExecutionQueueMaxDepth}, retries {ExecutionRetries}, " +
               $"exec reports {ExecutionReportsReceived}, recon issues {ReconciliationIssues}, fills applied {ReconciliationFillsApplied}, " +
               $"orphans adopted {OrphanPositionsAdopted}, orphan orders canceled {OrphanOrdersCanceled}, listenKey recreations {ListenKeyRecreations}, " +
               $"kill-switch {KillSwitchActivations}, " +
               $"user-data probes {UserDataStreamProbes} (failures {UserDataStreamProbeFailures}), polling reconciles {PollingFallbackReconciles}, " +
               $"recon failures {ReconciliationFailures}, execution-state blocks {ExecutionStateBlocks}, " +
               $"clock drift checks {ClockDriftChecks}, last {LastClockDriftMs?.ToString() ?? "n/a"} ms, " +
               $"max |drift| {MaxAbsClockDriftMs} ms, failures {ClockDriftFailures}, alerts {ClockDriftAlerts}, " +
               $"liquidity rejections {LiquidityRejections} (spread {LiquiditySpreadRejections}, depth {LiquidityDepthRejections}, " +
               $"volume {LiquidityVolumeRejections}, no-book {LiquidityNoBookRejections}), post-only rejections {PostOnlyRejections}, " +
               $"cancel audits {CancelAudits} (mismatches {CancelAuditMismatches}, residual {TotalCanceledResidual}), " +
               $"dust closes {DustMarketCloses} (failures {DustCloseFailures}), " +
               $"protection tails {ProtectionTails} (qty {ProtectionTailQuantity}), " +
               $"market-data disconnects {MarketDataDisconnects}, reconnects {MarketDataReconnects}, gaps {MarketDataGaps}, " +
               $"recoveries {MarketDataRecoveries} (attempts {MarketDataRecoveryAttempts}, failures {MarketDataRecoveryFailures}), stale-entry blocks {MarketDataStaleEntryBlocks}, " +
               $"live confirmations {MarketDataLiveConfirmations} (mean ttl {MarketDataMeanTimeToLiveSeconds:F1}s, max {MarketDataMaxTimeToLiveSeconds:F1}s), " +
               $"resubscribes {MarketDataResubscribes}, reconnect requests {MarketDataReconnectRequests}, " +
               $"quarantines {MarketDataQuarantines} (lifts {MarketDataQuarantineLifts}), " +
               $"REST weight {RestUsedWeight}/{RestWeightLimit} (max {RestMaxUsedWeight}), " +
               $"rate limits 429={RateLimit429Hits} -1003={RateLimit1003Hits} 418={IpBan418Hits}, " +
               $"weight throttles {WeightThrottleWaits} (max {MaxWeightThrottleWaitMs:F0} ms, total {TotalWeightThrottleWaitMs:F0} ms), " +
               $"confidence tune runs {ConfidenceTuneRuns} (applied {ConfidenceTuneApplications}, no-rec {ConfidenceTuneNoRecommendation}), " +
               $"portfolio rejections {PortfolioRiskRejections}, exposure {LastPortfolioExposure:F2}/{LastPortfolioEquity:F2}, " +
               $"correlated {LastCorrelatedExposure:F2}, max rho {LastMaxCorrelation:F2}, loss streak {PortfolioConsecutiveLosses}, " +
               $"pause until {PortfolioPauseUntilUtc?.ToString("O") ?? "none"}, " +
               $"MinConfidence {ActiveMinConfidence:0.##}, recommended {LastRecommendedMinConfidence?.ToString("0.##") ?? "n/a"} " +
               $"({LastConfidenceExpectancyR:0.###}R on {LastConfidenceSampleTrades} trades), " +
               $"scoring calibration runs {ScoringCalibrationRuns}, verdict {ScoringCalibrationVerdict ?? "n/a"}, " +
               $"ECE {LastCalibrationEce:0.###}, Brier {LastCalibrationBrier:0.###}, max gap {LastCalibrationMaxGap:0.##}, " +
               $"dead components {LastDeadScoreComponents}, contradicted {LastContradictedScoreComponents} " +
               $"(features {LastCalibrationFeatureTrades}/{LastCalibrationSampleTrades} trades)";
    }
}