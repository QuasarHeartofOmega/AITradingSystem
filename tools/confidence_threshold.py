#!/usr/bin/env python3
"""
P2.4: подбор порога MinConfidence по expectancy после издержек на истории сделок.

Оффлайн-инструмент: читает историю закрытых сделок (data/trades.json), нормирует каждую
сделку на принятый риск и ищет порог с максимальной net expectancy. Правила совпадают
с рантайм-контуром ConfidenceThresholdTuner (Core: ConfidenceExpectancyAnalyzer):

  * в подбор идут только сделки с SignalConfidence > 0 и ненулевым начальным риском
    (|EntryPrice - InitialStopLoss| * Quantity); старая история без этих полей не участвует;
  * expectancy считается ПОСЛЕ комиссий обеих ног: net = PnL - Commission (P1.2);
  * основная метрика — средний net R-мультипликатор (нормирует разные нотариалы 12 альтов);
  * порог-кандидат значим при выборке >= --min-bucket и expectancy > 0;
  * максимум expectancy, при равенстве — более высокий порог;
  * мало истории / нет значимых кандидатов -> рекомендации нет, порог не меняется.

Примеры:
  python3 tools/confidence_threshold.py --history AITradingSystem.UI/bin/Debug/net8.0-windows/data/trades.json
  python3 tools/confidence_threshold.py --history data/trades.json --min-trades 30 --min-bucket 10
  python3 tools/confidence_threshold.py --history data/trades.json --json docs/confidence-threshold.json
"""
from __future__ import annotations

import argparse
import json
import os
from dataclasses import dataclass, asdict
from decimal import Decimal

FALLBACK_MIN_CONFIDENCE = Decimal("0.55")
MAX_GRID_POINTS = 200


def dec(value) -> Decimal:
    """System.Text.Json пишет decimal числом; None/строка тоже допускаются."""
    if value is None:
        return Decimal(0)
    return Decimal(str(value))


@dataclass
class Sample:
    confidence: Decimal
    net: Decimal
    risk: Decimal

    @property
    def r_multiple(self) -> Decimal:
        return self.net / self.risk


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
        net = dec(trade.get("PnL")) - dec(trade.get("Commission"))
        samples.append(Sample(confidence, net, risk))
    return samples, len(raw), unlabeled, no_risk


def point(boundary: Decimal, rows: list[Sample], min_bucket: int) -> dict:
    if not rows:
        return {"boundary": boundary, "trades": 0, "wins": 0, "win_rate": Decimal(0),
                "total_net": Decimal(0), "expectancy_quote": Decimal(0),
                "expectancy_r": Decimal(0), "qualifies": False}
    net_sum = sum((r.net for r in rows), Decimal(0))
    r_sum = sum((r.r_multiple for r in rows), Decimal(0))
    wins = sum(1 for r in rows if r.net > 0)
    expectancy_r = r_sum / len(rows)
    return {"boundary": boundary, "trades": len(rows), "wins": wins,
            "win_rate": Decimal(wins) / len(rows), "total_net": net_sum,
            "expectancy_quote": net_sum / len(rows), "expectancy_r": expectancy_r,
            "qualifies": len(rows) >= min_bucket and expectancy_r > 0}


def tail(samples: list[Sample], threshold: Decimal, min_bucket: int) -> dict:
    return point(threshold, [s for s in samples if s.confidence >= threshold], min_bucket)


def buckets(samples: list[Sample], step: Decimal) -> list[dict]:
    result: list[dict] = []
    lower = Decimal(0)
    while lower < 1 and len(result) < MAX_GRID_POINTS:
        upper = lower + step
        rows = [s for s in samples if lower <= s.confidence < upper]
        if rows:
            result.append(point(lower, rows, 0))
        lower += step
    at_one = [s for s in samples if s.confidence >= 1]
    if at_one and result:
        last = result[-1]["boundary"]
        result[-1] = point(last, [s for s in samples if s.confidence >= last], 0)
    return result


def sweep(samples: list[Sample], floor: Decimal, ceiling: Decimal,
          step: Decimal, min_bucket: int) -> list[dict]:
    result: list[dict] = []
    threshold = floor
    while threshold <= ceiling and len(result) < MAX_GRID_POINTS:
        result.append(tail(samples, threshold, min_bucket))
        threshold += step
    if not result or result[-1]["boundary"] != ceiling:
        result.append(tail(samples, ceiling, min_bucket))
    return result


