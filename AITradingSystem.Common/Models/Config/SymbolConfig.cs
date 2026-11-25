using System.Text.Json.Serialization;
using AITradingSystem.Common.Configuration;
namespace AITradingSystem.Core.Models.Config;

public sealed class SymbolConfig
{
    public string Symbol { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; }
    public decimal MaxPositionPercent { get; set; } = 5m;
    public string Notes { get; set; } = string.Empty;
    /// <summary>Необязательная точность из конфигурации; exchangeInfo.tickSize имеет приоритет.</summary>
    public int? PriceDecimals { get; set; }

    /// <summary>P1.5: точечный порог спреда для входа, % (null — глобальный Trading:MaxEntrySpreadPercent).</summary>
    public decimal? MaxSpreadPercent { get; set; }

    /// <summary>P1.5: точечный минимум глубины стороны входа в котировке (null — глобальный Trading:MinEntryDepthQuote).</summary>
    public decimal? MinDepthQuote { get; set; }

    /// <summary>P1.5: точечный минимум среднего объёма свечи в котировке (null — глобальный Trading:MinCandleQuoteVolume).</summary>
    public decimal? MinCandleQuoteVolume { get; set; }

    /// <summary>P1.5: есть ли хотя бы один точечный порог ликвидности.</summary>
    [JsonIgnore]
    public bool HasLiquidityOverrides =>
        MaxSpreadPercent.HasValue || MinDepthQuote.HasValue || MinCandleQuoteVolume.HasValue;

    /// <summary>P1.5: снимок точечных порогов для TradingConfig (null — пороги не заданы).</summary>
    public SymbolLiquidityOverride? ToLiquidityOverride() =>
        HasLiquidityOverrides
            ? new SymbolLiquidityOverride
            {
                MaxSpreadPercent = MaxSpreadPercent,
                MinDepthQuote = MinDepthQuote,
                MinCandleQuoteVolume = MinCandleQuoteVolume
            }
            : null;
    [JsonIgnore]
    public IEnumerable<string>? KnownQuotes { get; set; }
    public string BaseAsset => SymbolNaming.TrySplit(Symbol, KnownQuotes, out var b, out _) ? b : Symbol;
    public string QuoteAsset => SymbolNaming.TrySplit(Symbol, KnownQuotes, out _, out var q) ? q : string.Empty;

    public void Validate()
    {
        var normalized = SymbolNaming.NormalizeSymbol(Symbol, KnownQuotes);
        if (normalized is null)
            throw new InvalidOperationException($"Symbol {Symbol}: ожидается BASEQUOTE с известной котировкой.");
        Symbol = normalized;
        if (MaxPositionPercent <= 0 || MaxPositionPercent > 100)
            throw new InvalidOperationException($"MaxPositionPercent {Symbol} должен быть в диапазоне (0; 100].");
        if (Priority < 0) throw new InvalidOperationException("Priority не может быть отрицательным.");
        if (PriceDecimals is < 0 or > 12) throw new InvalidOperationException($"PriceDecimals {Symbol} должен быть в [0; 12].");
        if (MaxSpreadPercent is <= 0 or > 10)
            throw new InvalidOperationException($"MaxSpreadPercent {Symbol} должен быть в диапазоне (0; 10].");
        if (MinDepthQuote is <= 0)
            throw new InvalidOperationException($"MinDepthQuote {Symbol} должен быть > 0.");
        if (MinCandleQuoteVolume is < 0)
            throw new InvalidOperationException($"MinCandleQuoteVolume {Symbol} должен быть >= 0 (0 — проверка выключена).");
    }
}

/// <summary>
/// P1.5: точечные пороги ликвидности пары из symbols.json.
/// null-поле означает «взять глобальное значение из TradingConfig».
/// </summary>
public sealed class SymbolLiquidityOverride
{
    public decimal? MaxSpreadPercent { get; set; }
    public decimal? MinDepthQuote { get; set; }
    public decimal? MinCandleQuoteVolume { get; set; }
}
