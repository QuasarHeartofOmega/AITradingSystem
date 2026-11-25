using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.Collections.Concurrent;
using AITradingSystem.Common.Configuration;
using AITradingSystem.Common.Extensions;
using AITradingSystem.Common.Logging;
using AITradingSystem.Core.Models.MarketData;
using AITradingSystem.Core.Models.Trading;
using Microsoft.Extensions.Logging;

namespace AITradingSystem.Data.Binance;

/// <summary>
/// WebSocket клиент Binance с автопереподключением (NFR-2.1).
/// Окружение берётся из конфигурации: Live / Testnet / Demo (demo-stream.binance.com).
/// Стакан — partial-book stream (@depth20@1000ms): готовый снимок топ-20 уровней.
/// </summary>
public sealed class BinanceWebSocketClient : IBinanceWebSocketClient
{
    private readonly BinanceConfig _config;
    private readonly ILogger<BinanceWebSocketClient> _logger;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _receiveTask;
    private string? _streamUrl;
    private long _messagesReceived;
    private long _parseErrors;

    public event Action<OHLCV>? OnKline;
    public event Action<string, decimal, decimal, DateTime>? OnTrade;
    public event Action<OrderBook>? OnOrderBook;
    public event Action<Exception>? OnError;
    public event Action? OnDisconnected;
    public event Action? OnReconnected;
    public event Action<ExecutionReport>? OnExecutionReport;

    // P0.5: отдельный socket user-data stream — не смешивается с рыночными стримами.
    private ClientWebSocket? _userWs;
    private CancellationTokenSource? _userCts;
    private Task? _userReceiveTask;
    private string? _userStreamUrl;

    public bool IsConnected => _ws?.State == WebSocketState.Open;
    public bool IsUserDataStreamConnected => _userWs?.State == WebSocketState.Open;
    private readonly ConcurrentDictionary<string, decimal> _lastPrices = new(StringComparer.OrdinalIgnoreCase);
    public bool TryGetLastPrice(string symbol, out decimal price) => _lastPrices.TryGetValue(symbol, out price);

    public BinanceWebSocketClient(BinanceConfig config, ILogger<BinanceWebSocketClient> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ConnectAsync(IEnumerable<string> symbols, string interval, CancellationToken ct = default)
    {
        var symbolList = symbols.ToList();
        if (symbolList.Count == 0)
            throw new ArgumentException("Symbols list cannot be empty", nameof(symbols));

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var streams = symbolList.SelectMany(s => new[]
        {
            $"{s.ToLowerInvariant()}@kline_{interval}",
            $"{s.ToLowerInvariant()}@depth20@1000ms"
        });

        var combinedStream = string.Join("/", streams);
        _streamUrl = $"{_config.MarketStreamBaseUrl}?streams={combinedStream}";

        ExchangeLog.WebSocket("подключение",
            $"{symbolList.Count * 2} стримов, окружение {_config.ResolvedEnvironment}, url {_streamUrl}");
        _logger.LogInformation("Подключение к WebSocket. Стримов: {Count}", symbolList.Count * 2);

        await ConnectInternalAsync(_cts.Token);
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
    }

    private async Task ConnectInternalAsync(CancellationToken ct)
    {
        _ws?.Dispose();
        _ws = new ClientWebSocket();
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        try
        {
            await _ws.ConnectAsync(new Uri(_streamUrl!), ct);
            ExchangeLog.WebSocket("подключен", _streamUrl);
            _logger.LogInformation("WebSocket подключен");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось подключиться к WebSocket {Url}", _streamUrl);
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        _logger.LogInformation("Отключение от WebSocket (получено сообщений: {Count}, ошибок парсинга: {Errors})",
            _messagesReceived, _parseErrors);

        // P0.5: user-data socket гасится вместе с рыночным.
        await DisconnectUserDataStreamAsync();

        _cts?.Cancel();

        if (_receiveTask != null)
        {
            try { await _receiveTask; }
            catch (OperationCanceledException) { /* ожидаемо */ }
            catch (Exception ex) { _logger.LogWarning(ex, "Ошибка завершения receive loop"); }
            _receiveTask = null;
        }

        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", closeCts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ошибка корректного закрытия WebSocket");
            }
        }

        _ws?.Dispose();
        _ws = null;
        _cts?.Dispose();
        _cts = null;

        _logger.LogInformation("WebSocket отключен");
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[1024 * 64];
        var reconnectDelayMs = 2000;

