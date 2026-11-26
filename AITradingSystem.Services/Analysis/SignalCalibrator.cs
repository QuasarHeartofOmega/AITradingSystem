using System.Globalization;
using AITradingSystem.Common.Configuration;
using AITradingSystem.Common.Logging;
using AITradingSystem.Core.Models.Enums;
using AITradingSystem.Core.Models.Trading;

namespace AITradingSystem.Services.Analysis;

/// <summary>Один вклад в детерминированную оценку сетапа (для лога и промпта).</summary>
public sealed class ScoreComponent
{
    public string Name { get; init; } = string.Empty;
    public decimal Weight { get; init; }
    public decimal Value { get; init; }
    public decimal Contribution { get; init; }

    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture,
            "{0}={1:0.##} (w={2:0.##}, вклад {3:0.###})", Name, Value, Weight, Contribution);
}

/// <summary>
/// Детерминированная оценка сетапа БЕЗ ИИ (B9): направление, prior-уверенность и причины.
/// Модель получает её как «Engine pre-score» и якорится на фактах, а не на выдуманном числе.
/// </summary>
public sealed class EngineScore
{
    /// <summary>Направление и сила сетапа в [-1; 1]: &gt;0 — long, &lt;0 — short/exit.</summary>
    public decimal Direction { get; init; }

    /// <summary>Prior-уверенность движка в [0.1; 0.9].</summary>
    public decimal PriorConfidence { get; init; }

    public IReadOnlyList<ScoreComponent> Components { get; init; } = Array.Empty<ScoreComponent>();
    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();

    /// <summary>P2.2: нейтральные факты для промпта (без формулировок «штраф/бонус»).
    /// Reasons остаются для лога/аудита и содержат модификаторы prior; в промпт они не идут,
    /// чтобы модель не вычитала второй раз за те же ADX/объём/BB/волатильность.</summary>
    public IReadOnlyList<string> Facts { get; init; } = Array.Empty<string>();

    /// <summary>LONG / SHORT / FLAT — как в промпте.</summary>
    public string DirectionName => Direction > 0.05m ? "LONG" : Direction < -0.05m ? "SHORT" : "FLAT";

    public string Summary =>
        string.Format(CultureInfo.InvariantCulture,
            "direction={0}, score={1:0.##}, prior_confidence={2:0.##}", DirectionName, Direction, PriorConfidence);

    public string ReasonsText => Reasons.Count == 0 ? "нет выраженных факторов" : string.Join("; ", Reasons);
}

/// <summary>Параметры калибровки: пороги, спот-ограничения и цены для детерминированного входа.</summary>
public sealed class CalibrationContext
{
    /// <summary>
    /// Порог FINAL confidence. Значение по умолчанию — документированный fallback
    /// «с потолка» (<see cref="ConfidenceExpectancyOptions.FallbackMinConfidence"/>);
    /// рабочее значение приходит из AI:MinConfidence, который P2.4 подбирает по
    /// expectancy после издержек на истории сделок (ConfidenceThresholdTuner).
    /// </summary>
    public decimal MinConfidence { get; init; } = ConfidenceExpectancyOptions.FallbackMinConfidence;
    public bool HasPosition { get; init; }
    public bool AllowShort { get; init; }

    /// <summary>Текущая цена (для расчёта entry/SL/TP, если действие пересмотрено движком).</summary>
    public decimal Price { get; init; }

    /// <summary>ATR в абсолютных единицах цены.</summary>
    public decimal AtrAbsolute { get; init; }

    public decimal StopLossAtrMultiplier { get; init; } = 2m;
    public decimal MinRiskReward { get; init; } = 2m;

    /// <summary>Знаков после запятой для цен символа (tickSize, D24).</summary>
    public int PriceDecimals { get; init; } = 8;
}

