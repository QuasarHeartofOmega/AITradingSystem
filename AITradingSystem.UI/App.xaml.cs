using System.Text;
using System.Windows;
using System.Windows.Threading;
using AITradingSystem.Common.Configuration;
using AITradingSystem.Common.Logging;
using AITradingSystem.Core.RiskManagement;
using AITradingSystem.Data.Binance;
using AITradingSystem.Data.News;
using AITradingSystem.Data.Storage;
using AITradingSystem.Services.AI;
using AITradingSystem.Services.Analysis;
using AITradingSystem.Services.Monitoring;
using AITradingSystem.Services.Trading;
using AITradingSystem.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AITradingSystem.UI;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Русские сообщения в консоли иначе выводятся кракозябрами (CP866 против UTF-8).
        TryEnableUtf8Console();

        // Глобальные обработчики: необработанное исключение не должно ронять процесс молча.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            _host = Host.CreateDefaultBuilder()
                .ConfigureLogging(Logger.AttachToLoggingBuilder)
                .ConfigureServices((context, services) =>
                {
                    var configPath = AppDomain.CurrentDomain.BaseDirectory;
                    var config = ConfigLoader.Load(configPath);

                    services.AddSingleton(config);
                    services.AddSingleton(config.Trading);
                    services.AddSingleton(config.Binance);
                    services.AddSingleton(config.AI);
                    services.AddSingleton(config.News);
                    services.AddSingleton(config.Telegram);
                    services.AddSingleton(config.Logging);

                    Logger.Initialize(config.Logging);

                    // Сводка запуска: сразу видно, куда ходим за данными и чем считаем модель.
                    Serilog.Log.ForContext("Channel", LogChannel.System).Information(
                        "Старт: Binance={Environment} (REST {Rest}), режим={Mode}, symbols={Symbols}, " +
                        "AI backend={Backend}, thinking={Thinking}, новости: cv={Cv} cryptopanic={Cp}",
                        config.Binance.ResolvedEnvironment,
                        config.Binance.RestBaseUrl,
                        config.Trading.PaperTradingMode ? "PAPER" : "REAL ORDERS",
                        string.Join(",", config.Trading.Symbols),
                        config.AI.Backend,
                        config.AI.EnableThinking,
                        config.News.CryptocurrencyCvEnabled,
                        config.News.CryptoPanicEnabled && !string.IsNullOrWhiteSpace(config.News.CryptoPanicApiKey));

                    Serilog.Log.Information("GPU preflight: {Summary}", GpuProbe.Detect().Summary);
                    Serilog.Log.Information(
                        "Конфигурация: AutoFit={AutoFit}, ctx={Context}, thinkingBudget={Budget}, symbols={Count}, confidence={Policy}, accountRefresh={AccountSeconds}s",
                        config.AI.AutoFitGpu, config.AI.ContextSize, config.AI.ThinkingBudgetTokens,
                        config.Trading.Symbols.Count, config.AI.ConfidencePolicy, config.Trading.AccountRefreshIntervalSeconds);

                    // Регистрируем резолвер нативных библиотек llama.cpp ДО любого вызова LLamaSharp.
                    // Иначе NativeApi..cctor падает с "The native library cannot be correctly loaded".
                    LlamaNativeLoader.Initialize(config.AI.NativeLibraryPath);

                    services.AddSingleton<IBinanceRestClient, BinanceRestClient>();
                    services.AddSingleton<IBinanceWebSocketClient, BinanceWebSocketClient>();

                    // Новостные провайдеры: бесплатный агрегатор cryptocurrency.cv (без ключа)
                    // и CryptoPanic (если задан ключ). NewsAggregator получает их все.
                    services.AddSingleton<INewsProvider, CryptocurrencyCvClient>();
                    services.AddSingleton<INewsProvider, CryptoPanicClient>();
                    services.AddSingleton<NewsAggregator>();
                    services.AddSingleton<ITradeHistoryRepository>(sp =>
                        new JsonTradeHistoryRepository(System.IO.Path.Combine(configPath, "data", "trades.json"), sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<JsonTradeHistoryRepository>>()));
                    services.AddSingleton<IStateRepository>(sp =>
                        new JsonStateRepository(System.IO.Path.Combine(configPath, "data", "state"), sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<JsonStateRepository>>()));

                    // P0.3: durable-журнал намерений исполнения — идемпотентность переживает рестарт.
                    services.AddSingleton<IExecutionJournal>(sp =>
                        new JsonExecutionJournal(System.IO.Path.Combine(configPath, "data", "state"), sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<JsonExecutionJournal>>()));

                    services.AddSingleton<PromptBuilder>();
                    services.AddSingleton<IAIModel, QwenModel>();
                    // P2.6: веса direction-скоринга из AI:ScoringWeights (дефолты = прежние константы);
                    // рантайм их не меняет — подгонка оффлайн-инструментом, применение оператором.
                    services.AddSingleton(config.AI.ScoringWeights);
                    services.AddSingleton<SignalCalibrator>();
                    services.AddSingleton<ExchangeAccountService>();
                    services.AddSingleton<MarketAnalyzer>();
                    services.AddSingleton<SentimentAnalyzer>();
                    // P1.7: портфельный риск — один экземпляр на RiskManager и TradingEngine.
                    services.AddSingleton<PortfolioRiskManager>();
                    services.AddSingleton<RiskManager>(sp =>
                        new RiskManager(config.Trading,
                            config.Trading.PaperTradingMode ? config.Trading.PaperTradingInitialBalance : 10000m,
                            portfolioRisk: sp.GetRequiredService<PortfolioRiskManager>()));
                    services.AddSingleton<PositionManager>();
                    // P0.3: очередь исполнения с приоритетами и идемпотентностью по clientOrderId.
                    services.AddSingleton<ExecutionQueue>();
                    services.AddSingleton<IOrderExecutor, OrderExecutor>();
                    // P0.2: тик-контур сопровождения позиций — отдельно от цикла анализа.
                    services.AddSingleton<PositionMonitor>();
                    // P0.5: реконсиляция + user-data stream; TradingEngine зависит от него.
                    services.AddSingleton<ReconciliationService>();
                    services.AddSingleton<TradingEngine>();
                    services.AddSingleton<TelegramAlertService>();
                    services.AddSingleton<MetricsCollector>();
                    // P1.6: метрики весов/rate-limit REST-клиента идут в тот же синглтон MetricsCollector.
                    services.AddSingleton<AITradingSystem.Data.Binance.IRestRateLimitMetrics>(sp =>
                        sp.GetRequiredService<MetricsCollector>());
                    // P1.8: независимый мониторинг дрейфа часов + алерты.
                    services.AddSingleton<ClockDriftMonitor>();
                    // P2.4: подбор порога MinConfidence по expectancy после издержек на истории.
                    services.AddSingleton<ConfidenceThresholdTuner>();

                    services.AddSingleton<MainViewModel>();
                    services.AddSingleton<DashboardViewModel>();
                    services.AddSingleton<TradingViewModel>();
                    services.AddSingleton<SettingsViewModel>();
                    services.AddSingleton<MainWindow>();
                })
                .Build();

            await _host.StartAsync();
            // Read-only опрос аккаунта не зависит от кнопки запуска торговли.
            _host.Services.GetRequiredService<ExchangeAccountService>().Start();

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();

            // КОРРЕКТНОЕ ЗАКРЫТИЕ: при закрытии окна останавливаем хост
            mainWindow.Closed += async (sender, args) =>
            {
                try
                {
                    if (_host != null)
                    {
                        await _host.StopAsync(TimeSpan.FromSeconds(5));
                        _host.Dispose();
                        _host = null;
                    }
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "Ошибка при остановке хоста");
                }
                finally
                {
                    Logger.Shutdown();
                    Environment.Exit(0); // Гарантированный выход
                }
            };

            mainWindow.Show();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Критическая ошибка при запуске");
            MessageBox.Show($"Критическая ошибка при запуске:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            Logger.Shutdown();
            Environment.Exit(1);
        }
    }

    private static void TryEnableUtf8Console()
    {
        try
        {
            if (Console.IsOutputRedirected || Environment.UserInteractive)
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
        }
        catch (Exception)
        {
            // Консоли нет (запуск ярлыком) — просто пропускаем.
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Serilog.Log.Error(e.Exception, "Необработанное исключение в UI-потоке");
        MessageBox.Show($"Ошибка: {e.Exception.Message}", "AITradingSystem", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Serilog.Log.Fatal(e.ExceptionObject as Exception, "Необработанное исключение домена, IsTerminating: {Terminating}", e.IsTerminating);
        Logger.Shutdown();
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Serilog.Log.Error(e.Exception, "Неobserved исключение фоновой задачи");
        e.SetObserved();
    }
}