        while (!ct.IsCancellationRequested)
        {
            while (!ct.IsCancellationRequested && _ws?.State == WebSocketState.Open)
            {
                try
                {
                    var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        ExchangeLog.WebSocket("закрыт сервером", _ws.CloseStatusDescription);
                        _logger.LogWarning("WebSocket закрыт сервером: {Status}", _ws.CloseStatus);
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        using var ms = new MemoryStream();
                        ms.Write(buffer, 0, result.Count);
                        while (!result.EndOfMessage)
                        {
                            result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                            ms.Write(buffer, 0, result.Count);
                        }

                        var message = Encoding.UTF8.GetString(ms.ToArray());
                        ProcessMessage(message);
                        reconnectDelayMs = 2000;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка в WebSocket receive loop");
                    OnError?.Invoke(ex);
                    break;
                }
            }

            if (ct.IsCancellationRequested)
                break;

            // P1.4: соединение потеряно — рыночные данные больше нельзя считать свежими.
            // Событие до задержки переподключения, чтобы движок сразу заблокировал входы.
            try { OnDisconnected?.Invoke(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Ошибка обработчика OnDisconnected"); }

            ExchangeLog.WebSocket("переподключение", $"через {reconnectDelayMs} мс");
            _logger.LogWarning("Переподключение через {Delay} мс...", reconnectDelayMs);

            try
            {
                await Task.Delay(reconnectDelayMs, ct);
                await ConnectInternalAsync(ct);
                reconnectDelayMs = Math.Min(reconnectDelayMs * 2, 60_000);

                // P1.4: сокет снова открыт, но история могла потерять свечи/стакан.
                // Движок обязан выполнить REST-дозагрузку и повторный прогрев до разрешения входов.
                try { OnReconnected?.Invoke(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Ошибка обработчика OnReconnected"); }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка переподключения");
                OnError?.Invoke(ex);
                reconnectDelayMs = Math.Min(reconnectDelayMs * 2, 60_000);
            }
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Комбинированный стрим: {"stream":"btcusdt@kline_15m","data":{...}}
            if (!root.TryGetProperty("stream", out var streamProp))
            {
                // Ответ на подписку {"result":null,"id":1} — игнорируем.
                if (root.TryGetProperty("error", out var error))
                    ExchangeLog.WebSocket("ошибка от сервера", error.GetRawText());
                return;
            }

            var stream = streamProp.GetString();
            if (stream is null) return;

            if (!root.TryGetProperty("data", out var data))
                return;

            _messagesReceived++;

            if (stream.Contains("@kline_", StringComparison.Ordinal))
            {
                var kline = JsonSerializer.Deserialize<BinanceKlineStream>(data.GetRawText());
                if (kline?.Kline is null || string.IsNullOrEmpty(kline.Kline.Symbol))
                {
                    _parseErrors++;
                    ExchangeLog.WebSocket("пустой kline payload", stream);
                    return;
                }

                ProcessKline(kline.Kline);
            }
            else if (stream.Contains("@depth", StringComparison.Ordinal))
            {
                // В partial-book stream нет поля "s" — символ берём из имени стрима.
                var symbol = stream.Split('@')[0].ToUpperInvariant();
                var depth = JsonSerializer.Deserialize<BinancePartialDepthStream>(data.GetRawText());
                if (depth is null)
                {
                    _parseErrors++;
                    return;
                }

                ProcessDepth(depth, symbol);
            }
        }
        catch (Exception ex)
        {
            _parseErrors++;
            ExchangeLog.WebSocketParseError(json, ex);
            _logger.LogError(ex, "Ошибка парсинга WebSocket сообщения");
        }
    }

    private void ProcessKline(BinanceKlineData kline)
    {
        var ohlcv = new OHLCV
        {
            Symbol = kline.Symbol.ToUpperInvariant(),
            OpenTime = DateTimeExtensions.FromUnixMilliseconds(kline.OpenTime),
            CloseTime = DateTimeExtensions.FromUnixMilliseconds(kline.CloseTime),
            Open = ParseDecimal(kline.Open),
            High = ParseDecimal(kline.High),
            Low = ParseDecimal(kline.Low),
            Close = ParseDecimal(kline.Close),
            Volume = ParseDecimal(kline.Volume),
            QuoteVolume = ParseDecimal(kline.QuoteVolume),
            TradesCount = kline.TradesCount
        };

        if (ohlcv.Close <= 0)
        {
            _parseErrors++;
            _logger.LogWarning("Некорректная свеча {Symbol}: close={Close}", ohlcv.Symbol, kline.Close);
            return;
        }

        // Флаг закрытия берём из payload ("x"), а не только из системных часов:
        // при рассинхроне часов незакрытая свеча могла попасть в индикаторы.
        if (kline.IsClosed && ohlcv.CloseTime > DateTime.UtcNow)
            ohlcv.CloseTime = DateTime.UtcNow.AddMilliseconds(-1);

        _lastPrices[ohlcv.Symbol] = ohlcv.Close;
        OnKline?.Invoke(ohlcv);
        OnTrade?.Invoke(ohlcv.Symbol, ohlcv.Close, ohlcv.Volume, ohlcv.CloseTime);
    }

    private void ProcessDepth(BinancePartialDepthStream depth, string symbol)
    {
        var orderBook = new OrderBook
        {
            Symbol = symbol,
            Timestamp = DateTime.UtcNow,
            LastUpdateId = depth.LastUpdateId,
            Bids = ParseLevels(depth.Bids),
            Asks = ParseLevels(depth.Asks)
        };

        if (orderBook.Bids.Count == 0 || orderBook.Asks.Count == 0)
        {
            _logger.LogDebug("Пустой стакан {Symbol} (bids={Bids}, asks={Asks})",
                symbol, orderBook.Bids.Count, orderBook.Asks.Count);
            return;
        }

        OnOrderBook?.Invoke(orderBook);
    }

    private static List<OrderBookEntry> ParseLevels(List<List<string>> levels)
    {
        var result = new List<OrderBookEntry>(levels.Count);

        foreach (var level in levels)
        {
            if (level.Count < 2) continue;

            var price = ParseDecimal(level[0]);
            var quantity = ParseDecimal(level[1]);

            // Нулевые уровни в partial-book означают удаление — пропускаем.
            if (price <= 0 || quantity <= 0) continue;

            result.Add(new OrderBookEntry { Price = price, Quantity = quantity });
        }

        return result;
    }

    private static decimal ParseDecimal(string? text) =>
        decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0m;

    // ===== P0.5: user-data stream (executionReport) =====

    public async Task ConnectUserDataStreamAsync(string listenKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(listenKey))
            throw new ArgumentException("listenKey cannot be empty", nameof(listenKey));

        await DisconnectUserDataStreamAsync();

        _userCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _userStreamUrl = $"{_config.WebSocketBaseUrl}/{listenKey}";

        await ConnectUserInternalAsync(_userCts.Token);
        _userReceiveTask = Task.Run(() => UserReceiveLoopAsync(_userCts.Token), _userCts.Token);
        ExchangeLog.WebSocket("user-data подключен", _config.ResolvedEnvironment.ToString());
        _logger.LogInformation("User-data stream подключен (окружение {Env})", _config.ResolvedEnvironment);
    }

    private async Task ConnectUserInternalAsync(CancellationToken ct)
    {
        _userWs?.Dispose();
        _userWs = new ClientWebSocket();
        _userWs.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        await _userWs.ConnectAsync(new Uri(_userStreamUrl!), ct);
    }

    public async Task DisconnectUserDataStreamAsync()
    {
        _userCts?.Cancel();

        if (_userReceiveTask != null)
        {
            try { await _userReceiveTask; }
            catch (OperationCanceledException) { /* ожидаемо */ }
            catch (Exception ex) { _logger.LogWarning(ex, "Ошибка завершения user-data receive loop"); }
            _userReceiveTask = null;
        }

        if (_userWs?.State == WebSocketState.Open)
        {
            try
            {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _userWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", closeCts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ошибка корректного закрытия user-data WebSocket");
            }
        }

        _userWs?.Dispose();
        _userWs = null;
        _userCts?.Dispose();
        _userCts = null;
    }

