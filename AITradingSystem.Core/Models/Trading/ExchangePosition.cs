namespace AITradingSystem.Core.Models.Trading;

/// <summary>Спотовый остаток актива на бирже, не позиция бота и не маржинальный шорт.</summary>
public sealed class ExchangePosition
{
    public string Asset { get; set; } = string.Empty;
    public decimal Free { get; set; }
    public decimal Locked { get; set; }
    public decimal Total => Free + Locked;
    public decimal? LastPrice { get; set; }
    /// <summary>Оценка в USDT; null означает отсутствие надёжного маршрута конвертации.</summary>
    public decimal? ValueInQuote { get; set; }
    public string PriceRoute { get; set; } = string.Empty;
}
