using System.Globalization;
using System.Text.RegularExpressions;
using Serilog;
using Serilog.Events;

namespace AITradingSystem.Common.Logging;

/// <summary>
/// Логические каналы. По ним Serilog раскладывает события по файлам:
///   system    → trading-*.log   (жизненный цикл приложения, ошибки инфраструктуры)
///   trade     → trades-*.log    (решения, ордера, позиции, риск)
///   exchange  → exchange-*.log  (REST/WS биржи)
///   ai        → ai-*.log        (промпт, рассуждение, ответ, разобранное предложение)
///   ai-native → ai-native-*.log (нативный вывод llama.cpp/ggml — отдельно, чтобы не топил ai)
///   news      → news-*.log      (новостные провайдеры и контекст для модели)
/// </summary>
public static class LogChannel
{
    public const string Ai = "ai";
    public const string AiNative = "ai-native";
    public const string Exchange = "exchange";
    public const string News = "news";
    public const string Trade = "trade";
    public const string System = "system";
}

/// <summary>
/// Настройки детализации. Заполняются из <see cref="Configuration.LoggingConfig"/>
/// при инициализации логгера и читаются каналами ниже.
/// </summary>
public static class LogOptions
{
    /// <summary>Логировать полный текст промпта, уходящего в модель.</summary>
    public static bool LogPrompts { get; set; } = true;

    /// <summary>Логировать сырой ответ модели (до парсинга).</summary>
    public static bool LogAiResponses { get; set; } = true;

    /// <summary>Логировать блок рассуждений модели (thinking).</summary>
    public static bool LogThinking { get; set; } = true;

    /// <summary>Логировать нативный вывод llama.cpp/ggml в отдельный канал ai-native.</summary>
    public static bool LogNativeAiOutput { get; set; } = true;

    /// <summary>Логировать HTTP-запросы/ответы к бирже и новостным API.</summary>
    public static bool LogHttpTraffic { get; set; } = true;

    /// <summary>Логировать тела HTTP-ответов (совместимость: false = Off).</summary>
    public static bool LogHttpBodies { get; set; } = true;

    /// <summary>
    /// Что писать вместо тела HTTP-ответа: Summary (одна строка-выжимка, по умолчанию),
    /// Full (полное тело с обрезкой по MaxTextLength), Off (только размер).
    /// </summary>
    public static string HttpBodyMode { get; set; } = "Summary";

    /// <summary>Логировать новостной трафик и итоговый контекст для модели.</summary>
    public static bool LogNews { get; set; } = true;

    /// <summary>Максимальная длина одного текстового блока в логе (0 = без обрезки).</summary>
    public static int MaxTextLength { get; set; } = 20000;

    /// <summary>Маскировать ключи/подписи/токены в URL и телах.</summary>
    public static bool MaskSecrets { get; set; } = true;

    /// <summary>Режим тел HTTP с учётом старого флага LogHttpBodies.</summary>
    public static string EffectiveHttpBodyMode =>
        !LogHttpBodies ? "Off" : string.IsNullOrWhiteSpace(HttpBodyMode) ? "Summary" : HttpBodyMode;

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

        return Regex.Replace(text,
            @"(signature|auth_token|token|api_key|apiKey|X-MBX-APIKEY|listenKey)([""'\s:=]+)([A-Za-z0-9_\-\.]+)",
            m => $"{m.Groups[1].Value}{m.Groups[2].Value}***",
            RegexOptions.IgnoreCase);
    }

    /// <summary>Число в инвариантной культуре — чтобы логи не зависели от локали машины.</summary>
    public static string N(double value, string format = "F0") => value.ToString(format, CultureInfo.InvariantCulture);

    /// <inheritdoc cref="N(double,string)"/>
    public static string N(decimal value, string format = "0.########") => value.ToString(format, CultureInfo.InvariantCulture);

    /// <summary>Обрамить многострочный блок, чтобы в ленте было видно его границы.</summary>
    public static string Block(string title, string? text) =>
        $"----- {title} BEGIN -----\n{Clip(text)}\n----- {title} END -----";
}

/// <summary>
/// AI-пайплайн: промпт → рассуждение → сырой ответ → разобранное предложение.
/// Файл logs/ai-YYYYMMDD.log. Тяжёлые блоки идут на уровне Debug (видны в файле канала,
/// но не засоряют консоль и общий лог), короткие вердикты — на Information.
/// </summary>
public static class AiLog
{
    private static Serilog.ILogger Ch => Serilog.Log.ForContext("Channel", LogChannel.Ai);

    public static void Prompt(string symbol, int attempt, int maxAttempts, string prompt, string? meta = null)
    {
        if (!LogOptions.LogPrompts) return;

        Ch.Debug(
            "AI-PROMPT symbol={Symbol} attempt={Attempt}/{Max} chars={Chars} meta={Meta}\n{Block}",
            symbol, attempt, maxAttempts, prompt.Length, meta ?? "-",
            LogOptions.Block("PROMPT", prompt));
    }

