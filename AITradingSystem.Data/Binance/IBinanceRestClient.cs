using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.MarketData;
using AITradingSystem.Core.Models.Trading;

namespace AITradingSystem.Data.Binance;

/// <summary>
/// Интерфейс REST клиента Binance (FR-6).
/// </summary>
public interface IBinanceRestClient
{
    /// <summary>
    /// Получить список валидных торговых пар.
    /// </summary>
    Task<List<string>> GetValidSymbolsAsync(CancellationToken ct = default);

    /// <summary>
    /// Получить информацию о символе (фильтры).
    /// </summary>
    Task<BinanceSymbolInfo?> GetSymbolInfoAsync(string symbol, CancellationToken ct = default);

    /// <summary>
    /// Получить исторические свечи.
    /// </summary>
    Task<List<OHLCV>> GetKlinesAsync(string symbol, string interval, int limit, CancellationToken ct = default);

    /// <summary>
    /// Получить баланс аккаунта.
    /// </summary>
    Task<AccountBalance> GetAccountBalanceAsync(CancellationToken ct = default);

    /// <summary>
    /// Разместить ордер.
    /// </summary>
    /// <param name="clientOrderId">
    /// P0.3: newClientOrderId — ключ идемпотентности. Ретрай внутри клиента переиспользует
    /// тот же id, поэтому биржа не примет второй ордер при сетевом сбое.
    /// </param>
    Task<Order> PlaceOrderAsync(
        string symbol,
        OrderSide side,
        decimal quantity,
        decimal price,
        bool postOnly = true,
        string? clientOrderId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Отменить ордер.
    /// </summary>
    Task CancelOrderAsync(string symbol, long orderId, CancellationToken ct = default);

    /// <summary>
    /// Получить статус ордера.
    /// </summary>
    Task<Order> GetOrderAsync(string symbol, long orderId, CancellationToken ct = default);

    /// <summary>
    /// Состояние ордера по clientOrderId (origClientOrderId). Нужно после ответа
    /// «Duplicate order sent»: ордер уже принят, повторять отправку нельзя (P0.3).
    /// </summary>
    Task<Order> GetOrderAsync(string symbol, string clientOrderId, CancellationToken ct = default);

    /// <summary>
    /// Получить открытые ордера.
    /// </summary>
    Task<List<Order>> GetOpenOrdersAsync(string? symbol = null, CancellationToken ct = default);

    /// <summary>E1: authoritative exchange trade ids and per-trade fees for an order.</summary>
    Task<List<BinanceMyTrade>> GetMyTradesAsync(string symbol, long? orderId = null, CancellationToken ct = default,
        long? startTime = null, long? endTime = null, int limit = 1000);

    /// <summary>
    /// Разместить MARKET-ордер (гарантированное исполнение, для закрытия позиций).
    /// </summary>
    Task<Order> PlaceMarketOrderAsync(
        string symbol,
        OrderSide side,
        decimal quantity,
        string? clientOrderId = null,
        CancellationToken ct = default);

    /// <summary>
    /// P1.2: marketable LIMIT IOC для выхода с ограничением проскальзывания.
    /// Неисполненный остаток не остаётся в стакане (IOC), позиция дозакрывается следующим контуром.
    /// </summary>
    Task<Order> PlaceIocOrderAsync(
        string symbol,
        OrderSide side,
        decimal quantity,
        decimal price,
        string? clientOrderId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Поставить биржевую защиту позиции — OCO: STOP_LOSS_LIMIT + TAKE_PROFIT_LIMIT (P0.1).
    /// POST /api/v3/orderList/oco. <paramref name="side"/> — сторона ЗАЩИТНЫХ ордеров
    /// (противоположна стороне позиции). Цены должны быть уже округлены к tickSize,
    /// объём — к stepSize (см. ProtectiveOrderPricing).
    /// </summary>
    Task<ProtectiveOrderList> PlaceOcoAsync(
        string symbol,
        OrderSide side,
        decimal quantity,
        decimal stopPrice,
        decimal stopLimitPrice,
        decimal takeProfitPrice,
        decimal takeProfitLimitPrice,
        string? listClientOrderId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Отменить OCO-связку целиком (DELETE /api/v3/orderList).
    /// Используется перед market-закрытием и при cancel/replace трейлинга.
    /// </summary>
    Task CancelOcoAsync(string symbol, long orderListId, string? listClientOrderId = null, CancellationToken ct = default);

    /// <summary>
    /// Открытые OCO-связки счёта (GET /api/v3/openOrderList).
    /// В ответе только id ног без типов/цен — цены остаются нулевыми.
    /// </summary>
    Task<List<ProtectiveOrderList>> GetOpenOrderListsAsync(string? symbol = null, CancellationToken ct = default);

    /// <summary>
    /// Серверное время биржи в мс (используется для синхронизации подписи).
    /// </summary>
    Task<long> GetServerTimeAsync(CancellationToken ct = default);

    /// <summary>
    /// P0.5: создать listenKey user-data stream (POST /api/v3/userDataStream).
    /// Ключ живёт 60 минут без keepalive.
    /// </summary>
    Task<string> CreateListenKeyAsync(CancellationToken ct = default);

    /// <summary>P0.5: продлить listenKey (PUT /api/v3/userDataStream).</summary>
    Task KeepAliveListenKeyAsync(string listenKey, CancellationToken ct = default);

    /// <summary>P0.5: закрыть listenKey (DELETE /api/v3/userDataStream).</summary>
    Task CloseListenKeyAsync(string listenKey, CancellationToken ct = default);

    /// <summary>
    /// Имя активного окружения: Live | Testnet | Demo.
    /// </summary>
    string EnvironmentName { get; }

    /// <summary>P0.1: стабильный не-секретный отпечаток API-ключа для ключа risk-state.</summary>
    string AccountId { get; }
    /// <summary>Наличие обоих зашифрованных ключей в конфигурации; не результат их расшифровки.</summary>
    bool HasApiKeys { get; }
    /// <summary>Публичные последние цены всех символов выбранного окружения.</summary>
    Task<Dictionary<string, decimal>> GetAllPricesAsync(CancellationToken ct = default);
}