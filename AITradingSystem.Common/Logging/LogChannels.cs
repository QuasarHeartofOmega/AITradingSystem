using System.Globalization;
using System.Text.RegularExpressions;
using Serilog;

namespace AITradingSystem.Common.Logging;

/// <summary>
/// Имена логических каналов логирования. По ним Serilog раскладывает события
/// в отдельные файлы (ai-*.log, exchange-*.log, news-*.log, trades-*.log).
/// </summary>
public static class LogChannel
{
    public const string Ai = "ai";
    public const string Exchange = "exchange";
    public const string News = "news";
    public const string Trade = "trade";
    public const string System = "system";
}

/// <summary>
/// Настройки детализации логов. Заполняются из <see cref="Configuration.LoggingConfig"/>
/// при инициализации логгера и читаются статическими каналами ниже.
/// </summary>
public static class LogOptions
{
    /// <summary>Логировать полный текст промпта, уходящего в модель.</summary>
    public static bool LogPrompts { get; set; } = true;

    /// <summary>Логировать сырой ответ модели (до парсинга).</summary>
    public static bool LogAiResponses { get; set; } = true;

    /// <summary>Логировать блок рассуждений модели (thinking).</summary>
    public static bool LogThinking { get; set; } = true;

    /// <summary>Логировать HTTP-запросы/ответы к бирже и новостным API.</summary>
    public static bool LogHttpTraffic { get; set; } = true;

    /// <summary>Логировать тела HTTP-ответов (обрезается по MaxTextLength).</summary>
    public static bool LogHttpBodies { get; set; } = true;

    /// <summary>Логировать новостной трафик и итоговый контекст для модели.</summary>
    public static bool LogNews { get; set; } = true;

    /// <summary>Максимальная длина одного текстового блока в логе (0 = без обрезки).</summary>
    public static int MaxTextLength { get; set; } = 20000;

    /// <summary>Маскировать ключи/подписи/токены в URL и телах.</summary>
    public static bool MaskSecrets { get; set; } = true;

    /// <summary>Обрезка длинных текстов для лога.</summary>
    public static string Clip(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (MaxTextLength <= 0 || text.Length <= MaxTextLength) return text;
        return string.Concat(text.AsSpan(0, MaxTextLength),
            $"\n... [обрезано {text.Length - MaxTextLength} символов]");
    }

    /// <summary>Маскирует секреты в query-строке/теле запроса.</summary>
    public static string Mask(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (!MaskSecrets) return text;

        var masked = Regex.Replace(text,
            @"(signature|auth_token|token|api_key|apiKey|X-MBX-APIKEY)([""'\s:=]+)([A-Za-z0-9_\-\.]+)",
            m => $"{m.Groups[1].Value}{m.Groups[2].Value}***",
            RegexOptions.IgnoreCase);

        return masked;
    }
}

/// <summary>
/// Лог AI-пайплайна: входящий промпт, сырой ответ, рассуждения, распарсенный сигнал.
/// Пишет в общий лог и в отдельный файл logs/ai-YYYYMMDD.log.
/// </summary>
public static class AiLog
{
    private static Serilog.ILogger Ch => Serilog.Log.ForContext("Channel", LogChannel.Ai);

    public static void Prompt(string symbol, int attempt, int maxAttempts, string prompt, string? meta = null)
    {
        if (!LogOptions.LogPrompts) return;

        Ch.Information(
            "AI PROMPT → {Symbol} (попытка {Attempt}/{Max}, {Chars} символов){Meta}\n{Prompt}",
            symbol, attempt, maxAttempts, prompt.Length,
            meta is null ? string.Empty : $" [{meta}]",
            LogOptions.Clip(prompt));
    }

    public static void Response(string symbol, int attempt, string rawResponse, double elapsedMs)
    {
        if (!LogOptions.LogAiResponses) return;

        Ch.Information(
            "AI RESPONSE ← {Symbol} (попытка {Attempt}, {Elapsed:F0} мс, {Chars} символов)\n{Response}",
            symbol, attempt, elapsedMs, rawResponse.Length, LogOptions.Clip(rawResponse));
    }