    public static void Response(string symbol, int attempt, string rawResponse, double elapsedMs)
    {
        if (!LogOptions.LogAiResponses) return;

        Ch.Debug(
            "AI-RESPONSE symbol={Symbol} attempt={Attempt} elapsed_ms={Elapsed} chars={Chars}\n{Block}",
            symbol, attempt, LogOptions.N(elapsedMs), rawResponse.Length,
            LogOptions.Block("RESPONSE", rawResponse));
    }

    public static void Thinking(string symbol, string thinking, double elapsedMs)
    {
        if (!LogOptions.LogThinking || string.IsNullOrWhiteSpace(thinking)) return;

        Ch.Debug("AI-THINKING symbol={Symbol} elapsed_ms={Elapsed} chars={Chars}\n{Block}",
            symbol, LogOptions.N(elapsedMs), thinking.Length, LogOptions.Block("THINKING", thinking));
    }

    /// <summary>Мнение модели после парсинга — ещё не ордер.</summary>
    public static void Parsed(string symbol, string action, decimal confidence, string json, double elapsedMs)
    {
        Ch.Information(
            "AI-SIGNAL symbol={Symbol} action={Action} confidence={Confidence} elapsed_ms={Elapsed} — мнение модели, не ордер. {Json}",
            symbol, action, LogOptions.N(confidence, "F2"), LogOptions.N(elapsedMs), LogOptions.Clip(json));
    }

    public static void ParseFailed(string symbol, int attempt, string reason, string? rawResponse)
    {
        Ch.Warning("AI-PARSE-FAIL symbol={Symbol} attempt={Attempt} — {Reason}\n{Block}",
            symbol, attempt, reason, LogOptions.Block("RAW RESPONSE", LogOptions.Mask(rawResponse)));
    }

    public static void Retry(string symbol, int attempt, string reason)
    {
        Ch.Warning("AI-RETRY symbol={Symbol} attempt={Attempt} — {Reason}", symbol, attempt, reason);
    }

    /// <summary>Событие загрузки/конфигурации модели (backend, ctx, offload, KV).</summary>
    public static void Backend(string message)
    {
        var text = message ?? string.Empty;

        // Нативный вывод llama.cpp — в отдельный канал, он не должен топить промпты и ответы.
        if (text.StartsWith("[native", StringComparison.Ordinal))
        {
            Native(text);
            return;
        }

        // «WARN: …» внутри текста — это уровень события, а не часть сообщения.
        if (text.StartsWith("WARN: ", StringComparison.Ordinal))
        {
            Ch.Warning("AI-BACKEND — {Message}", text[6..]);
            return;
        }

        // Служебные детали KV-кэша повторяются на каждом инференсе — только в файл канала.
        if (text.StartsWith("KV: ", StringComparison.Ordinal))
        {
            Ch.Debug("AI-KV — {Message}", text[4..]);
            return;
        }

        Ch.Information("AI-BACKEND — {Message}", text);
    }

    /// <summary>Предупреждение AI-контура без исключения (таймаут, исчерпанные попытки).</summary>
    public static void Warn(string code, string? symbol, string detail)
    {
        var scope = string.IsNullOrWhiteSpace(symbol) ? string.Empty : $" symbol={symbol}";
        Ch.Warning("{Code}{Scope} — {Detail}", code, scope, detail);
    }

    /// <summary>Сырой нативный лог llama.cpp/ggml: отдельный файл ai-native-*.log, уровень Debug.</summary>
    public static void Native(string line)
    {
        if (!LogOptions.LogNativeAiOutput) return;

        var text = line ?? string.Empty;
        var level = text.StartsWith("[native ERROR]", StringComparison.Ordinal) ? LogEventLevel.Error
            : text.StartsWith("[native WARN]", StringComparison.Ordinal) ? LogEventLevel.Warning
            : LogEventLevel.Debug;

        var payload = text.StartsWith("[native", StringComparison.Ordinal)
            ? text[(text.IndexOf(']') + 1)..].TrimStart()
            : text;

        Serilog.Log.ForContext("Channel", LogChannel.AiNative).Write(level, "LLAMA {Line}", payload);
    }

    public static void Failure(string symbol, Exception ex, string context)
    {
        Ch.Error(ex, "AI-ERROR symbol={Symbol} — {Context}", symbol, context);
    }
}

/// <summary>
/// Обмен с биржей: REST, ошибки API, WebSocket. Файл logs/exchange-YYYYMMDD.log.
/// Один запрос = одна строка (метод, путь, статус, время, вес, выжимка тела).
/// </summary>
public static class ExchangeLog
{
    private static Serilog.ILogger Ch => Serilog.Log.ForContext("Channel", LogChannel.Exchange);

