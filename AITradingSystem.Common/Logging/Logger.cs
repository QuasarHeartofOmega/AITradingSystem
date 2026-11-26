using Serilog;
using Serilog.Core;
using Serilog.Events;
using AITradingSystem.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace AITradingSystem.Common.Logging;

/// <summary>
/// Централизованная инициализация логгера (FR-8.1).
///
/// Каналы и файлы:
///   logs/trading-*.log   — общий лог: жизненный цикл, ошибки инфраструктуры, торговые события;
///   logs/trades-*.log    — только торговые решения/ордера/позиции/риск (канал trade);
///   logs/exchange-*.log  — REST/WS биржи: одна строка на запрос + выжимка тела;
///   logs/ai-*.log        — промпты, рассуждения, ответы модели, разобранные предложения;
///   logs/ai-native-*.log — нативный вывод llama.cpp/ggml (отдельно, чтобы не топил ai-лог);
///   logs/news-*.log      — новостные провайдеры и контекст для модели.
///
/// Правила, которые делают лог разборчивым:
///   • один формат строки для всех приёмников (<see cref="UnifiedLogFormatter"/>);
///   • одно событие — одна запись: каналы пишут события, ILogger сервисов — только
///     жизненный цикл и инфраструктурные ошибки, без дублей тегов;
///   • повторяющиеся состояния подавляются (<see cref="LogGate"/>) и возвращаются
///     строкой с repeats=N, а не тысячами одинаковых строк;
///   • «диагностические» каналы (ai/ai-native/exchange/news) пишутся на уровне
///     <see cref="LoggingConfig.DiagnosticsLevel"/> в свои файлы и по умолчанию не
///     попадают ни в консоль, ни в общий лог.
/// </summary>
public static class Logger
{
    private static bool _initialized;
    private static readonly object _lock = new();

    /// <summary>Логгер инициализирован.</summary>
    public static bool IsInitialized => _initialized;

    /// <summary>
    /// Инициализирует глобальный логгер Serilog. Повторный вызов переинициализирует
    /// логгер (например, после смены настроек в UI).
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
            var tradeLevel = (LogEventLevel)Math.Max((int)appLevel, (int)LogEventLevel.Information);
            // Без отдельных файлов диагностику нельзя одновременно исключить из общего лога —
            // иначе ai/exchange/news не попадут никуда.
            var excludeDiagnostics = config.SeparateChannelFiles && config.ExcludeDiagnosticsFromMainLog;

            Directory.CreateDirectory(config.LogPath);

