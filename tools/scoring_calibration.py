#!/usr/bin/env python3
"""
P2.6: валидация весов direction-скоринга и калибровки уверенности на истории сделок.

Оффлайн-инструмент. Читает историю закрытых сделок (data/trades.json) и:

  1. Reliability diagram: заявленная FINAL-уверенность против фактической частоты успеха
     (net PnL > 0 после комиссий обеих ног, P1.2). ECE, Brier, разрывы корзин, вердикт
     calibrated / overconfident / underconfident / miscalibrated.
  2. Валидация компонентов скоринга по записанным фичам входа (TradeRecord.ScoreFeatures,
     проход 24): покрытие (мёртвый компонент, как ema_align в логе P2.1), expectancy
     сделок «за»/«против» компонента, вердикт Dead / Contradicts / Confirmed / Neutral.
  3. --fit: подгонка 7 весов координатным подъёмом на симплексе (сумма = 1). Цель —
     среднее score·side·R (направление скоринга, взвешенное исходом после издержек).
     Печатает рекомендованный блок AI:ScoringWeights. НИЧЕГО не применяет автоматически.

Правила совпадают с рантайм-контуром (Core: ScoringCalibrationAnalyzer, формулы —
ScoringMath; контур живёт в цикле ConfidenceThresholdTuner):

  * reliability — только сделки с SignalConfidence > 0 и ненулевым начальным риском;
  * компоненты — только сделки с записанными ScoreFeatures (старая история не участвует);
  * мало выборки -> вердиктов нет; покрытие < --dead-coverage -> Dead;
  * «за» убыточно при выборке >= --min-side -> Contradicts; «за» прибыльно и лучше
    «против» (или «против» мало) -> Confirmed; иначе Neutral.

Ограничение подгонки (честное): история содержит только ИСПОЛНЕННЫЕ входы — выборка
смещена действующими весами/порогом (selection bias). Подогнанные веса — кандидат для
paper-прогона, не истина; применяет их оператор через AI:ScoringWeights в appsettings.json.

Примеры:
  python3 tools/scoring_calibration.py --history data/trades.json
  python3 tools/scoring_calibration.py --history data/trades.json --fit --json docs/scoring-calibration.json
  python3 tools/scoring_calibration.py --history data/trades.json --graduated-ema
"""
from __future__ import annotations

import argparse
import json
import os
from dataclasses import dataclass
from decimal import Decimal

MAX_BINS = 100
WEIGHT_NAMES = ["EmaAlignment", "Supertrend", "EmaPosition", "Rsi", "Macd", "StochRsi", "Obv"]
COMPONENT_NAMES = ["ema_align", "supertrend", "ema_position", "rsi", "macd_hist", "stoch_rsi", "obv_trend"]
DEFAULT_WEIGHTS = [Decimal("0.30"), Decimal("0.20"), Decimal("0.15"),
                   Decimal("0.15"), Decimal("0.10"), Decimal("0.05"), Decimal("0.05")]


def dec(value) -> Decimal:
    """System.Text.Json пишет decimal числом; None/строка тоже допускаются."""
    if value is None:
        return Decimal(0)
    return Decimal(str(value))


def clamp(value: Decimal, low: Decimal = Decimal(-1), high: Decimal = Decimal(1)) -> Decimal:
    return max(low, min(high, value))


def rsi_score(rsi: Decimal) -> Decimal:
    """Как ScoringMath.RsiScore (она же в SignalCalibrator)."""
    if rsi <= 0:
        return Decimal(0)
    if rsi >= 80:
        return Decimal("0.2")
    if rsi <= 20:
        return Decimal("-0.2")
    return clamp((rsi - 50) / 25)


def stoch_score(k: Decimal, d: Decimal) -> Decimal:
    """Как ScoringMath.StochScore: K/D в 0..1, крайние зоны гасим."""
    cross = clamp((k - d) * 4)
    if k > Decimal("0.9"):
        return min(cross, Decimal("0.2"))
    if k < Decimal("0.1"):
        return max(cross, Decimal("-0.2"))
    return cross


