using System.Globalization;

namespace AITradingSystem.Common.Extensions;

/// <summary>
/// Расширения для работы с decimal (ценовые精度 Binance).
/// </summary>
public static class DecimalExtensions
{
    /// <summary>
    /// Округление вниз до указанного количества знаков (floor для цен).
    /// Binance требует точного stepSize.
    /// </summary>
    public static decimal FloorToPrecision(this decimal value, int decimals)
    {
        if (decimals < 0)
            throw new ArgumentOutOfRangeException(nameof(decimals));

        var multiplier = (decimal)Math.Pow(10, decimals);
        return Math.Floor(value * multiplier) / multiplier;
    }

    /// <summary>
    /// Форматирование с invariant culture (для API).
    /// </summary>
    public static string ToInvariantString(this decimal value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Форматирование с invariant culture и фиксированным числом знаков.
    /// </summary>
    public static string ToInvariantString(this decimal value, int decimals)
    {
        return value.ToString($"F{decimals}", CultureInfo.InvariantCulture);
    }
}