    public static void Thinking(string symbol, string thinking, double elapsedMs)
    {
        if (!LogOptions.LogThinking || string.IsNullOrWhiteSpace(thinking)) return;

        Ch.Debug("AI THINKING {Symbol} ({Elapsed:F0} мс)\n{Thinking}",
            symbol, elapsedMs, LogOptions.Clip(thinking));
    }

    public static void Parsed(string symbol, string action, decimal confidence, string json, double elapsedMs)
    {
        Ch.Information(
            "AI ПРЕДЛОЖЕНИЕ {Symbol}: {Action} conf={Confidence:F2} ({Elapsed:F0} мс) — мнение модели, ещё не ордер. {Json}",
            symbol, action, confidence, elapsedMs, LogOptions.Clip(json));
    }

    public static void ParseFailed(string symbol, int attempt, string reason, string? rawResponse)
    {
        Ch.Warning("AI PARSE FAIL {Symbol} (попытка {Attempt}): {Reason}\nСырой ответ: {Response}",
            symbol, attempt, reason, LogOptions.Clip(LogOptions.Mask(rawResponse)));
    }

    public static void Retry(string symbol, int attempt, string reason)
    {
        Ch.Warning("AI RETRY {Symbol}: попытка {Attempt}, причина: {Reason}", symbol, attempt, reason);
    }

    public static void Backend(string message)
    {
        Ch.Information("AI BACKEND: {Message}", message);
    }

    public static void Failure(string symbol, Exception ex, string context)
    {
        Ch.Error(ex, "AI ERROR {Symbol}: {Context}", symbol, context);
    }
}

/// <summary>
/// Лог обмена с биржей: каждый REST-запрос/ответ, ошибки API, ордера, WebSocket.
/// Отдельный файл logs/exchange-YYYYMMDD.log.
/// </summary>
public static class ExchangeLog
{
    private static Serilog.ILogger Ch => Serilog.Log.ForContext("Channel", LogChannel.Exchange);

    public static void Request(string method, string url, string? body = null)
    {
        if (!LogOptions.LogHttpTraffic) return;

        Ch.Debug("HTTP → {Method} {Url}{Body}", method, LogOptions.Mask(url),
            string.IsNullOrEmpty(body) ? string.Empty : " body=" + LogOptions.Clip(LogOptions.Mask(body)));
    }

    public static void Response(string method, string url, int statusCode, double elapsedMs, string? body)
    {
        if (!LogOptions.LogHttpTraffic) return;

        Ch.Debug("HTTP ← {Status} {Method} {Url} ({Elapsed:F0} мс){Body}",
            statusCode, method, LogOptions.Mask(url), elapsedMs,
            LogOptions.LogHttpBodies && !string.IsNullOrEmpty(body)
                ? "\n" + LogOptions.Clip(LogOptions.Mask(body))
                : string.Empty);
    }

    public static void ApiError(string method, string url, int statusCode, string body, int? apiCode, string? apiMessage)
    {
        Ch.Error("BINANCE API ERROR {Status} {Method} {Url} code={Code} msg={Msg}\n{Body}",
            statusCode, method, LogOptions.Mask(url), apiCode, apiMessage,
            LogOptions.Clip(LogOptions.Mask(body)));
    }

    public static void Retry(string url, int attempt, int maxAttempts, string reason)
    {
        Ch.Warning("HTTP RETRY {Attempt}/{Max} {Url}: {Reason}", attempt, maxAttempts, LogOptions.Mask(url), reason);
    }

    public static void Order(string stage, string symbol, string side, decimal quantity, decimal price, string? details = null)
    {
        Serilog.Log.ForContext("Channel", LogChannel.Trade)
            .Information("ORDER {Stage}: {Symbol} {Side} qty={Quantity} price={Price} {Details}",
                stage, symbol, side, quantity, price, details ?? string.Empty);
    }

    public static void WebSocket(string message, string? details = null)
    {
        Ch.Information("WS: {Message} {Details}", message, details ?? string.Empty);
    }