def component_values(features: dict, graduated_ema: bool) -> list[Decimal]:
    """7 компонентов из фич — как ScoringMath.ComponentValues."""
    get = lambda key: dec(features.get(key))
    align = get("ema_align_grad") if graduated_ema and "ema_align_grad" in features else get("ema_align")
    ema_pos = (get("ema20_pos") + get("ema50_pos") + get("ema200_pos")) / 3
    stoch = stoch_score(get("stoch_k_raw") / 100, get("stoch_d_raw") / 100)
    return [clamp(align), clamp(get("supertrend")), clamp(ema_pos),
            clamp(rsi_score(get("rsi_raw"))), clamp(get("macd_hist")),
            clamp(stoch), clamp(get("obv_trend"))]


def modifiers(features: dict) -> Decimal:
    """Множители силы сетапа — как SignalCalibrator.PreScore с дефолтными факторами."""
    get = lambda key: dec(features.get(key))
    adx = get("adx_raw")
    adx_factor = Decimal(1) if adx >= 25 else Decimal("0.85") if adx >= 20 else Decimal("0.6")
    vr = get("volume_ratio")
    volume_factor = (Decimal(1) if vr <= 0 else Decimal("1.1") if vr >= Decimal("1.5")
                     else Decimal(1) if vr >= 1 else Decimal("0.9") if vr >= Decimal("0.6")
                     else Decimal("0.8"))
    atr = get("atr_pct")
    vol_factor = (Decimal("0.8") if 0 < atr < Decimal("0.001")
                  else Decimal("0.75") if atr > Decimal("0.06") else Decimal(1))
    return adx_factor * volume_factor * vol_factor


def direction_score(values: list[Decimal], weights: list[Decimal], features: dict) -> Decimal:
    """score = clamp(Σ w·v · модификаторы · bb-фактор) — как PreScore."""
    direction = sum((w * v for w, v in zip(weights, values)), Decimal(0))
    bb = dec(features.get("bb_pos"))
    bb_factor = Decimal("0.85") if (direction > 0 and bb > Decimal("0.85")) or \
                                   (direction < 0 and bb < Decimal("-0.85")) else Decimal(1)
    return clamp(direction * modifiers(features) * bb_factor)


def prior_confidence(score: Decimal) -> Decimal:
    return clamp(Decimal("0.2") + Decimal("0.7") * abs(score), Decimal("0.1"), Decimal("0.9"))


@dataclass
class Sample:
    confidence: Decimal
    net: Decimal
    risk: Decimal
    side: Decimal            # +1 Buy, -1 Sell
    engine_direction: Decimal
    model_confidence: Decimal
    features: dict | None

    @property
    def r_multiple(self) -> Decimal:
        return self.net / self.risk

    @property
    def win(self) -> bool:
        return self.net > 0


def load_samples(path: str) -> tuple[list[Sample], int, int, int]:
    with open(path, "r", encoding="utf-8-sig") as handle:
        raw = json.load(handle)
    if not isinstance(raw, list):
        raise SystemExit(f"{path}: ожидался массив сделок (List<TradeRecord>)")

    samples: list[Sample] = []
    unlabeled = no_risk = 0
    for trade in raw:
        if not isinstance(trade, dict):
            continue
        confidence = dec(trade.get("SignalConfidence"))
        if confidence <= 0:
            unlabeled += 1
            continue
        entry = dec(trade.get("EntryPrice"))
        initial_sl = dec(trade.get("InitialStopLoss"))
        quantity = dec(trade.get("Quantity"))
        risk = abs(entry - initial_sl) * quantity if initial_sl > 0 and entry > 0 else Decimal(0)
        if risk <= 0:
            no_risk += 1
            continue
        side_raw = trade.get("Side", 0)
        side = Decimal(-1) if side_raw in (1, "1", "Sell") else Decimal(1)
        features = trade.get("ScoreFeatures")
        samples.append(Sample(confidence, dec(trade.get("PnL")) - dec(trade.get("Commission")),
                              risk, side, dec(trade.get("EngineDirection")),
                              dec(trade.get("ModelConfidence")),
                              features if isinstance(features, dict) and features else None))
    return samples, len(raw), unlabeled, no_risk