    private async Task UserReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[1024 * 64];
        var reconnectDelayMs = 2000;

        while (!ct.IsCancellationRequested)
        {
            while (!ct.IsCancellationRequested && _userWs?.State == WebSocketState.Open)
            {
                try
                {
                    var result = await _userWs.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogWarning("User-data WebSocket закрыт сервером: {Status}", _userWs.CloseStatus);
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Text)
                    {
                        using var ms = new MemoryStream();
                        ms.Write(buffer, 0, result.Count);
                        while (!result.EndOfMessage)
                        {
                            result = await _userWs.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                            ms.Write(buffer, 0, result.Count);
                        }

                        ProcessUserMessage(Encoding.UTF8.GetString(ms.ToArray()));
                        reconnectDelayMs = 2000;
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Ошибка в user-data receive loop");
                    OnError?.Invoke(ex);
                    break;
                }
            }

            if (ct.IsCancellationRequested) break;

            _logger.LogWarning("User-data WebSocket: переподключение через {Delay} мс", reconnectDelayMs);
            try
            {
                await Task.Delay(reconnectDelayMs, ct);
                await ConnectUserInternalAsync(ct);
                reconnectDelayMs = Math.Min(reconnectDelayMs * 2, 60_000);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Просроченный listenKey сюда же: пересоздание ключа — остаток P0.5,
                // пока вызывающий (ReconciliationService) видит ошибку и может перезапустить контур.
                _logger.LogError(ex, "Ошибка переподключения user-data WebSocket");
                OnError?.Invoke(ex);
                reconnectDelayMs = Math.Min(reconnectDelayMs * 2, 60_000);
            }
        }
    }

    private void ProcessUserMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("e", out var eventType)) return;

            //executionReport — единственный обрабатываемый тип в фундаменте P0.5;
            //outboundAccountPosition/balanceUpdate придут с полной реконсиляцией.
            if (eventType.GetString() != "executionReport") return;

            var report = JsonSerializer.Deserialize<BinanceExecutionReportStream>(root.GetRawText());
            if (report is null || string.IsNullOrEmpty(report.Symbol))
            {
                _parseErrors++;
                return;
            }

            OnExecutionReport?.Invoke(report.ToExecutionReport());
        }
        catch (Exception ex)
        {
            _parseErrors++;
            ExchangeLog.WebSocketParseError(json, ex);
            _logger.LogError(ex, "Ошибка парсинга user-data сообщения");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
