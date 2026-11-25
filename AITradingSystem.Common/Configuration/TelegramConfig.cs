namespace AITradingSystem.Common.Configuration;

/// <summary>
/// Конфигурация Telegram-алертов (FR-8.2).
/// </summary>
public sealed class TelegramConfig
{
    public string BotToken { get; set; } = string.Empty;
    public string ChatId { get; set; } = string.Empty;
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Минимальный уровень события для отправки алерта.
    /// </summary>
    public string MinAlertLevel { get; set; } = "Warning";

    public void Validate()
    {
        if (!Enabled) return;

        if (string.IsNullOrWhiteSpace(BotToken))
            throw new InvalidOperationException("Telegram BotToken не указан.");

        if (string.IsNullOrWhiteSpace(ChatId))
            throw new InvalidOperationException("Telegram ChatId не указан.");

        var validLevels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Info", "Warning", "Error", "Critical"
        };
        if (!validLevels.Contains(MinAlertLevel))
            throw new InvalidOperationException(
                $"MinAlertLevel должен быть одним из: {string.Join(", ", validLevels)}");
    }
}