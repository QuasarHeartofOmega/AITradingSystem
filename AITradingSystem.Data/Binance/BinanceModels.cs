using System.Text.Json.Serialization;

namespace AITradingSystem.Data.Binance;

/// <summary>
/// DTO для WebSocket stream Binance.
/// Базовый класс для всех потоковых сообщений.
/// </summary>
public class BinanceStreamMessage
{
    [JsonPropertyName("e")]
    public string EventType { get; set; } = string.Empty;

    [JsonPropertyName("E")]
    public long EventTime { get; set; }

    [JsonPropertyName("s")]
    public string Symbol { get; set; } = string.Empty;
}

/// <summary>
/// Trade stream (агрегированные сделки).
/// </summary>
public sealed class BinanceTradeStream : BinanceStreamMessage
{
    [JsonPropertyName("t")]
    public long TradeId { get; set; }

    [JsonPropertyName("p")]
    public string Price { get; set; } = string.Empty;

    [JsonPropertyName("q")]
    public string Quantity { get; set; } = string.Empty;

    [JsonPropertyName("T")]
    public long TradeTime { get; set; }

    [JsonPropertyName("m")]
    public bool IsBuyerMaker { get; set; }
}

/// <summary>
/// Kline/Candlestick stream.
/// </summary>
public sealed class BinanceKlineStream : BinanceStreamMessage
{
    [JsonPropertyName("k")]
    public BinanceKlineData Kline { get; set; } = new();
}

public sealed class BinanceKlineData
{
    [JsonPropertyName("t")]
    public long OpenTime { get; set; }

    [JsonPropertyName("T")]
    public long CloseTime { get; set; }

    [JsonPropertyName("s")]
    public string Symbol { get; set; } = string.Empty;

    [JsonPropertyName("i")]
    public string Interval { get; set; } = string.Empty;

    [JsonPropertyName("o")]
    public string Open { get; set; } = string.Empty;

    [JsonPropertyName("c")]
    public string Close { get; set; } = string.Empty;

    [JsonPropertyName("h")]
    public string High { get; set; } = string.Empty;

    [JsonPropertyName("l")]
    public string Low { get; set; } = string.Empty;

    [JsonPropertyName("v")]
    public string Volume { get; set; } = string.Empty;

    [JsonPropertyName("q")]
    public string QuoteVolume { get; set; } = string.Empty;

    [JsonPropertyName("n")]
    public int TradesCount { get; set; }

    [JsonPropertyName("x")]
    public bool IsClosed { get; set; }
}

/// <summary>
/// Partial book depth stream (@depth20) — готовый снимок топ-20 уровней стакана.
/// Символ в payload отсутствует — берется из имени стрима.
/// </summary>
public sealed class BinancePartialDepthStream
{
    [JsonPropertyName("lastUpdateId")]
    public long LastUpdateId { get; set; }

    [JsonPropertyName("bids")]
    public List<List<string>> Bids { get; set; } = new();

    [JsonPropertyName("asks")]
    public List<List<string>> Asks { get; set; } = new();
}

/// <summary>
/// REST API: Order response.
/// </summary>
public sealed class BinanceOrderResponse
{
    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = string.Empty;

    [JsonPropertyName("orderId")]
    public long OrderId { get; set; }

    [JsonPropertyName("clientOrderId")]
    public string ClientOrderId { get; set; } = string.Empty;

    [JsonPropertyName("transactTime")]
    public long TransactTime { get; set; }

    [JsonPropertyName("updateTime")]
    public long UpdateTime { get; set; }

    [JsonPropertyName("price")]
    public string Price { get; set; } = string.Empty;

    [JsonPropertyName("origQty")]
    public string OrigQty { get; set; } = string.Empty;

    [JsonPropertyName("executedQty")]
    public string ExecutedQty { get; set; } = string.Empty;

    [JsonPropertyName("cummulativeQuoteQty")]
    public string CummulativeQuoteQty { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("timeInForce")]
    public string TimeInForce { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("side")]
    public string Side { get; set; } = string.Empty;

    /// <summary>Триггер стоп/тейк-ноги (STOP_LOSS_LIMIT / TAKE_PROFIT_LIMIT).</summary>
    [JsonPropertyName("stopPrice")]
    public string StopPrice { get; set; } = string.Empty;

    [JsonPropertyName("fills")]
    public List<BinanceFill> Fills { get; set; } = new();
}

/// <summary>
/// Ответ POST /api/v3/orderList/oco и GET /api/v3/orderList (P0.1).
/// orderReports приходит только при постановке (newOrderRespType=FULL);
/// в списках открытых связок есть лишь orders (id + clientOrderId, без типов ног).
/// </summary>
public sealed class BinanceOrderListResponse
{
    [JsonPropertyName("orderListId")]
    public long OrderListId { get; set; }

    [JsonPropertyName("contingencyType")]
    public string ContingencyType { get; set; } = string.Empty;

    [JsonPropertyName("listStatusType")]
    public string ListStatusType { get; set; } = string.Empty;