            var loggerConfig = new LoggerConfiguration()
                .MinimumLevel.Is(globalLevel)
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("System", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .Enrich.With<DefaultChannelEnricher>()
                .Enrich.WithProperty("Application", "AITradingSystem");

            if (config.WriteToConsole)
            {
                loggerConfig.WriteTo.Logger(lc => lc
                    .Filter.ByExcluding(e => IsQuiet(e) || (IsDiagnostic(e) && e.Level < appLevel))
                    .WriteTo.Console(
                        restrictedToMinimumLevel: appLevel,
                        formatter: new UnifiedLogFormatter(compactTime: true, showSource: false)));
            }

            if (config.WriteToFile)
            {
                // Общий лог: всё, кроме тихих/диагностических каналов.
                loggerConfig.WriteTo.Logger(lc => lc
                    .Filter.ByExcluding(e =>
                        IsQuiet(e) ||
                        e.Level < appLevel ||
                        (excludeDiagnostics && IsDiagnostic(e)))
                    .WriteTo.File(
                        formatter: new UnifiedLogFormatter(),
                        path: Path.Combine(config.LogPath, "trading-.log"),
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: config.RetainedFileCount,
                        fileSizeLimitBytes: (long)config.MaxFileSizeMb * 1024 * 1024,
                        rollOnFileSizeLimit: true,
                        shared: true));

                if (config.SeparateChannelFiles)
                {
                    AddChannelFile(loggerConfig, config, LogChannel.Trade, "trades-.log", tradeLevel);
                    AddChannelFile(loggerConfig, config, LogChannel.Exchange, "exchange-.log", diagLevel);
                    AddChannelFile(loggerConfig, config, LogChannel.Ai, "ai-.log", diagLevel);
                    AddChannelFile(loggerConfig, config, LogChannel.AiNative, "ai-native-.log", diagLevel);
                    AddChannelFile(loggerConfig, config, LogChannel.News, "news-.log", diagLevel);
                }
                else
                {
                    // Один файл торговых событий, если разделение каналов выключено.
                    AddChannelFile(loggerConfig, config, LogChannel.Trade, "trades-.log", tradeLevel);
                }
            }

            if (_initialized)
                Log.CloseAndFlush();

            Log.Logger = loggerConfig.CreateLogger();
            _initialized = true;

            Log.Information(
                "LOGGER-START level={Level} diagnostics={Diagnostics} path={Path} http_bodies={Bodies} state_heartbeat_s={Heartbeat} channels={Channels}",
                config.MinimumLevel, config.DiagnosticsLevel, Path.GetFullPath(config.LogPath),
                LogOptions.EffectiveHttpBodyMode, config.StateHeartbeatSeconds,
                config.SeparateChannelFiles ? "system/trade/exchange/ai/ai-native/news" : "system/trade");
        }
    }

    /// <summary>
    /// Подключает Serilog к Microsoft.Extensions.Logging (ILogger&lt;T&gt;).
    /// Без этого сообщения сервисов не попадают в файлы логов.
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
        root.WriteTo.Logger(lc => lc
            .Filter.ByIncludingOnly(e => IsChannel(e, channel))
            .WriteTo.File(
                formatter: new UnifiedLogFormatter(),
                path: Path.Combine(config.LogPath, fileName),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: config.RetainedFileCount,
                fileSizeLimitBytes: (long)config.MaxFileSizeMb * 1024 * 1024,
                rollOnFileSizeLimit: true,
                restrictedToMinimumLevel: channelLevel,
                shared: true));
    }

    private static bool IsChannel(LogEvent e, string channel) =>
        e.Properties.TryGetValue("Channel", out var value) &&
        value is ScalarValue { Value: string s } &&
        string.Equals(s, channel, StringComparison.OrdinalIgnoreCase);

    private static bool IsDiagnostic(LogEvent e) =>
        IsChannel(e, LogChannel.Ai) || IsChannel(e, LogChannel.Exchange) || IsChannel(e, LogChannel.News);

    /// <summary>Нативный вывод модели живёт только в своём файле — он не нужен ни в консоли, ни в общем логе.</summary>
    private static bool IsQuiet(LogEvent e) => IsChannel(e, LogChannel.AiNative);

    private static LogEventLevel ParseLevel(string text, LogEventLevel fallback) =>
        Enum.TryParse<LogEventLevel>(text, true, out var level) ? level : fallback;

    private static void ApplyOptions(LoggingConfig config)
    {
        LogOptions.LogPrompts = config.LogPrompts;
        LogOptions.LogAiResponses = config.LogAiResponses;
        LogOptions.LogThinking = config.LogThinking;
        LogOptions.LogNativeAiOutput = config.LogNativeAiOutput;
        LogOptions.LogHttpTraffic = config.LogHttpTraffic;
        LogOptions.LogHttpBodies = config.LogHttpBodies;
        LogOptions.HttpBodyMode = config.HttpBodyMode;
        LogOptions.LogNews = config.LogNews;
        LogOptions.MaxTextLength = config.MaxTextLength;
        LogOptions.MaskSecrets = config.MaskSecrets;

        LogGate.HeartbeatSeconds = config.StateHeartbeatSeconds;
        LogGate.Reset();
    }

    /// <summary>Корректное завершение работы логгера.</summary>
    public static void Shutdown()
    {
        lock (_lock)
        {
            if (!_initialized) return;

            Log.Information("LOGGER-STOP");
            Log.CloseAndFlush();
            _initialized = false;
        }
    }

    /// <summary>Событие без канала относится к system — чтобы поле [канал] никогда не было пустым.</summary>
    private sealed class DefaultChannelEnricher : ILogEventEnricher
    {
        private static readonly LogEventProperty SystemChannel = new("Channel", new ScalarValue(LogChannel.System));

        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            if (!logEvent.Properties.ContainsKey("Channel"))
                logEvent.AddPropertyIfAbsent(SystemChannel);
        }
    }
}