def build_bins(samples: list[Sample], step: Decimal, min_bin: int) -> list[dict]:
    def make(boundary: Decimal, rows: list[Sample]) -> dict:
        conf = sum((s.confidence for s in rows), Decimal(0)) / len(rows)
        wins = sum(1 for s in rows if s.win)
        win_rate = Decimal(wins) / len(rows)
        return {"boundary": boundary, "trades": len(rows), "wins": wins,
                "avg_confidence": conf, "win_rate": win_rate,
                "expectancy_r": sum((s.r_multiple for s in rows), Decimal(0)) / len(rows),
                "gap": win_rate - conf, "qualifies": len(rows) >= min_bin}

    result: list[dict] = []
    lower = Decimal(0)
    while lower < 1 and len(result) < MAX_BINS:
        rows = [s for s in samples if lower <= s.confidence < lower + step]
        if rows:
            result.append(make(lower, rows))
        lower += step
    if any(s.confidence >= 1 for s in samples) and result:
        last = result[-1]["boundary"]
        result[-1] = make(last, [s for s in samples if s.confidence >= last])
    return result


def calibration(samples: list[Sample], bins: list[dict], min_trades: int, max_ece: Decimal) -> dict:
    if not samples:
        return {"ece": Decimal(0), "brier": Decimal(0), "max_abs_gap": Decimal(0),
                "verdict": "insufficient-data"}
    n = len(samples)
    brier = sum(((s.confidence - (1 if s.win else 0)) ** 2 for s in samples), Decimal(0)) / n
    ece = Decimal(0)
    signed_gap = Decimal(0)
    max_abs_gap = Decimal(0)
    for b in bins:
        share = Decimal(b["trades"]) / n
        ece += share * abs(b["gap"])
        signed_gap += share * (-b["gap"])
        if b["qualifies"] and abs(b["gap"]) > max_abs_gap:
            max_abs_gap = abs(b["gap"])
    verdict = ("insufficient-data" if n < min_trades else
               "calibrated" if ece <= max_ece else
               "overconfident" if signed_gap > max_ece else
               "underconfident" if signed_gap < -max_ece else "miscalibrated")
    return {"ece": ece, "brier": brier, "max_abs_gap": max_abs_gap, "verdict": verdict}


def analyze_components(feature_samples: list[Sample], graduated_ema: bool, min_trades: int,
                       dead_coverage: Decimal, min_side: int) -> list[dict]:
    result = []
    if not feature_samples:
        return [{"name": name, "verdict": "InsufficientData", "coverage": Decimal(0),
                 "supported": 0, "opposed": 0, "support_r": Decimal(0), "oppose_r": Decimal(0),
                 "mean_signed_r": Decimal(0)} for name in COMPONENT_NAMES]

    per_trade = [(s.side, s.r_multiple, component_values(s.features, graduated_ema))
                 for s in feature_samples]
    n = len(per_trade)
    for i, name in enumerate(COMPONENT_NAMES):
        non_zero = sum(1 for _, _, values in per_trade if values[i] != 0)
        support = [(side, r) for side, r, values in per_trade if values[i] * side > 0]
        oppose = [(side, r) for side, r, values in per_trade if values[i] * side < 0]
        support_r = sum((r for _, r in support), Decimal(0)) / len(support) if support else Decimal(0)
        oppose_r = sum((r for _, r in oppose), Decimal(0)) / len(oppose) if oppose else Decimal(0)
        signed_r = sum((values[i] * side * r for side, r, values in per_trade), Decimal(0)) / n
        coverage = Decimal(non_zero) / n
        verdict = ("InsufficientData" if n < min_trades else
                   "Dead" if coverage < dead_coverage else
                   "Contradicts" if len(support) >= min_side and support_r < 0 else
                   "Confirmed" if len(support) >= min_side and support_r > 0 and
                   (len(oppose) < min_side or support_r > oppose_r) else "Neutral")
        result.append({"name": name, "verdict": verdict, "coverage": coverage,
                       "supported": len(support), "opposed": len(oppose),
                       "support_r": support_r, "oppose_r": oppose_r, "mean_signed_r": signed_r})
    return result


