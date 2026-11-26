using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AITradingSystem.Common.Configuration;
using AITradingSystem.Common.Extensions;
using AITradingSystem.Common.Logging;
using AITradingSystem.Common.Security;
using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.MarketData;
using AITradingSystem.Core.Models.Trading;
using Microsoft.Extensions.Logging;

namespace AITradingSystem.Data.Binance;

/// <summary>
/// REST клиент Binance (FR-6.4) с поддержкой Live / Spot Testnet / Demo Mode (demo.binance.com).
///
/// Что важно в парсинге:
///  - klines — массив массивов со смешанными типами (long + string), читаем через JsonElement;
///  - exchangeInfo.filters — числа приходят строками, включён AllowReadingFromString;
///  - ошибки API разбираются как {"code":..,"msg":..}, ретраи только для транзиентных сбоев;
///  - время подписи синхронизируется с /api/v3/time (иначе -1021).
/// Каждый запрос/ответ пишется в logs/exchange-*.log.
/// </summary>
public sealed class BinanceRestClient : IBinanceRestClient, IDisposable
{
    private readonly BinanceConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ILogger<BinanceRestClient> _logger;
    private readonly IRestRateLimitMetrics? _rateLimitMetrics;
    private readonly string? _apiKey;
    private readonly string? _apiSecret;
    private readonly SemaphoreSlim _timeSyncLock = new(1, 1);

    /// <summary>P1.6: бюджет весов запросов и баны 429/-1003/418 (см. RequestWeightBudget).</summary>
    public RequestWeightBudget WeightBudget { get; }

    private long _serverTimeOffsetMs;
    private bool _timeSynced;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    public BinanceRestClient(
        BinanceConfig config,
        ILogger<BinanceRestClient> logger,
        IRestRateLimitMetrics? rateLimitMetrics = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _rateLimitMetrics = rateLimitMetrics;

        WeightBudget = new RequestWeightBudget(
            _config.RequestWeightLimitPerMinute,
            _config.RequestWeightSoftLimitPercent,
            _config.WeightThrottleEnabled);

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_config.RestBaseUrl),
            Timeout = TimeSpan.FromMilliseconds(_config.HttpTimeoutMs)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AITradingSystem/1.0");

        // P0.7: ключи выбираются по окружению — Live может иметь отдельные (EncryptedLive*).
        _apiKey = TryDecrypt(_config.EffectiveEncryptedApiKey);
        _apiSecret = TryDecrypt(_config.EffectiveEncryptedApiSecret);

        if (!string.IsNullOrWhiteSpace(_apiKey))
            _httpClient.DefaultRequestHeaders.Add("X-MBX-APIKEY", _apiKey);
        else
            _logger.LogWarning(
                "API Key не задан. Окружение {Env}: доступны только публичные данные и Paper Trading.",
                _config.ResolvedEnvironment);

