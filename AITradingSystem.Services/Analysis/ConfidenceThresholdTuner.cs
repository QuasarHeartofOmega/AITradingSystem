using System.Globalization;
using AITradingSystem.Common.Configuration;
using AITradingSystem.Common.Logging;
using AITradingSystem.Core.Models.Trading;
using AITradingSystem.Data.Storage;
using AITradingSystem.Services.Monitoring;
using Microsoft.Extensions.Logging;

namespace AITradingSystem.Services.Analysis;

/// <summary>
/// P2.4: контур подбора порога <c>AI:MinConfidence</c> по expectancy после издержек.
///
/// Порог 0.55 был взят «с потолка». Здесь он проверяется данными: берётся полная история
/// закрытых сделок (не последние 100), каждая нормируется на принятый риск, и по сетке
/// порогов ищется максимум net expectancy (R). Комиссии обеих ног уже вычтены в NetPnL (P1.2).
///
/// Безопасность:
/// — по умолчанию контур только РЕКОМЕНДУЕТ (лог CONFIDENCE-TUNE + метрики), порог не меняет;
/// — авто-применение (MinConfidenceAutoTuneEnabled) ограничено коридором [Floor; Ceiling],
///   требует MinTrades сделок в истории и MinBucketTrades выше порога-кандидата;
/// — нет значимой положительной expectancy → порог остаётся прежним (никакого «понизим, чтобы торговало»).
///
/// Работает независимо от цикла анализа и не обращается к бирже/модели.
///
/// P2.6: в том же цикле и на той же полной истории выполняется валидация весов скоринга
/// (ScoringCalibrationAnalyzer): reliability diagram «уверенность vs фактическая частота
/// успеха», ECE/Brier и вердикты по компонентам (мёртвый/противоречит/подтверждён).
/// Только рекомендует (лог SCORING-CALIBRATION + метрики) — веса рантайм не меняет никогда;
/// подгонка — оффлайн tools/scoring_calibration.py --fit, применение — оператор через конфиг.
/// </summary>
public sealed class ConfidenceThresholdTuner : IAsyncDisposable
{
    private readonly AppConfig _config;
    private readonly ITradeHistoryRepository _history;
    private readonly MetricsCollector _metrics;
    private readonly TelegramAlertService _alerts;
    private readonly ILogger<ConfidenceThresholdTuner> _logger;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _running;

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    /// <summary>Последний разбор истории (для отчёта/диагностики).</summary>
    public ConfidenceExpectancyReport? LastReport { get; private set; }

    /// <summary>P2.6: последняя валидация скоринга/калибровки (для отчёта/диагностики).</summary>
    public ScoringCalibrationReport? LastScoringReport { get; private set; }

