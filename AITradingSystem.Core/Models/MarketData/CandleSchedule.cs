namespace AITradingSystem.Core.Models.MarketData;

/// <summary>
/// P1.4: детерминированная сетка свечей Binance и классификация разрывов.
/// Не зависит от системных часов в момент вызова кроме явно переданного nowUtc.
/// </summary>
public static class CandleSchedule
{
    public static bool TryParseInterval(string interval, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(interval))
            return false;

        var span = interval.AsSpan().Trim();
        if (span.Length < 2)
            return false;

        var unit = span[^1];
        if (!int.TryParse(span[..^1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var amount) || amount <= 0)
            return false;

        value = unit switch
        {
            'm' => TimeSpan.FromMinutes(amount),
            'h' => TimeSpan.FromHours(amount),
            'd' => TimeSpan.FromDays(amount),
            _ => default
        };

        return value > TimeSpan.Zero;
    }

    /// <summary>OpenTime текущей (возможно ещё не закрытой) свечи для момента nowUtc.</summary>
    public static DateTime GetCurrentOpenTime(DateTime nowUtc, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval));

        var ticks = nowUtc.Ticks - nowUtc.Ticks % interval.Ticks;
        return new DateTime(ticks, DateTimeKind.Utc);
    }

    /// <summary>OpenTime последней закрытой свечи для момента nowUtc.</summary>
    public static DateTime GetLastClosedOpenTime(DateTime nowUtc, TimeSpan interval) =>
        GetCurrentOpenTime(nowUtc, interval) - interval;
}

/// <summary>Результат попытки добавить закрытую свечу в последовательность индикаторов.</summary>
public enum CandleAppendResult
{
    Accepted,
    Duplicate,
    OutOfOrder,
    Gap
}