        ExchangeLog.Environment(_config.ResolvedEnvironment.ToString(), _config.RestBaseUrl, _config.MarketStreamBaseUrl);
    }

    /// <summary>Текущее окружение (Live/Testnet/Demo).</summary>
    public string EnvironmentName => _config.ResolvedEnvironment.ToString();
    public bool HasApiKeys => !string.IsNullOrWhiteSpace(_config.EffectiveEncryptedApiKey) && !string.IsNullOrWhiteSpace(_config.EffectiveEncryptedApiSecret);

    /// <summary>P0.1: SHA-256 fingerprint API-ключа; сам ключ не логируется и не сохраняется.</summary>
    public string AccountId => string.IsNullOrWhiteSpace(_apiKey)
        ? "no-api-key"
        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_apiKey)))[..16].ToLowerInvariant();

    public async Task<Dictionary<string, decimal>> GetAllPricesAsync(CancellationToken ct = default)
    {
        var prices = await SendRequestAsync<List<BinanceTickerPrice>>(
            "/api/v3/ticker/price", HttpMethod.Get, signed: false, ct: ct, marketData: false);
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in prices)
        {
            var price = ParseDecimal(item.Price);
            if (!string.IsNullOrWhiteSpace(item.Symbol) && price > 0) result[item.Symbol] = price;
        }
        return result;
    }

    private string? TryDecrypt(string encrypted)
    {
        if (string.IsNullOrWhiteSpace(encrypted)) return null;

        try
        {
            var value = ApiKeyEncryptor.Decrypt(encrypted);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Не удалось расшифровать API-ключ (DPAPI привязан к пользователю Windows). " +
                "Перезашифруйте ключ через SecretEncryptor на этой машине.");
            return null;
        }
    }

    public async Task<List<string>> GetValidSymbolsAsync(CancellationToken ct = default)
    {
        var response = await SendRequestAsync<BinanceExchangeInfo>(
            "/api/v3/exchangeInfo", HttpMethod.Get, signed: false, ct: ct, marketData: false);

        return response.Symbols
            .Where(s => string.Equals(s.Status, "TRADING", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Symbol)
            .ToList();
    }

    public async Task<BinanceSymbolInfo?> GetSymbolInfoAsync(string symbol, CancellationToken ct = default)
    {
        // symbols=["BTCUSDT"] — точечный запрос вместо всего exchangeInfo (~2 МБ).
        var query = $"symbols=%5B%22{Uri.EscapeDataString(symbol.ToUpperInvariant())}%22%5D";
        var response = await SendRequestAsync<BinanceExchangeInfo>(
            $"/api/v3/exchangeInfo?{query}", HttpMethod.Get, signed: false, ct: ct, marketData: false);

        var info = response.Symbols.FirstOrDefault(s =>
            s.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));

        if (info is null)
            _logger.LogWarning("Символ {Symbol} не найден в exchangeInfo ({Env})", symbol, EnvironmentName);
        else
            _logger.LogDebug(
                "Фильтры {Symbol}: step={Step} tick={Tick} minQty={MinQty} minNotional={MinNotional}",
                symbol, info.StepSize, info.TickSize, info.MinQty, info.MinNotional);

        return info;
    }

    public async Task<List<OHLCV>> GetKlinesAsync(
        string symbol,
        string interval,
        int limit,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 1000);
        var query = $"symbol={symbol.ToUpperInvariant()}&interval={interval}&limit={limit}";

        var response = await SendRequestAsync<List<JsonElement[]>>(
            $"/api/v3/klines?{query}", HttpMethod.Get, signed: false, ct: ct, marketData: true);

        var result = new List<OHLCV>(response.Count);
        var skipped = 0;

        foreach (var k in response)
        {
            if (k.Length < 9)
            {
                skipped++;
                continue;
            }

            try
            {
                result.Add(new OHLCV
                {
                    Symbol = symbol.ToUpperInvariant(),
                    OpenTime = DateTimeExtensions.FromUnixMilliseconds(ReadInt64(k[0])),
                    Open = ReadDecimal(k[1]),
                    High = ReadDecimal(k[2]),
                    Low = ReadDecimal(k[3]),
                    Close = ReadDecimal(k[4]),
                    Volume = ReadDecimal(k[5]),
                    CloseTime = DateTimeExtensions.FromUnixMilliseconds(ReadInt64(k[6])),
                    QuoteVolume = ReadDecimal(k[7]),
                    TradesCount = (int)ReadInt64(k[8])
                });
            }
            catch (Exception ex)
            {
                skipped++;
                _logger.LogWarning(ex, "Пропущена некорректная свеча {Symbol}", symbol);
            }
        }

        if (skipped > 0)
            _logger.LogWarning("Klines {Symbol}: пропущено {Skipped} некорректных строк из {Total}",
                symbol, skipped, response.Count);

        _logger.LogDebug("Klines {Symbol} {Interval}: получено {Count} свечей, последняя закрыта={Closed}",
            symbol, interval, result.Count, result.Count > 0 && result[^1].IsClosed);

        return result;
    }

    public async Task<AccountBalance> GetAccountBalanceAsync(CancellationToken ct = default)
    {
        var response = await SendRequestAsync<BinanceAccountInfo>(
            "/api/v3/account", HttpMethod.Get, signed: true, ct: ct);

        var balance = new AccountBalance { Timestamp = DateTime.UtcNow };

        foreach (var b in response.Balances)
        {
            var free = ParseDecimal(b.Free);
            var locked = ParseDecimal(b.Locked);

            if (free > 0 || locked > 0)
                balance.UpdateAsset(b.Asset, free, locked);
        }

        _logger.LogInformation("Баланс {Env}: {Usdt} USDT доступно", EnvironmentName, balance.AvailableUSDT);
        return balance;
    }

    public async Task<Order> PlaceOrderAsync(
        string symbol,
        OrderSide side,
        decimal quantity,
        decimal price,
        bool postOnly = true,
        string? clientOrderId = null,
        CancellationToken ct = default)
    {
        var queryParams = new Dictionary<string, string>
        {
            ["symbol"] = symbol.ToUpperInvariant(),
            ["side"] = side.ToString().ToUpperInvariant(),
            // Post-Only на споте — LIMIT_MAKER: ордер отклоняется, если исполнился бы как taker.
            ["type"] = postOnly ? "LIMIT_MAKER" : "LIMIT",
            ["quantity"] = quantity.ToInvariantString(),
            ["price"] = price.ToInvariantString(),
            ["newOrderRespType"] = "FULL"
        };

        if (!postOnly)
            queryParams["timeInForce"] = "GTC";

        // P0.3: один и тот же newClientOrderId на всех попытках — биржа отклонит дубль (-2010).
        if (!string.IsNullOrWhiteSpace(clientOrderId))
            queryParams["newClientOrderId"] = clientOrderId!;

        var query = BuildQuery(queryParams);

        ExchangeLog.Order("SUBMIT", symbol, side.ToString(), quantity, price, postOnly ? "LIMIT_MAKER" : "LIMIT GTC");

        var response = await SendRequestAsync<BinanceOrderResponse>(
            $"/api/v3/order?{query}", HttpMethod.Post, signed: true, ct: ct);

        var order = MapToOrder(response);
        ExchangeLog.Order("ACCEPTED", symbol, side.ToString(), order.ExecutedQuantity, order.Price,
            $"id={order.Id} status={order.Status} filled={order.FillPercent:F1}%");
        return order;
    }

    public async Task<Order> PlaceIocOrderAsync(
        string symbol,
        OrderSide side,
        decimal quantity,
        decimal price,
        string? clientOrderId = null,
        CancellationToken ct = default)
    {
        var queryParams = new Dictionary<string, string>
        {
            ["symbol"] = symbol.ToUpperInvariant(),
            ["side"] = side.ToString().ToUpperInvariant(),
            ["type"] = "LIMIT",
            ["timeInForce"] = "IOC",
            ["quantity"] = quantity.ToInvariantString(),
            ["price"] = price.ToInvariantString(),
            ["newOrderRespType"] = "FULL"
        };

        if (!string.IsNullOrWhiteSpace(clientOrderId))
            queryParams["newClientOrderId"] = clientOrderId!;

        var query = BuildQuery(queryParams);
        ExchangeLog.Order("SUBMIT-IOC", symbol, side.ToString(), quantity, price, "LIMIT IOC");

        var response = await SendRequestAsync<BinanceOrderResponse>(
            $"/api/v3/order?{query}", HttpMethod.Post, signed: true, ct: ct);

        var order = MapToOrder(response);
        ExchangeLog.Order("IOC-RESULT", symbol, side.ToString(), order.ExecutedQuantity,
            order.AverageFillPrice ?? price, $"id={order.Id} status={order.Status}");
        return order;
    }

    public async Task<Order> PlaceMarketOrderAsync(
        string symbol,
        OrderSide side,
        decimal quantity,
        string? clientOrderId = null,
        CancellationToken ct = default)
    {
        var queryParams = new Dictionary<string, string>
        {
            ["symbol"] = symbol.ToUpperInvariant(),
            ["side"] = side.ToString().ToUpperInvariant(),
            ["type"] = "MARKET",
            ["quantity"] = quantity.ToInvariantString(),
            ["newOrderRespType"] = "FULL"
        };

        if (!string.IsNullOrWhiteSpace(clientOrderId))
            queryParams["newClientOrderId"] = clientOrderId!;

        var query = BuildQuery(queryParams);

        ExchangeLog.Order("SUBMIT-MARKET", symbol, side.ToString(), quantity, 0m);

        var response = await SendRequestAsync<BinanceOrderResponse>(
            $"/api/v3/order?{query}", HttpMethod.Post, signed: true, ct: ct);

        var order = MapToOrder(response);
        ExchangeLog.Order("FILLED-MARKET", symbol, side.ToString(), order.ExecutedQuantity,
            order.AverageFillPrice ?? 0m, $"id={order.Id} status={order.Status}");
        return order;
    }

    public async Task CancelOrderAsync(string symbol, long orderId, CancellationToken ct = default)
    {
        var query = $"symbol={symbol.ToUpperInvariant()}&orderId={orderId}";
        await SendRequestAsync<BinanceOrderResponse>(
            $"/api/v3/order?{query}", HttpMethod.Delete, signed: true, ct: ct);

        ExchangeLog.Order("CANCELED", symbol, "-", 0m, 0m, $"id={orderId}");
    }

    /// <summary>
    /// OCO-защита позиции (P0.1): STOP_LOSS_LIMIT + TAKE_PROFIT_LIMIT одной связкой.
    /// Активация одной ноги немедленно отменяет другую — позиция закрыта биржей
    /// даже если процесс бота мёртв.
    ///
    /// above/below задаются относительно текущей цены:
    ///   SELL (защита long):  above = TAKE_PROFIT_LIMIT, below = STOP_LOSS_LIMIT;
    ///   BUY  (защита short): above = STOP_LOSS_LIMIT,   below = TAKE_PROFIT_LIMIT.
    /// </summary>
    public async Task<ProtectiveOrderList> PlaceOcoAsync(
        string symbol,
        OrderSide side,
        decimal quantity,
        decimal stopPrice,
        decimal stopLimitPrice,
        decimal takeProfitPrice,
        decimal takeProfitLimitPrice,
        string? listClientOrderId = null,
        CancellationToken ct = default)
    {
        var isSell = side == OrderSide.Sell;

        // listClientOrderId <= 36 символов: "oco-" + 32 hex = 36.
        var listId = string.IsNullOrWhiteSpace(listClientOrderId)
            ? $"oco-{Guid.NewGuid():N}"
            : listClientOrderId!;
        var stopClientId = $"sl-{Guid.NewGuid():N}";
        var takeClientId = $"tp-{Guid.NewGuid():N}";

        var aboveTrigger = isSell ? takeProfitPrice : stopPrice;
        var aboveLimit = isSell ? takeProfitLimitPrice : stopLimitPrice;
        var belowTrigger = isSell ? stopPrice : takeProfitPrice;
        var belowLimit = isSell ? stopLimitPrice : takeProfitLimitPrice;

        var query = BuildQuery(new Dictionary<string, string>
        {
            ["symbol"] = symbol.ToUpperInvariant(),
            ["listClientOrderId"] = listId,
            ["side"] = side.ToString().ToUpperInvariant(),
            ["quantity"] = quantity.ToInvariantString(),
            ["aboveType"] = isSell ? "TAKE_PROFIT_LIMIT" : "STOP_LOSS_LIMIT",
            ["aboveClientOrderId"] = isSell ? takeClientId : stopClientId,
            ["aboveStopPrice"] = aboveTrigger.ToInvariantString(),
            ["abovePrice"] = aboveLimit.ToInvariantString(),
            ["aboveTimeInForce"] = "GTC",
            ["belowType"] = isSell ? "STOP_LOSS_LIMIT" : "TAKE_PROFIT_LIMIT",
            ["belowClientOrderId"] = isSell ? stopClientId : takeClientId,
            ["belowStopPrice"] = belowTrigger.ToInvariantString(),
            ["belowPrice"] = belowLimit.ToInvariantString(),
            ["belowTimeInForce"] = "GTC",
            ["newOrderRespType"] = "FULL"
        });

        ExchangeLog.Order("SUBMIT-OCO", symbol, side.ToString(), quantity, stopPrice,
            $"SL={stopPrice}/{stopLimitPrice} TP={takeProfitPrice}/{takeProfitLimitPrice} list={listId}");

        var response = await SendRequestAsync<BinanceOrderListResponse>(
            $"/api/v3/orderList/oco?{query}", HttpMethod.Post, signed: true, ct: ct);

        var protection = MapToProtection(response, symbol, side, quantity,
            stopPrice, stopLimitPrice, takeProfitPrice, takeProfitLimitPrice);

        ExchangeLog.Order("ACCEPTED-OCO", symbol, side.ToString(), quantity, stopPrice,
            $"listId={protection.OrderListId} status={protection.ListOrderStatus} " +
            $"slId={protection.StopOrderId} tpId={protection.TakeProfitOrderId}");

        return protection;
    }

    public async Task CancelOcoAsync(
        string symbol, long orderListId, string? listClientOrderId = null, CancellationToken ct = default)
    {
        var key = orderListId > 0
            ? $"orderListId={orderListId}"
            : $"listClientOrderId={Uri.EscapeDataString(listClientOrderId ?? string.Empty)}";

        await SendRequestAsync<BinanceOrderListResponse>(
            $"/api/v3/orderList?symbol={symbol.ToUpperInvariant()}&{key}",
            HttpMethod.Delete, signed: true, ct: ct);

        ExchangeLog.Order("CANCELED-OCO", symbol, "-", 0m, 0m, $"listId={orderListId} client={listClientOrderId}");
    }

    public async Task<List<ProtectiveOrderList>> GetOpenOrderListsAsync(string? symbol = null, CancellationToken ct = default)
    {
        var response = await SendRequestAsync<List<BinanceOrderListResponse>>(
            "/api/v3/openOrderList", HttpMethod.Get, signed: true, ct: ct);

        var lists = new List<ProtectiveOrderList>(response.Count);
        foreach (var item in response)
        {
            if (symbol != null && !string.Equals(item.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
                continue;

            // openOrderList не содержит orderReports: сторона/цены/типы ног неизвестны,
            // заполняются только orderListId и OrderIds. Разбор ног — задача реконсиляции (P0.5).
            var protection = MapToProtection(item, item.Symbol, OrderSide.Sell, 0m, 0m, 0m, 0m, 0m);
            lists.Add(protection);
        }

        return lists;
    }

    /// <summary>
    /// Ответ orderList → доменная модель защиты. Типы ног известны только из orderReports;
    /// если их нет (openOrderList), сохраняем все orderId в OrderIds — разбор ног делает реконсиляция.
    /// </summary>
    private ProtectiveOrderList MapToProtection(
        BinanceOrderListResponse response,
        string symbol,
        OrderSide side,
        decimal quantity,
        decimal stopPrice,
        decimal stopLimitPrice,
        decimal takeProfitPrice,
        decimal takeProfitLimitPrice)
    {
        var protection = new ProtectiveOrderList
        {
            OrderListId = response.OrderListId,
            ListClientOrderId = response.ListClientOrderId,
            Symbol = string.IsNullOrWhiteSpace(response.Symbol) ? symbol.ToUpperInvariant() : response.Symbol,
            Side = side,
            Quantity = quantity,
            StopTriggerPrice = stopPrice,
            StopLimitPrice = stopLimitPrice,
            TakeProfitTriggerPrice = takeProfitPrice,
            TakeProfitLimitPrice = takeProfitLimitPrice,
            ListOrderStatus = response.ListOrderStatus,
            CreatedAt = response.TransactionTime > 0
                ? DateTimeExtensions.FromUnixMilliseconds(response.TransactionTime)
                : DateTime.UtcNow,
            OrderIds = response.Orders.Select(o => o.OrderId).ToList()
        };

        foreach (var report in response.OrderReports)
        {
            var type = report.Type.ToUpperInvariant();
            var trigger = ParseDecimal(report.StopPrice);
            var limit = ParseDecimal(report.Price);

            if (type.StartsWith("STOP_LOSS", StringComparison.Ordinal))
            {
                protection.StopOrderId = report.OrderId;
                protection.StopClientOrderId = report.ClientOrderId;
                if (trigger > 0) protection.StopTriggerPrice = trigger;
                if (limit > 0) protection.StopLimitPrice = limit;
            }
            else if (type.StartsWith("TAKE_PROFIT", StringComparison.Ordinal))
            {
                protection.TakeProfitOrderId = report.OrderId;
                protection.TakeProfitClientOrderId = report.ClientOrderId;
                if (trigger > 0) protection.TakeProfitTriggerPrice = trigger;
                if (limit > 0) protection.TakeProfitLimitPrice = limit;
            }
        }

        if (response.OrderReports.Count > 0)
        {
            protection.Quantity = ParseDecimal(response.OrderReports[0].OrigQty);
            protection.Side = string.Equals(response.OrderReports[0].Side, "BUY", StringComparison.OrdinalIgnoreCase)
                ? OrderSide.Buy
                : OrderSide.Sell;
        }

        return protection;
    }

    public async Task<Order> GetOrderAsync(string symbol, long orderId, CancellationToken ct = default)
    {
        var query = $"symbol={symbol.ToUpperInvariant()}&orderId={orderId}";
        var response = await SendRequestAsync<BinanceOrderResponse>(
            $"/api/v3/order?{query}", HttpMethod.Get, signed: true, ct: ct);

        return await EnrichOrderCommissionAsync(MapToOrder(response), ct);
    }

    /// <summary>P0.3: состояние ордера по clientOrderId — после «Duplicate order sent».</summary>
    public async Task<Order> GetOrderAsync(string symbol, string clientOrderId, CancellationToken ct = default)
    {
        var query = $"symbol={symbol.ToUpperInvariant()}&origClientOrderId={Uri.EscapeDataString(clientOrderId)}";
        var response = await SendRequestAsync<BinanceOrderResponse>(
            $"/api/v3/order?{query}", HttpMethod.Get, signed: true, ct: ct);

        return await EnrichOrderCommissionAsync(MapToOrder(response), ct);
    }

    private async Task<Order> EnrichOrderCommissionAsync(Order order, CancellationToken ct)
    {
        if (order.ExecutedQuantity <= 0m || order.CommissionByAsset.Count > 0 || order.BinanceOrderId <= 0)
            return order;
        var trades = await SendRequestAsync<List<BinanceMyTrade>>(
            $"/api/v3/myTrades?symbol={order.Symbol.ToUpperInvariant()}&orderId={order.BinanceOrderId}",
            HttpMethod.Get, signed: true, ct: ct);
        var fees = trades
            .GroupBy(t => t.CommissionAsset, StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key))
            .ToDictionary(g => g.Key, g => g.Sum(t => ParseDecimal(t.Commission)), StringComparer.OrdinalIgnoreCase);
        if (fees.Count == 0) return order;
        order.CommissionByAsset = fees;
        order.Commission = fees.Values.Sum();
        order.CommissionAsset = fees.Keys.First();
        return order;
    }

    public async Task<List<Order>> GetOpenOrdersAsync(string? symbol = null, CancellationToken ct = default)
    {
        var query = symbol != null ? $"symbol={symbol.ToUpperInvariant()}" : string.Empty;
        var url = string.IsNullOrEmpty(query) ? "/api/v3/openOrders" : $"/api/v3/openOrders?{query}";

        var response = await SendRequestAsync<List<BinanceOrderResponse>>(
            url, HttpMethod.Get, signed: true, ct: ct);

        return response.Select(MapToOrder).ToList();
    }

    /// <summary>Серверное время биржи (для синхронизации подписи).</summary>
    public async Task<long> GetServerTimeAsync(CancellationToken ct = default)
    {
        var response = await SendRequestAsync<BinanceServerTime>(
            "/api/v3/time", HttpMethod.Get, signed: false, ct: ct, marketData: true);
        return response.ServerTime;
    }

    /// <summary>P0.5: listenKey user-data stream. Подпись не нужна — только заголовок X-MBX-APIKEY.</summary>
    public async Task<string> CreateListenKeyAsync(CancellationToken ct = default)
    {
        var response = await SendRequestAsync<BinanceListenKey>(
            "/api/v3/userDataStream", HttpMethod.Post, signed: false, ct: ct);
        if (string.IsNullOrWhiteSpace(response.ListenKey))
            throw new InvalidOperationException("Binance вернула пустой listenKey");
        return response.ListenKey;
    }

    public async Task KeepAliveListenKeyAsync(string listenKey, CancellationToken ct = default)
    {
        await SendRequestAsync<JsonElement>(
            $"/api/v3/userDataStream?listenKey={Uri.EscapeDataString(listenKey)}",
            HttpMethod.Put, signed: false, ct: ct);
    }

    public async Task CloseListenKeyAsync(string listenKey, CancellationToken ct = default)
    {
        await SendRequestAsync<JsonElement>(
            $"/api/v3/userDataStream?listenKey={Uri.EscapeDataString(listenKey)}",
            HttpMethod.Delete, signed: false, ct: ct);
    }

    private async Task EnsureTimeSyncedAsync(CancellationToken ct)
    {
        if (!_config.SyncServerTime || _timeSynced) return;

        await _timeSyncLock.WaitAsync(ct);
        try
        {
            if (_timeSynced) return;

            var localBefore = DateTime.UtcNow.ToUnixMilliseconds();
            var serverTime = await GetServerTimeAsync(ct);
            var localAfter = DateTime.UtcNow.ToUnixMilliseconds();
            var latency = (localAfter - localBefore) / 2;

            _serverTimeOffsetMs = serverTime - localBefore - latency;
            _timeSynced = true;

            _logger.LogInformation("Синхронизация времени с Binance: смещение {Offset} мс (RTT {Rtt} мс)",
                _serverTimeOffsetMs, localAfter - localBefore);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось синхронизировать время с сервером — используем локальные часы");
            _timeSynced = true; // не блокируем торговлю из-за недоступности /time
        }
        finally
        {
            _timeSyncLock.Release();
        }
    }

    private async Task<T> SendRequestAsync<T>(
        string endpoint,
        HttpMethod method,
        bool signed,
        CancellationToken ct,
        bool marketData = false)
    {
        if (signed && string.IsNullOrWhiteSpace(_apiSecret))
            throw new InvalidOperationException(
                $"Для подписанных запросов нужен API Secret (окружение {EnvironmentName}). " +
                "Введите ключи в настройках или включите Paper Trading.");

        if (signed)
            await EnsureTimeSyncedAsync(ct);

        // Публичные рыночные данные можно брать с mainnet (актуально для testnet).
        var absoluteBase = marketData && _config.UseMainnetMarketData
            ? _config.MarketRestBaseUrl
            : _config.RestBaseUrl;

        var attempt = 0;
        var maxAttempts = Math.Max(1, _config.MaxRetries + 1);
        Exception? lastError = null;

        while (attempt < maxAttempts)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;

            // P1.6: бюджет весов. Некритичные запросы (polling/данные) ждут конца окна
            // при приближении к лимиту; постановка/снятие ордеров обходит soft-limit.
            // Бан 429/-1003/418 действует для всех. Ожидание — ДО построения timestamp подписи.
            var critical = RequestWeightBudget.IsCriticalOperation(method, endpoint);
            var throttle = WeightBudget.GetThrottleDelay(DateTime.UtcNow, critical);
            if (throttle > TimeSpan.Zero)
            {
                _rateLimitMetrics?.RecordWeightThrottle(throttle.TotalMilliseconds, critical);
                _logger.LogWarning(
                    "RATE-THROTTLE {Endpoint}: ожидание {Delay:F0} мс (вес {Used}/{Limit}, бан={Banned}, критичный={Critical})",
                    endpoint, throttle.TotalMilliseconds, WeightBudget.GetUsedWeight(DateTime.UtcNow),
                    WeightBudget.LimitPerMinute, WeightBudget.IsBanned(DateTime.UtcNow), critical);
                await Task.Delay(throttle, ct);
            }

            // Локальная оценка веса запроса — бюджет учитывает и запросы между сэмплами заголовка.
            WeightBudget.AddEstimatedWeight(RequestWeightBudget.EstimateWeight(method, endpoint), DateTime.UtcNow);

            var url = endpoint;
            if (signed)
            {
                var timestamp = DateTime.UtcNow.ToUnixMilliseconds() + _serverTimeOffsetMs;
                var separator = url.Contains('?') ? "&" : "?";
                url = $"{url}{separator}timestamp={timestamp}&recvWindow={_config.RecvWindowMs}";
                var payload = url[(url.IndexOf('?') + 1)..];
                url = $"{url}&signature={CreateSignature(payload)}";
            }

            var fullUrl = $"{absoluteBase.TrimEnd('/')}{url}";
            var sw = Stopwatch.StartNew();

            // P0.8: жёсткий дедлайн попытки поверх HttpClient.Timeout.
            // Timeout покрывает не все фазы транспорта (connect/DNS/чтение тела могут
            // проигнорировать отмену): 26.11 11:47:57 klines DASHUSDT ушёл в 4-ю попытку
            // и не вернулся НИКОГДА — вместе с ним навсегда встал весь основной цикл.
            // Дедлайн гарантирует, что вызывающий контур получит управление обратно.
            var attemptBudget = TimeSpan.FromMilliseconds(_config.HttpTimeoutMs);
            var hardDeadline = attemptBudget + HardDeadlineGrace;

            try
            {
                using var request = new HttpRequestMessage(method, new Uri(fullUrl));
                ExchangeLog.Request(method.Method, fullUrl);

                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(hardDeadline);

                using var response = await WithHardDeadlineAsync(
                    _httpClient.SendAsync(request, attemptCts.Token), hardDeadline, ct, endpoint);

                var bodyBudget = hardDeadline - sw.Elapsed;
                if (bodyBudget <= TimeSpan.Zero)
                    throw new TimeoutException(
                        $"{endpoint}: дедлайн попытки {hardDeadline.TotalMilliseconds:F0} мс исчерпан до чтения тела");

                var content = await WithHardDeadlineAsync(
                    response.Content.ReadAsStringAsync(attemptCts.Token), bodyBudget, ct, $"{endpoint} (body)");
                sw.Stop();

                ExchangeLog.Response(method.Method, fullUrl, (int)response.StatusCode,
                    sw.Elapsed.TotalMilliseconds, content);

                // P1.6: факт израсходованного веса из заголовка биржи — важнее локальных оценок.
                if (TryReadUsedWeight(response, out var usedWeight))
                {
                    WeightBudget.ObserveUsedWeight(usedWeight, DateTime.UtcNow);
                    _rateLimitMetrics?.RecordRestWeight(usedWeight, WeightBudget.LimitPerMinute);
                }

                if (response.IsSuccessStatusCode)
                {
                    var parsed = JsonSerializer.Deserialize<T>(content, _jsonOptions);
                    if (parsed is null)
                        throw new InvalidOperationException($"Binance вернул пустой/некорректный JSON для {endpoint}");
                    return parsed;
                }

                var (apiCode, apiMessage) = ParseApiError(content);
                ExchangeLog.ApiError(method.Method, fullUrl, (int)response.StatusCode, content, apiCode, apiMessage);

                // -1021: часы уехали — пересинхронизируем и повторим.
                if (apiCode == -1021)
                {
                    _timeSynced = false;
                    await EnsureTimeSyncedAsync(ct);
                }

                // P1.6: явная ветка rate-limit — 429 / -1003 / 418 (IP-бан).
                // Регистрируем бан в бюджете: ВСЕ параллельные запросы (включая закрытие)
                // ждут его окончания, а не бьют по бирже независимыми ретраями.
                var rateLimited = (int)response.StatusCode == 429 || (int)response.StatusCode == 418 || apiCode == -1003;
                var rateLimitBackoff = TimeSpan.Zero;
                if (rateLimited)
                {
                    rateLimitBackoff = GetRetryDelay(attempt, response);
                    // 418: Retry-After содержит длительность IP-бана — она может превышать
                    // обычный потолок ретрая (120 с); регистрируем фактическую (≤ 1 часа).
                    if ((int)response.StatusCode == 418 &&
                        TryReadRetryAfterSeconds(response, out var banSeconds) && banSeconds > 0)
                    {
                        rateLimitBackoff = TimeSpan.FromSeconds(Math.Min(banSeconds, 3600));
                    }

                    WeightBudget.RegisterBan(rateLimitBackoff, DateTime.UtcNow);
                    _rateLimitMetrics?.RecordRateLimitHit((int)response.StatusCode, apiCode, rateLimitBackoff.TotalMilliseconds);
                    _logger.LogWarning(
                        "RATE-LIMIT {Status} code={Code} {Endpoint}: backoff {Backoff:F0} мс, все запросы приостановлены до окончания бана",
                        (int)response.StatusCode, apiCode?.ToString(CultureInfo.InvariantCulture) ?? "-", endpoint,
                        rateLimitBackoff.TotalMilliseconds);
                }

                var transient = IsTransient(response.StatusCode, apiCode);
                var error = new BinanceApiException((int)response.StatusCode, apiCode, apiMessage, content);

                if (!transient || attempt >= maxAttempts)
                    throw error;

                lastError = error;
                // Ретрай не раньше окончания бана (он мог быть продлён параллельным ответом).
                var delay = GetRetryDelay(attempt, response);
                if (rateLimited)
                {
                    var banRemaining = WeightBudget.GetBanRemaining(DateTime.UtcNow);
                    if (banRemaining > delay) delay = banRemaining;
                    if (rateLimitBackoff > delay) delay = rateLimitBackoff;
                }
                ExchangeLog.Retry(fullUrl, attempt, maxAttempts, $"{(int)response.StatusCode} {apiMessage}");
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (BinanceApiException)
            {
                throw;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested &&
                                       ex is HttpRequestException or TimeoutException or JsonException
                                           or OperationCanceledException)
            {
                sw.Stop();
                lastError = ex;

                if (attempt >= maxAttempts)
                {
                    _logger.LogError(ex, "Запрос {Endpoint} провалился после {Attempts} попыток", endpoint, attempt);
                    throw;
                }

                var delay = TimeSpan.FromMilliseconds(_config.RetryBaseDelayMs * Math.Pow(2, attempt - 1));
                ExchangeLog.Retry(fullUrl, attempt, maxAttempts, ex.Message);
                await Task.Delay(delay, ct);
            }
        }

        throw lastError ?? new InvalidOperationException($"Запрос {endpoint} не выполнен");
    }

    /// <summary>P0.8: запас поверх HttpClient.Timeout — время на корректную отмену транспорта.</summary>
    private static readonly TimeSpan HardDeadlineGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// P0.8: жёсткий дедлайн поверх задачи. В отличие от HttpClient.Timeout срабатывает даже
    /// если транспорт залип и не реагирует на отмену: вызывающий код всегда получает управление
    /// и может ретраить/продолжить цикл. Брошенная задача досчитывается в фоне, её исключение
    /// наблюдается (не роняет процесс), залипший ответ освобождается.
    /// </summary>
    private static async Task<T> WithHardDeadlineAsync<T>(
        Task<T> task, TimeSpan deadline, CancellationToken ct, string operation)
    {
        var completed = await Task.WhenAny(task, Task.Delay(deadline, ct)).ConfigureAwait(false);
        if (completed == task)
            return await task.ConfigureAwait(false);

        // Отмена бота — не таймаут: пробрасываем как есть.
        ct.ThrowIfCancellationRequested();
        ObserveAbandoned(task);
        throw new TimeoutException(
            $"{operation}: жёсткий дедлайн {deadline.TotalMilliseconds:F0} мс истёк (транспорт не ответил на отмену)");
    }

    private static void ObserveAbandoned<T>(Task<T> task) =>
        task.ContinueWith(
            t =>
            {
                _ = t.Exception;
                if (t.IsCompletedSuccessfully && t.Result is IDisposable disposable)
                    disposable.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private TimeSpan GetRetryDelay(int attempt, HttpResponseMessage response)
    {
        if (TryReadRetryAfterSeconds(response, out var seconds) && seconds > 0)
            return TimeSpan.FromSeconds(Math.Min(seconds, 120));

        return TimeSpan.FromMilliseconds(Math.Min(_config.RetryBaseDelayMs * Math.Pow(2, attempt - 1), 30_000));
    }

    /// <summary>P1.6: Retry-After в секундах (429/418). Нет/не число — false.</summary>
    public static bool TryReadRetryAfterSeconds(HttpResponseMessage response, out int seconds)
    {
        seconds = 0;
        if (!response.Headers.TryGetValues("Retry-After", out var values)) return false;
        var raw = values.FirstOrDefault();
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds);
    }

    /// <summary>
    /// P1.6: израсходованный вес минутного окна из заголовка X-MBX-USED-WEIGHT-1M
    /// (легаси-имя X-MBX-USED-WEIGHT тоже принимается). Несколько значений — максимум.
    /// </summary>
    public static bool TryReadUsedWeight(HttpResponseMessage response, out int usedWeight)
    {
        usedWeight = 0;
        var found = false;

        foreach (var name in new[] { "X-MBX-USED-WEIGHT-1M", "X-MBX-USED-WEIGHT" })
        {
            if (!response.Headers.TryGetValues(name, out var values)) continue;
            foreach (var raw in values)
            {
                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) && w >= 0)
                {
                    found = true;
                    if (w > usedWeight) usedWeight = w;
                }
            }
        }

        return found;
    }

    private static bool IsTransient(HttpStatusCode status, int? apiCode)
    {
        if (status == HttpStatusCode.TooManyRequests || (int)status == 418) return true;
        if ((int)status >= 500) return true;

        // -1000/-1001/-1003/-1006/-1007 — внутренние/сетевые/лимитные ошибки Binance.
        return apiCode is -1000 or -1001 or -1003 or -1006 or -1007;
    }

    private static (int? Code, string? Message) ParseApiError(string content)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, null);

            int? code = doc.RootElement.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci) ? ci : null;
            string? msg = doc.RootElement.TryGetProperty("msg", out var m) ? m.GetString() : null;
            return (code, msg);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private string CreateSignature(string queryString)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_apiSecret ?? string.Empty));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(queryString));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string BuildQuery(Dictionary<string, string> parameters) =>
        string.Join("&", parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));

    private Order MapToOrder(BinanceOrderResponse response)
    {
        var executed = ParseDecimal(response.ExecutedQty);
        var quote = ParseDecimal(response.CummulativeQuoteQty);
        var commissionByAsset = (response.Fills ?? new List<BinanceFill>())
            .GroupBy(f => f.CommissionAsset, StringComparer.OrdinalIgnoreCase)
            .Where(g => !string.IsNullOrWhiteSpace(g.Key))
            .ToDictionary(g => g.Key, g => g.Sum(f => ParseDecimal(f.Commission)), StringComparer.OrdinalIgnoreCase);
        var commission = commissionByAsset.Values.Sum();

        // Если биржа не прислала cummulativeQuoteQty (некоторые площадки/типы),
        // считаем среднюю цену по fills.
        if (quote <= 0 && response.Fills is { Count: > 0 })
        {
            quote = response.Fills.Sum(f => ParseDecimal(f.Price) * ParseDecimal(f.Qty));
            if (executed <= 0)
                executed = response.Fills.Sum(f => ParseDecimal(f.Qty));
        }

        return new Order
        {
            Id = response.OrderId.ToString(CultureInfo.InvariantCulture),
            ClientOrderId = response.ClientOrderId,
            Symbol = response.Symbol,
            Side = string.Equals(response.Side, "BUY", StringComparison.OrdinalIgnoreCase) ? OrderSide.Buy : OrderSide.Sell,
            Type = MapOrderType(response.Type),
            Status = MapOrderStatus(response.Status),
            Price = ParseDecimal(response.Price),
            OriginalQuantity = ParseDecimal(response.OrigQty),
            ExecutedQuantity = executed,
            FilledQuoteQuantity = quote,
            BinanceOrderId = response.OrderId,
            CreatedAt = response.TransactTime > 0
                ? DateTimeExtensions.FromUnixMilliseconds(response.TransactTime)
                : DateTime.UtcNow,
            UpdatedAt = response.UpdateTime > 0
                ? DateTimeExtensions.FromUnixMilliseconds(response.UpdateTime)
                : null,
            Commission = commission,
            CommissionAsset = commissionByAsset.Keys.FirstOrDefault() ?? string.Empty,
            CommissionByAsset = commissionByAsset,
            IsPostOnly = string.Equals(response.Type, "LIMIT_MAKER", StringComparison.OrdinalIgnoreCase)
        };
    }

    private static OrderType MapOrderType(string type) => type.ToUpperInvariant() switch
    {
        "MARKET" => OrderType.Market,
        "STOP_LOSS" or "STOP_LOSS_LIMIT" or "TAKE_PROFIT" or "TAKE_PROFIT_LIMIT" => OrderType.StopLoss,
        _ => OrderType.Limit
    };

    private static OrderStatus MapOrderStatus(string status) => status.ToUpperInvariant() switch
    {
        "NEW" => OrderStatus.New,
        "PARTIALLY_FILLED" => OrderStatus.PartiallyFilled,
        "FILLED" => OrderStatus.Filled,
        "CANCELED" or "PENDING_CANCEL" => OrderStatus.Canceled,
        "REJECTED" => OrderStatus.Rejected,
        "EXPIRED" or "EXPIRED_IN_MATCH" => OrderStatus.Expired,
        _ => OrderStatus.New
    };

    /// <summary>Binance отдаёт числа и строками, и числами — читаем оба варианта.</summary>
    private static decimal ReadDecimal(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.GetDecimal(),
        JsonValueKind.String => ParseDecimal(element.GetString()),
        _ => 0m
    };

    private static long ReadInt64(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => element.GetInt64(),
        JsonValueKind.String => long.TryParse(element.GetString(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var v) ? v : 0L,
        _ => 0L
    };

    private static decimal ParseDecimal(string? text) =>
        decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0m;

    public void Dispose()
    {
        _httpClient.Dispose();
        _timeSyncLock.Dispose();
    }
}

