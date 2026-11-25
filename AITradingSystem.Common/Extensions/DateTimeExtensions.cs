namespace AITradingSystem.Common.Extensions;

public static class DateTimeExtensions
{
    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Конвертация DateTime в Unix timestamp в миллисекундах (формат Binance).
    /// </summary>
    public static long ToUnixMilliseconds(this DateTime dateTime)
    {
        if (dateTime.Kind != DateTimeKind.Utc)
            dateTime = dateTime.ToUniversalTime();

        return (long)(dateTime - UnixEpoch).TotalMilliseconds;
    }

    /// <summary>
    /// Конвертация Unix timestamp (мс) в DateTime UTC.
    /// </summary>
    public static DateTime FromUnixMilliseconds(long milliseconds)
    {
        return UnixEpoch.AddMilliseconds(milliseconds);
    }
}