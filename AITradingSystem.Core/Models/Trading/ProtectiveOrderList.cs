using AITradingSystem.Core.Models.Enums;

namespace AITradingSystem.Core.Models.Trading;

/// <summary>
/// Биржевая защита позиции: OCO-связка STOP_LOSS_LIMIT + TAKE_PROFIT_LIMIT (P0.1).
/// Хранится внутри <see cref="Position"/> и персистится в positions.json,
/// чтобы после рестарта знать, какие ноги стоят на бирже.
///
/// Смысл: позиция защищена САМОЙ БИРЖЕЙ. Если процесс упал/убит —
/// стоп и тейк остаются в стакане биржи и закрывают позицию без бота.
/// Локальный SL в TradingEngine — только дублёр.
/// </summary>
public sealed class ProtectiveOrderList
{
    /// <summary>orderListId биржи (0 для симуляции в paper-режиме).</summary>
    public long OrderListId { get; set; }

    /// <summary>listClientOrderId — ключ идемпотентности связки.</summary>
    public string ListClientOrderId { get; set; } = string.Empty;

    public string Symbol { get; set; } = string.Empty;

    /// <summary>Сторона защитных ордеров: противоположна стороне позиции.</summary>
    public OrderSide Side { get; set; }

    /// <summary>Объём защиты (после округления к stepSize; может быть меньше объёма позиции).</summary>
    public decimal Quantity { get; set; }

    /// <summary>Триггер стопа (stopPrice ноги STOP_LOSS_LIMIT).</summary>
    public decimal StopTriggerPrice { get; set; }

    /// <summary>Лимитная цена стопа (price ноги STOP_LOSS_LIMIT).</summary>
    public decimal StopLimitPrice { get; set; }

    /// <summary>Триггер тейка (stopPrice ноги TAKE_PROFIT_LIMIT).</summary>
    public decimal TakeProfitTriggerPrice { get; set; }

    /// <summary>Лимитная цена тейка (price ноги TAKE_PROFIT_LIMIT).</summary>
    public decimal TakeProfitLimitPrice { get; set; }

    public long StopOrderId { get; set; }
    public string StopClientOrderId { get; set; } = string.Empty;
    public long TakeProfitOrderId { get; set; }
    public string TakeProfitClientOrderId { get; set; } = string.Empty;

    /// <summary>
    /// Все orderId связки. Заполняется, когда биржа вернула только refs (без orderReports),
    /// например в GET /api/v3/openOrderList — там нет типов ног.
    /// </summary>
    public List<long> OrderIds { get; set; } = new();

    /// <summary>listOrderStatus биржи: EXECUTING / ALL_DONE / REJECT.</summary>
    public string ListOrderStatus { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReplacedAt { get; set; }

    /// <summary>Симуляция (paper-режим): на бирже ничего не стоит.</summary>
    public bool IsSimulated { get; set; }

    /// <summary>Защита реально стоит на бирже (не симуляция и есть id связки).</summary>
    public bool IsLive => !IsSimulated && OrderListId > 0;

    public override string ToString() =>
        $"{Symbol} OCO#{OrderListId} qty={Quantity} SL={StopTriggerPrice}/{StopLimitPrice} " +
        $"TP={TakeProfitTriggerPrice}/{TakeProfitLimitPrice}{(IsSimulated ? " (simulated)" : string.Empty)}";
}
