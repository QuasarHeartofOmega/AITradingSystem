using AITradingSystem.Core.Models.Enums;

namespace AITradingSystem.Core.Models.Trading;

/// <summary>
/// P0.5: факт исполнения/отмены ордера с биржи (executionReport из user-data stream).
/// Это сырой факт для реконсиляции, а не позиция бота: применение филлов к позициям —
/// следующий этап P0.5.
/// </summary>
public sealed class ExecutionReport
{
    public string Symbol { get; init; } = string.Empty;
    public OrderSide Side { get; init; }
    public long OrderId { get; init; }
    public string ClientOrderId { get; init; } = string.Empty;

    /// <summary>Id OCO-связки, если ордер — её нога; -1 для одиночного ордера.</summary>
    public long OrderListId { get; init; } = -1;

    /// <summary>Exchange trade id (t) when present; never fabricate from cumulative quantity.</summary>
    public long TradeId { get; init; }

    /// <summary>NEW / TRADE / CANCELED / EXPIRED / REJECTED …</summary>
    public string ExecutionType { get; init; } = string.Empty;

    /// <summary>NEW / PARTIALLY_FILLED / FILLED / CANCELED / EXPIRED / REJECTED …</summary>
    public string OrderStatus { get; init; } = string.Empty;

    /// <summary>Объём последней сделки (l).</summary>
    public decimal LastQuantity { get; init; }

    /// <summary>Цена последней сделки (L).</summary>
    public decimal LastPrice { get; init; }

    /// <summary>Суммарно исполнено по ордеру (z).</summary>
    public decimal CumulativeQuantity { get; init; }

    /// <summary>Суммарный котировочный объём исполнения (Z).</summary>
    public decimal CumulativeQuoteQuantity { get; init; }

    public decimal Commission { get; init; }
    public string CommissionAsset { get; init; } = string.Empty;
    public DateTime TransactionTimeUtc { get; init; }
    public string RejectReason { get; init; } = string.Empty;

    /// <summary>В отчёте есть реальный филл (частичный или полный).</summary>
    public bool IsFill => ExecutionType == "TRADE" && LastQuantity > 0;

    public bool IsTerminal => OrderStatus is "FILLED" or "CANCELED" or "REJECTED" or "EXPIRED";
}