    [JsonPropertyName("listOrderStatus")]
    public string ListOrderStatus { get; set; } = string.Empty;

    [JsonPropertyName("listClientOrderId")]
    public string ListClientOrderId { get; set; } = string.Empty;

    [JsonPropertyName("transactionTime")]
    public long TransactionTime { get; set; }

    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = string.Empty;

    [JsonPropertyName("orders")]
    public List<BinanceOrderListRef> Orders { get; set; } = new();

    [JsonPropertyName("orderReports")]
    public List<BinanceOrderResponse> OrderReports { get; set; } = new();
}

/// <summary>Ссылка на ногу связки в ответе orderList.</summary>
public sealed class BinanceOrderListRef
{
    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = string.Empty;

    [JsonPropertyName("orderId")]
    public long OrderId { get; set; }

    [JsonPropertyName("clientOrderId")]
    public string ClientOrderId { get; set; } = string.Empty;
}

public sealed class BinanceFill
{
    [JsonPropertyName("price")]
    public string Price { get; set; } = string.Empty;

    [JsonPropertyName("qty")]
    public string Qty { get; set; } = string.Empty;

    [JsonPropertyName("commission")]
    public string Commission { get; set; } = string.Empty;

    [JsonPropertyName("commissionAsset")]
    public string CommissionAsset { get; set; } = string.Empty;
}

/// <summary>
/// REST API: Account information.
/// </summary>
public sealed class BinanceMyTrade
{
    [JsonPropertyName("commission")]
    public string Commission { get; set; } = "0";

    [JsonPropertyName("commissionAsset")]
    public string CommissionAsset { get; set; } = string.Empty;
}

public sealed class BinanceTickerPrice
{
    [JsonPropertyName("symbol")] public string Symbol { get; set; } = string.Empty;
    [JsonPropertyName("price")] public string Price { get; set; } = string.Empty;
}

public sealed class BinanceAccountInfo
{
    [JsonPropertyName("balances")]
    public List<BinanceBalance> Balances { get; set; } = new();

    [JsonPropertyName("updateTime")]
    public long UpdateTime { get; set; }
}

public sealed class BinanceBalance
{
    [JsonPropertyName("asset")]
    public string Asset { get; set; } = string.Empty;

    [JsonPropertyName("free")]
    public string Free { get; set; } = string.Empty;

    [JsonPropertyName("locked")]
    public string Locked { get; set; } = string.Empty;
}

/// <summary>
/// REST API: Exchange info (symbol filters).
/// </summary>
public sealed class BinanceExchangeInfo
{
    [JsonPropertyName("symbols")]
    public List<BinanceSymbolInfo> Symbols { get; set; } = new();
}

public sealed class BinanceSymbolInfo
{
    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("baseAsset")]
    public string BaseAsset { get; set; } = string.Empty;

    [JsonPropertyName("quoteAsset")]
    public string QuoteAsset { get; set; } = string.Empty;

    [JsonPropertyName("filters")]
    public List<BinanceFilter> Filters { get; set; } = new();

    public decimal StepSize => GetFilterValue("LOT_SIZE", "stepSize");
    public decimal MinQty => GetFilterValue("LOT_SIZE", "minQty");
    public decimal MaxQty => GetFilterValue("LOT_SIZE", "maxQty");
    public decimal MarketStepSize => GetFilterValue("MARKET_LOT_SIZE", "stepSize");
    public decimal MarketMinQty => GetFilterValue("MARKET_LOT_SIZE", "minQty");
    public decimal TickSize => GetFilterValue("PRICE_FILTER", "tickSize");

    /// <summary>
    /// Минимальная сумма ордера. Binance использует фильтр NOTIONAL,
    /// на старых/тестовых площадках — MIN_NOTIONAL. Пробуем оба.
    /// </summary>
    public decimal MinNotional
    {
        get
        {
            var notional = GetFilterValue("NOTIONAL", "minNotional");
            return notional > 0 ? notional : GetFilterValue("MIN_NOTIONAL", "minNotional");
        }
    }

    /// <summary>NOTIONAL.applyMinToMarket / MIN_NOTIONAL.applyToMarket.</summary>
    public bool ApplyMinNotionalToMarket
    {
        get
        {
            var filter = Filters.FirstOrDefault(f =>
                string.Equals(f.FilterType, "NOTIONAL", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(f.FilterType, "MIN_NOTIONAL", StringComparison.OrdinalIgnoreCase));
            return filter?.ApplyMinToMarket ?? filter?.ApplyToMarket ?? true;
        }
    }

    /// <summary>Разрешена ли спотовая торговля по символу.</summary>
    [JsonPropertyName("isSpotTradingAllowed")]
    public bool IsSpotTradingAllowed { get; set; } = true;

    private decimal GetFilterValue(string filterType, string fieldName)
    {
        var filter = Filters.FirstOrDefault(f =>
            string.Equals(f.FilterType, filterType, StringComparison.OrdinalIgnoreCase));
        if (filter is null) return 0;

        return fieldName switch
        {
            "stepSize" => filter.StepSize,
            "minQty" => filter.MinQty,
            "maxQty" => filter.MaxQty,
            "tickSize" => filter.TickSize,
            "minNotional" => filter.MinNotional,
            _ => 0
        };
    }
}

