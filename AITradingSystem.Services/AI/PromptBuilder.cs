using System.Globalization;
using System.Text;

namespace AITradingSystem.Services.AI;

/// <summary>
/// Формирование промптов для AI-модели в формате ChatML (FR-4.2, B8).
///
/// Ключевое отличие от прежней версии: confidence НЕ якорится правилом «below 0.55».
/// Модель получает легенду индикаторов, детерминированный pre-score движка и рубрику,
/// по которой обязана вывести уверенность из фактов. Thinking остаётся включённым
/// (бюджет урезан), пустой think-блок в конце промпта НЕ префиллится (F3: давал 0 символов).
/// </summary>
public sealed class PromptBuilder
{
    private const string OpenThink = "<" + "think>";
    private const string CloseThink = "</" + "think>";

    /// <summary>Ключи индикаторов, которые являются ценой и форматируются по tick/decimals символа.</summary>
    private static readonly HashSet<string> PriceKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "price", "atr_abs", "ema20", "ema50", "ema200", "bb_upper", "bb_lower", "vwap", "macd_hist_abs"
    };

    public string BuildTradingPrompt(
        string symbol,
        Dictionary<string, decimal> indicators,
        string? newsContext,
        bool enableThinking = false,
        AiRequestContext? context = null)
    {
        context ??= new AiRequestContext();
        var sb = new StringBuilder();

        sb.AppendLine("<|im_start|>system");
        sb.AppendLine("You are a disciplined cryptocurrency SPOT trading analyst. You manage a real account and must avoid invalid orders.");
        sb.AppendLine("The trading engine computes all arithmetic (position size, stops, R/R). Your job is the qualitative judgement of the setup.");
        sb.AppendLine();
        sb.AppendLine("Hard rules:");
        sb.AppendLine(context.AllowShort
            ? "1. BUY opens a long, SELL opens a short, CLOSE closes the open position, HOLD does nothing."
            : "1. This is a SPOT account: shorting is NOT possible. BUY opens a long. SELL or CLOSE closes an existing long. Without an open position SELL is ignored, so use HOLD instead.");
        sb.AppendLine("2. If a position is already open, choose only HOLD or CLOSE (never re-open the same symbol).");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"3. For a new entry: stop_loss and take_profit must give NET risk/reward >= {Num(context.MinRiskReward)} after exchange fees on both legs. Quick check: |take_profit - entry_price| must be at least {Num(context.MinRiskReward)} times |entry_price - stop_loss|, plus extra for fees. Symmetric stop/target (equal distances) is always wrong. Never state a ratio you did not compute.");
        sb.AppendLine("4. For BUY: stop_loss < entry_price < take_profit. Prices must be realistic and close to the current price (within a few ATR).");
        sb.AppendLine("5. Never invent prices far from the market. entry_price should be within ~0.5% of the current price. Use the price precision shown in the context.");
        sb.AppendLine("6. confidence MUST be an independent assessment from the rubric below. The engine pre-score is context only; do NOT start from it and do NOT re-apply its indicator penalties. Do NOT default to any fixed number or copy a threshold from these rules.");
        sb.AppendLine("7. News sentiment is a secondary factor; technical structure has priority. Ignore headlines that are not about this asset.");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"8. The engine re-calculates the actual stop at {Num(context.StopLossAtrMultiplier)} x ATR from the real fill price, so keep stop_loss near that distance instead of an arbitrary round number.");
        sb.AppendLine();

        // --- Легенда индикаторов (B8) ---
        sb.AppendLine("Indicator legend:");
        sb.AppendLine("- Normalized keys: trend/momentum in [-1, 1] (positive = bullish), volatility in [0, 1]. ema20_pos/ema50_pos/ema200_pos = price deviation from that EMA (x10, clipped); ema_align = 1 bullish stack / -1 bearish stack / 0 mixed; supertrend = 1 up / -1 down; adx = ADX/100; rsi = (RSI-50)/50; macd_hist = MACD histogram / (3*ATR); stoch_rsi_k/d in [0, 1]; atr_pct = ATR/price; bb_width = band width; bb_pos = position inside bands [-1, 1]; obv_trend = OBV slope; vwap_dev = deviation from VWAP.");
        sb.AppendLine("- Raw keys (use these for the rubric): rsi_raw and adx_raw in [0, 100]; stoch_k_raw/stoch_d_raw in [0, 100]; atr_abs, ema20, ema50, ema200, bb_upper, bb_lower, vwap, macd_hist_abs are absolute prices; volume_ratio = last candle volume / 20-period average volume (1.0 = average).");
        sb.AppendLine();

        // --- Рубрика уверенности (P2.2: независимая оценка, без двойного штрафа) ---
        sb.AppendLine("Confidence rubric (independent assessment, follow it literally):");
        sb.AppendLine("a) Do NOT start from engine_prior_confidence. It is shown only for transparency; the engine blends it with your confidence later.");
        sb.AppendLine("b) Start from a neutral 0.50. Adjust only for independent qualitative evidence: asset-specific news, market structure (support/resistance, trend clarity), and event risk.");
        sb.AppendLine("c) Do NOT subtract again for EMA/Supertrend/MACD conflicts, adx_raw < 20, volume_ratio < 0.6, Bollinger-band pinning, or low/high ATR. Those modifiers are already included in the engine prior. You may mention them as context, but they must not change confidence a second time.");
        sb.AppendLine("d) Add +0.05 to +0.20 for independent confirmation (strong asset-specific positive news, clean structure, clear invalidation level). Subtract -0.05 to -0.20 only for independent risk (asset-specific negative news, broken structure, imminent event risk).");
        sb.AppendLine("e) Clamp the result to [0.10, 0.90]. Report that number as confidence — it must reflect independent evidence, not a habit and not a recomputation of the prior.");
        sb.AppendLine("f) Action follows the evidence: a coherent setup with independent confirmation is a BUY (or CLOSE when a position is open and the structure broke); a genuinely mixed setup without independent confirmation is HOLD with a mid-range confidence.");
        sb.AppendLine("Examples: strong aligned setup + positive asset-specific news -> BUY, confidence 0.70.");
        sb.AppendLine("Mixed technicals, no asset-specific news -> HOLD, confidence 0.50 (the engine prior already reflects weak ADX/volume).");
        sb.AppendLine("Asset-specific negative news or broken structure -> HOLD/CLOSE, confidence 0.30.");
        sb.AppendLine();

        sb.AppendLine("Answer format — ONE valid JSON object, no markdown, no comments, no extra text:");
        sb.AppendLine("{");
        sb.AppendLine("  \"action\": \"BUY\" | \"SELL\" | \"HOLD\" | \"CLOSE\",");
        sb.AppendLine("  \"confidence\": 0.0-1.0,");
        sb.AppendLine("  \"entry_price\": number or null,");
        sb.AppendLine("  \"stop_loss\": number or null,");
        sb.AppendLine("  \"take_profit\": number or null,");
        sb.AppendLine("  \"reasoning\": \"one or two sentences\"");
        sb.AppendLine("}");
        sb.AppendLine("For HOLD and CLOSE all price fields must be null.");

        if (enableThinking)
        {
            sb.AppendLine();
            sb.AppendLine("Before the JSON, write ONE short sentence inside " + OpenThink + " ... " + CloseThink + " (maximum 30 words):");
            sb.AppendLine("state only independent asset-specific evidence or its absence, the allowed action, and the confidence adjustment.");
            sb.AppendLine("Do not retell indicators, recalculate prices/ATR/R/R, or show step-by-step reasoning; the engine validates arithmetic.");
            sb.AppendLine("Close the think tag immediately after that sentence, then output ONLY the JSON object.");
        }

        sb.AppendLine("<|im_end|>");

        sb.AppendLine("<|im_start|>user");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Symbol: {symbol}");

        var priceText = indicators.TryGetValue("price", out var p) ? FmtPrice(p, context) : "unknown";
        sb.AppendLine(CultureInfo.InvariantCulture, $"Current price: {priceText}");
        // B8: у StringBuilder.AppendLine НЕТ перегрузки (IFormatProvider, string) — есть только
        // (IFormatProvider, ref AppendInterpolatedStringHandler). Конкатенация $"..." + (...)
        // даёт обычный string и не компилируется (CS1503). Форматируем строку целиком через
        // string.Create(IFormatProvider, ...), а в AppendLine отдаём готовый string.
        var tickText = context.TickSize.HasValue && context.TickSize.Value > 0
            ? string.Create(CultureInfo.InvariantCulture,
                $" (tick size {context.TickSize.Value:0.##########})")
            : string.Empty;
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"Price precision: {PriceDecimals(context)} decimals{tickText}"));

        if (indicators.TryGetValue("atr_pct", out var atrPct))
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"ATR: {(atrPct * 100m).ToString("0.###", CultureInfo.InvariantCulture)}% of price (use it to scale stop distance)");
        }

        sb.AppendLine();
        sb.AppendLine("Technical indicators:");
        foreach (var kvp in indicators)
        {
            if (string.Equals(kvp.Key, "price", StringComparison.OrdinalIgnoreCase))
                continue;

            var valueText = PriceKeys.Contains(kvp.Key)
                ? FmtPrice(kvp.Value, context)
                : kvp.Value.ToString("0.####", CultureInfo.InvariantCulture);

            sb.AppendLine(CultureInfo.InvariantCulture, $"- {kvp.Key}: {valueText}");
        }
        sb.AppendLine();

        // --- Детерминированный pre-score движка (P2.2: справка, не якорь рубрики) ---
        if (context.EngineScore.HasValue)
        {
            sb.AppendLine("Engine pre-score (deterministic context only; already includes ADX/volume/volatility/BB modifiers):");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"- direction: {context.EngineDirection ?? "FLAT"}");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"- score: {Num(context.EngineScore.Value)} (range -1..1)");
            if (context.EnginePriorConfidence.HasValue)
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"- engine_prior_confidence: {Num(context.EnginePriorConfidence.Value)} (reference only, do not start from it)");
            if (context.EngineFacts is { Count: > 0 })
                sb.AppendLine("- neutral facts: " + string.Join("; ", context.EngineFacts));
            sb.AppendLine("Do not re-score these facts or subtract for the same weaknesses again; assess independent news/structure evidence.");
        }
        else
        {
            sb.AppendLine("Engine pre-score: not available — derive an independent confidence from the rubric using the raw indicators and news.");
        }
        sb.AppendLine();

        if (context.HasPosition)
        {
            sb.AppendLine("Open position:");
            sb.AppendLine(CultureInfo.InvariantCulture, $"- side: {context.PositionSide ?? "LONG"}");
            if (context.Quantity.HasValue)
                sb.AppendLine(CultureInfo.InvariantCulture, $"- quantity: {FmtPrice(context.Quantity.Value, context)}");
            if (context.EntryPrice.HasValue)
                sb.AppendLine(CultureInfo.InvariantCulture, $"- entry_price: {FmtPrice(context.EntryPrice.Value, context)}");
            if (context.UnrealizedPnlPercent.HasValue)
                sb.AppendLine(CultureInfo.InvariantCulture, $"- unrealized_pnl_percent: {context.UnrealizedPnlPercent.Value.ToString("0.##", CultureInfo.InvariantCulture)}");
            if (context.StopLoss.HasValue)
                sb.AppendLine(CultureInfo.InvariantCulture, $"- stop_loss: {FmtPrice(context.StopLoss.Value, context)}");
            if (context.TakeProfit.HasValue)
                sb.AppendLine(CultureInfo.InvariantCulture, $"- take_profit: {FmtPrice(context.TakeProfit.Value, context)}");
            if (context.MinutesInPosition.HasValue)
                sb.AppendLine(CultureInfo.InvariantCulture, $"- minutes_open: {context.MinutesInPosition.Value.ToString("F0", CultureInfo.InvariantCulture)}");
            sb.AppendLine("Allowed actions now: HOLD (keep) or CLOSE (exit at market).");
        }
        else
        {
            sb.AppendLine(context.AllowShort
                ? "No open position. Allowed actions: BUY, SELL or HOLD."
                : "No open position. Allowed actions: BUY or HOLD (spot account, no shorting).");
        }
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(newsContext))
        {
            sb.AppendLine("Asset-specific news (relevance-filtered, secondary factor):");
            sb.AppendLine(newsContext);
        }
        else
        {
            sb.AppendLine("Asset-specific news: none found — treat as absence of independent news evidence, not as missing data.");
        }
        sb.AppendLine();

        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Execution threshold: signals with final confidence >= {Num(context.MinConfidence)} may be executed by the engine; the engine blends your confidence with its own pre-score, so an honest number matters more than a safe one.");
        sb.AppendLine("Provide the trading decision as the JSON object described above.");

        // Мягкие переключатели режима рассуждений Qwen3.
        sb.AppendLine(enableThinking ? "/think" : "/no_think");
        sb.AppendLine("<|im_end|>");
        sb.AppendLine("<|im_start|>assistant");

        // F3: пустой think-блок НЕ префиллим — модель открывает его сама.
        return sb.ToString();
    }

    /// <summary>
    /// Промпт «ремонта»: модель видит свой невалидный (или пустой) ответ и текст ошибки.
    /// Дешевле и надёжнее, чем повторять исходный промпт вслепую. Think не префиллится.
    /// </summary>
    public string BuildRepairPrompt(
        string originalPrompt,
        string invalidResponse,
        string error,
        bool enableThinking = true)
    {
        var sb = new StringBuilder();
        sb.Append(originalPrompt);
        sb.AppendLine(Trim(invalidResponse, 1500));
        sb.AppendLine("<|im_end|>");
        sb.AppendLine("<|im_start|>user");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Your previous answer could not be parsed: {error}.");
        sb.AppendLine(enableThinking
            ? "After the short think block, reply with ONLY the JSON object (action, confidence, entry_price, stop_loss, take_profit, reasoning)."
            : "Reply again with ONLY the JSON object (action, confidence, entry_price, stop_loss, take_profit, reasoning).");
        sb.AppendLine("No markdown, no code fences, no explanations outside JSON. Use null for prices on HOLD/CLOSE.");
        sb.AppendLine("Derive confidence from the independent rubric above; do not start from the engine prior or re-apply its penalties, and do not answer with an empty message.");
        if (enableThinking)
            sb.AppendLine("Before JSON, write one sentence inside " + OpenThink + " ... " + CloseThink + " (maximum 15 words): independent evidence, action, confidence adjustment. No arithmetic or indicator retelling. Close the tag, then output ONLY the JSON object.");
        sb.AppendLine(enableThinking ? "/think" : "/no_think");
        sb.AppendLine("<|im_end|>");
        sb.AppendLine("<|im_start|>assistant");
        return sb.ToString();
    }

    /// <summary>Точность цен: из tickSize, иначе из конфига символа (BTTC ≠ BTC).</summary>
    private static int PriceDecimals(AiRequestContext context)
    {
        if (context.TickSize.HasValue && context.TickSize.Value > 0m)
        {
            var tick = context.TickSize.Value;
            var decimals = 0;
            while (decimals < 18 && Math.Round(tick, decimals) != tick)
                decimals++;
            return decimals;
        }

        return Math.Clamp(context.PriceDecimals, 0, 18);
    }

    private static string FmtPrice(decimal value, AiRequestContext context) =>
        value.ToString("F" + PriceDecimals(context).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static string Num(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Trim(string text, int max) =>
        string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max] + "…";
}