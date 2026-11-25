namespace AITradingSystem.Common.Configuration;

/// <summary>
/// Корневой класс конфигурации приложения.
/// </summary>
public sealed class AppConfig
{
    public TradingConfig Trading { get; set; } = new();
    public BinanceConfig Binance { get; set; } = new();
    public AIConfig AI { get; set; } = new();
    public NewsConfig News { get; set; } = new();
    public TelegramConfig Telegram { get; set; } = new();
    public LoggingConfig Logging { get; set; } = new();

    /// <summary>
    /// Валидация всех секций конфигурации.
    /// </summary>
    public void Validate()
    {
        Trading.Validate();
        Binance.Validate();
        AI.Validate();
        News.Validate();
        Telegram.Validate();
        Logging.Validate();
    }
}