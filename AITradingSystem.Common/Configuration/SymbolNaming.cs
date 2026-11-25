namespace AITradingSystem.Common.Configuration;

/// <summary>Общие правила BASE+QUOTE для конфигурации и реестра символов.</summary>
public static class SymbolNaming
{
    public static readonly string[] DefaultQuotes =
    {
        "USDT", "FDUSD", "USDC", "TUSD", "USDP", "DAI", "BUSD", "USD",
        "BTC", "ETH", "BNB", "EUR", "TRY", "GBP", "BRL", "AUD", "JPY", "RUB", "UAH"
    };

    public static List<string> NormalizeAssets(IEnumerable<string>? assets, IEnumerable<string>? fallback = null) =>
        (assets ?? fallback ?? DefaultQuotes)
        .Where(a => !string.IsNullOrWhiteSpace(a))
        .Select(a => a.Trim().ToUpperInvariant())
        .Where(a => a.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9'))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(a => a.Length).ThenBy(a => a).ToList();

    /// <summary>Longest-match: FDUSDUSDT → FDUSD + USDT, а не FDUSDUSD + T.</summary>
    public static bool TrySplit(string? symbol, IEnumerable<string>? quotes, out string baseAsset, out string quoteAsset)
    {
        baseAsset = quoteAsset = string.Empty;
        var value = symbol?.Trim().ToUpperInvariant() ?? string.Empty;
        if (value.Length < 5 || !value.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9')) return false;
        foreach (var quote in NormalizeAssets(quotes))
        {
            if (value.Length > quote.Length && value.EndsWith(quote, StringComparison.Ordinal))
            {
                baseAsset = value[..^quote.Length];
                quoteAsset = quote;
                return true;
            }
        }
        return false;
    }

    public static string? NormalizeSymbol(string? symbol, IEnumerable<string>? quotes) =>
        TrySplit(symbol, quotes, out var b, out var q) ? b + q : null;
}