def fit_objective(feature_samples: list[Sample], weights: list[Decimal], graduated_ema: bool) -> Decimal:
    """Среднее score·side·R: направление скоринга, взвешенное исходом после издержек."""
    total = Decimal(0)
    for s in feature_samples:
        values = component_values(s.features, graduated_ema)
        total += direction_score(values, weights, s.features) * s.side * s.r_multiple
    return total / len(feature_samples)


def fit_weights(feature_samples: list[Sample], graduated_ema: bool,
                step: Decimal, passes: int) -> tuple[list[Decimal], Decimal, Decimal]:
    """Координатный подъём на симплексе: w_i ± step, перенормировка, сумма = 1, w >= 0."""
    weights = list(DEFAULT_WEIGHTS)
    best = fit_objective(feature_samples, weights, graduated_ema)
    baseline = best
    for _ in range(passes):
        improved = False
        for i in range(len(weights)):
            for delta in (step, -step):
                if weights[i] + delta < 0:
                    continue
                candidate = list(weights)
                candidate[i] += delta
                total = sum(candidate, Decimal(0))
                candidate = [w / total for w in candidate]
                score = fit_objective(feature_samples, candidate, graduated_ema)
                if score > best:
                    weights, best, improved = candidate, score, True
        if not improved:
            break
    return weights, baseline, best


def tail_expectancy(feature_samples: list[Sample], weights: list[Decimal], graduated_ema: bool,
                    threshold: Decimal, blend_weight: Decimal) -> dict:
    """Что дал бы действующий порог на истории с новыми весами (Blend, как P2.3)."""
    selected = []
    for s in feature_samples:
        values = component_values(s.features, graduated_ema)
        prior = prior_confidence(direction_score(values, weights, s.features))
        model = s.model_confidence if s.model_confidence > 0 else s.confidence
        final = clamp(blend_weight * model + (1 - blend_weight) * prior, Decimal(0), Decimal(1))
        if final >= threshold:
            selected.append(s)
    if not selected:
        return {"trades": 0, "expectancy_r": Decimal(0)}
    return {"trades": len(selected),
            "expectancy_r": sum((s.r_multiple for s in selected), Decimal(0)) / len(selected)}


def jsonable(value):
    if isinstance(value, Decimal):
        return float(value)
    if isinstance(value, dict):
        return {k: jsonable(v) for k, v in value.items()}
    if isinstance(value, (list, tuple)):
        return [jsonable(v) for v in value]
    return value


