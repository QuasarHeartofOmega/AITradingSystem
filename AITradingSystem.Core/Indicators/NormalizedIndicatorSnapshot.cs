using AITradingSystem.Core.Models.MarketData;

namespace AITradingSystem.Core.Indicators;

/// <summary>
/// Нормализованный снимок индикаторов для AI-модели (FR-3.3).
/// Все значения приведены к диапазону [-1; 1] или [0; 1].
/// </summary>
public sealed class NormalizedIndicatorSnapshot
{
    public string Symbol { get; }
    public DateTime Timestamp { get; }
    public decimal Price { get; }

    // Тренд (все в [-1; 1])
    public decimal EMA20Position { get; } // позиция цены относительно EMA20
    public decimal EMA50Position { get; }
    public decimal EMA200Position { get; }
    public decimal EMAAlignment { get; } // 1 = бычье выравнивание, -1 = медвежье
    /// <summary>
    /// P2.6: градуированное выравнивание EMA: ±1 — полное (20&gt;50&gt;200 / 20&lt;50&lt;200),
    /// ±0.5 — частичное (одна пара выстроена, вторая нет), 0 — пары смотрят в разные стороны.
    /// Диагностика P2.1: строгий ema_align = 0 в 28/30 случаев — крупнейший вес не contributes.
    /// Скоринг использует этот ключ только при AI:ScoringWeights:UseGraduatedEmaAlignment=true.
    /// </summary>
    public decimal EMAAlignmentGraduated { get; }
    public decimal SupertrendDirection { get; } // 1 = uptrend, -1 = downtrend
    public decimal ADXStrength { get; } // [0; 1] — сила тренда

    // Импульс (все в [-1; 1])
    public decimal RSI { get; } // нормализованный RSI
    public decimal MACDHistogram { get; } // нормализованный гистограм
    public decimal StochRSI_K { get; } // [0; 1]
    public decimal StochRSI_D { get; } // [0; 1]

    // Волатильность (все в [0; 1])
    public decimal ATRPercent { get; } // ATR в % от цены
    public decimal BollingerWidth { get; } // ширина BB
    public decimal BollingerPosition { get; } // позиция цены в BB [-1; 1]

    // Объем (все в [-1; 1])
    public decimal OBVTrend { get; } // тренд OBV
    public decimal VWAPDeviation { get; } // отклонение от VWAP

    // ------------------------------------------------------------------
    // B10. СЫРЫЕ значения: модели и детерминированному скорингу нужны факты,
    // а не только нормализованные величины (иначе confidence берётся «из правила»).
    // ------------------------------------------------------------------
    public decimal RsiRaw { get; }              // 0..100
    public decimal AdxRaw { get; }              // 0..100
    public decimal StochKRaw { get; }           // 0..100
    public decimal StochDRaw { get; }           // 0..100
    public decimal AtrAbsolute { get; }         // в цене
    public decimal Ema20 { get; }
    public decimal Ema50 { get; }
    public decimal Ema200 { get; }
    public decimal BollingerUpper { get; }
    public decimal BollingerLower { get; }
    public decimal MacdHistogramAbsolute { get; }
    public decimal Vwap { get; }
    public decimal VolumeRatio { get; }         // объём последней закрытой свечи / SMA20 объёма