    public static void WebSocketParseError(string rawMessage, Exception ex)
    {
        Ch.Error(ex, "WS PARSE ERROR\n{Raw}", LogOptions.Clip(rawMessage));
    }

    public static void Environment(string environment, string restUrl, string streamUrl)
    {
        Ch.Information("Binance environment: {Environment} | REST {Rest} | STREAM {Stream}",
            environment, restUrl, streamUrl);
    }
}

/// <summary>
/// Лог новостных провайдеров: запросы, количество, итоговый контекст для модели.
/// Отдельный файл logs/news-YYYYMMDD.log.
/// </summary>
public static class NewsLog
{
    private static Serilog.ILogger Ch => Serilog.Log.ForContext("Channel", LogChannel.News);

    public static void Request(string provider, string url)
    {
        if (!LogOptions.LogNews) return;
        Ch.Debug("NEWS → {Provider} {Url}", provider, LogOptions.Mask(url));
    }

    public static void Response(string provider, int statusCode, int count, double elapsedMs, string? sample = null)
    {
        if (!LogOptions.LogNews) return;
        Ch.Information("NEWS ← {Provider} status={Status} items={Count} ({Elapsed:F0} мс){Sample}",
            provider, statusCode, count, elapsedMs,
            string.IsNullOrEmpty(sample) ? string.Empty : "\n" + LogOptions.Clip(sample));
    }

    public static void Skipped(string provider, string reason)
    {
        if (!LogOptions.LogNews) return;
        Ch.Debug("NEWS SKIP {Provider}: {Reason}", provider, reason);
    }

    public static void Failure(string provider, Exception ex, string context)
    {
        Ch.Warning(ex, "NEWS ERROR {Provider}: {Context}", provider, context);
    }

    public static void Context(string symbol, int count, string context)
    {
        if (!LogOptions.LogNews) return;
        Ch.Debug("NEWS CONTEXT {Symbol} ({Count} новостей)\n{Context}", symbol, count, LogOptions.Clip(context));
    }
}

/// <summary>
/// Лог торговых решений и сделок: открытие/закрытие/отклонение с причинами.
/// Отдельный файл logs/trades-YYYYMMDD.log.
/// </summary>
public static class TradeLog
{
    private static Serilog.ILogger Ch => Serilog.Log.ForContext("Channel", LogChannel.Trade);

    public static void Decision(string symbol, string decision, string reason)
    {
        Ch.Information("DECISION {Symbol}: {Decision} — {Reason}", symbol, decision, reason);
    }

    /// <summary>Единый финал решения по символу: что бот РЕАЛЬНО сделал (или почему не сделал).</summary>
    public static void Outcome(string symbol, string verdict, string reason)
    {
        Ch.Information("ИТОГ {Symbol}: {Verdict} — {Reason}", symbol, verdict, reason);
    }

    public static void Opened(string symbol, string side, decimal quantity, decimal entry,
        decimal stopLoss, decimal takeProfit, string mode, string? orderId = null)
    {
        var verdict = string.Equals(side, "Buy", StringComparison.OrdinalIgnoreCase) ? "КУПЛЕНО" : "ПРОДАНО";
        Ch.Information(
            "ИТОГ [{Mode}] {Symbol}: {Verdict} qty={Quantity} entry={Entry} SL={SL} TP={TP} order={Order}",
            mode, symbol, verdict, quantity, entry, stopLoss, takeProfit, orderId ?? "-");
    }

    public static void Closed(string symbol, string reason, decimal quantity, decimal entry, decimal exit,
        decimal pnl, decimal pnlPercent, decimal commission, string mode)
    {
        Ch.Information(
            "ИТОГ [{Mode}] {Symbol}: ЗАКРЫТО ({Reason}) qty={Quantity} entry={Entry} exit={Exit} PnL={PnL} ({PnLPct}%) fee={Fee}",
            mode, symbol, reason, quantity, entry, exit,
            pnl.ToString("F4", CultureInfo.InvariantCulture),
            pnlPercent.ToString("F2", CultureInfo.InvariantCulture),
            commission.ToString("F4", CultureInfo.InvariantCulture));
    }