/// <summary>
/// Фильтры Binance приходят СТРОКАМИ ("0.00001000"), а не числами.
/// Без AllowReadingFromString десериализация decimal падает с JsonException,
/// из-за чего округление до stepSize/tickSize не работало и ордера отклонялись биржей.
/// </summary>
[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
public sealed class BinanceFilter
{
    [JsonPropertyName("filterType")]
    public string FilterType { get; set; } = string.Empty;

    [JsonPropertyName("stepSize")]
    public decimal StepSize { get; set; }

    [JsonPropertyName("minQty")]
    public decimal MinQty { get; set; }

    [JsonPropertyName("maxQty")]
    public decimal MaxQty { get; set; }

    [JsonPropertyName("tickSize")]
    public decimal TickSize { get; set; }

    [JsonPropertyName("minNotional")]
    public decimal MinNotional { get; set; }

    [JsonPropertyName("applyToMarket")]
    public bool ApplyToMarket { get; set; }

    [JsonPropertyName("applyMinToMarket")]
    public bool ApplyMinToMarket { get; set; }
}

/// <summary>Ответ сервера на /api/v3/time (синхронизация часов, ошибка -1021).</summary>
public sealed class BinanceServerTime
{
    [JsonPropertyName("serverTime")]
    public long ServerTime { get; set; }
}

/// <summary>Стандартный объект ошибки Binance: {"code":-1013,"msg":"..."}.</summary>
public sealed class BinanceApiError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("msg")]
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// REST API: Historical klines.
/// </summary>
public sealed class BinanceKline
{
    public long OpenTime { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public decimal Volume { get; set; }
    public long CloseTime { get; set; }
    public decimal QuoteVolume { get; set; }
    public int TradesCount { get; set; }
}
/// <summary>P0.5: ответ POST /api/v3/userDataStream.</summary>
public sealed class BinanceListenKey
{
    [JsonPropertyName("listenKey")]
    public string ListenKey { get; set; } = string.Empty;
}

/// <summary>
/// P0.5: payload executionReport из user-data stream (одиночный /ws/&lt;listenKey&gt;).
/// Имена полей — как в документации Binance user data stream.
/// </summary>
public sealed class BinanceExecutionReportStream
{
    [JsonPropertyName("e")] public string EventType { get; set; } = string.Empty;
    [JsonPropertyName("s")] public string Symbol { get; set; } = string.Empty;
    [JsonPropertyName("S")] public string Side { get; set; } = string.Empty;
    [JsonPropertyName("i")] public long OrderId { get; set; }
    [JsonPropertyName("c")] public string ClientOrderId { get; set; } = string.Empty;
    [JsonPropertyName("g")] public long OrderListId { get; set; } = -1;
    [JsonPropertyName("x")] public string ExecutionType { get; set; } = string.Empty;
    [JsonPropertyName("X")] public string OrderStatus { get; set; } = string.Empty;
    [JsonPropertyName("l")] public string LastQuantity { get; set; } = "0";
    [JsonPropertyName("L")] public string LastPrice { get; set; } = "0";
    [JsonPropertyName("z")] public string CumulativeQuantity { get; set; } = "0";
    [JsonPropertyName("Z")] public string CumulativeQuoteQuantity { get; set; } = "0";
    [JsonPropertyName("n")] public string Commission { get; set; } = "0";
    [JsonPropertyName("N")] public string CommissionAsset { get; set; } = string.Empty;
    [JsonPropertyName("T")] public long TransactionTime { get; set; }
    [JsonPropertyName("r")] public string RejectReason { get; set; } = string.Empty;

    /// <summary>Маппинг в доменный факт исполнения. Отдельный метод — чтобы покрыть тестом без сокета.</summary>
    public Core.Models.Trading.ExecutionReport ToExecutionReport() => new()
    {
        Symbol = Symbol.ToUpperInvariant(),
        Side = string.Equals(Side, "BUY", StringComparison.OrdinalIgnoreCase)
            ? Core.Models.Enums.OrderSide.Buy
            : Core.Models.Enums.OrderSide.Sell,
        OrderId = OrderId,
        ClientOrderId = ClientOrderId,
        OrderListId = OrderListId,
        ExecutionType = ExecutionType,
        OrderStatus = OrderStatus,
        LastQuantity = ParseDecimal(LastQuantity),
        LastPrice = ParseDecimal(LastPrice),
        CumulativeQuantity = ParseDecimal(CumulativeQuantity),
        CumulativeQuoteQuantity = ParseDecimal(CumulativeQuoteQuantity),
        Commission = ParseDecimal(Commission),
        CommissionAsset = CommissionAsset,
        TransactionTimeUtc = DateTimeOffset.FromUnixTimeMilliseconds(TransactionTime).UtcDateTime,
        RejectReason = RejectReason
    };

    private static decimal ParseDecimal(string? text) =>
        decimal.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0m;
}