    public NormalizedIndicatorSnapshot(string symbol, OHLCV candle, IndicatorCalculator calc)
    {
        Symbol = symbol;
        Timestamp = candle.CloseTime;
        Price = candle.Close;

        // Тренд
        EMA20Position = NormalizeDeviation(candle.Close, calc.EMA20.GetValue());
        EMA50Position = NormalizeDeviation(candle.Close, calc.EMA50.GetValue());
        EMA200Position = NormalizeDeviation(candle.Close, calc.EMA200.GetValue());

        var ema20 = calc.EMA20.GetValue();
        var ema50 = calc.EMA50.GetValue();
        var ema200 = calc.EMA200.GetValue();
        EMAAlignment = (ema20 > ema50 && ema50 > ema200) ? 1m :
                       (ema20 < ema50 && ema50 < ema200) ? -1m : 0m;
        // P2.6: половина балла за каждую выстроенную пару (20vs50 и 50vs200).
        EMAAlignmentGraduated = (Math.Sign(ema20 - ema50) + Math.Sign(ema50 - ema200)) / 2m;

        SupertrendDirection = calc.Supertrend.IsUptrend ? 1m : -1m;
        ADXStrength = Math.Min(calc.ADX.Value / 100m, 1m); // ADX обычно 0-100

        // Импульс
        RSI = (calc.RSI.GetValue() - 50m) / 50m; // RSI 0-100 → -1..1
        var macdHist = calc.MACD.Histogram;
        MACDHistogram = NormalizeWithATR(macdHist, calc.ATR.GetValue());
        StochRSI_K = calc.StochasticRsi.K / 100m;
        StochRSI_D = calc.StochasticRsi.D / 100m;

        // Волатильность
        ATRPercent = calc.ATR.GetValue() / candle.Close;
        BollingerWidth = calc.BollingerBands.Width;
        var bbUpper = calc.BollingerBands.Upper;
        var bbLower = calc.BollingerBands.Lower;
        var bbRange = bbUpper - bbLower;
        BollingerPosition = bbRange > 0 ? (candle.Close - bbLower) / bbRange * 2m - 1m : 0;

        // Объем
        OBVTrend = calc.OBV.Trend;
        var vwap = calc.VWAP.GetValue();
        VWAPDeviation = vwap > 0 ? (candle.Close - vwap) / vwap : 0;

        // B10. Сырые значения (нормализованные ключи не меняются — обратная совместимость).
        RsiRaw = calc.RSI.GetValue();
        AdxRaw = calc.ADX.Value;
        StochKRaw = calc.StochasticRsi.K;
        StochDRaw = calc.StochasticRsi.D;
        AtrAbsolute = calc.ATR.GetValue();
        Ema20 = ema20;
        Ema50 = ema50;
        Ema200 = ema200;
        BollingerUpper = bbUpper;
        BollingerLower = bbLower;
        MacdHistogramAbsolute = macdHist;
        Vwap = vwap;
        // currentCandle может быть незакрытым тиком: его цена нужна для оценки,
        // но объём для скоринга берём только из последней закрытой свечи калькулятора.
        VolumeRatio = calc.VolumeRatio;
    }

    /// <summary>
    /// Нормализация отклонения в % к диапазону [-1; 1].
    /// </summary>
    private static decimal NormalizeDeviation(decimal price, decimal indicator)
    {
        if (indicator == 0) return 0;
        var deviation = (price - indicator) / indicator;
        return Math.Max(-1m, Math.Min(1m, deviation * 10m)); // умножаем на 10 для чувствительности
    }

    /// <summary>
    /// Нормализация значения через ATR.
    /// </summary>
    private static decimal NormalizeWithATR(decimal value, decimal atr)
    {
        if (atr == 0) return 0;
        var normalized = value / atr;
        return Math.Max(-1m, Math.Min(1m, normalized / 3m));
    }

    /// <summary>
    /// Преобразовать в словарь для промпта.
    /// </summary>
    public Dictionary<string, decimal> ToDictionary()
    {
        return new Dictionary<string, decimal>
        {
            ["price"] = Price,
            ["ema20_pos"] = EMA20Position,
            ["ema50_pos"] = EMA50Position,
            ["ema200_pos"] = EMA200Position,
            ["ema_align"] = EMAAlignment,
            ["ema_align_grad"] = EMAAlignmentGraduated,
            ["supertrend"] = SupertrendDirection,
            ["adx"] = ADXStrength,
            ["rsi"] = RSI,
            ["macd_hist"] = MACDHistogram,
            ["stoch_rsi_k"] = StochRSI_K,
            ["stoch_rsi_d"] = StochRSI_D,
            ["atr_pct"] = ATRPercent,
            ["bb_width"] = BollingerWidth,
            ["bb_pos"] = BollingerPosition,
            ["obv_trend"] = OBVTrend,
            ["vwap_dev"] = VWAPDeviation,

            // B10. Сырые значения для рубрики уверенности и pre-score движка.
            ["rsi_raw"] = RsiRaw,
            ["adx_raw"] = AdxRaw,
            ["stoch_k_raw"] = StochKRaw,
            ["stoch_d_raw"] = StochDRaw,
            ["atr_abs"] = AtrAbsolute,
            ["ema20"] = Ema20,
            ["ema50"] = Ema50,
            ["ema200"] = Ema200,
            ["bb_upper"] = BollingerUpper,
            ["bb_lower"] = BollingerLower,
            ["macd_hist_abs"] = MacdHistogramAbsolute,
            ["vwap"] = Vwap,
            ["volume_ratio"] = VolumeRatio
        };
    }
}