    /// <summary>Ожидание (прогрев индикаторов, cooldown) — чтобы «бот ничего не делает» было видно в логе.</summary>
    public static void Waiting(string symbol, string reason)
    {
        Ch.Information("ИТОГ {Symbol}: ПАУЗА — {Reason}", symbol, reason);
    }

    public static void Rejected(string symbol, string stage, string reason)
    {
        Ch.Warning("ИТОГ {Symbol}: ОТКАЗ [{Stage}] — {Reason}", symbol, stage, reason);
    }

    public static void Balance(string mode, decimal cash, decimal equity, decimal drawdownPercent)
    {
        Ch.Information("BALANCE [{Mode}] cash={Cash} equity={Equity} drawdown={Drawdown}% ",
            mode, cash, equity, drawdownPercent.ToString("F2", CultureInfo.InvariantCulture));
    }

    /// <summary>P0.1: машинно-читаемый срез капитала для risk-аудита.</summary>
    public static void RiskSnapshot(string environment, string accountId, string quoteAsset,
        decimal equity, decimal free, decimal locked, decimal peak, decimal dayStart,
        decimal drawdownPercent, decimal dailyPnl, string source, bool valuationComplete)
    {
        Ch.Information(
            "RISK-SNAPSHOT env={Env} account={Account} quote={Quote} equity={Equity} free={Free} locked={Locked} peak={Peak} day_start={DayStart} drawdown={Drawdown} daily_pnl={DailyPnl} source={Source} valuation_complete={Complete}",
            environment, accountId, quoteAsset, equity, free, locked, peak, dayStart,
            drawdownPercent.ToString("F4", CultureInfo.InvariantCulture), dailyPnl, source, valuationComplete);
    }

    /// <summary>P0.2: аудит kill-switch.</summary>
    public static void KillSwitchEngaged(string reason, decimal equityAtEngage, bool requiresManualReset)
    {
        Ch.Warning("KILL-SWITCH-ENGAGED reason={Reason} equity_at_engage={Equity} requires_manual_reset={Manual}",
            reason, equityAtEngage, requiresManualReset);
    }

    public static void KillSwitchCloseResult(int closed, int total, int remaining, string status)
    {
        Ch.Warning("KILL-SWITCH-CLOSE-RESULT closed={Closed} total={Total} remaining={Remaining} status={Status}",
            closed, total, remaining, status);
    }

    public static void KillSwitchReset(string operatorReason)
    {
        Ch.Warning("KILL-SWITCH-RESET operator_reason={Reason}", operatorReason);
    }

    public static void KillSwitchResetDenied(string reason)
    {
        Ch.Warning("KILL-SWITCH-RESET-DENIED reason={Reason}", reason);
    }

    public static void KillSwitchRestored(string reason, string closeStatus)
    {
        Ch.Warning("KILL-SWITCH-RESTORED reason={Reason} close_status={Status}", reason, closeStatus);
    }

    /// <summary>P0.3: аудит здоровья рыночных данных — REST-прогрев и живой стрим пишутся раздельно.</summary>
    public static void DataStale(string symbol, string reason, double ageSeconds)
    {
        Ch.Warning("DATA-STALE {Symbol} reason={Reason} age_s={Age}",
            symbol, reason, ageSeconds.ToString("F1", CultureInfo.InvariantCulture));
    }

    public static void DataRestReload(string symbol, int closedCandles, double liveConfirmWindowSeconds)
    {
        Ch.Information("DATA-REST-RELOAD {Symbol} closed_candles={Count} live_confirm_window_s={Window} entries_allowed=false",
            symbol, closedCandles, liveConfirmWindowSeconds.ToString("F0", CultureInfo.InvariantCulture));
    }

    public static void DataWsResubscribe(string symbol, int attempt, int maxAttempts, string reason)
    {
        Ch.Warning("DATA-WS-RESUBSCRIBE {Symbol} attempt={Attempt}/{Max} reason={Reason}",
            symbol, attempt, maxAttempts, reason);
    }

