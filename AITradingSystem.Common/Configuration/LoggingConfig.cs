namespace AITradingSystem.Common.Configuration;

/// <summary>
/// Настройки логирования (FR-8.1).
/// Каналы: ai, ai-native, exchange, news, trade; прочее — system (общий лог).
/// Карта каналов, словарь событий и примеры: docs/LOGGING.md.
/// </summary>
public sealed class LoggingConfig
{
    public string MinimumLevel { get; set; } = "Information";
    public string LogPath { get; set; } = "logs";
    public bool WriteToConsole { get; set; } = true;
    public bool WriteToFile { get; set; } = true;

    /// <summary>Максимальный размер одного лог-файла в МБ.</summary>
    public int MaxFileSizeMb { get; set; } = 25;

    /// <summary>Количество хранимых файлов на каждый приёмник (суточные и размерные роли вместе).</summary>
    public int RetainedFileCount { get; set; } = 14;

    /// <summary>
    /// Уровень для «шумных» каналов (HTTP-трафик, сырые ответы модели, промпты).
    /// Ставьте Debug, чтобы видеть весь обмен в файлах.
    /// </summary>
    public string DiagnosticsLevel { get; set; } = "Debug";

    /// <summary>Раскладывать события по отдельным файлам каналов.</summary>
    public bool SeparateChannelFiles { get; set; } = true;

    /// <summary>Не дублировать тела промптов/HTTP в общий лог (только в файлы каналов).</summary>
    public bool ExcludeDiagnosticsFromMainLog { get; set; } = true;

    /// <summary>
    /// Что писать вместо тела HTTP-ответа биржи:
    ///   Summary — одна строка-выжимка (balances/candles/orders/price), по умолчанию;
    ///   Full    — полное тело с обрезкой по MaxTextLength (для глубокой отладки);
    ///   Off     — только размер тела.
    /// Полные тела /account и /ticker/price дают десятки мегабайт в час и не читаются.
    /// </summary>
    public string HttpBodyMode { get; set; } = "Summary";

    /// <summary>
    /// Через сколько секунд повторять в логе неизменившееся состояние (ИТОГ, RECON-SUMMARY,
    /// DATA-STALE и т.п.) со счётчиком repeats=N. 0 — писать только при изменении,
    /// -1 — отключить подавление и писать каждое событие.
    /// </summary>
    public int StateHeartbeatSeconds { get; set; } = 300;

    /// <summary>Писать нативный вывод llama.cpp/ggml в отдельный файл ai-native-*.log.</summary>
    public bool LogNativeAiOutput { get; set; } = true;

    /// <summary>Логировать полный текст промпта, уходящего в модель.</summary>
    public bool LogPrompts { get; set; } = true;

    /// <summary>Логировать сырой ответ модели до парсинга.</summary>
    public bool LogAiResponses { get; set; } = true;

    /// <summary>Логировать рассуждения модели (thinking).</summary>
    public bool LogThinking { get; set; } = true;

    /// <summary>Логировать HTTP-запросы/ответы к бирже.</summary>
    public bool LogHttpTraffic { get; set; } = true;

    /// <summary>Логировать тела HTTP-ответов.</summary>
    public bool LogHttpBodies { get; set; } = true;

    /// <summary>Логировать новостные запросы и контекст для модели.</summary>
    public bool LogNews { get; set; } = true;

    /// <summary>Максимальная длина текстового блока в логе, символов (0 — без обрезки).</summary>
    public int MaxTextLength { get; set; } = 8000;

    /// <summary>Маскировать подписи/ключи/токены.</summary>
    public bool MaskSecrets { get; set; } = true;

    public void Validate()
    {
        var validLevels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Verbose", "Debug", "Information", "Warning", "Error", "Fatal"
        };
        if (!validLevels.Contains(MinimumLevel))
            throw new InvalidOperationException($"Недопустимый уровень логирования: {MinimumLevel}");

        if (!validLevels.Contains(DiagnosticsLevel))
            throw new InvalidOperationException($"Недопустимый уровень DiagnosticsLevel: {DiagnosticsLevel}");

        if (string.IsNullOrWhiteSpace(LogPath))
            throw new InvalidOperationException("LogPath не указан.");

        if (MaxFileSizeMb < 1 || MaxFileSizeMb > 1000)
            throw new InvalidOperationException("MaxFileSizeMb должен быть в диапазоне [1; 1000].");

        if (RetainedFileCount < 1 || RetainedFileCount > 365)
            throw new InvalidOperationException("RetainedFileCount должен быть в диапазоне [1; 365].");

        if (MaxTextLength < 0 || MaxTextLength > 1_000_000)
            throw new InvalidOperationException("MaxTextLength должен быть в диапазоне [0; 1000000].");

        var validBodyModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Summary", "Full", "Off" };
        if (!validBodyModes.Contains(HttpBodyMode))
            throw new InvalidOperationException($"Недопустимый HttpBodyMode: {HttpBodyMode} (Summary|Full|Off).");

        if (StateHeartbeatSeconds < -1 || StateHeartbeatSeconds > 86400)
            throw new InvalidOperationException("StateHeartbeatSeconds должен быть в диапазоне [-1; 86400].");
    }
}
