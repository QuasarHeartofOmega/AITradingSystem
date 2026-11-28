namespace AITradingSystem.Core.Indicators;

/// <summary>
/// Phase 1.6: версия формул индикаторов. Записывается в E1
/// (<c>GateVersions["indicators"]</c>), чтобы бакеты решений, собранные на старых
/// формулах (ключа нет — считаем v1), помечались несравнимыми с v2.
///
/// v2 (2026-11-28):
///  - ADX: сглаживание +DM/−DM/TR/DX через RMA Уайлдера (α=1/p, SMA-затравка)
///    вместо EMA; при +DM == −DM обнуляются оба DM; первый ADX = SMA первых p DX;
///  - EMA: затравка SMA первых p значений (конвенция TA-Lib) вместо первой цены;
///  - RSI: плоский рынок (avgGain == avgLoss == 0) → 50 вместо 100;
///  - Supertrend: инициализация на свече готовности ATR (без транзиентной нулевой полосы);
///  - OBV.Trend: scale-free нормировка (отклонение от EMA20 в средних объёмах бара,
///    насыщение ±1 при 3 барах) вместо ×10/(|EMA|+1);
///  - закрытие свечи: по биржевому флагу "x" (OHLCV.ClosedByExchange), локальные
///    часы — только fallback (WS: payload; REST: серверное время TIME-SYNC).
/// </summary>
public static class IndicatorFormulaVersion
{
    /// <summary>Текущая версия формул (значение для E1 GateVersions["indicators"]).
    /// Записи до 1.6 не содержат этого ключа — трактовать как v1 и не сравнивать с v2.</summary>
    public const string Version = "2";
}