/// <summary>
/// B9. Калибратор уверенности: математика — алгоритмам, ИИ — качественная оценка.
///
/// PreScore считает направление/силу сетапа из индикаторов (тренд, моментум, ADX, объём,
/// волатильность, положение в Bollinger). Calibrate сводит уверенность модели и prior движка
/// по политике AI:ConfidencePolicy, лечит «якорные» ответы модели (HOLD conf≈0.45) и, с P2.5,
/// пересматривает любой HOLD с сильным согласованным prior.
/// SL/TP/сайзинг остаются за RiskManager; здесь цены считаются только если движок
/// сам меняет HOLD на вход (иначе сигнал был бы без цен и всё равно отклонился).
/// </summary>
public sealed class SignalCalibrator
{
    // P2.6: веса/модификаторы больше не константы — они пришли из AI:ScoringWeights
    // (дефолты ScoringWeightsConfig ТОЧНО воспроизводят прежние значения: EMA 0.30 /
    // Supertrend 0.20 / EMA-pos 0.15 / RSI 0.15 / MACD 0.10 / Stoch 0.05 / OBV 0.05,
    // prior = 0.2 + 0.7·|score|, штрафы ×0.6/×0.8/…). Валидация весов — P2.6:
    // ScoringCalibrationAnalyzer + tools/scoring_calibration.py; рантайм их не меняет.
    private readonly ScoringWeightsConfig _weights;

    /// <summary>Параметр опционален: старые вызовы (в т.ч. тесты) получают прежние константы.</summary>
    public SignalCalibrator(ScoringWeightsConfig? weights = null)
    {
        _weights = weights ?? new ScoringWeightsConfig();
    }