def analyze(samples: list[Sample], total: int, unlabeled: int, no_risk: int,
            floor: Decimal, ceiling: Decimal, step: Decimal,
            min_trades: int, min_bucket: int, current: Decimal) -> dict:
    grid = sweep(samples, floor, ceiling, step, min_bucket)
    report = {
        "total_trades": total,
        "evaluated_trades": len(samples),
        "unlabeled_trades": unlabeled,
        "no_risk_trades": no_risk,
        "buckets": buckets(samples, step),
        "sweep": grid,
        "current": tail(samples, current, min_bucket),
        "recommended_threshold": None,
        "recommended_expectancy_r": Decimal(0),
        "reason": "",
    }

    if total == 0:
        report["reason"] = "история сделок пуста — порог остаётся прежним"
        return report
    if not samples:
        report["reason"] = (f"нет сделок с записанной уверенностью и начальным риском "
                            f"(без метки {unlabeled}, без риска {no_risk}) — порог остаётся прежним")
        return report
    if len(samples) < min_trades:
        report["reason"] = (f"пригодных сделок {len(samples)} < минимума {min_trades} "
                            f"— порог остаётся прежним")
        return report

    best = None
    for candidate in grid:
        if not candidate["qualifies"]:
            continue
        if (best is None or candidate["expectancy_r"] > best["expectancy_r"]
                or (candidate["expectancy_r"] == best["expectancy_r"]
                    and candidate["boundary"] > best["boundary"])):
            best = candidate

    if best is None:
        report["reason"] = (f"нет порога в [{floor}; {ceiling}] с положительной expectancy "
                            f"и выборкой >= {min_bucket} — порог остаётся прежним")
        return report

    report["recommended_threshold"] = best["boundary"]
    report["recommended_expectancy_r"] = best["expectancy_r"]
    report["reason"] = (f"максимум expectancy {best['expectancy_r']:.3f}R на пороге "
                        f"{best['boundary']:.2f} ({best['trades']} сделок); "
                        f"действующий порог {current:.2f} даёт "
                        f"{report['current']['expectancy_r']:.3f}R")
    return report


def print_table(title: str, rows: list[dict], marker: Decimal | None = None) -> None:
    print(f"\n{title}")
    print(f"{'порог':>8} {'сделок':>7} {'win%':>6} {'expectancy R':>13} {'USDT/сделку':>12} {'net USDT':>11}  значима")
    for row in rows:
        flag = " <== текущий" if marker is not None and row["boundary"] == marker else ""
        print(f"{row['boundary']:>8.2f} {row['trades']:>7} {row['win_rate'] * 100:>5.0f}% "
              f"{row['expectancy_r']:>13.3f} {row['expectancy_quote']:>12.2f} "
              f"{row['total_net']:>11.2f}  {'да' if row['qualifies'] else 'нет'}{flag}")


def jsonable(value):
    if isinstance(value, Decimal):
        return float(value)
    if isinstance(value, dict):
        return {k: jsonable(v) for k, v in value.items()}
    if isinstance(value, list):
        return [jsonable(v) for v in value]
    return value


def main() -> None:
    parser = argparse.ArgumentParser(description="Подбор MinConfidence по expectancy после издержек (P2.4)")
    parser.add_argument("--history", default=os.path.join("data", "trades.json"),
                        help="файл истории сделок (JsonTradeHistoryRepository)")
    parser.add_argument("--current", type=Decimal, default=FALLBACK_MIN_CONFIDENCE,
                        help="действующий порог AI:MinConfidence")
    parser.add_argument("--floor", type=Decimal, default=Decimal("0.45"))
    parser.add_argument("--ceiling", type=Decimal, default=Decimal("0.80"))
    parser.add_argument("--step", type=Decimal, default=Decimal("0.05"))
    parser.add_argument("--min-trades", type=int, default=40)
    parser.add_argument("--min-bucket", type=int, default=15)
    parser.add_argument("--json", dest="json_out", help="куда сохранить машинный отчёт")
    args = parser.parse_args()

    if args.step <= 0:
        raise SystemExit("--step должен быть > 0")
    if args.ceiling < args.floor:
        raise SystemExit("--ceiling должен быть >= --floor")

    samples, total, unlabeled, no_risk = load_samples(args.history)
    report = analyze(samples, total, unlabeled, no_risk, args.floor, args.ceiling,
                     args.step, args.min_trades, args.min_bucket, args.current)

    print(f"История: {args.history}")
    print(f"Сделок всего {total}, пригодных {report['evaluated_trades']}, "
          f"без метки уверенности {unlabeled}, без начального риска {no_risk}")
    print_table("Корзины уверенности (net после комиссий)", report["buckets"])
    print_table("Sweep порогов: сделки с conf >= порога", report["sweep"], args.current)
    print(f"\nРекомендация: "
          f"{report['recommended_threshold'] if report['recommended_threshold'] is not None else 'нет'}")
    print(f"Причина: {report['reason']}")

    if args.json_out:
        os.makedirs(os.path.dirname(os.path.abspath(args.json_out)), exist_ok=True)
        with open(args.json_out, "w", encoding="utf-8") as handle:
            json.dump(jsonable(report), handle, ensure_ascii=False, indent=2)
        print(f"Отчёт сохранён: {args.json_out}")


if __name__ == "__main__":
    main()