    /// <summary>
    /// Исходящий запрос. Чтение (GET) — только Verbose, чтобы не удваивать ленту:
    /// о факте запроса достаточно строки ответа. Постановка/снятие ордеров видна всегда.
    /// </summary>
    public static void Request(string method, string url, string? body = null)
    {
        if (!LogOptions.LogHttpTraffic) return;

        var mutating = !string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase);
        var level = mutating ? LogEventLevel.Debug : LogEventLevel.Verbose;
        if (!Ch.IsEnabled(level)) return;

        Ch.Write(level, "HTTP-REQ {Method} {Url}{Body}", method, LogSummary.ShortUrl(url),
            string.IsNullOrEmpty(body) ? string.Empty : " body=" + LogOptions.Clip(LogOptions.Mask(body)));
    }

    public static void Response(string method, string url, int statusCode, double elapsedMs, string? body,
        int? usedWeight = null, int? weightLimit = null)
    {
        if (!LogOptions.LogHttpTraffic) return;

        var mode = LogOptions.EffectiveHttpBodyMode;
        var weight = usedWeight.HasValue
            ? $" weight={usedWeight}{(weightLimit.HasValue ? "/" + weightLimit : string.Empty)}"
            : string.Empty;

        var payload = mode switch
        {
            "Full" => "\n" + LogOptions.Block("BODY", LogOptions.Mask(body)),
            "Off" => $" bytes={body?.Length ?? 0}",
            _ => " " + LogSummary.Of(url, body)
        };

        Ch.Debug("HTTP {Method} {Url} status={Status} elapsed_ms={Elapsed}{Weight}{Payload}",
            method, LogSummary.ShortUrl(url), statusCode, LogOptions.N(elapsedMs), weight, payload);
    }

    public static void ApiError(string method, string url, int statusCode, string body, int? apiCode, string? apiMessage)
    {
        Ch.Error("BINANCE-API-ERROR {Method} {Url} status={Status} code={Code} — {Message}\n{Block}",
            method, LogSummary.ShortUrl(url), statusCode, apiCode?.ToString() ?? "-", apiMessage ?? "-",
            LogOptions.Block("ERROR BODY", LogOptions.Mask(body)));
    }

    public static void Retry(string url, int attempt, int maxAttempts, string reason)
    {
        var key = "http-retry:" + LogSummary.ShortUrl(url);
        if (!LogGate.Pass(key, reason, out var repeats)) return;

        Ch.Warning("HTTP-RETRY {Url} attempt={Attempt}/{Max}{Repeats} — {Reason}",
            LogSummary.ShortUrl(url), attempt, maxAttempts, Repeats(repeats), reason);
    }

    /// <summary>Жизненный цикл ордера: SUBMIT → ACCEPTED/FILLED/CANCELED. Канал trade.</summary>
    public static void Order(string stage, string symbol, string side, decimal quantity, decimal price, string? details = null)
    {
        var qty = quantity > 0m ? $" qty={LogOptions.N(quantity)}" : string.Empty;
        var px = price > 0m ? $" price={LogOptions.N(price)}" : string.Empty;
        var extra = string.IsNullOrWhiteSpace(details) ? string.Empty : " " + details;

        Serilog.Log.ForContext("Channel", LogChannel.Trade)
            .Information("ORDER stage={Stage} symbol={Symbol} side={Side}{Qty}{Price}{Details}",
                stage, symbol, side, qty, px, extra);
    }

    /// <summary>Снятие одиночного ордера — без бессмысленных qty=0 price=0.</summary>
    public static void OrderCanceled(string symbol, string orderId)
    {
        Serilog.Log.ForContext("Channel", LogChannel.Trade)
            .Information("ORDER stage=CANCELED symbol={Symbol} order_id={OrderId}", symbol, orderId);
    }

    /// <summary>Снятие защитной связки OCO.</summary>
    public static void OcoCanceled(string symbol, long orderListId, string? clientListId = null)
    {
        Serilog.Log.ForContext("Channel", LogChannel.Trade)
            .Information("ORDER stage=CANCELED-OCO symbol={Symbol} order_list_id={ListId} client_list_id={ClientId}",
                symbol, orderListId, clientListId ?? "-");
    }

    public static void WebSocket(string message, string? details = null)
    {
        var key = "ws:" + message + ":" + (details ?? string.Empty);
        if (!LogGate.Pass(key, message + details, out var repeats)) return;

        Ch.Information("WS event={Event}{Repeats} — {Details}",
            message, Repeats(repeats), details ?? "-");
    }

    public static void WebSocketParseError(string rawMessage, Exception ex)
    {
        Ch.Error(ex, "WS-PARSE-ERROR\n{Block}", LogOptions.Block("RAW MESSAGE", rawMessage));
    }

    public static void Environment(string environment, string restUrl, string streamUrl)
    {
        Ch.Information("BINANCE-ENV env={Environment} rest={Rest} stream={Stream}", environment, restUrl, streamUrl);
    }

    /// <summary>Баланс счёта одной строкой; повторяющееся значение не дублируется.</summary>
    public static void Balance(string environment, decimal availableUsdt, string quoteAsset)
    {
        var value = LogOptions.N(availableUsdt, "F2");
        if (!LogGate.Pass("balance:" + environment, value, out var repeats)) return;

        Ch.Information("ACCOUNT-BALANCE env={Env} available={Available} quote={Quote}{Repeats}",
            environment, value, quoteAsset, Repeats(repeats));
    }

    // Универсальные события канала (заменяют дублирующие ILogger-строки с тегом в тексте).
    public static void Info(string code, string? symbol, string detail) =>
        Write(LogEventLevel.Information, code, symbol, detail, null);

    public static void Warn(string code, string? symbol, string detail, Exception? ex = null, string? gateKey = null)
    {
        if (gateKey is not null && !LogGate.Pass(gateKey, detail, out _)) return;
        Write(LogEventLevel.Warning, code, symbol, detail, ex);
    }

    public static void Error(string code, string? symbol, string detail, Exception? ex = null) =>
        Write(LogEventLevel.Error, code, symbol, detail, ex);

    private static void Write(LogEventLevel level, string code, string? symbol, string detail, Exception? ex)
    {
        var scope = string.IsNullOrWhiteSpace(symbol) ? string.Empty : $" symbol={symbol}";
        Ch.Write(level, ex, "{Code}{Scope} — {Detail}", code, scope, detail);
    }

    internal static string Repeats(int repeats) => repeats > 0 ? $" repeats={repeats}" : string.Empty;
}