    /// <summary>
    /// Детерминированный pre-score по словарю индикаторов (нормализованные + сырые ключи B10).
    /// Работает и без сырых ключей — тогда берутся нормализованные аналоги.
    /// </summary>
    public EngineScore PreScore(Dictionary<string, decimal> indicators)
    {
        if (indicators == null)
            throw new ArgumentNullException(nameof(indicators));

        var components = new List<ScoreComponent>();
        var reasons = new List<string>();
        var facts = new List<string>();

        // P2.6: градуированное выравнивание (±1/±0.5/0) вместо строгого троичного —
        // только при явном включении в конфиге; ключ отсутствует (старый снапшот) → строгий.
        var emaAlign = _weights.UseGraduatedEmaAlignment && indicators.TryGetValue("ema_align_grad", out var grad)
            ? grad
            : Get(indicators, "ema_align");
        var supertrend = Get(indicators, "supertrend");
        var emaPos = (Get(indicators, "ema20_pos") + Get(indicators, "ema50_pos") + Get(indicators, "ema200_pos")) / 3m;

        var rsiRaw = indicators.TryGetValue("rsi_raw", out var rr) ? rr : Get(indicators, "rsi") * 50m + 50m;
        var rsiComponent = ScoringMath.RsiScore(rsiRaw);

        var macd = Get(indicators, "macd_hist");
        var stochK = indicators.TryGetValue("stoch_k_raw", out var sk) ? sk / 100m : Get(indicators, "stoch_rsi_k");
        var stochD = indicators.TryGetValue("stoch_d_raw", out var sd) ? sd / 100m : Get(indicators, "stoch_rsi_d");
        var stoch = ScoringMath.StochScore(stochK, stochD);
        var obv = Get(indicators, "obv_trend");

        Add(components, "ema_align", _weights.EmaAlignment, emaAlign);
        Add(components, "supertrend", _weights.Supertrend, supertrend);
        Add(components, "ema_position", _weights.EmaPosition, emaPos);
        Add(components, "rsi", _weights.Rsi, rsiComponent);
        Add(components, "macd_hist", _weights.Macd, macd);
        Add(components, "stoch_rsi", _weights.StochRsi, stoch);
        Add(components, "obv_trend", _weights.Obv, obv);

        var direction = components.Sum(c => c.Contribution);

        // --- Модификаторы (штрафы/бонусы к силе сетапа, не к направлению) ---
        var adxRaw = indicators.TryGetValue("adx_raw", out var ar) ? ar : Get(indicators, "adx") * 100m;
        var adxFactor = adxRaw >= 25m ? 1.0m : adxRaw >= 20m ? _weights.AdxMidFactor : _weights.AdxWeakFactor;
        if (adxRaw < 20m)
            reasons.Add(string.Format(CultureInfo.InvariantCulture, "слабый тренд: ADX {0:0.#} < 20 (штраф ×{1:0.##})", adxRaw, _weights.AdxWeakFactor));
        else if (adxRaw >= 25m)
            reasons.Add(string.Format(CultureInfo.InvariantCulture, "тренд подтверждён: ADX {0:0.#} >= 25", adxRaw));

        var volumeRatio = Get(indicators, "volume_ratio");
        var volumeFactor = volumeRatio <= 0m ? 1.0m
            : volumeRatio >= 1.5m ? _weights.VolumeHighFactor
            : volumeRatio >= 1.0m ? 1.0m
            : volumeRatio >= 0.6m ? _weights.VolumeMidFactor
            : _weights.VolumeLowFactor;
        if (volumeRatio > 0m && volumeRatio < 0.6m)
            reasons.Add(string.Format(CultureInfo.InvariantCulture, "объём ниже среднего: volume_ratio {0:0.##} (штраф ×{1:0.##})", volumeRatio, _weights.VolumeLowFactor));
        else if (volumeRatio >= 1.5m)
            reasons.Add(string.Format(CultureInfo.InvariantCulture, "объём подтверждает движение: volume_ratio {0:0.##}", volumeRatio));

        var atrPct = Get(indicators, "atr_pct");
        var volatilityFactor = 1.0m;
        if (atrPct > 0m && atrPct < 0.001m)
        {
            volatilityFactor = _weights.LowVolatilityFactor;
            reasons.Add(string.Format(CultureInfo.InvariantCulture, "волатильность слишком низкая: ATR {0:0.###}% (штраф ×{1:0.##})", atrPct * 100m, _weights.LowVolatilityFactor));
        }
        else if (atrPct > 0.06m)
        {
            volatilityFactor = _weights.ExtremeVolatilityFactor;
            reasons.Add(string.Format(CultureInfo.InvariantCulture, "волатильность экстремальная: ATR {0:0.##}% (штраф ×{1:0.##})", atrPct * 100m, _weights.ExtremeVolatilityFactor));
        }

        var bbPos = Get(indicators, "bb_pos");
        var bbFactor = 1.0m;
        if (direction > 0m && bbPos > 0.85m)
        {
            bbFactor = _weights.BollingerOverheatFactor;
            reasons.Add(string.Format(CultureInfo.InvariantCulture, "цена у верхней полосы Bollinger — вход в long перегрет (штраф ×{0:0.##})", _weights.BollingerOverheatFactor));
        }
        else if (direction < 0m && bbPos < -0.85m)
        {
            bbFactor = _weights.BollingerOverheatFactor;
            reasons.Add(string.Format(CultureInfo.InvariantCulture, "цена у нижней полосы Bollinger — выход/short перегрет (штраф ×{0:0.##})", _weights.BollingerOverheatFactor));
        }

        var score = Math.Clamp(direction * adxFactor * volumeFactor * volatilityFactor * bbFactor, -1m, 1m);
        var prior = Math.Clamp(_weights.PriorBase + _weights.PriorScale * Math.Abs(score), 0.1m, 0.9m);

        // --- Человекочитаемые причины (уходят в промпт и в лог) ---
        if (emaAlign > 0m) reasons.Add("EMA20>EMA50>EMA200 — бычье выравнивание");
        else if (emaAlign < 0m) reasons.Add("EMA20<EMA50<EMA200 — медвежье выравнивание");
        else reasons.Add("EMA перепутаны — тренд не определён");

        reasons.Add(supertrend > 0m ? "Supertrend вверх" : "Supertrend вниз");

        if (rsiRaw >= 70m)
            reasons.Add(string.Format(CultureInfo.InvariantCulture, "RSI {0:0.#} — перекупленность", rsiRaw));
        else if (rsiRaw <= 30m)
            reasons.Add(string.Format(CultureInfo.InvariantCulture, "RSI {0:0.#} — перепроданность", rsiRaw));
        else
            reasons.Add(string.Format(CultureInfo.InvariantCulture, "RSI {0:0.#} — рабочая зона", rsiRaw));

        if (macd > 0.1m) reasons.Add("MACD-гистограмма положительная");
        else if (macd < -0.1m) reasons.Add("MACD-гистограмма отрицательная");

        if (stochK > stochD && stochK < 0.8m) reasons.Add("StochRSI K выше D (бычий крест)");
        else if (stochK < stochD && stochK > 0.2m) reasons.Add("StochRSI K ниже D (медвежий крест)");

        if (obv > 0.1m) reasons.Add("OBV растёт — объём поддерживает тренд");
        else if (obv < -0.1m) reasons.Add("OBV падает — объём против тренда");

        // P2.2: факты для модели — только значения, без оценок/штрафов.
        facts.Add(string.Format(CultureInfo.InvariantCulture, "ema_align={0:0.##}", emaAlign));
        facts.Add(string.Format(CultureInfo.InvariantCulture, "supertrend={0:0.##}", supertrend));
        facts.Add(string.Format(CultureInfo.InvariantCulture, "adx_raw={0:0.#}", adxRaw));
        facts.Add(string.Format(CultureInfo.InvariantCulture, "volume_ratio={0:0.##}", volumeRatio));
        facts.Add(string.Format(CultureInfo.InvariantCulture, "atr_pct={0:0.####}", atrPct));
        facts.Add(string.Format(CultureInfo.InvariantCulture, "bb_pos={0:0.##}", bbPos));
        facts.Add(string.Format(CultureInfo.InvariantCulture, "rsi_raw={0:0.#}", rsiRaw));
        facts.Add(string.Format(CultureInfo.InvariantCulture, "macd_hist={0:0.###}", macd));

        return new EngineScore
        {
            Direction = score,
            PriorConfidence = prior,
            Components = components,
            Reasons = reasons,
            Facts = facts
        };
    }

