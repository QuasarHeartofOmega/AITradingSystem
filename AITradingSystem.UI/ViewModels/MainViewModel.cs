using System.Windows;
using System.Windows.Threading;
using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.Trading;
using AITradingSystem.Services.Monitoring;
using AITradingSystem.Services.Trading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AITradingSystem.UI.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly TradingEngine _engine;
    private readonly MetricsCollector _metrics;
    private readonly ILogger<MainViewModel> _logger;
    private readonly DispatcherTimer _dashboardTimer;
    private readonly ExchangeAccountService _account;
    [ObservableProperty] private string _accountBanner = "Ожидание данных аккаунта";
    [ObservableProperty] private string _accountBannerBackground = "#423514";
    [ObservableProperty] private string _accountBannerForeground = "#FFD54F";



    [ObservableProperty]
    private BotStatus _botStatus = BotStatus.Stopped;

    [ObservableProperty]
    private string _statusText = "Остановлен";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _canStart = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _canStop;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PauseCommand))]
    private bool _canPause;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResumeCommand))]
    private bool _canResume;

    public DashboardViewModel Dashboard { get; }
    public TradingViewModel Trading { get; }
    public SettingsViewModel Settings { get; }

    public MainViewModel(
        TradingEngine engine,
        MetricsCollector metrics,
        DashboardViewModel dashboard,
        TradingViewModel trading,
        SettingsViewModel settings,
        ILogger<MainViewModel> logger,
        ExchangeAccountService account)
    {
        _engine = engine;
        _metrics = metrics;
        _logger = logger;
        Dashboard = dashboard;
        Trading = trading;
        Settings = settings;
        _account = account;
        _account.OnAccountUpdated += OnAccountUpdated;
        _account.OnOrdersUpdated += OnOrdersUpdated;
        _account.OnStatusUpdated += OnAccountUpdated;
        UpdateAccountBanner();

        // Подписка на события движка.
        // BeginInvoke вместо Invoke: события приходят из фонового цикла движка,
        // синхронный Invoke может заблокировать его (а при остановке — устроить дедлок).
        _engine.OnStatusChanged += OnStatusChanged;
        _engine.OnSignalGenerated += signal => RunOnUiThread(() => Trading.AddSignal(signal));
        _engine.OnPositionOpened += pos => RunOnUiThread(() => Trading.AddPosition(pos));
        // Phase 1.7: Release убирает строку без записи о продаже; список кандидатов перечитывается по событию.
        _engine.OnPositionReleased += symbol => RunOnUiThread(() =>
        {
            Trading.RemovePosition(symbol);
            Trading.RefreshExternalCandidates();
        });
        _engine.OnExternalCandidatesChanged += () => RunOnUiThread(() => Trading.RefreshExternalCandidates());
        _engine.OnKillSwitchEngaged += reason => RunOnUiThread(() =>
        {
            Trading.PanicStatusText = $"Kill-switch взведён ({reason}): новые входы заблокированы.";
            UpdateAccountBanner();
        });
        _engine.OnPartialPositionClosed += trade => RunOnUiThread(() =>
        {
            Trading.AddTradeRecord(trade);
            Dashboard.UpdateMetrics(_metrics);
        });
        _engine.OnPositionClosed += trade => RunOnUiThread(() =>
        {
            Trading.RemovePosition(trade.Symbol);
            Trading.AddTradeRecord(trade);
            _metrics.RecordTrade(trade);
            Dashboard.UpdateMetrics(_metrics);
        });

        // Периодическое обновление дашборда (баланс, PnL, просадка, метрики)
        _dashboardTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _dashboardTimer.Tick += (_, _) =>
        {
            Dashboard.UpdateMetrics(_metrics);
            Settings.RefreshBackendInfo();
            UpdateAccountBanner(); // баннер REAL MONEY не должен исчезать на паузе/остановке
        };
        _dashboardTimer.Start();
    }

    private void OnAccountUpdated() => RunOnUiThread(() => { Dashboard.UpdateAccount(); Trading.UpdateAccount(); UpdateAccountBanner(); });
    private void OnOrdersUpdated() => RunOnUiThread(() => Trading.UpdateAccount());
    private void UpdateAccountBanner()
    {
        var realMoneyLive = _account.Mode == "REAL ORDERS" && _account.EnvironmentName == "Live";
        AccountBannerBackground = realMoneyLive ? "#7F0000" : "#423514";
        AccountBannerForeground = realMoneyLive ? "#FFFFFF" : "#FFD54F";
        AccountBanner = _account.Mode == "PAPER"
            ? "PAPER: ордера не отправляются на биржу. Балансы ниже — аккаунт " + _account.EnvironmentName + "."
            : realMoneyLive
                ? "REAL MONEY — LIVE: реальные средства и реальные ордера."
                : "REAL ORDERS: ордера отправляются в " + _account.EnvironmentName + ".";
        if (_engine.IsKillSwitchEngaged)
            AccountBanner += $" KILL-SWITCH: новые входы заблокированы ({_engine.KillSwitchReason}; {_engine.KillSwitchCloseStatus}).";
        if (!_account.HasApiKeys) AccountBanner += " API-ключи не заданы: балансы недоступны.";
        if (_engine.UnavailableSymbols.Count > 0) AccountBanner += " Недоступные пары: " + string.Join(", ", _engine.UnavailableSymbols) + ".";
        if (_engine.UnadoptedExternalCount > 0)
            AccountBanner += $" External-остатки без сопровождения: {_engine.UnadoptedExternalCount} (вкладка «Торговля» → подхватить или Release).";
    }
    public void Dispose()
    {
        _dashboardTimer.Stop();
        _account.OnAccountUpdated -= OnAccountUpdated;
        _account.OnOrdersUpdated -= OnOrdersUpdated;
        _account.OnStatusUpdated -= OnAccountUpdated;
        // Обработчик kill-switch анонимный; движок живёт столько же, сколько оболочка.
    }
    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
            return;

        if (dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    private void OnStatusChanged(BotStatus status)
    {
        RunOnUiThread(() =>
        {
            BotStatus = status;
            StatusText = status switch
            {
                BotStatus.Stopped => "Остановлен",
                BotStatus.Starting => "Запуск...",
                BotStatus.Running => "Работает",
                BotStatus.Paused => "Приостановлен",
                BotStatus.Stopping => "Остановка...",
                BotStatus.Error => "Ошибка",
                _ => "Неизвестно"
            };

            IsRunning = status == BotStatus.Running || status == BotStatus.Paused;
            CanStart = status == BotStatus.Stopped || status == BotStatus.Error;
            CanStop = status == BotStatus.Running || status == BotStatus.Paused || status == BotStatus.Starting;
            CanPause = status == BotStatus.Running;
            CanResume = status == BotStatus.Paused;
        });
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        try
        {
            await _engine.StartAsync();
            UpdateAccountBanner();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при запуске движка");
            MessageBox.Show($"Ошибка запуска: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        try
        {
            await _engine.StopAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при остановке движка");
            MessageBox.Show($"Ошибка остановки: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanPause))]
    private void Pause()
    {
        _engine.Pause();
    }

    [RelayCommand(CanExecute = nameof(CanResume))]
    private void Resume()
    {
        _engine.Resume();
    }
}