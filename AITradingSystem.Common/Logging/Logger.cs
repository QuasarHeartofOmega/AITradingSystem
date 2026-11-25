using Serilog;
using Serilog.Events;
using Serilog.Filters;
using AITradingSystem.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace AITradingSystem.Common.Logging;

/// <summary>
/// Централизованная инициализация логгера (FR-8.1).
///
/// Схема вывода:
///   logs/trading-*.log   — общий лог приложения;
///   logs/ai-*.log        — промпты, сырые ответы модели, рассуждения, распарсенные сигналы;
///   logs/exchange-*.log  — все REST-запросы/ответы Binance, ошибки API, WebSocket;
///   logs/news-*.log      — запросы новостных агрегаторов и контекст для модели;
///   logs/trades-*.log    — торговые решения, ордера, открытие/закрытие позиций, баланс.
///
/// «Диагностические» каналы (ai/exchange/news) пишутся на уровне <see cref="LoggingConfig.DiagnosticsLevel"/>
/// независимо от общего MinimumLevel — чтобы промпт и ответ модели были видны всегда,
/// а консоль при этом не засорялась.
/// </summary>
public static class Logger
{
    private static bool _initialized;
    private static readonly object _lock = new();

    /// <summary>Логгер инициализирован.</summary>
    public static bool IsInitialized => _initialized;