    /// <summary>
    /// Сводит уверенность модели и prior движка по политике; лечит дегенерат HOLD conf≈0.45.
    /// Blend (P2.3): final = w·model + (1−w)·prior, w = blendModelWeight (по умолчанию 0.7 —
    /// независимая оценка модели весит больше prior); дегенерат по-прежнему уходит в prior.
    /// P2.5: пересмотр не ограничен окном 0.45±0.02 — любой HOLD с сильным согласованным
    /// prior (prior >= порога, направление не-флэт) уходит в prior с source Hold→Prior.
    /// Действие пересматривается ТОЛЬКО при явном согласии фактов (prior >= порога, направление
    /// совпадает, позиции нет) — и тогда цены входа считает математика, а не модель.
    /// </summary>
    public TradingSignal Calibrate(
        TradingSignal signal,
        EngineScore score,
        ConfidencePolicy policy,
        CalibrationContext context,
        decimal blendModelWeight = 0.7m)
    {
        if (signal == null) throw new ArgumentNullException(nameof(signal));
        if (score == null) throw new ArgumentNullException(nameof(score));
        if (context == null) throw new ArgumentNullException(nameof(context));

        var modelWeight = Math.Clamp(blendModelWeight, 0m, 1m);
        var modelConf = signal.ModelConfidence > 0m ? signal.ModelConfidence : signal.Confidence;
        var prior = score.PriorConfidence;

        // B9: отказ инференса — не мнение модели. Никакая политика (включая EngineOverride)
        // не имеет права превращать фолбэк-HOLD в BUY: движок не «знает лучше», когда ИИ упал.
        if (signal.IsInferenceFailure)
        {
            signal.ModelConfidence = modelConf;
            signal.EngineConfidence = prior;
            signal.IsDegenerateConfidence = false;
            signal.Confidence = 0m;
            signal.ConfidenceSource = "Failure→Hold";
            signal.Action = SignalAction.Hold;

            TradeLog.Decision(signal.Symbol, "CALIBRATE",
                string.Format(CultureInfo.InvariantCulture,
                    "отказ инференса — HOLD без пересмотра (policy={0}, engine_prior={1:0.##}): {2}",
                    policy, prior, signal.Reasoning));
            return signal;
        }

        var degenerate = signal.IsDegenerateConfidence ||
                         (signal.Action == SignalAction.Hold && Math.Abs(modelConf - 0.45m) <= 0.02m);

        // P2.5: «дегенерат» больше не ограничен узким окном 0.45±0.02. Любой HOLD с сильным
        // согласованным prior (prior >= порога и направление явно не-флэт; short — только если
        // разрешён) движок вправе пересмотреть. Guards прежние: позиции нет, модель «жива»
        // (conf > 0), политика Blend. Узкое окно сохраняет приоритет и свой source в логе.
        var strongAlignedPrior = prior >= context.MinConfidence &&
                                 (score.Direction > 0.05m ||
                                  (score.Direction < -0.05m && context.AllowShort));
        var holdOverride = policy == ConfidencePolicy.Blend &&
                           signal.Action == SignalAction.Hold &&
                           modelConf > 0m &&
                           !context.HasPosition &&
                           strongAlignedPrior;

        decimal final;
        string source;

        switch (policy)
        {
            case ConfidencePolicy.ModelOnly:
                final = modelConf;
                source = "ModelOnly";
                break;

            case ConfidencePolicy.EngineOverride:
                final = prior;
                source = "EngineOverride";
                break;

            default:
                if (degenerate)
                {
                    final = prior;
                    source = "Degenerate→Prior";
                }
                else if (holdOverride)
                {
                    // P2.5: движок берёт уверенность из своего prior — иначе blend с низким
                    // conf модели убил бы пересмотренный вход на пороге MinConfidence.
                    final = prior;
                    source = "Hold→Prior";
                }
                else
                {
                    // P2.3: уход от 50/50 — независимая оценка модели весит больше prior.
                    final = modelWeight * modelConf + (1m - modelWeight) * prior;
                    source = "Blend";
                }
                break;
        }

        final = Math.Clamp(final, 0m, 1m);

        signal.ModelConfidence = modelConf;
        signal.EngineConfidence = prior;
        signal.IsDegenerateConfidence = degenerate;
        signal.Confidence = final;
        signal.ConfidenceSource = source;

        // Пересмотр действия: когда модель «заякорилась» (окно 0.45±0.02), либо любой HOLD
        // с сильным согласованным prior (P2.5), либо движок решает сам (EngineOverride),
        // позиции нет и факты уверенно указывают направление. Нулевая уверенность модели
        // (отказ/пустой ответ) пересмотр запрещает при любой политике.
        var mayReconsider = policy != ConfidencePolicy.ModelOnly &&
                            (degenerate || holdOverride || policy == ConfidencePolicy.EngineOverride) &&
                            modelConf > 0m &&
                            signal.Action == SignalAction.Hold &&
                            !context.HasPosition &&
                            final >= context.MinConfidence;

        if (mayReconsider)
        {
            if (score.Direction > 0.05m)
                TryUpgrade(signal, SignalAction.Buy, score, context);
            else if (score.Direction < -0.05m && context.AllowShort)
                TryUpgrade(signal, SignalAction.Sell, score, context);
        }

        TradeLog.Decision(signal.Symbol, "CALIBRATE",
            string.Format(CultureInfo.InvariantCulture,
                "model_conf={0:0.##}, engine_prior={1:0.##}, final={2:0.##}, policy={3}, source={4}, blend_w={5:0.##}, action={6}, {7}",
                modelConf, prior, final, policy, source, modelWeight, signal.Action, score.Summary));

        return signal;
    }

