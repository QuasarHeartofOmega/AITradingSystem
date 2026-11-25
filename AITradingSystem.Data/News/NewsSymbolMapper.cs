using System.Globalization;
using System.Text.RegularExpressions;
using AITradingSystem.Core.Models.Enums;

namespace AITradingSystem.Data.News;

/// <summary>
/// Сопоставление новостей с торговыми активами и эвристическая оценка тональности.
/// Нужно для агрегаторов (cryptocurrency.cv), которые не тегируют новости тикерами.
/// </summary>
public static class NewsSymbolMapper
{
    /// <summary>Название/синонимы → тикер базового актива.</summary>
    private static readonly Dictionary<string, string> NameToTicker = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bitcoin"] = "BTC", ["btc"] = "BTC", ["satoshi"] = "BTC",
        ["ethereum"] = "ETH", ["eth"] = "ETH", ["ether"] = "ETH", ["vitalik"] = "ETH",
        ["solana"] = "SOL", ["sol"] = "SOL",
        ["ripple"] = "XRP", ["xrp"] = "XRP",
        ["cardano"] = "ADA", ["ada"] = "ADA",
        ["dogecoin"] = "DOGE", ["doge"] = "DOGE",
        ["binance coin"] = "BNB", ["bnb"] = "BNB", ["binance"] = "BNB",
        ["polkadot"] = "DOT", ["dot"] = "DOT",
        ["polygon"] = "POL", ["matic"] = "POL",
        ["avalanche"] = "AVAX", ["avax"] = "AVAX",
        ["chainlink"] = "LINK", ["link"] = "LINK",
        ["litecoin"] = "LTC", ["ltc"] = "LTC",
        ["tron"] = "TRX", ["trx"] = "TRX",
        ["toncoin"] = "TON", ["ton"] = "TON",
        ["shiba"] = "SHIB", ["shib"] = "SHIB",
        ["stellar"] = "XLM", ["xlm"] = "XLM",
        ["cosmos"] = "ATOM", ["atom"] = "ATOM",
        ["near protocol"] = "NEAR", ["near"] = "NEAR",
        ["aptos"] = "APT", ["apt"] = "APT",
        ["sui"] = "SUI",
        ["arbitrum"] = "ARB", ["arb"] = "ARB",
        ["optimism"] = "OP",
        ["uniswap"] = "UNI", ["uni"] = "UNI",
        ["aave"] = "AAVE",
        ["pepe"] = "PEPE",
        ["monero"] = "XMR", ["xmr"] = "XMR",
        ["bitcoin cash"] = "BCH", ["bch"] = "BCH",
        ["internet computer"] = "ICP", ["icp"] = "ICP",
        ["filecoin"] = "FIL", ["fil"] = "FIL",
        ["hedera"] = "HBAR", ["hbar"] = "HBAR",
        ["injective"] = "INJ", ["inj"] = "INJ",
        ["render"] = "RENDER",
        ["worldcoin"] = "WLD", ["wld"] = "WLD",
        // D25: активы расширенного списка symbols.json (BTTC/DASH/FLR/ENJ/STRAX) и стейблы.
        ["bittorrent"] = "BTTC", ["btt"] = "BTTC", ["bttc"] = "BTTC",
        ["dash"] = "DASH",
        ["flare"] = "FLR", ["flr"] = "FLR", ["flare networks"] = "FLR",
        ["enjin"] = "ENJ", ["enj"] = "ENJ",
        ["stratis"] = "STRAX", ["strax"] = "STRAX",
        ["tether"] = "USDT", ["usdt"] = "USDT",
        ["usd coin"] = "USDC", ["usdc"] = "USDC",
        ["first digital usd"] = "FDUSD", ["fdusd"] = "FDUSD"
    };

    /// <summary>Тикеры, которые нельзя искать как обычное слово (слишком частые в тексте).</summary>
    private static readonly HashSet<string> AmbiguousTickers = new(StringComparer.OrdinalIgnoreCase)
    {
        "LINK", "TON", "NEAR", "OP", "UNI", "SUI", "APT", "ARB", "DOT", "ATOM", "FIL", "RENDER", "ONE", "GAS", "ID",
        // D25: «dash» и «flare» — обычные английские слова, ищем только капсом ($DASH / DASH).
        "DASH", "FLR"
    };

    private static readonly string[] BullishWords =
    {
        "surge", "surges", "rally", "rallies", "soar", "soars", "jump", "jumps", "gain", "gains",
        "bullish", "breakout", "record high", "all-time high", "ath", "adoption", "approval",
        "approves", "etf inflow", "inflow", "upgrade", "partnership", "integration", "launch",
        "launches", "buy", "buys", "accumulation", "institutional", "listing", "lists", "upgrade",
        "recovery", "rebound", "green", "up ", "rise", "rises", "growth", "halving"
    };

    private static readonly string[] BearishWords =
    {
        "crash", "crashes", "plunge", "plunges", "drop", "drops", "fall", "falls", "selloff",
        "sell-off", "bearish", "hack", "hacked", "exploit", "exploited", "lawsuit", "sues", "sued",
        "sec charges", "ban", "bans", "banned", "fraud", "scam", "rug pull", "liquidation",
        "liquidations", "outflow", "delisting", "delists", "bankruptcy", "insolvency", "fear",
        "dump", "dumps", "decline", "declines", "down ", "slump", "warning", "investigation"
    };

    /// <summary>
    /// Достаёт тикеры базовых активов из заголовка/текста новости.
    /// Порядок: $TICKER → названия/синонимы → отдельные тикеры капсом.
    /// </summary>
    public static List<string> ExtractSymbols(string? title, string? text, string? category = null)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var haystack = $"{title} {text} {category}";
        if (string.IsNullOrWhiteSpace(haystack))
            return result.ToList();

        foreach (Match m in Regex.Matches(haystack, @"\$([A-Za-z]{2,10})\b"))
        {
            var ticker = m.Groups[1].Value.ToUpperInvariant();
            result.Add(NameToTicker.TryGetValue(ticker, out var mapped) ? mapped : ticker);
        }

        foreach (var (name, ticker) in NameToTicker)
        {
            // Короткие «слова-омонимы» (link, ton, near, dot, op, sui, ...) ищем только
            // в верхнем регистре: иначе "click the link" превращается в тикер LINK,
            // а "near the level" — в NEAR, и модель получает мусорный новостной контекст.
            var ambiguous = AmbiguousTickers.Contains(ticker) &&
                            name.Length <= 6 &&
                            !name.Contains(' ');

            var pattern = ambiguous
                ? $@"\b{Regex.Escape(name.ToUpperInvariant())}\b"
                : $@"\b{Regex.Escape(name)}\b";

            var options = ambiguous ? RegexOptions.None : RegexOptions.IgnoreCase;

            if (Regex.IsMatch(haystack, pattern, options))
                result.Add(ticker);
        }

        foreach (Match m in Regex.Matches(haystack, @"\b([A-Z]{2,6})\b"))
        {
            var candidate = m.Groups[1].Value;
            if (AmbiguousTickers.Contains(candidate)) continue;
            if (NameToTicker.TryGetValue(candidate, out var ticker))
                result.Add(ticker);
        }

        return result.ToList();
    }

    /// <summary>
    /// P2.8: относится ли статья к крипте (для GENERAL-фона): ключевые слова в заголовке/тексте.
    /// Общие деловые ленты (BBC Business, ZeroHedge, Seeking Alpha) без крипто-слов — не фон.
    /// </summary>
    public static bool IsCryptoRelated(string? title, string? text)
    {
        var haystack = $"{title} {text}";
        if (string.IsNullOrWhiteSpace(haystack))
            return false;
        foreach (var kw in CryptoKeywords)
            if (haystack.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static readonly string[] CryptoKeywords =
    {
        "bitcoin", "btc", "crypto", "ethereum", "altcoin", "stablecoin", "blockchain",
        "defi", "binance", "coinbase", "kraken", "bybit", "okx", "solana", "dogecoin",
        "ripple", "xrp", "tether", "usdt", "usdc", "memecoin", "satoshi", "halving", "web3"
    };

    /// <summary>
    /// Эвристическая тональность [-1; 1] по ключевым словам.
    /// credibility (0..1) используется как вес достоверности источника.
    /// </summary>
    public static (Sentiment Sentiment, decimal Score) ScoreSentiment(string? title, string? text, double credibility = 0.5)
    {
        var haystack = $"{title} {text}".ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(haystack))
            return (Sentiment.Neutral, 0m);

        var bull = BullishWords.Count(w => haystack.Contains(w, StringComparison.Ordinal));
        var bear = BearishWords.Count(w => haystack.Contains(w, StringComparison.Ordinal));
        var total = bull + bear;

        if (total == 0)
            return (Sentiment.Neutral, 0m);

        var raw = (decimal)(bull - bear) / total;
        var weight = (decimal)Math.Clamp(credibility <= 0 ? 0.5 : credibility, 0.1, 1.0);
        var score = Math.Clamp(raw * weight, -1m, 1m);

        var sentiment = score switch
        {
            > 0.15m => Sentiment.Bullish,
            < -0.15m => Sentiment.Bearish,
            _ => Sentiment.Neutral
        };

        return (sentiment, score);
    }

    /// <summary>Разбор даты публикации в UTC (ISO 8601 / RFC1123).</summary>
    public static DateTime ParsePublishedAt(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return DateTime.UtcNow;

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed;

        return DateTime.UtcNow;
    }
}