/// <summary>Новостные провайдеры и контекст для модели. Файл logs/news-YYYYMMDD.log.</summary>
public static class NewsLog
{
    private static Serilog.ILogger Ch => Serilog.Log.ForContext("Channel", LogChannel.News);

    public static void Request(string provider, string url)
    {
        if (!LogOptions.LogNews) return;
        Ch.Debug("NEWS-REQ provider={Provider} url={Url}", provider, LogOptions.Mask(url));
    }

    public static void Response(string provider, int statusCode, int count, double elapsedMs, string? sample = null)
    {
        if (!LogOptions.LogNews) return;

        Ch.Information("NEWS provider={Provider} status={Status} items={Count} elapsed_ms={Elapsed}{Sample}",
            provider, statusCode, count, LogOptions.N(elapsedMs),
            string.IsNullOrEmpty(sample) ? string.Empty : "\n" + LogOptions.Block("NEWS SAMPLE", sample));
    }

    public static void Skipped(string provider, string reason)
    {
        if (!LogOptions.LogNews) return;

        var key = "news-skip:" + provider;
        if (!LogGate.Pass(key, reason, out var repeats)) return;
        Ch.Debug("NEWS-SKIP provider={Provider}{Repeats} — {Reason}", provider, ExchangeLog.Repeats(repeats), reason);
    }

    public static void Failure(string provider, Exception ex, string context)
    {
        Ch.Warning(ex, "NEWS-ERROR provider={Provider} — {Context}", provider, context);
    }

    public static void Context(string symbol, int count, string context)
    {
        if (!LogOptions.LogNews) return;
        Ch.Debug("NEWS-CONTEXT symbol={Symbol} items={Count}\n{Block}",
            symbol, count, LogOptions.Block("NEWS CONTEXT", context));
    }
}

/// <summary>
/// Торговые решения, ордера, позиции, риск. Файл logs/trades-YYYYMMDD.log.
///
/// Правила канала:
///   • одно событие — одна запись (дублирующие ILogger-строки удалены);
///   • повторяющееся состояние пишется один раз, затем repeats=N по heartbeat;
///   • ожидаемый отказ входа (прогрев, нет данных, нет средств) — Information,
///     реальная проблема (нет защиты, недостоверное состояние исполнения) — Warning.
/// </summary>
public static class TradeLog
{
    private static Serilog.ILogger Ch => Serilog.Log.ForContext("Channel", LogChannel.Trade);

    /// <summary>Этапы отказа, требующие немедленного внимания (аварийное закрытие, потеря защиты).</summary>
    private static readonly HashSet<string> ErrorStages = new(StringComparer.OrdinalIgnoreCase)
    {
        "protection", "protection-cancel", "close", "close-filter", "cancel-audit", "exception", "exchange"
    };

