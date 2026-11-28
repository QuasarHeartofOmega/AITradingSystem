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
    private string _marketInterval = "15m";
    private CancellationToken _marketConnectToken;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    // P0.3a шаг 3: single-flight принудительного реконнекта рыночного сокета на весь процесс.
    private readonly SemaphoreSlim _marketReconnectLock = new(1, 1);
    private int _subscribeRequestId;
    private long _messagesReceived;
    private long _parseErrors;

    // P0.3a: per-stream диагностика. kline и depth одного символа — разные стримы и разные факты:
    // счётчики сообщений, возраст последнего payload, id/ack подписки. Без этого причину молчания
    // символа (ENJ/DASH) приходится угадывать, а эскалация recovery не объяснима в логе.
    private readonly ConcurrentDictionary<string, StreamStats> _streamStats = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ControlRequest> _pendingControlRequests = new();

    private sealed class StreamStats
    {
        public string Stream { get; init; } = string.Empty;
        public string Symbol { get; init; } = string.Empty;
        public MarketStreamKind Kind { get; init; }
        public long MessageCount;
        public long EmptyPayloadCount;
        public long ParseErrorCount;
        public DateTime? FirstMessageUtc;
        public DateTime? LastMessageUtc;
        public bool Subscribed;
        public int SubscribeRequestCount;
        public int? LastSubscribeRequestId;
        public DateTime? LastSubscribeRequestUtc;
        public bool LastSubscribeAcked;
        public string? LastSubscribeError;
    }

    private sealed record ControlRequest(string Method, string[] Streams, DateTime SentUtc);

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
        _marketConnectToken = ct;
        _marketInterval = interval;

        var streams = BuildStreams(symbolList, interval).ToArray();

        var combinedStream = string.Join("/", streams);
        _streamUrl = $"{_config.MarketStreamBaseUrl}?streams={combinedStream}";

        // P0.3a: стримы известны до подключения — фиксируем их в диагностике как подписанные через URL.
        RegisterUrlStreams(streams);

        ExchangeLog.WebSocket("подключение",
            $"{symbolList.Count * 2} стримов, окружение {_config.ResolvedEnvironment}, url {_streamUrl}");

        await ConnectInternalAsync(_cts.Token);
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);
    }

    private static IEnumerable<string> BuildStreams(IEnumerable<string> symbols, string interval) =>
        symbols.SelectMany(s => new[]
        {
            $"{s.ToLowerInvariant()}@kline_{interval}",
            $"{s.ToLowerInvariant()}@depth20@1000ms"
        });

    /// <summary>P0.3a: стримы, подписанные через URL комбинированного стрима (сохраняются для реконнекта).</summary>
    private string[] _marketStreams = Array.Empty<string>();

    /// <summary>
    /// P0.3a: подписка через URL не подтверждается сервером отдельным ack — помечаем стрим подписанным сразу,
    /// id/ack подписки остаются пустыми, в диагностике это видно как источник "url".
    /// </summary>
    private void RegisterUrlStreams(string[] streams)
    {
        _marketStreams = streams;
        foreach (var stream in streams)
            GetOrCreateStreamStats(stream).Subscribed = true;
    }

    private static (string Symbol, MarketStreamKind Kind)? ParseStreamName(string stream)
    {
        var at = stream.IndexOf('@');
        if (at <= 0 || at == stream.Length - 1)
            return null;

        var symbol = stream[..at].ToUpperInvariant();
        if (stream.Contains("@kline_", StringComparison.Ordinal))
            return (symbol, MarketStreamKind.Kline);
        if (stream.Contains("@depth", StringComparison.Ordinal))
            return (symbol, MarketStreamKind.Depth);
        return null;
    }

    private StreamStats GetOrCreateStreamStats(string stream) =>
        _streamStats.GetOrAdd(stream, s =>
        {
            var parsed = ParseStreamName(s);
            return new StreamStats
            {
                Stream = s,
                Symbol = parsed?.Symbol ?? s,
                Kind = parsed?.Kind ?? MarketStreamKind.Kline
            };
        });

    /// <summary>P0.3a: учёт принятого payload по конкретному стриму (kline и depth — раздельно).</summary>
    private StreamStats NoteStreamMessage(string stream, DateTime nowUtc)
    {
        var stats = GetOrCreateStreamStats(stream);
        Interlocked.Increment(ref stats.MessageCount);
        stats.LastMessageUtc = nowUtc;
        stats.FirstMessageUtc ??= nowUtc;
        return stats;
    }

    /// <summary>
    /// P0.3a: снимок per-stream диагностики на момент nowUtc. Счётчики монотонны в пределах процесса;
    /// реконнект их не обнуляет, поэтому по снимку видно, молчит стрим всегда или замолчал после реконнекта.
    /// </summary>
    public IReadOnlyList<MarketStreamDiagnostics> GetStreamDiagnostics(DateTime nowUtc)
    {
        var snapshot = new List<MarketStreamDiagnostics>(_streamStats.Count);
        foreach (var stats in _streamStats.Values)
        {
            var last = stats.LastMessageUtc;
            snapshot.Add(new MarketStreamDiagnostics(
                stats.Symbol,
                stats.Stream,
                stats.Kind,
                Interlocked.Read(ref stats.MessageCount),
                Interlocked.Read(ref stats.EmptyPayloadCount),
                Interlocked.Read(ref stats.ParseErrorCount),
                stats.FirstMessageUtc,
                last,
                last.HasValue ? Math.Max(0d, (nowUtc - last.Value).TotalSeconds) : null,
                stats.Subscribed,
                stats.SubscribeRequestCount,
                stats.LastSubscribeRequestId,
                stats.LastSubscribeRequestUtc,
                stats.LastSubscribeAcked,
                stats.LastSubscribeError));
        }

        return snapshot
            .OrderBy(d => d.Symbol, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Kind)
            .ThenBy(d => d.Stream, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// P0.3: UNSUBSCRIBE + SUBSCRIBE по молчащему символу. Соединение и остальные стримы не рвутся.
    /// Успех запроса не считается подтверждением live: это делает только реальный тик в MarketAnalyzer.
    /// </summary>
    public async Task ResubscribeAsync(IEnumerable<string> symbols, string interval, CancellationToken ct = default)
    {
        var streams = BuildStreams(symbols, interval).ToArray();
        if (streams.Length == 0)
            return;

        var ws = _ws;
        if (ws is not { State: WebSocketState.Open })
            throw new InvalidOperationException("Рыночный WebSocket не подключён — resubscribe невозможен.");

        await _sendLock.WaitAsync(ct);
        try
        {
            await SendControlFrameAsync(ws, "UNSUBSCRIBE", streams, ct);
            await SendControlFrameAsync(ws, "SUBSCRIBE", streams, ct);
        }
        finally
        {
            _sendLock.Release();
        }

        ExchangeLog.WebSocket("resubscribe", $"{string.Join("/", streams)}");
    }

    private async Task<int> SendControlFrameAsync(ClientWebSocket ws, string method, string[] streams, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _subscribeRequestId);
        var payload = JsonSerializer.Serialize(new
        {
            method,
            @params = streams,
            id
        });
        var bytes = Encoding.UTF8.GetBytes(payload);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);

        // P0.3a: id контрольного запроса сохраняется до ack — иначе в логе нельзя связать
        // SUBSCRIBE/UNSUBSCRIBE с ответом сервера и отличить «подписка не подтверждена» от «стрим молчит».
        var sentUtc = DateTime.UtcNow;
        _pendingControlRequests[id] = new ControlRequest(method, streams, sentUtc);

        var isSubscribe = string.Equals(method, "SUBSCRIBE", StringComparison.Ordinal);
        foreach (var stream in streams)
        {
            var stats = GetOrCreateStreamStats(stream);
            if (isSubscribe)
            {
                stats.SubscribeRequestCount++;
                stats.LastSubscribeRequestId = id;
                stats.LastSubscribeRequestUtc = sentUtc;
                stats.LastSubscribeAcked = false;
                stats.LastSubscribeError = null;
                // До ack сервера подписку нельзя считать действующей.
                stats.Subscribed = false;
            }
            else
            {
                stats.Subscribed = false;
            }
        }

        ExchangeLog.SubscribeControl(method, id, string.Join("/", streams));
        return id;
    }

    /// <summary>
    /// P0.3a: разбор ответа на SUBSCRIBE/UNSUBSCRIBE. Ранее ответ игнорировался, поэтому молчание стрима
    /// после неподтверждённой подписки выглядело как проблема биржевых данных.
    /// </summary>
    private void HandleControlResponse(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idProp) ||
            idProp.ValueKind != JsonValueKind.Number ||
            !idProp.TryGetInt32(out var requestId) ||
            !_pendingControlRequests.TryRemove(requestId, out var request))
        {
            // Ответ без известного id (или unsolicited error) — фиксируем, но не связываем с подпиской.
            if (root.TryGetProperty("error", out var orphanError) && orphanError.ValueKind != JsonValueKind.Null)
                ExchangeLog.WebSocket("ошибка от сервера", orphanError.GetRawText());
            return;
        }

        var latencyMs = (DateTime.UtcNow - request.SentUtc).TotalMilliseconds;
        var isSubscribe = string.Equals(request.Method, "SUBSCRIBE", StringComparison.Ordinal);
        var streams = string.Join("/", request.Streams);

        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
        {
            var text = error.GetRawText();
            foreach (var stream in request.Streams)
            {
                var stats = GetOrCreateStreamStats(stream);
                stats.LastSubscribeAcked = false;
                stats.LastSubscribeError = text;
                if (isSubscribe)
                    stats.Subscribed = false;
            }

            ExchangeLog.SubscribeError(request.Method, requestId, streams, text);
            return;
        }

        foreach (var stream in request.Streams)
        {
            var stats = GetOrCreateStreamStats(stream);
            stats.LastSubscribeAcked = true;
            stats.LastSubscribeError = null;
            // SUBSCRIBE подтверждён — стрим подписан; UNSUBSCRIBE подтверждён — подписки больше нет.
            stats.Subscribed = isSubscribe;
        }

        ExchangeLog.SubscribeAck(request.Method, requestId, streams, latencyMs);
    }

    /// <summary>
    /// P0.3: жёсткий реконнект рыночного сокета по инициативе движка; user-data socket остаётся жить.
    /// </summary>
    public async Task ReconnectMarketStreamsAsync(CancellationToken ct = default)
    {
        if (_streamUrl == null)
            throw new InvalidOperationException("Рыночный WebSocket ещё не подключался — реконнект невозможен.");

        // P0.3a шаг 3: параллельный запрос не запускает второй разрыв сокета — уже идущий
        // реконнект сам переподпишет все символы и уведомит OnReconnected.
        if (!await _marketReconnectLock.WaitAsync(0, ct))
        {
            ExchangeLog.WebSocket("реконнект пропущен", "другой принудительный реконнект уже выполняется (single-flight)");
            return;
        }

        try
        {
            try { OnDisconnected?.Invoke(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Ошибка обработчика OnDisconnected при принудительном реконнекте"); }

            _cts?.Cancel();
            if (_receiveTask != null)
            {
                try { await _receiveTask; }
                catch (OperationCanceledException) { /* ожидаемо */ }
                catch (Exception ex) { _logger.LogWarning(ex, "Ошибка завершения receive loop при реконнекте"); }
                _receiveTask = null;
            }

            if (_ws?.State == WebSocketState.Open)
            {
                try
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Market data reconnect", closeCts.Token);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Ошибка закрытия рыночного сокета при реконнекте"); }
            }

            _ws?.Dispose();
            _ws = null;
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(_marketConnectToken, ct);

            ExchangeLog.WebSocket("принудительный реконнект", _streamUrl);
            await ConnectInternalAsync(_cts.Token);
            _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token), _cts.Token);

            // P0.3a: после реконнекта подписка снова идёт через URL; контрольные запросы старого сокета
            // уже никогда не будут подтверждены — убираем их из ожидания, чтобы ack не «висел» в диагностике.
            _pendingControlRequests.Clear();
            RegisterUrlStreams(_marketStreams);

            try { OnReconnected?.Invoke(); }
            catch (Exception ex) { _logger.LogWarning(ex, "Ошибка обработчика OnReconnected при принудительном реконнекте"); }
        }
        finally
        {
            _marketReconnectLock.Release();
        }
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
        }
        catch (Exception ex)
        {
            ExchangeLog.Error("WS-CONNECT-FAIL", null, $"url={_streamUrl}", ex);
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        ExchangeLog.Info("WS-DISCONNECT", null, $"messages={_messagesReceived} parse_errors={_parseErrors}");

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
                        ExchangeLog.WebSocket("закрыт сервером", $"status={_ws.CloseStatus} {_ws.CloseStatusDescription}");
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
                    ExchangeLog.Error("WS-RECEIVE-FAIL", null, "ошибка цикла приёма рыночного стрима", ex);
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

            try
            {
                await Task.Delay(reconnectDelayMs, ct);
                await ConnectInternalAsync(ct);
                reconnectDelayMs = Math.Min(reconnectDelayMs * 2, 60_000);
                // P0.3a: контрольные запросы прежнего сокета уже не получат ack.
                _pendingControlRequests.Clear();

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
                ExchangeLog.Error("WS-RECONNECT-FAIL", null, "переподключение рыночного стрима не удалось", ex);
                OnError?.Invoke(ex);
                reconnectDelayMs = Math.Min(reconnectDelayMs * 2, 60_000);
            }
        }
    }

    private void ProcessMessage(string json)
    {
        string? streamName = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Комбинированный стрим: {"stream":"btcusdt@kline_15m","data":{...}}
            if (!root.TryGetProperty("stream", out var streamProp))
            {
                // P0.3a: ответ на SUBSCRIBE/UNSUBSCRIBE — фиксируем id/ack вместо молчаливого игнора.
                HandleControlResponse(root);
                return;
            }

            var stream = streamProp.GetString();
            if (stream is null) return;
            streamName = stream;

            if (!root.TryGetProperty("data", out var data))
                return;

            _messagesReceived++;
            var receivedAtUtc = DateTime.UtcNow;
            var stats = NoteStreamMessage(stream, receivedAtUtc);

            if (stream.Contains("@kline_", StringComparison.Ordinal))
            {
                var kline = JsonSerializer.Deserialize<BinanceKlineStream>(data.GetRawText());
                if (kline?.Kline is null || string.IsNullOrEmpty(kline.Kline.Symbol))
                {
                    _parseErrors++;
                    Interlocked.Increment(ref stats.EmptyPayloadCount);
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
                    Interlocked.Increment(ref stats.EmptyPayloadCount);
                    return;
                }

                if (!ProcessDepth(depth, symbol))
                    Interlocked.Increment(ref stats.EmptyPayloadCount);
            }
        }
        catch (Exception ex)
        {
            _parseErrors++;
            if (streamName is not null)
                Interlocked.Increment(ref GetOrCreateStreamStats(streamName).ParseErrorCount);
            ExchangeLog.WebSocketParseError(json, ex);
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
            ExchangeLog.Warn("WS-CANDLE-INVALID", ohlcv.Symbol, $"close={kline.Close}",
                gateKey: "ws-candle-invalid:" + ohlcv.Symbol);
            return;
        }

        // 1.6: флаг закрытия берём из payload ("x") и пробрасываем в OHLCV —
        // локальные часы участвуют только когда флага нет. CloseTime больше не
        // переписывается: преждевременные свечи отсекает IsClosed в индикаторном
        // контуре (UpdateIndicators/TryReloadHistory/OnKlineReceived).
        ohlcv.ClosedByExchange = kline.IsClosed;

        _lastPrices[ohlcv.Symbol] = ohlcv.Close;
        OnKline?.Invoke(ohlcv);
        OnTrade?.Invoke(ohlcv.Symbol, ohlcv.Close, ohlcv.Volume, ohlcv.CloseTime);
    }

    /// <summary>P0.3a: возвращает false, если стакан пришёл пустым — это факт для per-stream диагностики,
    /// а не «стрим молчит»: сообщения по depth-стриму идут, но usable-снаряжения не дают.</summary>
    private bool ProcessDepth(BinancePartialDepthStream depth, string symbol)
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
            return false;
        }

        OnOrderBook?.Invoke(orderBook);
        return true;
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
                        ExchangeLog.Warn("USER-WS-CLOSED", null, $"status={_userWs.CloseStatus}");
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
                    ExchangeLog.Error("USER-WS-RECEIVE-FAIL", null, "ошибка цикла приёма user-data стрима", ex);
                    OnError?.Invoke(ex);
                    break;
                }
            }

            if (ct.IsCancellationRequested) break;

            ExchangeLog.WebSocket("user-data переподключение", $"через {reconnectDelayMs} мс");
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
                ExchangeLog.Error("USER-WS-RECONNECT-FAIL", null, "переподключение user-data стрима не удалось", ex);
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
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
