namespace AITradingSystem.Core.Models.MarketData;

/// <summary>
/// P0.3a: тип рыночного стрима. kline и partial-depth диагностируются раздельно:
/// символ нельзя объявлять живым по одному типу сообщений, если вход требует оба.
/// </summary>
public enum MarketStreamKind
{
    /// <summary>Комбинированный стрим свечей: {symbol}@kline_{interval}.</summary>
    Kline,

    /// <summary>Partial book depth: {symbol}@depth20@1000ms.</summary>
    Depth
}

/// <summary>
/// P0.3a: снимок per-stream диагностики одного рыночного стрима.
/// Собирается в WebSocket-клиенте и позволяет отличить факты, а не догадки:
/// «kline молчит, стакан идёт», «стакан молчит, kline идёт», «подписка отправлена, но сервер не подтвердил»,
/// «стрим отвечает, но payload пустой», «сообщений не было ни разу с момента подписки».
/// Счётчики монотонны в пределах процесса и не сбрасываются реконнектом.
/// </summary>
/// <param name="Symbol">Символ в верхнем регистре (для depth берётся из имени стрима).</param>
/// <param name="Stream">Полное имя стрима, как оно приходит в поле "stream".</param>
/// <param name="Kind">Тип стрима: kline или depth.</param>
/// <param name="MessageCount">Сколько payload-сообщений принято по этому стриму.</param>
/// <param name="EmptyPayloadCount">Сколько сообщений принято, но payload пустой/непригодный (пустой kline, пустой стакан).</param>
/// <param name="ParseErrorCount">Сколько сообщений по этому стриму не удалось разобрать.</param>
/// <param name="FirstMessageUtc">Время первого принятого сообщения (null — сообщений не было).</param>
/// <param name="LastMessageUtc">Время последнего принятого сообщения (null — сообщений не было).</param>
/// <param name="LastMessageAgeSeconds">Возраст последнего сообщения относительно момента снимка (null — сообщений не было).</param>
/// <param name="Subscribed">Считается ли стрим подписанным: true после подписки через URL или после ack SUBSCRIBE,
/// false после UNSUBSCRIBE/ошибки подписки.</param>
/// <param name="SubscribeRequestCount">Сколько SUBSCRIBE-запросов отправлено по этому стриму (без учёта подписки через URL).</param>
/// <param name="LastSubscribeRequestId">id последнего SUBSCRIBE-запроса (null — подписка только через URL).</param>
/// <param name="LastSubscribeRequestUtc">Время отправки последнего SUBSCRIBE-запроса.</param>
/// <param name="LastSubscribeAcked">Подтвердил ли сервер последний SUBSCRIBE-запрос.</param>
/// <param name="LastSubscribeError">Текст последней ошибки подписки от сервера (null — ошибок не было).</param>
public sealed record MarketStreamDiagnostics(
    string Symbol,
    string Stream,
    MarketStreamKind Kind,
    long MessageCount,
    long EmptyPayloadCount,
    long ParseErrorCount,
    DateTime? FirstMessageUtc,
    DateTime? LastMessageUtc,
    double? LastMessageAgeSeconds,
    bool Subscribed,
    int SubscribeRequestCount,
    int? LastSubscribeRequestId,
    DateTime? LastSubscribeRequestUtc,
    bool LastSubscribeAcked,
    string? LastSubscribeError)
{
    /// <summary>По стриму принят хотя бы один payload.</summary>
    public bool HasEverDelivered => MessageCount > 0;

    /// <summary>Стрим считается подписанным, но не доставил ни одного сообщения.</summary>
    public bool IsSilentSinceSubscribe => Subscribed && MessageCount == 0;

    /// <summary>Подписка отправлена control-фреймом, но ответ сервера ещё не получен.</summary>
    public bool IsSubscribePending =>
        LastSubscribeRequestId is not null && !LastSubscribeAcked && LastSubscribeError is null;
}