    /// <summary>Этапы отказа, которые означают аварию, а не штатное «входа нет».</summary>
    private static readonly HashSet<string> AlarmStages = new(StringComparer.OrdinalIgnoreCase)
    {
        "execution-state", "close-partial", "kill-switch", "risk", "reconciliation", "filters", "dust-close", "post-only"
    };

    /// <summary>Промежуточное решение контура (не финал): калибровка, капы, коррекции.</summary>
    /// <param name="alarm">Событие требует внимания (задержка очереди, повтор отправки) → Warning.</param>
    public static void Decision(string symbol, string decision, string reason, bool alarm = false)
    {
        var key = $"decision:{symbol}:{decision}";
        if (!LogGate.Pass(key, reason, out var repeats)) return;

        Ch.Write(alarm ? LogEventLevel.Warning : LogEventLevel.Information,
            "DECISION symbol={Symbol} code={Decision}{Repeats} — {Reason}",
            symbol, decision, ExchangeLog.Repeats(repeats), reason);
    }

    /// <summary>Единый финал по символу: что бот РЕАЛЬНО сделал (или почему не сделал).</summary>
    public static void Outcome(string symbol, string verdict, string reason)
    {
        var key = $"outcome:{symbol}:{verdict}";
        if (!LogGate.Pass(key, reason, out var repeats)) return;

        Ch.Information("OUTCOME symbol={Symbol} verdict={Verdict}{Repeats} — {Reason}",
            symbol, verdict, ExchangeLog.Repeats(repeats), reason);
    }

    public static void Opened(string symbol, string side, decimal quantity, decimal entry,
        decimal stopLoss, decimal takeProfit, string mode, string? orderId = null)
    {
        var verdict = string.Equals(side, "Buy", StringComparison.OrdinalIgnoreCase) ? "КУПЛЕНО" : "ПРОДАНО";
        Ch.Information(
            "OUTCOME symbol={Symbol} verdict={Verdict} mode={Mode} qty={Quantity} entry={Entry} sl={SL} tp={TP} order_id={Order}",
            symbol, verdict, mode, LogOptions.N(quantity), LogOptions.N(entry),
            LogOptions.N(stopLoss), LogOptions.N(takeProfit), orderId ?? "-");
    }

    public static void Closed(string symbol, string reason, decimal quantity, decimal entry, decimal exit,
        decimal pnl, decimal pnlPercent, decimal commission, string mode)
    {
        Ch.Information(
            "OUTCOME symbol={Symbol} verdict=ЗАКРЫТО mode={Mode} qty={Quantity} entry={Entry} exit={Exit} pnl={PnL} pnl_pct={PnLPct} fee={Fee} — {Reason}",
            symbol, mode, LogOptions.N(quantity), LogOptions.N(entry), LogOptions.N(exit),
            LogOptions.N(pnl, "F4"), LogOptions.N(pnlPercent, "F2"), LogOptions.N(commission, "F4"), reason);
    }

    /// <summary>Ожидание (прогрев индикаторов, cooldown) — чтобы «бот ничего не делает» было видно.</summary>
    public static void Waiting(string symbol, string reason)
    {
        var key = $"waiting:{symbol}";
        if (!LogGate.Pass(key, reason, out var repeats)) return;

        Ch.Information("OUTCOME symbol={Symbol} verdict=ПАУЗА{Repeats} — {Reason}",
            symbol, ExchangeLog.Repeats(repeats), reason);
    }

    /// <summary>Отказ входа с указанием этапа. Уровень зависит от того, авария это или штатное ожидание.</summary>
    public static void Rejected(string symbol, string stage, string reason, Exception? ex = null)
    {
        var key = $"rejected:{symbol}:{stage}";
        if (!LogGate.Pass(key, reason, out var repeats)) return;

        var level = ErrorStages.Contains(stage) ? LogEventLevel.Error
            : AlarmStages.Contains(stage) ? LogEventLevel.Warning
            : LogEventLevel.Information;
        Ch.Write(level, ex, "OUTCOME symbol={Symbol} verdict=ОТКАЗ stage={Stage}{Repeats} — {Reason}",
            symbol, stage, ExchangeLog.Repeats(repeats), reason);
    }

    public static void Balance(string mode, decimal cash, decimal equity, decimal drawdownPercent)
    {
        var payload = $"{LogOptions.N(cash, "F2")}|{LogOptions.N(equity, "F2")}|{LogOptions.N(drawdownPercent, "F2")}";
        if (!LogGate.Pass("balance:" + mode, payload, out var repeats)) return;

        Ch.Information("BALANCE mode={Mode} cash={Cash} equity={Equity} drawdown_pct={Drawdown}{Repeats}",
            mode, LogOptions.N(cash, "F2"), LogOptions.N(equity, "F2"),
            LogOptions.N(drawdownPercent, "F2"), ExchangeLog.Repeats(repeats));
    }

