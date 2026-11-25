using System.Text;
using System.Text.RegularExpressions;

namespace AITradingSystem.Services.AI;

/// <summary>
/// Потоковый сторож деградации вывода LLM (llama.cpp issue #24812 и типичные зацикливания Qwen3).
/// Ловит:
///  - «????????…» — длинные прогоны одного символа (Vulkan-мусор);
///  - циклы спецтокенов вида &lt;unused49&gt; / &lt;|…|&gt; (тот же класс сбоя);
///  - повторяющиеся блоки («0.45, 0.45, 0.45…» и т.п.);
///  - replacement-символы U+FFFD (битая декодировка).
/// Срабатывание означает: генерацию нужно немедленно остановить, почистить KV и повторить запрос.
/// Класс чистый (без нативных зависимостей) — покрыт юнит-тестами.
/// </summary>
public sealed class DegenerateOutputDetector
{
    /// <summary>Сколько одинаковых символов подряд считаем мусором.</summary>
    public const int SameCharRunLimit = 32;

    /// <summary>Сколько U+FFFD терпим.</summary>
    public const int ReplacementCharLimit = 4;

    /// <summary>Сколько спецтокенов в ответе терпим (0 — нормальный ответ их не содержит).</summary>
    public const int SpecialTokenLimit = 3;

    private const int LoopMinPeriod = 2;
    private const int LoopMaxPeriod = 64;
    private const int LoopRepeats = 6;
    private const int FullCheckEveryChars = 16;

    private static readonly Regex SpecialTokenRegex =
        new(@"<unused\d+>|<\|[A-Za-z0-9_ ]+\|>", RegexOptions.Compiled);

    private readonly bool _enabled;
    private readonly StringBuilder _text = new();
    private int _runChar = -1;
    private int _runLength;
    private int _replacementCount;
    private int _lastFullCheckLength;

    /// <summary>Причина деградации (null — вывод здоров).</summary>
    public string? Reason { get; private set; }

    /// <summary>Вывод признан деградированным.</summary>
    public bool IsDegenerate => Reason != null;

    public DegenerateOutputDetector(bool enabled = true)
    {
        _enabled = enabled;
    }

    /// <summary>
    /// Скормить очередной кусок текста. Возвращает true, если вывод деградировал
    /// (Reason заполнен). После срабатывания детектор залипает в true.
    /// </summary>
    public bool Feed(string? chunk)
    {
        if (!_enabled || IsDegenerate || string.IsNullOrEmpty(chunk))
            return IsDegenerate;

        foreach (var c in chunk)
        {
            if (c == _runChar)
            {
                _runLength++;
                if (_runLength >= SameCharRunLimit)
                {
                    Reason = $"прогон из {_runLength} одинаковых символов '{Describe(c)}' (мусор генерации)";
                    return true;
                }
            }
            else
            {
                _runChar = c;
                _runLength = 1;
            }

            if (c == '\uFFFD' && ++_replacementCount >= ReplacementCharLimit)
            {
                Reason = $"{_replacementCount} replacement-символов U+FFFD (битая декодировка)";
                return true;
            }
        }

        _text.Append(chunk);

        // Дорогие проверки — не на каждый токен, а раз в ~16 символов.
        if (_text.Length - _lastFullCheckLength >= FullCheckEveryChars)
        {
            _lastFullCheckLength = _text.Length;

            var specialCount = SpecialTokenRegex.Matches(_text.ToString()).Count;
            if (specialCount >= SpecialTokenLimit)
            {
                Reason = $"{specialCount} спецтокенов в ответе (цикл спецтокенов, класс сбоя #24812)";
                return true;
            }

            if (TryFindLoop(out var period, out var block))
            {
                Reason = $"цикл: блок длины {period} повторяется {LoopRepeats}+ раз подряд (\"{block}\")";
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Ищет в хвосте текста ≥ LoopRepeats подряд идущих одинаковых блоков длины 2…64.
    /// Блок из одних пробелов/переносов не считается (форматирование JSON).
    /// </summary>
    private bool TryFindLoop(out int period, out string block)
    {
        period = 0;
        block = string.Empty;

        var maxTail = LoopMaxPeriod * LoopRepeats;
        var len = Math.Min(_text.Length, maxTail);
        if (len < LoopMinPeriod * LoopRepeats)
            return false;

        var tail = _text.ToString(_text.Length - len, len);

        for (var p = LoopMinPeriod; p <= LoopMaxPeriod; p++)
        {
            var need = p * LoopRepeats;
            if (len < need)
                break;

            var candidate = tail.Substring(len - p, p);
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            var ok = true;
            for (var r = 2; r <= LoopRepeats && ok; r++)
            {
                var start = len - p * r;
                for (var i = 0; i < p; i++)
                {
                    if (tail[start + i] != candidate[i])
                    {
                        ok = false;
                        break;
                    }
                }
            }

            if (ok)
            {
                period = p;
                block = candidate.Length > 24 ? candidate[..24] + "…" : candidate;
                return true;
            }
        }

        return false;
    }

    private static string Describe(char c) => c switch
    {
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        ' ' => "space",
        _ => c.ToString()
    };
}
