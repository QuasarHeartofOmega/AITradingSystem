using AITradingSystem.Common.Configuration;

namespace AITradingSystem.Services.AI;

/// <summary>
/// Контекст запроса к модели: что уже открыто по символу и какие ограничения действуют.
/// Без этого модель предлагала SELL по споту (шорт невозможен) и не могла закрыть позицию.
/// </summary>
public sealed record AiRequestContext
{
    /// <summary>Есть открытая позиция по символу.</summary>
    public AiRole Role { get; init; } = AiRole.VetoOnly;
    public SymbolUniverseClass SymbolClass { get; init; } = SymbolUniverseClass.Volatile;
    public string? SymbolExclusionReason { get; init; }
    public bool HasSeparateReversalSetup { get; init; }
    public bool HasPosition { get; init; }

    /// <summary>Сторона открытой позиции ("LONG"/"SHORT").</summary>
    public string? PositionSide { get; init; }

    /// <summary>Цена входа открытой позиции.</summary>
    public decimal? EntryPrice { get; init; }

    /// <summary>Объём открытой позиции.</summary>
    public decimal? Quantity { get; init; }

    /// <summary>Текущий нереализованный PnL, %.</summary>
    public decimal? UnrealizedPnlPercent { get; init; }

    /// <summary>Текущий Stop-Loss.</summary>
    public decimal? StopLoss { get; init; }

    /// <summary>Текущий Take-Profit.</summary>
    public decimal? TakeProfit { get; init; }

    /// <summary>Сколько минут позиция открыта.</summary>
    public double? MinutesInPosition { get; init; }

    /// <summary>Разрешён ли шорт (на споте Binance — нет).</summary>
    public bool AllowShort { get; init; }

    /// <summary>Минимальное соотношение риск/прибыль.</summary>
    public decimal MinRiskReward { get; init; } = 2m;

    /// <summary>Минимальная уверенность для исполнения сигнала.</summary>
    public decimal MinConfidence { get; init; } = 0.55m;

    /// <summary>Множитель ATR для Stop-Loss (для подсказки модели).</summary>
    public decimal StopLossAtrMultiplier { get; init; } = 2m;

    // ------------------------------------------------------------------
    // B8/B11. Детерминированный pre-score движка и точность цен символа.
    // ------------------------------------------------------------------

    /// <summary>Направление/сила сетапа по математике движка, [-1; 1].</summary>
    public decimal? EngineScore { get; init; }

    /// <summary>LONG | SHORT | FLAT — направление pre-score.</summary>
    public string? EngineDirection { get; init; }

    /// <summary>Prior-уверенность движка [0.1; 0.9]. P2.2: только справка для прозрачности,
    /// не стартовая точка рубрики модели; движок сам смешает её с уверенностью модели.</summary>
    public decimal? EnginePriorConfidence { get; init; }

    /// <summary>Причины pre-score (ADX, объём, выравнивание EMA и т.д.).</summary>
    public IReadOnlyList<string>? EngineReasons { get; init; }

    /// <summary>P2.2: нейтральные факты pre-score для промпта (без штрафов/бонусов).</summary>
    public IReadOnlyList<string>? EngineFacts { get; init; }

    /// <summary>Знаков после запятой в ценах символа (из tickSize, D24). BTTC ≠ BTC.</summary>
    public int PriceDecimals { get; init; } = 8;

    /// <summary>Шаг цены символа (tickSize), если известен.</summary>
    public decimal? TickSize { get; init; }

    /// <summary>
    /// P0.5: принудительно отключить thinking для этого запроса (VetoOnly/Shadow).
    /// Не включает thinking, если он выключен в конфиге.
    /// </summary>
    public bool DisableThinking { get; init; }
}