    /// <summary>Активная вселенная символов и причины исключений — на каждом старте.</summary>
    public static void SymbolUniverse(string environment, string configHash,
        IReadOnlyList<string> active, IReadOnlyDictionary<string, string> excluded,
        IReadOnlyList<string> volatileSymbols, IReadOnlyList<string> stablecoinPairs,
        IReadOnlyList<string> benchmarkSymbols)
    {
        Ch.Information(
            "SYMBOL-UNIVERSE env={Environment} config_hash={Hash} active={Active} excluded={Excluded} volatile={Volatile} stablecoin_pairs={Stable} benchmark_symbols={Bench}",
            environment, configHash, string.Join(",", active),
            AITradingSystem.Common.Configuration.SymbolUniversePolicy.FormatReasons(excluded),
            string.Join(",", volatileSymbols), string.Join(",", stablecoinPairs), string.Join(",", benchmarkSymbols));
    }

    public static void RiskSnapshot(string environment, string accountId, string quoteAsset,
        decimal equity, decimal free, decimal locked, decimal peak, decimal dayStart,
        decimal drawdownPercent, decimal dailyPnl, string source, bool valuationComplete)
    {
        var payload = string.Join('|', LogOptions.N(equity, "F2"), LogOptions.N(free, "F2"),
            LogOptions.N(drawdownPercent, "F2"), valuationComplete);
        if (!LogGate.Pass("risk-snapshot:" + environment, payload, out var repeats)) return;

        Ch.Information(
            "RISK-SNAPSHOT env={Env} account={Account} quote={Quote} equity={Equity} free={Free} locked={Locked} peak={Peak} day_start={DayStart} drawdown_pct={Drawdown} daily_pnl={DailyPnl} source={Source} valuation_complete={Complete}{Repeats}",
            environment, accountId, quoteAsset, LogOptions.N(equity, "F2"), LogOptions.N(free, "F2"),
            LogOptions.N(locked, "F2"), LogOptions.N(peak, "F2"), LogOptions.N(dayStart, "F2"),
            LogOptions.N(drawdownPercent, "F2"), LogOptions.N(dailyPnl, "F2"), source, valuationComplete,
            ExchangeLog.Repeats(repeats));
    }

    // ── Kill-switch ────────────────────────────────────────────────────────────────

    public static void KillSwitchEngaged(string reason, decimal equityAtEngage, bool requiresManualReset)
    {
        Ch.Fatal("KILL-SWITCH-ENGAGED reason={Reason} equity_at_engage={Equity} requires_manual_reset={Manual} — новые входы заблокированы, идёт аварийное закрытие",
            reason, LogOptions.N(equityAtEngage, "F2"), requiresManualReset);
    }

    public static void KillSwitchCloseResult(int closed, int total, int remaining, string status)
    {
        var level = remaining > 0 ? LogEventLevel.Fatal : LogEventLevel.Warning;
        Ch.Write(level, "KILL-SWITCH-CLOSE-RESULT closed={Closed} total={Total} remaining={Remaining} status={Status}",
            closed, total, remaining, status);
    }

    public static void KillSwitchReset(string operatorReason)
    {
        Ch.Warning("KILL-SWITCH-RESET operator_reason={Reason} — входы снова разрешены, peak/day_start сохранены", operatorReason);
    }

    public static void KillSwitchResetDenied(string reason)
    {
        Ch.Warning("KILL-SWITCH-RESET-DENIED reason={Reason}", reason);
    }

    public static void KillSwitchRestored(string reason, string closeStatus)
    {
        Ch.Fatal("KILL-SWITCH-RESTORED reason={Reason} close_status={Status} — требуется ручной сброс", reason, closeStatus);
    }

    // ── Роль LLM и veto ────────────────────────────────────────────────────────────

    public static void LlmRole(string role, bool enabled, int vetoBudgetSeconds)
    {
        Ch.Information("LLM-ROLE role={Role} enabled={Enabled} veto_budget_s={Budget}", role, enabled, vetoBudgetSeconds);
    }

    public static void LlmVetoResult(string symbol, string role, bool veto, string reason,
        double latencyMs, bool withinBudget, DateTime candleCloseUtc)
    {
        Ch.Information(
            "LLM-VETO-RESULT symbol={Symbol} role={Role} veto={Veto} latency_ms={Latency} within_budget={Within} candle_close={Candle:O} — {Reason}",
            symbol, role, veto, LogOptions.N(latencyMs), withinBudget, candleCloseUtc, reason);
    }

    public static void LlmVetoApplied(string symbol, string reason)
    {
        Ch.Warning("LLM-VETO-APPLIED symbol={Symbol} — {Reason}", symbol, reason);
    }