    /// <summary>
    /// P0.5: детерминированный сигнал БЕЗ LLM. Направление и prior — из PreScore,
    /// цены — из той же математики, что и ENGINE-ENTRY (entry=цена, SL=k·ATR, TP=R/R·risk).
    /// Слабый/флэт-сетап остаётся HOLD. Модель в этом пути не участвует вообще.
    /// </summary>
    public TradingSignal BuildEngineSignal(string symbol, EngineScore score, CalibrationContext context)
    {
        if (score == null) throw new ArgumentNullException(nameof(score));
        if (context == null) throw new ArgumentNullException(nameof(context));

        var signal = new TradingSignal
        {
            Symbol = symbol,
            Action = SignalAction.Hold,
            Confidence = score.PriorConfidence,
            ModelConfidence = 0m,
            EngineConfidence = score.PriorConfidence,
            ConfidenceSource = "EngineOnly",
            Reasoning = $"Детерминированный сетап движка ({score.Summary}): {score.ReasonsText}",
            Timestamp = DateTime.UtcNow,
            ModelVersion = "engine-deterministic"
        };

        if (!context.HasPosition && score.PriorConfidence >= context.MinConfidence)
        {
            if (score.Direction > 0.05m)
                TryUpgrade(signal, SignalAction.Buy, score, context);
            else if (score.Direction < -0.05m && context.AllowShort)
                TryUpgrade(signal, SignalAction.Sell, score, context);
        }

        TradeLog.Decision(symbol, "ENGINE-SIGNAL",
            string.Format(CultureInfo.InvariantCulture,
                "action={0}, prior={1:0.##}, порог={2:0.##}, source=EngineOnly (LLM вне критического пути)",
                signal.Action, score.PriorConfidence, context.MinConfidence));

        return signal;
    }

