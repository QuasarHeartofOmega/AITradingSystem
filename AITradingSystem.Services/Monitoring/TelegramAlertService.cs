using Telegram.Bot;
using Telegram.Bot.Types;
using AITradingSystem.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace AITradingSystem.Services.Monitoring;

/// <summary>
/// Сервис отправки алертов в Telegram (FR-8.2).
/// </summary>
public sealed class TelegramAlertService
{
    private readonly TelegramConfig _config;
    private readonly TelegramBotClient? _botClient;
    private readonly ILogger<TelegramAlertService> _logger;

    public TelegramAlertService(
        TelegramConfig config,
        ILogger<TelegramAlertService> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (_config.Enabled && !string.IsNullOrWhiteSpace(_config.BotToken))
        {
            _botClient = new TelegramBotClient(_config.BotToken);
        }
    }

    /// <summary>
    /// Отправить алерт.
    /// </summary>
    public async Task SendAlertAsync(string message, string level = "Info", CancellationToken ct = default)
    {
        if (!_config.Enabled || _botClient == null)
            return;

        var minLevel = ParseLevel(_config.MinAlertLevel);
        var currentLevel = ParseLevel(level);

        if (currentLevel < minLevel)
            return;

        try
        {
            var emoji = level.ToLowerInvariant() switch
            {
                "info" => "ℹ️",
                "warning" => "⚠️",
                "error" => "❌",
                "critical" => "🚨",
                _ => "📊"
            };

            var fullMessage = $"{emoji} [{level.ToUpperInvariant()}] {message}";

            // ChatId: числовой id или @username
            var chatId = long.TryParse(_config.ChatId, out var numericId)
                ? new ChatId(numericId)
                : new ChatId(_config.ChatId);

            await _botClient.SendTextMessageAsync(
                chatId: chatId,
                text: fullMessage,
                cancellationToken: ct);

            _logger.LogDebug("Telegram алерт отправлен: {Level}", level);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка отправки Telegram алерта");
        }
    }

    /// <summary>
    /// "Info" не входит в Microsoft.Extensions.Logging.LogLevel — маппим вручную.
    /// </summary>
    private static LogLevel ParseLevel(string level)
    {
        var normalized = level.Trim().ToLowerInvariant() switch
        {
            "info" => "information",
            "warn" => "warning",
            "fatal" => "critical",
            var other => other
        };

        return Enum.TryParse<LogLevel>(normalized, true, out var parsed)
            ? parsed
            : LogLevel.Information;
    }
}