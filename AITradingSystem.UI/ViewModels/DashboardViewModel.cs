using System.Collections.ObjectModel;
using System.Globalization;
using AITradingSystem.Common.Configuration;
using AITradingSystem.Core.Models.Trading;
using AITradingSystem.Core.RiskManagement;
using AITradingSystem.Services.AI;
using AITradingSystem.Services.Monitoring;
using AITradingSystem.Services.Trading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AITradingSystem.UI.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly RiskManager _riskManager;
    private readonly ExchangeAccountService _account;
    private readonly IAIModel _model;
    private readonly ExecutionQueue _executionQueue;
    private readonly TradingConfig _tradingConfig;
    private readonly ReconciliationService _reconciliation;
    private readonly PortfolioRiskManager _portfolioRisk;
    [ObservableProperty] private decimal _currentBalance;
    [ObservableProperty] private decimal _dailyPnL;
    [ObservableProperty] private string _dailyPnLPercentText = "0.00%";
    [ObservableProperty] private string _drawdownText = "0.00%";
    [ObservableProperty] private int _totalTrades;
    [ObservableProperty] private double _avgInferenceTime;
    [ObservableProperty] private string _usdtFree = "—";
    [ObservableProperty] private string _usdtLocked = "—";
    [ObservableProperty] private string _fdusdFree = "—";
    [ObservableProperty] private string _fdusdLocked = "—";
    [ObservableProperty] private string _usdcFree = "—";
    [ObservableProperty] private string _usdcLocked = "—";
    [ObservableProperty] private string _equityText = "Нет данных";
    [ObservableProperty] private string _environmentText = "—";
    [ObservableProperty] private string _modeText = "—";
    [ObservableProperty] private string _keysStatusText = "—";
    [ObservableProperty] private string _connectionText = "—";
    [ObservableProperty] private string _backendText = "Модель не загружена";
    [ObservableProperty] private string _lastErrorText = string.Empty;
    [ObservableProperty] private string _accountUpdatedText = "Аккаунт ещё не опрошен";
    public ObservableCollection<AssetBalance> AssetBalances { get; } = new();

    public DashboardViewModel(
        RiskManager riskManager,
        ExchangeAccountService account,
        IAIModel model,
        ExecutionQueue executionQueue,
        TradingConfig tradingConfig,
        ReconciliationService reconciliation,
        PortfolioRiskManager portfolioRisk)
    {
        _riskManager = riskManager; _account = account; _model = model;
        _executionQueue = executionQueue; _tradingConfig = tradingConfig;
        _reconciliation = reconciliation;
        _portfolioRisk = portfolioRisk;
        CurrentBalance = riskManager.DrawdownTracker.CurrentBalance;
        UpdateAccount();
    }
    private static string Amount(AccountBalance? balance, string asset, bool locked) =>
        balance == null ? "—" : (balance.Assets.TryGetValue(asset, out var a) ? (locked ? a.Locked : a.Free) : 0m)
            .ToString("0.########", CultureInfo.InvariantCulture);
    public void UpdateAccount()
    {
        var snapshot = _account.Snapshot;
        var balance = snapshot.Balance;
        UsdtFree = Amount(balance, "USDT", false); UsdtLocked = Amount(balance, "USDT", true);
        FdusdFree = Amount(balance, "FDUSD", false); FdusdLocked = Amount(balance, "FDUSD", true);
        UsdcFree = Amount(balance, "USDC", false); UsdcLocked = Amount(balance, "USDC", true);
        EquityText = snapshot.Equity.HasValue
            ? snapshot.Equity.Value.ToString("0.##", CultureInfo.InvariantCulture) + " USDT" + (snapshot.EquityIsPartial ? " (частичная оценка)" : string.Empty)
            : "Нет надёжной оценки";
        EnvironmentText = snapshot.Environment;
        ModeText = snapshot.Mode + (snapshot.Mode == "PAPER" ? " — ордера не отправляются" : " — ордера отправляются на биржу");
        KeysStatusText = _account.HasApiKeys ? "Ключи заданы (доступ проверяется запросом)" : "Ключи не заданы";
        ConnectionText = (_account.IsConnected ? "WS подключен" : "WS отключен") + " · REST " +
            (snapshot.LastRefreshUtc.HasValue && snapshot.Error == null ? "получен" : snapshot.Error == null ? "ожидание" : "ошибка/частичные данные");
        AccountUpdatedText = snapshot.LastRefreshUtc.HasValue ? "Обновлено UTC: " + snapshot.LastRefreshUtc.Value.ToString("yyyy-MM-dd HH:mm:ss") : "Аккаунт ещё не опрошен";
        LastErrorText = string.Join(" | ", new[] { snapshot.Error, (_model as QwenModel)?.LoadWarning }.Where(s => !string.IsNullOrEmpty(s)));
        AssetBalances.Clear();
        if (balance != null) foreach (var asset in balance.Assets.Values.OrderBy(a => a.Asset)) AssetBalances.Add(asset);
        UpdateBackend();
    }
    private void UpdateBackend()
    {
        BackendText = _model is QwenModel q ? q.IsLoaded ? q.BackendSummary : "Модель не загружена; конфигурация " + q.ActiveBackend : "Модель не загружена";
        LastInferenceText = _model.LastInferenceTimeMs > 0 ? _model.LastInferenceTimeMs.ToString("F0", CultureInfo.InvariantCulture) + " ms" : "Нет измерений";
    }
    [ObservableProperty] private string _lastInferenceText = "Нет измерений";
    /// <summary>P0.3: наблюдаемость очереди исполнения в дашборде (глубина, ожидание, повторы).</summary>
    [ObservableProperty] private string _executionQueueText = "Очередь исполнения: нет данных";
    [ObservableProperty] private string _executionQueueAlertText = string.Empty;

    private void UpdateExecutionQueue(MetricsCollector metrics)
    {
        ExecutionQueueText =
            $"В ожидании: {_executionQueue.PendingCount} · глубина тек./макс. {metrics.ExecutionQueueDepth}/{metrics.ExecutionQueueMaxDepth} · " +
            $"ожидание avg/max {metrics.AverageExecutionWaitMs:F0}/{metrics.MaxExecutionWaitMs:F0} мс · " +
            $"ретраев {metrics.ExecutionRetries} · склеено {_executionQueue.CoalescedRequests} · " +
            $"повторов пропущено {_executionQueue.ReplayedResults} · ключей из журнала {_executionQueue.RestoredIntents}";

        var alerts = new List<string>();
        if (metrics.MaxExecutionWaitMs > _tradingConfig.ExecutionQueueWarnWaitMs)
            alerts.Add($"ожидание в очереди превышало {_tradingConfig.ExecutionQueueWarnWaitMs} мс — закрытие могло стоять за входами");
        var unresolved = _executionQueue.UnresolvedIntents.Count;
        if (unresolved > 0)
            alerts.Add($"{unresolved} намерений без подтверждения биржи (сверяются при повторе; реконсиляция — P0.5)");
        ExecutionQueueAlertText = string.Join(" | ", alerts);
    }

    /// <summary>P1.8: наблюдаемость дрейфа часов относительно Binance.</summary>
    [ObservableProperty] private string _clockDriftText = "Дрейф часов: нет данных";
    [ObservableProperty] private string _clockDriftAlertText = string.Empty;

    private void UpdateClockDrift(MetricsCollector metrics)
    {
        var drift = metrics.LastClockDriftMs.HasValue
            ? metrics.LastClockDriftMs.Value.ToString("+0;-0;0", CultureInfo.InvariantCulture) + " мс"
            : "нет данных";
        var checkedAt = metrics.LastClockCheckUtc.HasValue
            ? metrics.LastClockCheckUtc.Value.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " UTC"
            : "не проверялось";
        ClockDriftText =
            $"Проверок {metrics.ClockDriftChecks} · дрейф {drift} · RTT {metrics.LastClockRttMs} мс · " +
            $"max |дрейф| {metrics.MaxAbsClockDriftMs} мс · порог {_tradingConfig.ClockDriftWarningThresholdMs} мс · " +
            $"ошибок {metrics.ClockDriftFailures} · алертов {metrics.ClockDriftAlerts} · {checkedAt}";

        if (metrics.IsClockDriftWarning)
            ClockDriftAlertText = $"Дрейф часов превышает порог {_tradingConfig.ClockDriftWarningThresholdMs} мс — возможны отказы подписи -1021";
        else if (!string.IsNullOrEmpty(metrics.ClockDriftError))
            ClockDriftAlertText = $"Проверка часов недоступна: {metrics.ClockDriftError}";
        else
            ClockDriftAlertText = string.Empty;
    }

    /// <summary>P0.5/P0.6: наблюдаемость реконсиляции, user-data stream и kill-switch.</summary>
    [ObservableProperty] private string _reconciliationText = "Реконсиляция: нет данных";
    [ObservableProperty] private string _reconciliationAlertText = string.Empty;

    private void UpdateReconciliation(MetricsCollector metrics)
    {
        var report = _reconciliation.LastReport;
        ReconciliationText = report == null
            ? $"User-data stream: {(_reconciliation.IsRunning ? "работает" : "не запущен")} · отчётов {metrics.ExecutionReportsReceived} · филлов применено {metrics.ReconciliationFillsApplied} · сирот подхвачено {metrics.OrphanPositionsAdopted} · сиротских ордеров снято {metrics.OrphanOrdersCanceled} · listenKey пересоздан {metrics.ListenKeyRecreations} раз · kill-switch {metrics.KillSwitchActivations}"
            : $"Позиций {report.PositionsChecked} · сирот OCO {report.OrphanOcoLists} · сирот ордеров {report.OrphanOrders} · внешних {report.ExternalOrders} · без защиты {report.ProtectionMissing} · отчётов {metrics.ExecutionReportsReceived} · филлов применено {metrics.ReconciliationFillsApplied} · сирот подхвачено {metrics.OrphanPositionsAdopted} · снято {metrics.OrphanOrdersCanceled} · listenKey пересоздан {metrics.ListenKeyRecreations} раз · kill-switch {metrics.KillSwitchActivations}";
        var alerts = new List<string>();
        if (report != null && report.TotalIssues > 0)
            alerts.Add($"реконсиляция нашла {report.TotalIssues} проблем — см. лог RECON");
        ReconciliationAlertText = string.Join(" | ", alerts);
    }

    /// <summary>P1.7: портфельный риск — экспозиция, корреляция, дневной лимит, серия убытков.</summary>
    [ObservableProperty] private string _portfolioRiskText = "Портфельный риск: нет данных";
    [ObservableProperty] private string _portfolioRiskAlertText = string.Empty;

    private void UpdatePortfolioRisk(MetricsCollector metrics)
    {
        var exposurePct = metrics.LastPortfolioEquity > 0m
            ? metrics.LastPortfolioExposure / metrics.LastPortfolioEquity * 100m : 0m;
        var correlatedPct = metrics.LastPortfolioEquity > 0m
            ? metrics.LastCorrelatedExposure / metrics.LastPortfolioEquity * 100m : 0m;
        var pause = _portfolioRisk.PauseUntilUtc is { } until && DateTime.UtcNow < until
            ? $"пауза до {until:HH:mm:ss} UTC" : "паузы нет";
        PortfolioRiskText =
            $"Экспозиция {exposurePct:F1}% (кап {_tradingConfig.MaxPortfolioExposurePercent:F0}%) · " +
            $"корр. кластер {correlatedPct:F1}% (кап {_tradingConfig.MaxCorrelatedExposurePercent:F0}%) · " +
            $"max ρ {metrics.LastMaxCorrelation:F2} · серия убытков {_portfolioRisk.ConsecutiveLosses} · {pause} · " +
            $"отказов {metrics.PortfolioRiskRejections}";

        var alerts = new List<string>();
        if (metrics.PortfolioPauseUntilUtc is { } p && DateTime.UtcNow < p)
            alerts.Add($"входы приостановлены после серии убытков до {p:HH:mm:ss} UTC");
        if (!string.IsNullOrEmpty(metrics.LastPortfolioRejectionReason))
            alerts.Add($"последний отказ: {metrics.LastPortfolioRejectionReason}");
        PortfolioRiskAlertText = string.Join(" | ", alerts);
    }

    /// <summary>P1.6: наблюдаемость бюджета весов REST и rate-limit (429/-1003/418).</summary>
    [ObservableProperty] private string _rateLimitText = "Бюджет весов REST: нет данных";
    [ObservableProperty] private string _rateLimitAlertText = string.Empty;

    private void UpdateRateLimits(MetricsCollector metrics)
    {
        var weight = metrics.RestWeightLimit > 0
            ? $"{metrics.RestUsedWeight}/{metrics.RestWeightLimit} (макс. {metrics.RestMaxUsedWeight})"
            : "нет сэмплов заголовка";
        RateLimitText =
            $"Вес минуты: {weight} · 429: {metrics.RateLimit429Hits} · -1003: {metrics.RateLimit1003Hits} · " +
            $"418 (IP-бан): {metrics.IpBan418Hits} · притормаживаний {metrics.WeightThrottleWaits} " +
            $"(макс. {metrics.MaxWeightThrottleWaitMs:F0} мс, суммарно {metrics.TotalWeightThrottleWaitMs / 1000:F1} с) · " +
            $"ликвидность: отказов {metrics.LiquidityRejections} (спред {metrics.LiquiditySpreadRejections}, глубина {metrics.LiquidityDepthRejections}, " +
            $"объём {metrics.LiquidityVolumeRejections}, нет стакана {metrics.LiquidityNoBookRejections}), post-only {metrics.PostOnlyRejections}";

        var alerts = new List<string>();
        if (metrics.IpBan418Hits > 0)
            alerts.Add($"получен IP-бан 418 ({metrics.IpBan418Hits}) — последний backoff {metrics.LastRateLimitBackoffMs / 1000:F0} с");
        if (metrics.RateLimit429Hits > 0 || metrics.RateLimit1003Hits > 0)
            alerts.Add($"биржа ограничивала запросы: 429={metrics.RateLimit429Hits}, -1003={metrics.RateLimit1003Hits}");
        if (metrics.RestWeightLimit > 0 && metrics.RestUsedWeight >= metrics.RestWeightLimit * 0.8)
            alerts.Add("вес минуты приближается к лимиту — polling притормаживается, ордера проходят без задержки");
        RateLimitAlertText = string.Join(" | ", alerts);
    }

    public void UpdateMetrics(MetricsCollector metrics)
    {
        var tracker = _riskManager.DrawdownTracker;
        CurrentBalance = tracker.CurrentBalance; DailyPnL = tracker.DailyPnL;
        DailyPnLPercentText = $"{tracker.DailyPnLPercent:P2}"; DrawdownText = $"{tracker.CurrentDrawdownPercent:P2}";
        TotalTrades = metrics.TotalTrades; AvgInferenceTime = metrics.AverageInferenceTimeMs;
        UpdateExecutionQueue(metrics);
        UpdateReconciliation(metrics);
        UpdateClockDrift(metrics);
        UpdateRateLimits(metrics);
        UpdatePortfolioRisk(metrics);
        UpdateBackend();
    }
}
