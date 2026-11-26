using System.Collections.Concurrent;
using System.Globalization;
using Serilog.Events;
using Serilog.Formatting;

namespace AITradingSystem.Common.Logging;

/// <summary>
/// Единый формат строки лога для ВСЕХ приёмников (консоль и все файлы каналов).
///
/// Схема: <c>yyyy-MM-dd HH:mm:ss.fff zzz [LVL] [канал] Источник: СОБЫТИЕ key=value — расшифровка</c>.
///
/// Что это чинит по сравнению с шаблонами Serilog:
///   • одинаковое время с таймзоной во всех файлах — события разных каналов сопоставимы;
///   • нет пустого {SourceContext} и двойных пробелов (источник печатается только когда он есть);
///   • числа рендерятся в инвариантной культуре —decimal не превращается в «0,0001» на ru-RU;
///   • продолжение многострочного блока (промпт, тело ответа) получает отступ — видно, где
///     заканчивается событие и начинается полезная нагрузка;
///   • исключение печатается с отступом, а не сливается с текстом события.
/// </summary>
public sealed class UnifiedLogFormatter : ITextFormatter
{
    private readonly bool _compactTime;
    private readonly bool _showSource;

    /// <param name="compactTime">Только время без даты и зоны (для консоли).</param>
    /// <param name="showSource">Печатать короткий источник (класс) перед сообщением.</param>
    public UnifiedLogFormatter(bool compactTime = false, bool showSource = true)
    {
        _compactTime = compactTime;
        _showSource = showSource;
    }

    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        ArgumentNullException.ThrowIfNull(output);

        output.Write(logEvent.Timestamp.ToString(
            _compactTime ? "HH:mm:ss.fff" : "yyyy-MM-dd HH:mm:ss.fff zzz",
            CultureInfo.InvariantCulture));
        output.Write(" [");
        output.Write(LevelTag(logEvent.Level));
        output.Write("] [");
        output.Write(Scalar(logEvent, "Channel") ?? LogChannel.System);
        output.Write(']');

        if (_showSource)
        {
            var source = ShortSource(Scalar(logEvent, "SourceContext"));
            if (source is not null)
            {
                output.Write(' ');
                output.Write(source);
                output.Write(':');
            }
        }

        output.Write(' ');
        output.Write(Indent(logEvent.RenderMessage(CultureInfo.InvariantCulture)));

        if (logEvent.Exception is { } ex)
        {
            output.Write(Environment.NewLine);
            output.Write("    ");
            output.Write(Indent(ex.ToString()));
        }

        output.Write(Environment.NewLine);
    }

    private static string LevelTag(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "VRB",
        LogEventLevel.Debug => "DBG",
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning => "WRN",
        LogEventLevel.Error => "ERR",
        LogEventLevel.Fatal => "FTL",
        _ => "???"
    };

    private static string? Scalar(LogEvent logEvent, string name) =>
        logEvent.Properties.TryGetValue(name, out var value) &&
        value is ScalarValue { Value: string s } &&
        !string.IsNullOrWhiteSpace(s)
            ? s
            : null;

    /// <summary>Полное имя типа → короткое понятное имя источника.</summary>
    private static string? ShortSource(string? sourceContext)
    {
        if (string.IsNullOrWhiteSpace(sourceContext)) return null;
        var lastDot = sourceContext.LastIndexOf('.');
        var name = lastDot >= 0 && lastDot < sourceContext.Length - 1
            ? sourceContext[(lastDot + 1)..]
            : sourceContext;
        var generic = name.IndexOf('`');
        return generic > 0 ? name[..generic] : name;
    }

    /// <summary>Многострочная полезная нагрузка получает отступ, чтобы не рвать ленту событий.</summary>
    private static string Indent(string text) =>
        text.Contains('\n')
            ? text.Replace("\r\n", "\n").Replace("\n", "\n    ")
            : text;
}

/// <summary>
/// Подавление повторов: одно и то же состояние символа/контура пишется один раз,
/// дальше — только при изменении или по heartbeat (с числом пропущенных повторов).
///
/// Именно это убирает из логов тысячи строк «ИТОГ … awaiting-live-tick» и
/// «RECON-SUMMARY positions=0 …» каждые 20 секунд, не теряя факта, что состояние держится.
/// </summary>
public static class LogGate
{
    private sealed class State
    {
        public string? Payload;
        public DateTime LastEmitUtc;
        public int Suppressed;
    }

    private static readonly ConcurrentDictionary<string, State> States = new(StringComparer.Ordinal);

    /// <summary>
    /// Через сколько секунд повторить неизменившееся состояние (0 — не повторять никогда,
    /// отрицательное значение — отключить подавление и писать всё).
    /// </summary>
    public static int HeartbeatSeconds { get; set; } = 300;

    /// <summary>
    /// Пропустить событие в лог? <paramref name="repeats"/> — сколько одинаковых
    /// событий было подавлено с прошлой записи (печатается как repeats=N).
    /// </summary>
    public static bool Pass(string key, string payload, out int repeats)
    {
        repeats = 0;
        if (HeartbeatSeconds < 0) return true;

        var now = DateTime.UtcNow;
        var state = States.GetOrAdd(key, _ => new State());

        lock (state)
        {
            var unchanged = string.Equals(state.Payload, payload, StringComparison.Ordinal);
            var heartbeatDue = HeartbeatSeconds > 0 &&
                               (now - state.LastEmitUtc).TotalSeconds >= HeartbeatSeconds;

            if (unchanged && !heartbeatDue)
            {
                state.Suppressed++;
                return false;
            }

            repeats = state.Suppressed;
            state.Suppressed = 0;
            state.Payload = payload;
            state.LastEmitUtc = now;
            return true;
        }
    }

    /// <summary>Сброс памяти состояний (переинициализация логгера, рестарт движка).</summary>
    public static void Reset() => States.Clear();
}