    /// <summary>
    /// HOLD → вход по детерминированным ценам: entry = текущая цена,
    /// SL = entry − k·ATR, TP = entry + R/R·risk. Без цен сигнал был бы отклонён как невалидный.
    /// </summary>
    /// <summary>P0.6: отдельного reversal setup нет; вход строго согласован с направлением движка.</summary>
    public static bool IsEntryDirectionAllowed(SignalAction action, decimal engineDirection) =>
        action == SignalAction.Buy ? engineDirection > 0.05m
        : action == SignalAction.Sell && engineDirection < -0.05m;

    private static void TryUpgrade(TradingSignal signal, SignalAction action, EngineScore score, CalibrationContext ctx)
    {
        if (!IsEntryDirectionAllowed(action, score.Direction))
        {
            TradeLog.Rejected(signal.Symbol, "symbol-policy",
                $"direction_mismatch: engine={score.DirectionName}, proposed={action}; отдельный reversal setup не реализован");
            return;
        }
        if (ctx.Price <= 0m)
        {
            TradeLog.Rejected(signal.Symbol, "engine-prices",
                $"нет корректной цены для расчёта входа (price={ctx.Price}) — сетап остался HOLD");
            return;
        }

        var atr = ctx.AtrAbsolute > 0m ? ctx.AtrAbsolute : ctx.Price * 0.01m;
        var risk = atr * Math.Max(0.1m, ctx.StopLossAtrMultiplier);
        var reward = risk * Math.Max(1m, ctx.MinRiskReward);
        var decimals = Math.Clamp(ctx.PriceDecimals, 0, 18);

        var entry = Math.Round(ctx.Price, decimals, MidpointRounding.AwayFromZero);
        decimal stop, target;

        if (action == SignalAction.Buy)
        {
            stop = Math.Round(entry - risk, decimals, MidpointRounding.AwayFromZero);
            target = Math.Round(entry + reward, decimals, MidpointRounding.AwayFromZero);
        }
        else
        {
            stop = Math.Round(entry + risk, decimals, MidpointRounding.AwayFromZero);
            target = Math.Round(entry - reward, decimals, MidpointRounding.AwayFromZero);
        }

        if (stop <= 0m || target <= 0m || entry <= 0m)
        {
            // Молчаливый return здесь выглядел как «движок решил HOLD», хотя это отказ расчёта цен.
            TradeLog.Rejected(signal.Symbol, "engine-prices",
                $"цены входа обнулились: price={ctx.Price}, price_decimals={decimals}, atr={atr}, " +
                $"entry={entry}, sl={stop}, tp={target} — проверь tickSize/PriceDecimals символа и множитель SL");
            return;
        }

        signal.Action = action;
        signal.EntryPrice = entry;
        signal.StopLoss = stop;
        signal.TakeProfit = target;
        signal.Reasoning =
            $"Движок пересмотрел HOLD по фактам ({score.Summary}): {score.ReasonsText}. " +
            $"Цены рассчитаны алгоритмом: entry={entry}, SL={stop} ({ctx.StopLossAtrMultiplier}×ATR), TP={target}. " +
            (string.IsNullOrWhiteSpace(signal.Reasoning) ? string.Empty : "Модель: " + signal.Reasoning);

        TradeLog.Decision(signal.Symbol, "ENGINE-ENTRY",
            string.Format(CultureInfo.InvariantCulture,
                "HOLD → {0}: prior={1:0.##} >= порога {2:0.##}, entry={3}, sl={4}, tp={5}",
                action, score.PriorConfidence, ctx.MinConfidence, entry, stop, target));
    }

    private static decimal Get(Dictionary<string, decimal> dict, string key) =>
        dict.TryGetValue(key, out var value) ? value : 0m;

    private static void Add(List<ScoreComponent> list, string name, decimal weight, decimal value)
    {
        var v = Math.Clamp(value, -1m, 1m);
        list.Add(new ScoreComponent
        {
            Name = name,
            Weight = weight,
            Value = v,
            Contribution = v * weight
        });
    }

    // P2.6: RsiScore/StochScore переехали в чистый Core ScoringMath — общие формулы
    // для рантайма, анализатора калибровки и оффлайн-инструмента (без расхождений).
}