    public static void LlmVetoLate(string symbol, string reason, double latencyMs)
    {
        Ch.Warning("LLM-VETO-LATE symbol={Symbol} latency_ms={Latency} — вердикт опоздал, к принятому решению не применён. {Reason}",
            symbol, LogOptions.N(latencyMs), reason);
    }

    public static void LlmVetoPending(string symbol)
    {
        var key = "llm-veto-pending:" + symbol;
        if (!LogGate.Pass(key, "pending", out var repeats)) return;

        Ch.Information("LLM-VETO-PENDING symbol={Symbol}{Repeats} — вердикт по закрытой свече не готов, вход не разрешён (fail-closed)",
            symbol, ExchangeLog.Repeats(repeats));
    }

    // ── Здоровье рыночных данных ───────────────────────────────────────────────────

    public static void DataStale(string symbol, string reason, double ageSeconds)
    {
        var key = "data-stale:" + symbol;
        if (!LogGate.Pass(key, reason, out var repeats)) return;

        Ch.Warning("DATA-STALE symbol={Symbol} age_s={Age}{Repeats} — {Reason}",
            symbol, LogOptions.N(ageSeconds, "F1"), ExchangeLog.Repeats(repeats), reason);
    }

    public static void DataRestReload(string symbol, int closedCandles, double liveConfirmWindowSeconds)
    {
        Ch.Information("DATA-REST-RELOAD symbol={Symbol} closed_candles={Count} live_confirm_window_s={Window} entries_allowed=false",
            symbol, closedCandles, LogOptions.N(liveConfirmWindowSeconds));
    }

    public static void DataWsResubscribe(string symbol, int attempt, int maxAttempts, string reason)
    {
        Ch.Warning("DATA-WS-RESUBSCRIBE symbol={Symbol} attempt={Attempt}/{Max} — {Reason}",
            symbol, attempt, maxAttempts, reason);
    }

    public static void DataWsReconnect(string symbol, string reason)
    {
        Ch.Warning("DATA-WS-RECONNECT symbol={Symbol} — {Reason}", symbol, reason);
    }

    public static void DataLiveConfirmed(string symbol, double secondsToLive, int resubscribeAttempts)
    {
        Ch.Information("DATA-LIVE-CONFIRMED symbol={Symbol} time_to_live_s={Ttl} resubscribes={Attempts} entries_allowed_after_fresh_book=true",
            symbol, LogOptions.N(secondsToLive, "F1"), resubscribeAttempts);
    }

    public static void DataQuarantine(string symbol, string reason, double durationSeconds)
    {
        Ch.Warning("DATA-QUARANTINE symbol={Symbol} duration_s={Duration} entries_allowed=false — {Reason}",
            symbol, LogOptions.N(durationSeconds), reason);
    }

    public static void DataQuarantineLifted(string symbol, string reason)
    {
        Ch.Information("DATA-QUARANTINE-LIFTED symbol={Symbol} previous_reason={Reason} entries_allowed=false", symbol, reason);
    }

    // ── User-data stream и режим исполнения ────────────────────────────────────────

    public static void UserDataProbe(string environment, bool supported, int? httpStatus, int? apiCode, string? detail)
    {
        var level = supported ? LogEventLevel.Information : LogEventLevel.Warning;
        Ch.Write(level, "USER-DATA-PROBE env={Env} supported={Supported} http={Http} code={Code} — {Detail}",
            environment, supported, httpStatus?.ToString() ?? "-", apiCode?.ToString() ?? "-",
            string.IsNullOrWhiteSpace(detail) ? "-" : detail);
    }

    public static void UserDataStart(int keepaliveMinutes, int healthCheckSeconds)
    {
        Ch.Information("USER-DATA-START keepalive_min={Keepalive} health_check_s={Health}", keepaliveMinutes, healthCheckSeconds);
    }

    public static void ExecutionMode(string mode, string reason, int pollingIntervalSeconds)
    {
        var level = string.Equals(mode, "STREAM", StringComparison.OrdinalIgnoreCase)
            ? LogEventLevel.Information
            : LogEventLevel.Warning;

        Ch.Write(level, "EXECUTION-MODE mode={Mode} polling_interval_s={Interval} — {Reason}",
            mode, pollingIntervalSeconds, reason);
    }

    // ── Реконсиляция ───────────────────────────────────────────────────────────────

    public static void ReconStart(string trigger, int positions)
    {
        // Старт пишется только когда есть что сверять или причина запуска сменилась.
        var key = "recon-start:" + trigger;
        if (positions == 0 && !LogGate.Pass(key, positions.ToString(), out _)) return;

        Ch.Information("RECON-START trigger={Trigger} positions={Positions}", trigger, positions);
    }