def main() -> None:
    parser = argparse.ArgumentParser(description="Валидация весов скоринга и reliability diagram (P2.6)")
    parser.add_argument("--history", default=os.path.join("data", "trades.json"),
                        help="файл истории сделок (JsonTradeHistoryRepository)")
    parser.add_argument("--min-trades", type=int, default=40)
    parser.add_argument("--min-bin", type=int, default=10)
    parser.add_argument("--step", type=Decimal, default=Decimal("0.1"), help="шаг корзин уверенности")
    parser.add_argument("--dead-coverage", type=Decimal, default=Decimal("0.05"))
    parser.add_argument("--min-side", type=int, default=10)
    parser.add_argument("--max-ece", type=Decimal, default=Decimal("0.10"))
    parser.add_argument("--graduated-ema", action="store_true",
                        help="анализировать/подгонять с градуированным ema_align_grad")
    parser.add_argument("--fit", action="store_true", help="подгонка весов (только рекомендация)")
    parser.add_argument("--fit-step", type=Decimal, default=Decimal("0.05"))
    parser.add_argument("--fit-passes", type=int, default=4)
    parser.add_argument("--current", type=Decimal, default=Decimal("0.55"),
                        help="действующий порог MinConfidence для сравнения хвоста")
    parser.add_argument("--blend-weight", type=Decimal, default=Decimal("0.7"),
                        help="вес модели в Blend (AI:BlendModelWeight)")
    parser.add_argument("--json", dest="json_out", help="куда сохранить машинный отчёт")
    args = parser.parse_args()

    if args.step <= 0:
        raise SystemExit("--step должен быть > 0")

    samples, total, unlabeled, no_risk = load_samples(args.history)
    feature_samples = [s for s in samples if s.features]
    bins = build_bins(samples, args.step, args.min_bin)
    cal = calibration(samples, bins, args.min_trades, args.max_ece)
    components = analyze_components(feature_samples, args.graduated_ema, args.min_trades,
                                    args.dead_coverage, args.min_side)

    print(f"История: {args.history}")
    print(f"Сделок всего {total}, пригодных {len(samples)}, без метки {unlabeled}, "
          f"без риска {no_risk}, с фичами скоринга {len(feature_samples)}")

    print("\nReliability diagram (заявлено vs факт, net после комиссий)")
    print(f"{'корзина':>8} {'сделок':>7} {'заявлено':>9} {'факт':>6} {'разрыв':>8} {'expectancy R':>13}  значима")
    for b in bins:
        print(f"{b['boundary']:>8.2f} {b['trades']:>7} {b['avg_confidence'] * 100:>8.0f}% "
              f"{b['win_rate'] * 100:>5.0f}% {b['gap']:>+8.2f} {b['expectancy_r']:>13.3f}  "
              f"{'да' if b['qualifies'] else 'нет'}")
    print(f"\nКалибровка: {cal['verdict']}; ECE {cal['ece']:.3f}, Brier {cal['brier']:.3f}, "
          f"max |разрыв| {cal['max_abs_gap']:.2f}")

    print("\nКомпоненты скоринга против исходов")
    for c in components:
        print(f"  {c['name']:<13} покрытие {c['coverage'] * 100:>3.0f}%, "
              f"за {c['supported']:>4} ({c['support_r']:+.3f}R), "
              f"против {c['opposed']:>4} ({c['oppose_r']:+.3f}R)  -> {c['verdict']}")

    report = {"total_trades": total, "evaluated_trades": len(samples),
              "unlabeled_trades": unlabeled, "no_risk_trades": no_risk,
              "feature_trades": len(feature_samples), "bins": bins,
              "calibration": cal, "components": components,
              "graduated_ema": args.graduated_ema, "fit": None}

    if args.fit:
        if len(feature_samples) < args.min_trades:
            print(f"\nПодгонка пропущена: сделок с фичами {len(feature_samples)} < минимума {args.min_trades}")
        else:
            weights, baseline, best = fit_weights(feature_samples, args.graduated_ema,
                                                  args.fit_step, args.fit_passes)
            base_tail = tail_expectancy(feature_samples, DEFAULT_WEIGHTS, args.graduated_ema,
                                        args.current, args.blend_weight)
            fit_tail = tail_expectancy(feature_samples, weights, args.graduated_ema,
                                       args.current, args.blend_weight)
            print(f"\nПодгонка весов (координатный подъём, цель mean score·side·R):")
            print(f"  базовые веса: {baseline:+.4f} -> подогнанные: {best:+.4f}")
            for name, old, new in zip(WEIGHT_NAMES, DEFAULT_WEIGHTS, weights):
                print(f"  {name:<13} {old:.2f} -> {new:.3f}")
            print(f"  хвост conf >= {args.current}: базовые {base_tail['expectancy_r']:+.3f}R "
                  f"({base_tail['trades']} сделок) -> подогнанные {fit_tail['expectancy_r']:+.3f}R "
                  f"({fit_tail['trades']} сделок)")
            suggested = {name: float(round(w, 3)) for name, w in zip(WEIGHT_NAMES, weights)}
            print("\nРекомендованный блок AI:ScoringWeights (применяет оператор, не инструмент):")
            print(json.dumps({"ScoringWeights": suggested}, ensure_ascii=False, indent=2))
            print("ВНИМАНИЕ: история содержит только исполненные входы — выборка смещена "
                  "действующими весами/порогом. Подогнанные веса — кандидат для paper-прогона.")
            report["fit"] = {"baseline_objective": baseline, "fitted_objective": best,
                             "weights": suggested, "baseline_tail": base_tail, "fitted_tail": fit_tail}

    if args.json_out:
        os.makedirs(os.path.dirname(os.path.abspath(args.json_out)), exist_ok=True)
        with open(args.json_out, "w", encoding="utf-8") as handle:
            json.dump(jsonable(report), handle, ensure_ascii=False, indent=2)
        print(f"\nОтчёт сохранён: {args.json_out}")


if __name__ == "__main__":
    main()
