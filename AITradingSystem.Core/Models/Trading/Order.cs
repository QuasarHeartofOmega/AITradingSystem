using AITradingSystem.Core.Models.Enums;

namespace AITradingSystem.Core.Models.Trading;

/// <summary>
/// Ордер на бирже (FR-6).
/// </summary>
public sealed class Order
{
    public string Id { get; set; } = string.Empty;
    public string ClientOrderId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public OrderSide Side { get; set; }
    public OrderType Type { get; set; }
    public OrderStatus Status { get; set; }
    public decimal Price { get; set; }
    public decimal OriginalQuantity { get; set; }
    public decimal ExecutedQuantity { get; set; }
    public decimal FilledQuoteQuantity { get; set; }
    public decimal Commission { get; set; }
    public string CommissionAsset { get; set; } = string.Empty;
    public Dictionary<string, decimal> CommissionByAsset { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public decimal GetCommission(string asset) =>
        !string.IsNullOrWhiteSpace(asset) && CommissionByAsset.TryGetValue(asset, out var fee) ? fee : 0m;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public bool IsPostOnly { get; set; }
    public long BinanceOrderId { get; set; }

    /// <summary>
    /// Средняя цена исполнения.
    /// </summary>
    public decimal? AverageFillPrice =>
        ExecutedQuantity > 0 ? FilledQuoteQuantity / ExecutedQuantity : null;

    /// <summary>
    /// Процент исполнения.
    /// </summary>
    public decimal FillPercent =>
        OriginalQuantity > 0 ? ExecutedQuantity / OriginalQuantity * 100m : 0;

    public bool IsActive =>
        Status == OrderStatus.New || Status == OrderStatus.PartiallyFilled;
}