    public static void DataWsReconnect(string symbol, string reason)
    {
        Ch.Warning("DATA-WS-RECONNECT {Symbol} reason={Reason}", symbol, reason);
    }

    public static void DataLiveConfirmed(string symbol, double secondsToLive, int resubscribeAttempts)
    {
        Ch.Information("DATA-LIVE-CONFIRMED {Symbol} time_to_live_s={Ttl} resubscribes={Attempts} entries_allowed_after_fresh_book=true",
            symbol, secondsToLive.ToString("F1", CultureInfo.InvariantCulture), resubscribeAttempts);
    }

    public static void DataQuarantine(string symbol, string reason, double durationSeconds)
    {
        Ch.Warning("DATA-QUARANTINE {Symbol} reason={Reason} duration_s={Duration} entries_allowed=false",
            symbol, reason, durationSeconds.ToString("F0", CultureInfo.InvariantCulture));
    }

    public static void DataQuarantineLifted(string symbol, string reason)
    {
        Ch.Information("DATA-QUARANTINE-LIFTED {Symbol} previous_reason={Reason} entries_allowed=false",
            symbol, reason);
    }

    /// <summary>P0.4: поддержка user-data stream в конкретном окружении и режим исполнения.</summary>
    public static void UserDataProbe(string environment, bool supported, int? httpStatus, int? apiCode, string detail)
    {
        Ch.Warning("USER-DATA-PROBE env={Env} supported={Supported} http={Http} code={Code} detail={Detail}",
            environment, supported, httpStatus?.ToString() ?? "-", apiCode?.ToString() ?? "-", detail);
    }

    public static void ExecutionMode(string mode, string reason, int pollingIntervalSeconds)
    {
        Ch.Warning("EXECUTION-MODE={Mode} reason={Reason} polling_interval_s={Interval}",
            mode, reason, pollingIntervalSeconds);
    }

    /// <summary>P0.4: журнал реконсиляции — старт, итог, отказ, подхват, снятие, применённый филл защиты.</summary>
    public static void ReconStart(string trigger, int positions)
    {
        Ch.Information("RECON-START trigger={Trigger} positions={Positions}", trigger, positions);
    }

    public static void ReconSummary(int positions, int orphanOco, int orphanOrders, int externalOrders,
        int protectionMissing, int unresolved, bool entriesAllowed)
    {
        Ch.Information(
            "RECON-SUMMARY positions={Positions} orphan_oco={OrphanOco} orphan_orders={OrphanOrders} external_orders={External} protection_missing={Missing} unresolved={Unresolved} entries_allowed={Allowed}",
            positions, orphanOco, orphanOrders, externalOrders, protectionMissing, unresolved, entriesAllowed);
    }

    public static void ReconFail(string trigger, string reason)
    {
        Ch.Warning("RECON-FAIL trigger={Trigger} reason={Reason} entries_allowed=false", trigger, reason);
    }

    public static void ReconAdopt(string symbol, decimal quantity, decimal price, string clientOrderId)
    {
        Ch.Warning("RECON-ADOPT {Symbol} qty={Qty} price={Price} client_order_id={ClientId}",
            symbol, quantity, price, clientOrderId);
    }

    public static void ReconCancel(string symbol, string kind, string identifier)
    {
        Ch.Warning("RECON-CANCEL {Symbol} kind={Kind} id={Id}", symbol, kind, identifier);
    }

    public static void ProtectionFillApplied(string symbol, string reason, decimal cumulativeQuantity, string source)
    {
        Ch.Warning("PROTECTION-FILL-APPLIED {Symbol} reason={Reason} cum_qty={CumQty} source={Source}",
            symbol, reason, cumulativeQuantity, source);
    }

    public static void ExecutionStateBlocked(string reason)
    {
        Ch.Warning("EXECUTION-STATE-BLOCKED reason={Reason} entries_allowed=false", reason);
    }
}