/// <summary>Ошибка Binance API с кодом и текстом (для корректной обработки в исполнителе ордеров).</summary>
public sealed class BinanceApiException : HttpRequestException
{
    public int HttpStatus { get; }
    public int? ApiCode { get; }
    public string ApiMessage { get; }
    public string RawBody { get; }

    public BinanceApiException(int httpStatus, int? apiCode, string? apiMessage, string rawBody)
        : base($"Binance API error {(int)httpStatus} code={apiCode?.ToString(CultureInfo.InvariantCulture) ?? "-"}: {apiMessage}")
    {
        HttpStatus = httpStatus;
        ApiCode = apiCode;
        ApiMessage = apiMessage ?? string.Empty;
        RawBody = rawBody;
    }

    /// <summary>Нарушение Post-Only (ордер исполнился бы как taker).</summary>
    public bool IsPostOnlyRejection =>
        ApiCode == -2010 && ApiMessage.Contains("post only", StringComparison.OrdinalIgnoreCase);

    /// <summary>Недостаточно средств.</summary>
    public bool IsInsufficientBalance => ApiCode == -2010 ||
        (ApiCode == -1013 && !IsOrderFilterFailure);

    /// <summary>Нарушение фильтров ордера — это не «недостаточно средств».</summary>
    public bool IsOrderFilterFailure => ApiCode == -1013 ||
        ApiMessage.Contains("Filter failure", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ордер/связка неизвестны бирже: уже исполнены, отменены или сняты по OCO.
    /// Для cancel это не ошибка — защиты на бирже больше нет.
    /// </summary>
    public bool IsUnknownOrderOrList =>
        ApiCode == -2011 ||
        ApiCode == -2013 ||
        ApiMessage.Contains("Unknown order", StringComparison.OrdinalIgnoreCase) ||
        ApiMessage.Contains("Order does not exist", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// P0.3: «Duplicate order sent» — ордер с этим clientOrderId уже принят биржей.
    /// Это не отказ: нужно взять его состояние, а не отправлять новый.
    /// </summary>
    public bool IsDuplicateOrder =>
        ApiCode == -2010 && ApiMessage.Contains("duplicate", StringComparison.OrdinalIgnoreCase);

    /// <summary>Стоп-цена уже за рынком: «Order would immediately trigger».</summary>
    public bool IsWouldImmediatelyTrigger =>
        ApiCode == -2010 && ApiMessage.Contains("immediately", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// P0.3: транзиентный сбой — сеть/внутренние ошибки биржи/лимиты. Такой отказ имеет смысл
    /// повторить с тем же clientOrderId (дубль не создастся), в отличие от отказа по существу.
    /// Совпадает с классификацией ретраев внутри <c>BinanceRestClient.SendRequestAsync</c>.
    /// </summary>
    public bool IsTransient =>
        HttpStatus >= 500 ||
        HttpStatus == 429 ||
        HttpStatus == 418 ||
        ApiCode is -1000 or -1001 or -1003 or -1006 or -1007;
}
