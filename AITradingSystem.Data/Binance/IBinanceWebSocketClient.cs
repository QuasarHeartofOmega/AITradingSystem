using AITradingSystem.Core.Models.MarketData;
using AITradingSystem.Core.Models.Trading;

namespace AITradingSystem.Data.Binance;

/// <summary>
/// Интерфейс WebSocket клиента Binance (FR-1.1, FR-1.2, FR-1.3).
/// </summary>
public interface IBinanceWebSocketClient : IAsyncDisposable
{
    /// <summary>
    /// Событие: новая свеча (закрытая или обновленная).
    /// </summary>
    event Action<OHLCV>? OnKline;

    /// <summary>
    /// Событие: новая сделка (для построения OHLCV из тиков).
    /// </summary>
    event Action<string, decimal, decimal, DateTime>? OnTrade;

    /// <summary>
    /// Событие: обновленный стакан ордеров.
    /// </summary>
    event Action<OrderBook>? OnOrderBook;

    /// <summary>
    /// Событие: ошибка соединения.
    /// </summary>
    event Action<Exception>? OnError;

    /// <summary>P1.4: рыночный WS потерял соединение и начал переподключение; данные считаются протухшими.</summary>
    event Action? OnDisconnected;

    /// <summary>P1.4: рыночный WS успешно переподключился; требуется REST-дозагрузка истории и повторный прогрев.</summary>
    event Action? OnReconnected;

    /// <summary>
    /// P0.5: событие executionReport из user-data stream (факт исполнения/отмены ордера).
    /// </summary>
    event Action<ExecutionReport>? OnExecutionReport;

    /// <summary>
    /// P0.5: подключить отдельный user-data socket (/ws/&lt;listenKey&gt;).
    /// Рыночные стримы не затрагиваются; keepalive listenKey — забота вызывающего.
    /// </summary>
    Task ConnectUserDataStreamAsync(string listenKey, CancellationToken ct = default);

    /// <summary>P0.5: отключить user-data socket (рыночные стримы продолжают работать).</summary>
    Task DisconnectUserDataStreamAsync();

    /// <summary>
    /// Подключиться и подписаться на указанные пары.
    /// </summary>
    Task ConnectAsync(IEnumerable<string> symbols, string interval, CancellationToken ct = default);

    /// <summary>
    /// P0.3: переподписать стримы конкретных символов (UNSUBSCRIBE + SUBSCRIBE) без разрыва соединения.
    /// Используется, когда символ молчит после REST-прогрева: history_reloaded ≠ stream_live.
    /// </summary>
    Task ResubscribeAsync(IEnumerable<string> symbols, string interval, CancellationToken ct = default);

    /// <summary>
    /// P0.3: принудительный реконнект только рыночного сокета (user-data socket не затрагивается).
    /// Вызывает OnDisconnected/OnReconnected, чтобы движок заново прошёл REST-прогрев и подтверждение live.
    /// </summary>
    Task ReconnectMarketStreamsAsync(CancellationToken ct = default);

    /// <summary>
    /// Отключиться.
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    /// Подключен ли клиент.
    /// </summary>
    bool IsConnected { get; }
    /// <summary>P0.5: подключён ли отдельный user-data socket (listenKey).</summary>
    bool IsUserDataStreamConnected { get; }
    /// <summary>Последняя цена из уже полученного kline; без сетевого запроса.</summary>
    bool TryGetLastPrice(string symbol, out decimal price);
}