    public ConfidenceThresholdTuner(
        AppConfig config,
        ITradeHistoryRepository history,
        MetricsCollector metrics,
        TelegramAlertService alerts,
        ILogger<ConfidenceThresholdTuner> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _alerts = alerts ?? throw new ArgumentNullException(nameof(alerts));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Start(CancellationToken ct)
    {
        // P2.6: цикл нужен, если включён подбор порога (P2.4) ИЛИ валидация скоринга.
        if (!_config.AI.MinConfidenceTuneEnabled && !_config.AI.ScoringCalibrationEnabled)
            return;
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => LoopAsync(_cts.Token), _cts.Token);
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _running, 0) == 0)
            return;

        var cts = _cts;
        var loop = _loop;
        _cts = null;
        _loop = null;

        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { }

        if (loop != null)
        {
            try { await loop; }
            catch (OperationCanceledException) { }
        }

        cts?.Dispose();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(_config.AI.MinConfidenceTuneIntervalMinutes);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await TuneOnceAsync(ct);
                await Task.Delay(interval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Один разбор истории: рекомендация, метрики, лог; при включённом авто-режиме —
    /// применение порога в пределах коридора. Публичный метод: вызывается и вручную/из тестов.
    /// </summary>
    public async Task<ConfidenceExpectancyReport> TuneOnceAsync(CancellationToken ct = default)
    {
        var ai = _config.AI;
        var current = ai.MinConfidence;

        var trades = await _history.GetAllTradesAsync(ct);
        var report = ConfidenceExpectancyAnalyzer.Analyze(trades, BuildOptions(ai, current));
        LastReport = report;

        // P2.6: флаг выключен — подбор порога не выполняется вовсе (ни применения, ни лога/метрик).
        if (ai.MinConfidenceTuneEnabled)
        {
            var applied = false;
            decimal active = current;

            if (ai.MinConfidenceAutoTuneEnabled && report.ChangesThreshold(current))
            {
                active = Math.Clamp(report.RecommendedThreshold!.Value, ai.MinConfidenceFloor, ai.MinConfidenceCeiling);
                if (active != current)
                {
                    ai.MinConfidence = active;
                    applied = true;
                }
            }

            _metrics.RecordConfidenceTune(report, active, applied, DateTime.UtcNow);

            TradeLog.Decision("PORTFOLIO", "CONFIDENCE-TUNE", string.Format(CultureInfo.InvariantCulture,
                "сделок {0} (пригодных {1}, без метки {2}, без риска {3}), текущий порог {4:0.##} → {5:0.###}R, рекомендация {6}, auto={7}, применено={8}: {9}",
                report.TotalTrades, report.EvaluatedTrades, report.UnlabeledTrades, report.NoRiskTrades,
                current, report.Current?.ExpectancyR ?? 0m,
                report.HasRecommendation ? report.RecommendedThreshold!.Value.ToString("0.##", CultureInfo.InvariantCulture) : "нет",
                ai.MinConfidenceAutoTuneEnabled, applied, report.Reason));

            if (applied)
            {
                _logger.LogWarning(
                    "CONFIDENCE-TUNE-APPLIED: порог MinConfidence {Old} → {New} по expectancy {Expectancy}R на {Trades} сделках",
                    current, active, report.RecommendedExpectancyR, report.EvaluatedTrades);

                await _alerts.SendAlertAsync(
                    $"Порог MinConfidence изменён по истории сделок: {current:0.##} → {active:0.##} " +
                    $"(expectancy {report.RecommendedExpectancyR:0.###}R, сделок в выборке {report.EvaluatedTrades}). " +
                    "Авто-режим AI:MinConfidenceAutoTuneEnabled включён.",
                    "Warning", ct);
            }
            else
            {
                _logger.LogInformation(
                    "CONFIDENCE-TUNE: рекомендация {Recommendation}, порог {Current} не изменён ({Reason})",
                    report.HasRecommendation ? report.RecommendedThreshold!.Value : "нет",
                    current, report.Reason);
            }
        }

        // P2.6: валидация весов скоринга на той же истории — только диагностика/рекомендация.
        if (ai.ScoringCalibrationEnabled)
        {
            var scoring = ScoringCalibrationAnalyzer.Analyze(trades, BuildScoringOptions(ai));
            LastScoringReport = scoring;
            _metrics.RecordScoringCalibration(scoring, DateTime.UtcNow);

            TradeLog.Decision("PORTFOLIO", "SCORING-CALIBRATION", string.Format(CultureInfo.InvariantCulture,
                "сделок {0} (пригодных {1}, с фичами {2}), калибровка {3}, ECE {4:0.###}, Brier {5:0.###}, мёртвых компонентов {6}, противоречат {7}: {8}",
                scoring.TotalTrades, scoring.EvaluatedTrades, scoring.FeatureTrades,
                scoring.CalibrationVerdict, scoring.Ece, scoring.BrierScore,
                scoring.DeadComponents, scoring.ContradictedComponents, scoring.Reason));

            _logger.LogInformation(
                "SCORING-CALIBRATION: {Verdict}, ECE {Ece}, Brier {Brier}, dead {Dead}, contradicts {Contradicts} ({Reason})",
                scoring.CalibrationVerdict, scoring.Ece, scoring.BrierScore,
                scoring.DeadComponents, scoring.ContradictedComponents, scoring.Reason);
        }

        return report;
    }

    /// <summary>Параметры подбора из конфига (единый источник значений для контура и инструмента).</summary>
    public static ConfidenceExpectancyOptions BuildOptions(AIConfig ai, decimal currentThreshold) => new()
    {
        CurrentThreshold = currentThreshold,
        Floor = ai.MinConfidenceFloor,
        Ceiling = ai.MinConfidenceCeiling,
        Step = ai.MinConfidenceTuneStep,
        MinTrades = ai.MinConfidenceTuneMinTrades,
        MinBucketTrades = ai.MinConfidenceTuneMinBucketTrades
    };

    /// <summary>P2.6: параметры валидации скоринга из конфига (контур и инструмент согласованы).</summary>
    public static ScoringCalibrationOptions BuildScoringOptions(AIConfig ai) => new()
    {
        MinTrades = ai.ScoringCalibrationMinTrades,
        MinBinTrades = ai.ScoringCalibrationMinBinTrades,
        BinStep = ai.ScoringCalibrationBinStep,
        UseGraduatedEmaAlignment = ai.ScoringWeights.UseGraduatedEmaAlignment
    };

    public async ValueTask DisposeAsync() => await StopAsync();
}