    public static void ReconSummary(int positions, int orphanOco, int orphanOrders, int externalOrders,
        int protectionMissing, int unresolved, bool entriesAllowed, int activeEntries = 0, int skippedActiveEntryCancels = 0)
    {
        var payload = string.Join('|', positions, orphanOco, orphanOrders, externalOrders,
            protectionMissing, unresolved, entriesAllowed, activeEntries, skippedActiveEntryCancels);
        if (!LogGate.Pass("recon-summary", payload, out var repeats)) return;

        Ch.Information(
            "RECON-SUMMARY positions={Positions} orphan_oco={OrphanOco} orphan_orders={OrphanOrders} external_orders={External} protection_missing={Missing} unresolved={Unresolved} entries_allowed={Allowed} active_entries={ActiveEntries} skipped_active_entry_cancels={Skipped}{Repeats}",
            positions, orphanOco, orphanOrders, externalOrders, protectionMissing, unresolved,
            entriesAllowed, activeEntries, skippedActiveEntryCancels, ExchangeLog.Repeats(repeats));
    }

    public static void ReconFail(string trigger, string reason, Exception? ex = null)
    {
        Ch.Write(LogEventLevel.Warning, ex, "RECON-FAIL trigger={Trigger} entries_allowed=false — {Reason}", trigger, reason);
    }

    public static void ReconFinding(string finding)
    {
        Ch.Warning("RECON-FINDING — {Finding}", finding);
    }

    public static void ReconAdopt(string symbol, decimal quantity, decimal price, string clientOrderId)
    {
        Ch.Warning("RECON-ADOPT symbol={Symbol} qty={Qty} price={Price} client_order_id={ClientId} — подхвачена сиротская позиция",
            symbol, LogOptions.N(quantity), LogOptions.N(price), clientOrderId);
    }

    public static void ReconCancel(string symbol, string kind, string identifier, string reason = "orphan")
    {
        Ch.Warning("RECON-CANCEL symbol={Symbol} kind={Kind} id={Id} — {Reason}", symbol, kind, identifier, reason);
    }

    public static void ReconSkipActiveEntry(string symbol, string id, Guid owner, double remainingSeconds, decimal filled)
    {
        Ch.Information(
            "RECON-SKIP-ACTIVE-ENTRY symbol={Symbol} id={Id} owner={Owner} remaining_s={Remaining} filled={Filled} protection_required={Protection} — активный вход в пределах дедлайна",
            symbol, id, owner, LogOptions.N(remainingSeconds, "F1"), LogOptions.N(filled), filled > 0m);
    }

    public static void ReconExpireEntry(string symbol, string id, decimal filled)
    {
        Ch.Warning("RECON-EXPIRE-ENTRY symbol={Symbol} id={Id} filled={Filled} protection_required={Protection} — дедлайн входа истёк",
            symbol, id, LogOptions.N(filled), filled > 0m);
    }

    public static void ProtectionFillApplied(string symbol, string reason, decimal cumulativeQuantity, string source)
    {
        Ch.Warning("PROTECTION-FILL-APPLIED symbol={Symbol} cum_qty={CumQty} source={Source} — {Reason}",
            symbol, LogOptions.N(cumulativeQuantity), source, reason);
    }

    public static void ExecutionStateBlocked(string reason)
    {
        var key = "execution-state-blocked";
        if (!LogGate.Pass(key, reason, out var repeats)) return;

        Ch.Warning("EXECUTION-STATE-BLOCKED entries_allowed=false{Repeats} — {Reason}",
            ExchangeLog.Repeats(repeats), reason);
    }

    // ── Универсальные события канала ───────────────────────────────────────────────
    // Заменяют дублирующие _logger.Log* с тегом в тексте: одно событие — одна запись,
    // исключение (если есть) attached к той же записи, а не к второй строке.

    public static void Info(string code, string? symbol, string detail) =>
        Write(LogEventLevel.Information, code, symbol, detail, null);

    /// <param name="gateKey">Ключ подавления повторов: одинаковая деталь пишется раз в heartbeat.</param>
    public static void Warn(string code, string? symbol, string detail, Exception? ex = null, string? gateKey = null)
    {
        if (gateKey is not null && !LogGate.Pass(gateKey, detail, out _)) return;
        Write(LogEventLevel.Warning, code, symbol, detail, ex);
    }

    public static void Error(string code, string? symbol, string detail, Exception? ex = null) =>
        Write(LogEventLevel.Error, code, symbol, detail, ex);

    public static void Critical(string code, string? symbol, string detail, Exception? ex = null) =>
        Write(LogEventLevel.Fatal, code, symbol, detail, ex);

    private static void Write(LogEventLevel level, string code, string? symbol, string detail, Exception? ex)
    {
        var scope = string.IsNullOrWhiteSpace(symbol) ? string.Empty : $" symbol={symbol}";
        Ch.Write(level, ex, "{Code}{Scope} — {Detail}", code, scope, detail);
    }
}
