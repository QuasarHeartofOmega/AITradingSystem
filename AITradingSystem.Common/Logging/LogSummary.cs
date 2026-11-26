using System.Globalization;
using System.Text.Json;

namespace AITradingSystem.Common.Logging;

/// <summary>
/// Компактные сводки вместо сырых тел ответов биржи.
///
/// До этого в exchange-лог падали полные тела /api/v3/account (сотни активов с нулями)
/// и /api/v3/ticker/price (все символы биржи) на каждый опрос — 37 МБ из 45 МБ за час.
/// Сводка сохраняет всё, что нужно для разбора («сколько активов с балансом, сколько
/// свечей, какая последняя цена, сколько ордеров»), и стоит одну строку.
/// </summary>
public static class LogSummary
{
    /// <summary>Путь эндпоинта без хоста и без служебных параметров подписи.</summary>
    public static string ShortUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return string.Empty;

        var path = url;
        var schemeEnd = path.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            var hostEnd = path.IndexOf('/', schemeEnd + 3);
            path = hostEnd >= 0 ? path[hostEnd..] : "/";
        }

        var queryStart = path.IndexOf('?');
        if (queryStart < 0) return LogOptions.Mask(path);

        var endpoint = path[..queryStart];
        var kept = new List<string>();
        foreach (var pair in path[(queryStart + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = pair.Split('=')[0];
            if (name is "timestamp" or "recvWindow" or "signature") continue;
            kept.Add(pair);
        }

        var query = kept.Count == 0 ? string.Empty : "?" + string.Join("&", kept);
        return LogOptions.Mask(endpoint + query);
    }

    /// <summary>
    /// Однострочная выжимка тела ответа. Полный текст пишется только в режиме
    /// <see cref="LogOptions.HttpBodyMode"/> = Full.
    /// </summary>
    public static string Of(string url, string? body)
    {
        if (string.IsNullOrEmpty(body)) return "body=empty";

        var endpoint = EndpointOf(url);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            switch (endpoint)
            {
                case "/api/v3/account":
                case "/sapi/v1/capital/config/getall":
                    return Account(root);
                case "/api/v3/ticker/price":
                case "/api/v3/ticker/bookTicker":
                    return Tickers(root);
                case "/api/v3/klines":
                case "/api/v3/uiKlines":
                    return Klines(root);
                case "/api/v3/exchangeInfo":
                    return root.TryGetProperty("symbols", out var symbols) && symbols.ValueKind == JsonValueKind.Array
                        ? $"symbols={symbols.GetArrayLength()}"
                        : Bytes(body);
                case "/api/v3/time":
                    return root.TryGetProperty("serverTime", out var st)
                        ? $"server_time={st.GetRawText()}"
                        : Bytes(body);
                case "/api/v3/userDataStream":
                    return root.TryGetProperty("listenKey", out _) ? "listenKey=***" : Bytes(body);
            }

            if (root.ValueKind == JsonValueKind.Array)
                return $"items={root.GetArrayLength()} {Bytes(body)}";

            if (root.ValueKind == JsonValueKind.Object)
                return Object(root, body);
        }
        catch (JsonException)
        {
            // Не JSON (HTML-страница ошибки, обрыв тела) — важны размер и начало.
        }

        return Bytes(body) + " head=" + Head(body);
    }

    private static string EndpointOf(string url)
    {
        var path = ShortUrl(url);
        var queryStart = path.IndexOf('?');
        return queryStart >= 0 ? path[..queryStart] : path;
    }

    private static string Account(JsonElement root)
    {
        if (!root.TryGetProperty("balances", out var balances) || balances.ValueKind != JsonValueKind.Array)
            return Object(root, root.GetRawText());

        var total = 0;
        var nonZero = new List<string>();
        foreach (var item in balances.EnumerateArray())
        {
            total++;
            var free = Decimal(item, "free");
            var locked = Decimal(item, "locked");
            if (free == 0m && locked == 0m) continue;

            var asset = item.TryGetProperty("asset", out var a) ? a.GetString() : "?";
            nonZero.Add(locked > 0m
                ? $"{asset}:{Num(free)}+{Num(locked)}locked"
                : $"{asset}:{Num(free)}");
        }

        const int shown = 8;
        var head = string.Join(",", nonZero.Take(shown));
        if (nonZero.Count > shown) head += $",…(+{nonZero.Count - shown})";

        return $"assets={total} nonzero={nonZero.Count}" + (head.Length > 0 ? $" balances={head}" : string.Empty);
    }

    private static string Tickers(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
            return $"symbols={root.GetArrayLength()}";

        var symbol = root.TryGetProperty("symbol", out var s) ? s.GetString() : "?";
        var price = root.TryGetProperty("price", out var p) ? p.GetString() : null;
        return price is null ? $"symbol={symbol}" : $"symbol={symbol} price={price}";
    }

    private static string Klines(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) return Bytes(root.GetRawText());

        var count = root.GetArrayLength();
        if (count == 0) return "candles=0";

        var last = root[count - 1];
        if (last.ValueKind != JsonValueKind.Array || last.GetArrayLength() < 7)
            return $"candles={count}";

        var closeTime = last[6].GetInt64();
        var close = last[4].GetString();
        var closed = DateTimeOffset.FromUnixTimeMilliseconds(closeTime) <= DateTimeOffset.UtcNow;
        return $"candles={count} last_close={close} last_close_time={closeTime} closed={closed.ToString().ToLowerInvariant()}";
    }

    private static string Object(JsonElement root, string body)
    {
        var parts = new List<string>();
        foreach (var key in new[] { "orderId", "orderListId", "clientOrderId", "status", "executedQty", "price", "symbol", "transactTime" })
        {
            if (!root.TryGetProperty(key, out var value)) continue;
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
            if (string.IsNullOrEmpty(text)) continue;
            parts.Add($"{Snake(key)}={text}");
        }

        return parts.Count > 0 ? string.Join(" ", parts) : Bytes(body);
    }

    private static string Snake(string camel) =>
        string.Concat(camel.Select((c, i) => char.IsUpper(c) && i > 0 ? "_" + char.ToLowerInvariant(c) : c.ToString()));

    private static decimal Decimal(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) &&
        decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0m;

    private static string Num(decimal value) => value.ToString("0.########", CultureInfo.InvariantCulture);

    private static string Bytes(string body) => $"bytes={body.Length}";

    private static string Head(string body)
    {
        var flat = body.Replace("\r", " ").Replace("\n", " ");
        return LogOptions.Mask(flat.Length <= 160 ? flat : flat[..160] + "…");
    }
}
