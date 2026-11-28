using AITradingSystem.Core.Models.Enums;

namespace AITradingSystem.Core.Models.Trading;

/// <summary>Phase 1.7: источник lifecycle позиции.</summary>
public enum PositionSource
{
    Bot,
    External,
    /// <summary>Символ совпал с позицией бота; объёмы объединены в один lifecycle с раздельным учётом долей.</summary>
    Mixed
}

/// <summary>
/// Phase 1.7: кандидат на подхват ручного/external остатка по символу реестра.
/// Не является позицией бота, пока оператор не подтвердил (или режим Auto).
/// </summary>
public sealed class ExternalPositionCandidate
{
    public string Symbol { get; init; } = string.Empty;
    public string BaseAsset { get; init; } = string.Empty;
    public decimal ExchangeTotalQuantity { get; init; }
    public decimal ExchangeFreeQuantity { get; init; }
    public decimal ExchangeLockedQuantity { get; init; }
    public decimal BotQuantity { get; init; }
    /// <summary>Объём, не принадлежащий боту и ещё не отпущенный оператору (Release).</summary>
    public decimal ExternalQuantity { get; init; }
    public decimal MarkPrice { get; init; }
    public decimal ExternalNotionalQuote => ExternalQuantity * MarkPrice;
    public decimal ProposedEntryPrice { get; init; }
    public decimal ProposedStopLoss { get; init; }
    public decimal ProposedTakeProfit { get; init; }
    /// <summary>mytrades-vwap | mark-price-estimate | manual.</summary>
    public string EntryBasis { get; init; } = "mark-price-estimate";
    public bool EntryIsEstimated => EntryBasis == "mark-price-estimate";
    public string EntryEvidence { get; init; } = string.Empty;
    public DateTime? FirstExternalTradeUtc { get; init; }
    public bool HasExternalOpenOrders { get; init; }
    public bool IsReleased { get; init; }
    public bool IsEligible { get; init; }
    public string Status { get; init; } = string.Empty;
    public DateTime DetectedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>Phase 1.7: durable-журнал операторских решений по external-остаткам.</summary>
public sealed class ExternalPositionLedgerState
{
    public Dictionary<string, ExternalReleaseRecord> Released { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Phase 1.7: биржевые orderId входов бота по символу (переживают закрытие позиции).
    /// Реконструкция external entry исключает их из myTrades, иначе VWAP ручного остатка
    /// загрязнялся бы собственными сделками бота за lookback-окно.</summary>
    public Dictionary<string, HashSet<long>> BotOrderIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ExternalReleaseRecord
{
    public string Symbol { get; set; } = string.Empty;
    /// <summary>Объём, который оператор забрал себе на момент Release; новые покупки выше него снова кандидаты.</summary>
    public decimal ReleasedQuantity { get; set; }
    public DateTime ReleasedAtUtc { get; set; } = DateTime.UtcNow;
    public string Reason { get; set; } = string.Empty;
}