    /// <summary>
    /// Инициализирует глобальный логгер Serilog. Повторный вызов переинициализирует
    /// логгер (например, после смены настроек в UI) вместо выброса исключения.
    /// </summary>
    public static void Initialize(LoggingConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();

        lock (_lock)
        {
            ApplyOptions(config);

            var appLevel = ParseLevel(config.MinimumLevel, LogEventLevel.Information);
            var diagLevel = ParseLevel(config.DiagnosticsLevel, LogEventLevel.Debug);
            var globalLevel = (LogEventLevel)Math.Min((int)appLevel, (int)diagLevel);

            Directory.CreateDirectory(config.LogPath);

            var loggerConfig = new LoggerConfiguration()
                .MinimumLevel.Is(globalLevel)
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("System", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .Enrich.WithThreadId()
                .Enrich.WithMachineName()
                .Enrich.WithProperty("Application", "AITradingSystem");

            if (config.WriteToConsole)
            {
                loggerConfig.WriteTo.Logger(lc => lc
                    .Filter.ByExcluding(e => IsDiagnostic(e) && GetLevel(e) < appLevel)
                    .WriteTo.Console(
                        restrictedToMinimumLevel: appLevel,
                        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{Channel}] {Message:lj}{NewLine}{Exception}"));
            }

            if (config.WriteToFile)
            {
                var mainTemplate =
                    "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{ThreadId}] [{Channel}] {SourceContext} {Message:lj}{NewLine}{Exception}";

                loggerConfig.WriteTo.Logger(lc =>
                {
                    lc.Filter.ByExcluding(e =>
                        (config.ExcludeDiagnosticsFromMainLog && IsDiagnostic(e)) || GetLevel(e) < appLevel);
                    lc.WriteTo.File(
                        path: Path.Combine(config.LogPath, "trading-.log"),
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: config.RetainedFileCount,
                        fileSizeLimitBytes: (long)config.MaxFileSizeMb * 1024 * 1024,
                        rollOnFileSizeLimit: true,
                        outputTemplate: mainTemplate,
                        shared: true);
                });

                if (config.SeparateChannelFiles)
                {
                    AddChannelFile(loggerConfig, config, LogChannel.Ai, "ai-.log", diagLevel);
                    AddChannelFile(loggerConfig, config, LogChannel.Exchange, "exchange-.log", diagLevel);
                    AddChannelFile(loggerConfig, config, LogChannel.News, "news-.log", diagLevel);
                    AddChannelFile(loggerConfig, config, LogChannel.Trade, "trades-.log", LogEventLevel.Information);
                }
                else
                {
                    // Обратная совместимость: один файл торговых событий.
                    loggerConfig.WriteTo.Logger(lc => lc
                        .Filter.ByIncludingOnly(Matching.WithProperty("Channel", LogChannel.Trade))
                        .WriteTo.File(
                            path: Path.Combine(config.LogPath, "trades-.log"),
                            rollingInterval: RollingInterval.Day,
                            retainedFileCountLimit: 30,
                            restrictedToMinimumLevel: LogEventLevel.Information,
                            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Message:lj}{NewLine}"));
                }
            }

            if (_initialized)
                Log.CloseAndFlush();

            Log.Logger = loggerConfig.CreateLogger();
            _initialized = true;

            Log.Information(
                "Логгер инициализирован. Level={Level}, Diagnostics={Diagnostics}, Path={Path}, Channels={Channels}",
                config.MinimumLevel, config.DiagnosticsLevel, Path.GetFullPath(config.LogPath),
                config.SeparateChannelFiles ? "ai/exchange/news/trade" : "trade");
        }
    }

    /// <summary>
    /// Подключает Serilog к Microsoft.Extensions.Logging (ILogger&lt;T&gt;).
    /// Без этого все ILogger-сообщения сервисов уходят в консоль по умолчанию
    /// и НЕ попадают в файлы логов.
    /// </summary>
    public static void AttachToLoggingBuilder(ILoggingBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ClearProviders();
        builder.AddSerilog(Log.Logger, dispose: false);
    }

    private static void AddChannelFile(
        LoggerConfiguration root,
        LoggingConfig config,
        string channel,
        string fileName,
        LogEventLevel channelLevel)
    {
        // События канала идут в свой файл; всё, что выше общего уровня,
        // дополнительно остаётся в основном логе (его фильтр настроен отдельно).
        root.WriteTo.Logger(lc => lc
            .Filter.ByIncludingOnly(e => IsChannel(e, channel) || (channel == LogChannel.Trade && IsTradeContext(e)))
            .WriteTo.File(
                path: Path.Combine(config.LogPath, fileName),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: config.RetainedFileCount,
                fileSizeLimitBytes: (long)config.MaxFileSizeMb * 1024 * 1024,
                rollOnFileSizeLimit: true,
                restrictedToMinimumLevel: channelLevel,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                shared: true));
    }

    private static bool IsChannel(LogEvent e, string channel) =>
        e.Properties.TryGetValue("Channel", out var value) &&
        value is ScalarValue scalar &&
        scalar.Value is string s &&
        string.Equals(s, channel, StringComparison.OrdinalIgnoreCase);

    private static bool IsDiagnostic(LogEvent e) =>
        IsChannel(e, LogChannel.Ai) || IsChannel(e, LogChannel.Exchange) || IsChannel(e, LogChannel.News);

    private static bool IsTradeContext(LogEvent e) =>
        e.Properties.TryGetValue("SourceContext", out var value) &&
        value is ScalarValue scalar &&
        scalar.Value is string s &&
        (s.Contains(".Trading.", StringComparison.Ordinal) || s.EndsWith("OrderExecutor", StringComparison.Ordinal));

    private static LogEventLevel GetLevel(LogEvent e) => e.Level;

    private static LogEventLevel ParseLevel(string text, LogEventLevel fallback) =>
        Enum.TryParse<LogEventLevel>(text, true, out var level) ? level : fallback;

    private static void ApplyOptions(LoggingConfig config)
    {
        LogOptions.LogPrompts = config.LogPrompts;
        LogOptions.LogAiResponses = config.LogAiResponses;
        LogOptions.LogThinking = config.LogThinking;
        LogOptions.LogHttpTraffic = config.LogHttpTraffic;
        LogOptions.LogHttpBodies = config.LogHttpBodies;
        LogOptions.LogNews = config.LogNews;
        LogOptions.MaxTextLength = config.MaxTextLength;
        LogOptions.MaskSecrets = config.MaskSecrets;
    }

    /// <summary>Корректное завершение работы логгера.</summary>
    public static void Shutdown()
    {
        lock (_lock)
        {
            if (!_initialized) return;

            Log.Information("Логгер завершает работу.");
            Log.CloseAndFlush();
            _initialized = false;
        }
    }
}
