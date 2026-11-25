namespace AITradingSystem.Common.Configuration;

/// <summary>
/// Настройки логирования (FR-8.1).
/// Каналы: ai (промпты/ответы модели), exchange (HTTP/WS биржи), news, trade.
/// </summary>
public sealed class LoggingConfig
{
    public string MinimumLevel { get; set; } = "Information";
    public string LogPath { get; set; } = "logs";
    public bool WriteToConsole { get; set; } = true;
    public bool WriteToFile { get; set; } = true;

    /// <summary>Максимальный размер одного лог-файла в МБ.</summary>
    public int MaxFileSizeMb { get; set; } = 50;

    /// <summary>Количество хранимых лог-файлов.</summary>
    public int RetainedFileCount { get; set; } = 10;

    /// <summary>
    /// Уровень для «шумных» каналов (HTTP-трафик, сырые ответы модели, промпты).
    /// Ставьте Debug, чтобы видеть весь обмен в файлах.
    /// </summary>
    public string DiagnosticsLevel { get; set; } = "Debug";

    /// <summary>Раскладывать события по отдельным файлам каналов.</summary>
    public bool SeparateChannelFiles { get; set; } = true;

    /// <summary>Не дублировать тела промптов/HTTP в общий лог (только в файлы каналов).</summary>
    public bool ExcludeDiagnosticsFromMainLog { get; set; } = false;

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
    public int MaxTextLength { get; set; } = 20000;

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
    }
}
