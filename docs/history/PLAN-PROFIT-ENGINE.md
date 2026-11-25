# PLAN-PROFIT-ENGINE — исправленный план превращения AITradingSystem в проверяемый торговый движок

**Дата редакции:** 2026-11-26  
**Статус:** активный план; runtime-аудит выполнен по предоставленным логам, код-исправления ещё не внесены  
**Базовый документ:** `docs/history/PLAN-PROFIT-ENGINE.md` сохранён как история  
**Ограничение аудита:** без сборок, тестовых прогонов и новых ордеров  
**Главный вывод:** за 6 ч 47 м торговых решений получено 501 AI-предложение, 20 BUY, 0 отправленных ордеров, 0 филлов, 0 закрытых сделок. Прибыльность не доказана; сначала нужно починить учёт капитала, данные, журнал исходов и исполняемость.

---

## 0. Что изменено относительно исторического плана

Исторический план был правильным по направлению — статистика вместо «магии LLM», журнал исходов, EdgeGate, VetoOnly, защита вне LLM. Но runtime-аудит выявил проблемы, которых в нём не было или которые были сформулированы слишком оптимистично:

1. **Equity и free cash смешаны.** `TradingEngine` обновляет `DrawdownTracker` через `AvailableUSDT`, а `GetFreeCash` вычитает из этого баланса ещё и notional открытых позиций. Это может создавать ложную просадку и двойное ограничение кэша.
2. **Kill-switch и risk-state не имеют достаточной runtime-трассы.** В логе есть отказы, но нет видимых переходов engage/reset, персиста и причины 50% просадки.
3. **Market-data recovery зацикливается.** `DATA-RECOVERED` чередуется со `stale-kline`; REST-прогрев не доказывает, что WS снова delivering.
4. **LLM фактически блокирует последовательный цикл.** Среднее 47.25 с на символ, суммарно ~6.57 ч инференса за ~6.78 ч окна решений.
5. **Post-only и OCO не подтверждены runtime.** План должен начинаться с контролируемого Demo smoke-test, а не со статистики.
6. **Paper post-only симулирует мгновенный филл.** Это завышает fill probability и делает paper-статистику непригодной для EdgeGate.
7. **Wilson lower bound по win rate недостаточен для EV.** Нужен нижний доверительный интервал среднего net R, а не только вероятности TP.
8. **`min_samples=200` не магическое число.** Нужна проверка мощности, ширины интервала, walk-forward и деградации.
9. **Риск разорения нельзя обещать как «≈0».** Можно ограничивать ставку, убыток и экспозицию; гэпы и stop-limit всё равно дают хвостовой риск.
10. **Новости и стабильные монеты требуют отдельной политики.** `USDCUSDT`/`FDUSDUSDT` не должны оцениваться как обычный volatile-актив; отсутствие новостей не является ни bullish, ни bearish доказательством.

---

## 1. Целевые метрики без гарантий доходности

| Метрика | Цель | Где считается |
|---|---|---|
| `net_expectancy_R` | нижняя граница > 0 после всех издержек | `EdgeEngine` + `BucketTable` |
| `expectancy_lower_bound` | bootstrap/t-интервал по net R, не только Wilson win rate | `EmpiricalEdgeEstimator` |
| `fill_probability` | отдельно по post-only bucket, горизонт и режим | `ExecutionOutcomeLedger` |
| `adverse_selection` | движение цены против maker-филла через 1/5/15 мин | `ExecutionQualityMonitor` |
| `slippage_R` | фактическое отклонение от reference mid/arrival price | `ExecutionQualityMonitor` |
| `max_drawdown` | в пределах лимита, дальше kill-switch | `DrawdownTracker` |
| `risk_of_ruin` | оценка по распределению net R и ставке, не гарантия нуля | `CapitalRiskModel` |
| `decision_latency` | детерминированный путь без LLM; целевой бюджет задаётся конфигом | `DecisionMetrics` |
| `journal_coverage` | 100% решений имеют `decision_id`, стадию и причину | `OutcomeLedger` |
| `live_gate` | Live запрещён без runtime-приёмки и положительного edge | `ProfitabilityGate` |

Рост капитала = `expectancy × frequency × sizing × time`. Если expectancy не доказана, увеличение частоты или сайзинга только ускоряет убыток.

---

## 2. Runtime-приёмка E0: что уже подтверждено

Легенда:

- `[x]` подтверждено предоставленным runtime-логом;
- `[~]` частично подтверждено, но есть дефект или неполная трасса;
- `[ ]` не подтверждено / не наблюдалось.

| Блок | Статус | Факт из логов | Что требуется |
|---|---|---|---|
| Demo environment | `[x]` | REST `demo-api.binance.com`, режим REAL ORDERS | зафиксировать environment/account id в каждом run |
| Market WS | `[~]` | 22 стрима подключены; явных disconnect нет | per-symbol freshness, resubscribe, gap metrics |
| AI load / GPU | `[~]` | Vulkan, 23/41 слоёв, CPU fallback | обновить llama.cpp, измерить latency/CPU/GPU |
| PreScore / Calibrate / ИТОГ | `[x]` | 502 PreScore, 501 AI, 583 ИТОГ | structured decision journal |
| Confidence gate | `[x]` | 16 отказов | отделить model conf от engine prior и edge |
| Risk gate | `[~]` | 4 отказа MaxDrawdown 50% | проверить equity/free-cash/deposit/peak |
| Kill-switch block | `[~]` | 11 отказов | persist engaged state, engage/reset audit |
| Market-data fail-closed | `[~]` | 71 отказ, но recovery loops | исправить stale-kline после REST recovery |
| Order submit / fill | `[ ]` | 0 наблюдаемых | Demo smoke-test post-only и market |
| OCO SL/TP | `[ ]` | нет постановки/срабатывания | Demo smoke-test обеих ног |
| Independent protection monitor | `[~]` | MONITOR-START есть | тест во время занятого LLM/паузы/сети |
| Idempotency | `[ ]` | нет повторной отправки | тест duplicate clientOrderId |
| Risk-state persistence | `[ ]` | нет RISK-STATE-RESTORED | тест рестарта после убытка/просадки |
| User-data stream | `[ ]` | 410 POST userDataStream | проверить Demo support/fail-closed |
| Reconciliation | `[ ]` | нет RECON-SUMMARY | orphan order/OCO/position tests |
| 429 / -1003 | `[ ]` | не наблюдались | контролируемый weight-budget test |
| Tick/step/minNotional | `[ ]` | до filters не дошло | тесты на BTTC/STRAX/FLR и dust |
| Portfolio/symbol/cash caps | `[ ]` | до sizing не дошло | логировать requested/capped quantity |
| PnL / fees / slippage | `[ ]` | нет закрытых сделок | журнал филлов и net R |

**E0 нельзя считать закрытым.** Сначала закрыть P0-дефекты ниже, затем повторить Demo-приёмку.

---

## 3. P0: исправить до любой статистики и Live

### P0.1. Разделить equity, free cash, locked и position notional

**Проблема:** `AvailableUSDT` используется как баланс для просадки, а затем из него вычитается капитал в позициях. При открытой позиции это может выглядеть как потеря денег.

**Задачи:**

- `[ ]` Ввести `AccountSnapshot`: `equity_quote`, `free_cash_quote`, `locked_cash_quote`, `position_notional_quote`, `unrealized_pnl_quote`, `account_id`, `environment`, `as_of_utc`.
- `[ ]` `DrawdownTracker` обновлять только от mark-to-market equity, не от свободного USDT.
- `[ ]` `GetFreeCash` брать из биржевого free/locked или из отдельного cash ledger, не вычитать notional из equity повторно.
- `[ ]` Депозиты/выводы/трансферы корректировать baseline, а не считать торговым PnL.
- `[ ]` Risk-state хранить по `(environment, account_id, quote_asset)`; не переносить пик Live в Demo и наоборот.
- `[ ]` Логировать `RISK-SNAPSHOT`: equity, free, locked, peak, day_start, drawdown, daily_pnl, source.

**Критерий:** открытие/закрытие позиции без изменения цены не создаёт ложную просадку; депозит не создаёт ложную прибыль.

### P0.2. Kill-switch: персист, причина, сброс

**Задачи:**

- `[ ]` Персистить `kill_switch_engaged`, `reason`, `engaged_at`, `equity_at_engage`, `requires_manual_reset`.
- `[ ]` При старте, если лимит всё ещё пробит, не разрешать входы до явного reset.
- `[ ]` Логировать `KILL-SWITCH-ENGAGED`, `KILL-SWITCH-CLOSE-RESULT`, `KILL-SWITCH-RESET`, `KILL-SWITCH-RESET-DENIED`.
- `[ ]` Reset не должен обнулять peak/day_start автоматически.
- `[ ]` Если автозакрытие не удалось, статус `UNPROTECTED_OR_OPEN` и алерт, а не тихое продолжение.

**Критерий:** рестарт не «забывает» аварию; оператор видит причину и остаточные позиции.

### P0.3. Market-data recovery: REST-прогрев ≠ живой WS

**Проблема:** `DATA-RECOVERED` повторяется 101 раз, но `stale-kline` продолжается для ENJ/STRAX/FDUSD/DASH.

**Задачи:**

- `[ ]` Разделить `history_reloaded` и `stream_live`.
- `[ ]` После REST recovery требовать свежий WS tick по символу в течение `N` секунд.
- `[ ]` Если WS молчит: resubscribe symbol/stream, затем reconnect, затем quarantine.
- `[ ]` Не писать `DATA-RECOVERED` как «входы разрешены», пока не подтверждён live tick и стакан.
- [ ] Логировать `DATA-STALE`, `DATA-REST-RELOAD`, `DATA-WS-RESUBSCRIBE`, `DATA-LIVE-CONFIRMED`, `DATA-QUARANTINE`.
- `[ ]` Счётчики: stale_blocks, recovery_attempts, recovery_success, mean_time_to_live.

**Критерий:** нет бесконечного цикла recovered → stale; символ либо жив, либо явно карантинирован.

### P0.4. User-data stream и реконсиляция fail-closed

**Проблема:** `410 POST /userDataStream`; дальнейшая работа stream/recon не подтверждена.

**Задачи:**

- `[ ]` Явно проверить поддержку user-data stream в Demo/Testnet/Live.
- `[ ]` Если stream обязателен для режима, при недоступности запретить новые входы.
- `[ ]` Если разрешён fallback polling, задать интервал и логировать `EXECUTION-MODE=POLLING-FALLBACK`.
- `[ ]` Реконсиляция должна писать `RECON-START`, `RECON-SUMMARY`, `RECON-FAIL`, `RECON-ADOPT`, `RECON-CANCEL`, `PROTECTION-FILL-APPLIED`.
- `[ ]` При неизвестном состоянии ордера/позиции — freeze entries, не угадывать.

**Критерий:** бот не считает ордер исполненным только потому, что REST-запрос был отправлен.

### P0.5. LLM вне последовательного критического пути

**Проблема:** 14B на RX590 с частичным оффлоадом даёт ~47 с на решение и почти полностью занимает цикл.

**Задачи:**

- `[ ]` Добавить `AI.Enabled` и `AI.Role = Off | Shadow | VetoOnly | Explainer`.
- `[ ]` Для baseline допустить `Role=Off` или `Shadow`; вход только из engine setup + EdgeGate.
- `[ ]` Deterministic path не ждёт LLM.
- `[ ]` Veto вызывается асинхронно на закрытой свече, кэш на `(symbol, candle_close, config_hash)`.
- `[ ]` Жёсткий бюджет, например 5–15 с; опоздал → veto не применён к уже принятому решению, но событие логируется.
- `[ ]` Для VetoOnly отключить thinking или использовать меньшую модель; 14B explainer — отдельно.
- `[ ]` Timeout/parse fail/degenerate не разрешают вход и не блокируют защиту.
- `[ ]` Метрики: `llm_latency`, `veto_coverage`, `unique_veto`, `avoided_R`, `false_veto_cost`.

**Критерий:** выключение LLM не ломает торговый контур; польза LLM измеряется в R.

### P0.6. Символы и стабильные пары

**Задачи:**

- `[ ]` Явно разделить `volatile_symbols`, `stablecoin_pairs`, `benchmark_symbols`.
- `[ ]` `USDCUSDT`/`FDUSDUSDT` не анализировать как обычный трендовый актив; либо исключить, либо отдельная depeg/liquidity strategy.
- `[ ]` `FLRUSDT` и другие unavailable symbols логировать с причиной и не включать в active universe.
- `[ ]` Запретить BUY, если engine direction = SHORT и нет отдельного reversal setup.
- `[ ]` В начале сессии логировать effective config hash и active/excluded symbols.

**Критерий:** список анализируемых символов совпадает с торговым смыслом стратегии.

---

## 4. E1: журнал решений, ордеров и исходов

### 4.1. Decision journal

Каждое решение получает `decision_id` и `run_id`. Минимальная запись:

```json
{
  "ts_utc": "...",
  "run_id": "...",
  "decision_id": "...",
  "symbol": "BTCUSDT",
  "candle_close_utc": "...",
  "stage": "confidence|risk|cash|sizing|filters|liquidity|post-only|exchange|protection|edge|coverage|event|llm-veto|market-data|kill-switch",
  "outcome": "ALLOW|REJECT|HOLD|ERROR",
  "reason_code": "...",
  "reason_text": "...",
  "engine_direction": "LONG|SHORT|FLAT",
  "engine_prior": 0.0,
  "model_action": "BUY|SELL|HOLD|CLOSE|null",
  "model_confidence": null,
  "final_confidence": null,
  "bucket_key": "...",
  "config_hash": "...",
  "data_snapshot_hash": "..."
}
```

Требования:

- `[ ]` Один финальный исход на `decision_id`.
- `[ ]` Все ранние return пишут причину, включая cooldown, max positions, no-book, warmup.
- `[ ]` Отказы до AI и после AI разделены.
- `[ ]` HOLD без позиции не считается сделкой и не попадает в win rate.
- `[ ]` Логи человека и machine-readable journal не заменяют друг друга.

### 4.2. Execution journal

События на каждый ордер:

```text
INTENT_CREATED
SUBMIT_REQUESTED
SUBMIT_ACK / SUBMIT_REJECTED
ORDER_NEW
PARTIAL_FILL
FILLED
CANCEL_REQUESTED
CANCELED
EXPIRED
REJECTED
POST_ONLY_REJECTED
CANCEL_AUDIT
PROTECTION_INTENT
PROTECTION_ACK / PROTECTION_REJECTED
PROTECTION_FILLED
PROTECTION_REPLACED
PROTECTION_GONE
EMERGENCY_CLOSE
RECON_ADOPT / RECON_CANCEL
```

Поля: `client_order_id`, `exchange_order_id`, `list_id`, `side`, `type`, `time_in_force`, `post_only`, `requested_qty`, `rounded_qty`, `limit_price`, `arrival_mid`, `best_bid/ask`, `filled_qty`, `avg_fill_price`, `fee`, `fee_asset`, `latency_ms`, `reject_code`, `cancel_reason`.

### 4.3. Outcome ledger

`OutcomeRecord` должен хранить:

```text
decision_id, symbol, bucket_key, bucket_level, estimator_version,
source = replay|shadow|paper|demo|live,
entry_intent_id, entry_fill_id, exit_fill_id,
initial_risk_quote, initial_risk_R,
realized_gross_R, realized_fee_R, realized_slippage_R, realized_net_R,
exit_reason = TP|SL|TRAILING|BREAKEVEN|AI_CLOSE|MANUAL|EMERGENCY|TIME_STOP|RECON,
holding_seconds, mae_R, mfe_R,
protection_confirmed, unprotected_seconds,
veto_by, event_flags,
data_snapshot_hash, config_hash
```

Задачи:

- `[ ]` Сохранять point-in-time признаки на момент сигнала.
- `[ ]` Запретить future leakage: границы бакетов и веса фиксируются до теста.
- `[ ]` Неисполненные post-only entry писать как `NO_FILL`, а не как убыток/прибыль.
- `[ ]` Отменённые/истекшие ордера имеют отдельный исход.
- `[ ]` Частичные филлы и хвосты после отмены аудируются.
- `[ ]` Replay должен моделировать очередь/ликвидность консервативно, не считать каждый сигнал исполненным.

---

## 5. E2: BucketTable и статистическое ядро

### 5.1. BucketKey

Начать с малого числа признаков:

```text
symbol_group
setup_direction
regime
prior_band
atr_quantile
spread_bucket
depth_bucket
horizon
execution_type = post_only|market
```

Правила:

- `[ ]` Границы корзин фиксируются по train-периоду.
- `[ ]` Признак, дробящий бакеты ниже минимального покрытия, отклоняется.
- `[ ]` Fallback: полный ключ → убрать слабый признак → direction×regime → direction.
- `[ ]` Ниже минимального покрытия → `INSUFFICIENT_COVERAGE`, вход запрещён.
- `[ ]` Версионировать `bucket_definition_hash`, `artifact_hash`, `train_window`, `embargo`.

### 5.2. Оценка edge

Историческая формула `p_lower·R_tp − (1−p_lower) − costs_R` полезна как грубая модель, но недостаточна:

- выходы бывают trailing/breakeven/time-stop/manual;
- R/R плавает;
- fill probability < 1;
- maker-филлы имеют adverse selection;
- комиссии и проскальзывание уже входят в net R, их нельзя вычитать дважды.

Целевая оценка:

```text
net_R_i = realized_gross_R_i - fee_R_i - slippage_R_i - gap_penalty_R_i
EV_hat = mean(net_R_i)
EV_lower = lower_confidence_bound(mean(net_R_i), method=bootstrap|t, alpha=0.05)
```

Дополнительно:

```text
win_rate_lower = WilsonLower(k, n)
fill_probability = filled_intents / submitted_intents
expected_net_per_intent = fill_probability * EV_per_fill - no_fill_opportunity_cost
```

Задачи:

- `[ ]` Реализовать `EmpiricalEdgeEstimator` по net R.
- `[ ]` Wilson оставить для диагностики win rate, не как единственный EV gate.
- `[ ]` Walk-forward, purged split, embargo.
- `[ ]` Проверять ширину интервала и стабильность по окнам.
- `[ ]` `min_samples` задавать по мощности/ширине интервала, не магическим 200.
- `[ ]` Reliability/Brier/ECE считать отдельно для engine prior, model confidence и bucket probability.
- `[ ]` Закрыть SECUPD P2.6: `Dead/Contradicts` веса исключаются только через версионированный артефакт.

---

## 6. E3: EdgeGate и запрет недоказанного Live

Порядок гейтов:

```text
0 infrastructure: clock, rate-limit, account snapshot, recon, kill-switch
1 data: closed candle, fresh kline, fresh book
2 symbol policy: volatile/stable/benchmark, direction alignment
3 regime rules
4 event veto / blackout
5 deterministic setup + PreScore
6 BucketTable coverage
7 EdgeGate: EV_lower > threshold, interval width acceptable
8 optional LLM VetoOnly
9 confidence secondary filter
10 portfolio risk / correlation / daily loss
11 sizing: equity, risk, ATR, cash, symbol cap, portfolio cap
12 filters: tick/step/minQty/minNotional
13 liquidity: spread/depth/notional ratio
14 execution: post-only lifecycle
15 protection: OCO confirmed
16 monitor and outcome ledger
```

Задачи:

- `[ ]` `ProfitabilityGate` до сайзинга.
- `[ ]` Запрет входа при `insufficient coverage`, `stale table`, широком интервале, `EV_lower <= threshold`.
- `[ ]` `AI.MinConfidence` вторичен и не заменяет edge.
- `[ ]` Live запрещён, пока E0 не закрыт и edge не доказан на Demo/shadow.
- `[ ]` Каждый отказ имеет `reason_code` и числовые поля.
- `[ ]` Сравнивать old gate (`MinConfidence`) и new gate (`EV_lower`) в shadow.

---

## 7. E4: новости, события и режимы

Текущий скалярный sentiment стирает тип события. Нужен каталог:

```text
BLOCK: delisting, trading halt, exchange outage, depeg, hack/exploit
REDUCE: regulatory enforcement, macro window, unknown risk
IGNORE: listing, partnership, upgrade — не является разрешением на вход
```

Задачи:

- `[ ]` `config/event-catalog.json`: типы, severity, keywords, negation, actions.
- `[ ]` Дедуп по нормализованному заголовку.
- `[ ]` Окно свежести и credibility.
- `[ ]` Критичное событие: 2 независимых источника или официальный анонс биржи/эмитента.
- `[ ]` Макро-календарь и blackout-окна без парсинга новостей.
- `[ ]` Рыночная аномалия: движение + объём + спред.
- `[ ]` Для stablecoin pairs отдельный depeg-контур.
- `[ ]` LLM не является единственным детектором критичного события.
- `[ ]` Prompt injection: новости — недоверенный текст, не инструкции.

Критерий: событийный запрет работает без LLM и объясним одной строкой.

---

## 8. E5: LLM VetoOnly / Shadow / Off

Задачи:

- `[ ]` `AI.Role` и `AI.Enabled`.
- `[ ]` Вход только из engine setup; LLM BUY не создаёт ордер.
- `[ ]` `Hold→Prior` и `EngineOverride` не являются источником входа в Live.
- `[ ]` Контракт veto: `veto`, `veto_reason`, `event_type`, `risk_flags`, `invalidation`.
- `[ ]` Строгая JSON-схема; битый JSON/дегенерат/timeout не разрешают вход.
- `[ ]` Асинхронный вызов, кэш, бюджет, отмена по устареванию свечи.
- `[ ]` SL/TP/protection никогда не ждут LLM.
- `[ ]` Explainer вызывается после решения.
- `[ ]` Тень отклонённых: считать исходы veto-сигналов.
- `[ ]` Метрики: `veto_precision`, `avoided_R`, `unique_veto`, `false_veto_cost`, `llm_latency`.
- `[ ]` Если `avoided_R <= false_veto_cost` на достаточной выборке — роль сжимается до explainer/off.

Критерий: LLM может только запретить и объяснить; её польза измерена в R.

---

## 9. E6: исполнение, post-only и защита

### 9.1. Post-only lifecycle

Текущий код:

- ставит `LIMIT_MAKER`;
- ждёт `OrderFillTimeoutSeconds`;
- отменяет остаток;
- не догоняет market-входом.

Но runtime не подтвердил это, а paper симулирует мгновенный филл.

Задачи:

- `[ ]` Demo smoke-test: успешный maker fill, post-only reject, timeout cancel, partial fill, cancel race.
- `[ ]` Отмена при уходе цены/спреда за допустимый коридор, не только по таймауту.
- `[ ]` Запрет догоняющего market-входа после отмены.
- `[ ]` Частичный филл защищается немедленно; остаток отменяется и аудируется.
- `[ ]` `fill_probability` считать по submitted intents, а не по AI-сигналам.
- `[ ]` Paper post-only моделировать очередь/вероятность филла, а не мгновенное исполнение.
- `[ ]` Метрики: `time_to_fill`, `fill_ratio`, `post_only_reject_rate`, `cancel_rate`, `expired_rate`.

### 9.2. Adverse selection и slippage

Для каждого maker-филла:

```text
arrival_mid
fill_price
mid_after_1m / 5m / 15m
adverse_selection_R = side_sign * (mid_after - fill_price) / initial_risk
slippage_vs_arrival = side_sign * (fill_price - arrival_mid) / initial_risk
```

Задачи:

- `[ ]` Таблицы `fill_probability`, `expected_slippage`, `adverse_selection` по bucket `size×depth×vol×spread`.
- `[ ]` EdgeGate использует ожидаемые издержки исполнения, а не только комиссию.
- `[ ]` Серия плохих филлов → пауза symbol/execution mode.

### 9.3. Защита

Задачи:

- `[ ]` Вход не считается состоявшимся для статистики, пока OCO не подтверждена.
- `[ ]` Если OCO не поставлена: аварийное закрытие или quarantine, в зависимости от режима.
- `[ ]` Crash-safe intent journal: восстановить незавершённый entry/protection после падения.
- `[ ]` Учитывать гэп через stop-limit: stop может не исполниться по limit.
- `[ ]` `unprotected_seconds` и `protection_missing` — метрики и алерты.
- `[ ]` Тест: LLM занята, UI закрыт, сеть моргнула, процесс упал — защита/реконсиляция восстанавливают состояние.

Критерий: позиция не остаётся неуправляемой; издержки в EV соответствуют фактическим.

---

## 10. E7: капитал, сайзинг и риск разорения

Задачи:

- `[ ]` Сайзинг от equity, initial risk, ATR и edge; не от свободного USDT как equity.
- `[ ]` Явный журнал: `requested_qty`, `risk_qty`, `cash_cap_qty`, `symbol_cap_qty`, `portfolio_cap_qty`, `correlation_cap_qty`, `rounded_qty`, `final_qty`.
- `[ ]` Лимиты: символ, актив, коррелированная группа, суммарная экспозиция, max open positions.
- `[ ]` Throttle и kill-switch по просадке и дневному убытку.
- `[ ]` Loss-streak pause и symbol daily loss limit подтверждаются runtime.
- `[ ]` Оценка риска разорения по распределению net R, ставке и хвостовым сценариям.
- `[ ]` Ступени капитала: минимальная → следующая только после N сделок и удержания expectancy.
- `[ ]` Запрет увеличения риска после убытков и ручного снятия лимитов в Live без отдельного audit flow.

Критерий: система снижает риск и останавливается при деградации; нельзя обещать нулевой риск разорения.

---

## 11. E8: надёжность, поставка, безопасность

Задачи:

- `[ ]` Headless-режим: торговля не зависит от окна WPF.
- `[ ]` Fail-closed для критических зависимостей.
- `[ ]` Секреты: DPAPI, маскирование, запрет полных дампов аккаунта в логах/промптах.
- `[ ]` Зафиксировать версии нативных DLL; для AMD Polaris/RX590 обновить llama.cpp до версии с исправлением кластерного мусора.
- `[ ]` Воспроизводимая поставка и откат версии.
- `[ ]` Метрики гейтов, отказов, latency, деградации; алерты в Telegram.
- `[ ]` Effective config hash в каждом run.
- `[ ]` README и assessment обновляются после каждой runtime-приёмки.

---

## 12. E9: Shadow → Demo acceptance → Live ramp-up

Этапы:

1. **Replay/shadow:** engine setup + BucketTable + EdgeGate без ордеров.
2. **Demo execution acceptance:** post-only, OCO, cancel, partial, recon, kill-switch, risk-state, 429-budget.
3. **Demo edge validation:** достаточное покрытие, положительный `EV_lower`, стабильность walk-forward.
4. **Live minimum capital:** только если Demo acceptance закрыт и edge не деградировал.
5. **Auto-degradation:** снижение риска/запрет входов при потере edge, росте slippage/adverse selection, ошибках исполнения.

Задачи:

- `[ ]` Shadow сравнивает old/new gate по net expectancy и частоте.
- `[ ]` Измерять долю отказов coverage и влияние на frequency.
- `[ ]` Измерять расхождение replay-издержек и фактических.
- `[ ]` Мониторить наполненность бакетов, расхождение p и факта, expectancy decay.
- `[ ]` Пересчёт таблиц по расписанию, approval по хешу, откат версии.
- `[ ]` Переход на следующую ступень только по факту удержания expectancy.

---

## 13. События, необходимые для BucketTable / EdgeGate

Минимальный набор:

```text
DECISION_CREATED
GATE_RESULT
PRE_SCORE_SNAPSHOT
BUCKET_ASSIGNED
EDGE_ESTIMATE
LLM_VETO_RESULT
SIZING_BREAKDOWN
ORDER_INTENT
ORDER_SUBMIT
ORDER_STATUS
ORDER_FILL
ORDER_CANCEL
ORDER_EXPIRE
ORDER_REJECT
PROTECTION_STATUS
POSITION_OPEN
POSITION_UPDATE
POSITION_CLOSE
OUTCOME_RECORDED
RISK_SNAPSHOT
KILL_SWITCH
RECONCILIATION
DATA_HEALTH
EXECUTION_QUALITY
```

Без этих событий нельзя честно посчитать:

- coverage бакетов;
- fill probability;
- expected slippage;
- adverse selection;
- net expectancy;
- вклад LLM;
- частоту и причины отказов;
- деградацию преимущества.

---

## 14. Критерии готовности

```text
E0 закрыт на Demo, а не только в коде.
Equity/free-cash/deposit accounting проверен.
Kill-switch переживает рестарт и имеет audit trail.
Market-data recovery не зацикливается.
User-data/recon подтверждены или включён явный polling fallback.
Журнал решений и исходов покрывает 100% decision_id.
Post-only fill/cancel/partial/reject подтверждены.
OCO SL/TP/trailing/emergency close подтверждены.
BucketTable версионирована, coverage известна.
EV_lower считается по net R, а не только по win rate.
Ни один Live-вход не проходит без coverage и положительного edge.
LLM вне критического пути; её польза измерена или роль отключена.
Защита не зависит от LLM и статистики.
Риск разорения оценён, лимиты и деградация работают.
Поставка воспроизводима, версии зафиксированы, откат возможен.
Нет обещаний гарантированной прибыли.
```

---

## 15. Итог

Сначала сделать систему **наблюдаемой и исполняемой**, затем **статистически проверяемой**, и только потом масштабировать капитал. Текущий baseline показывает не прибыльность, а отсутствие исполненных входов и несколько инфраструктурных/учётных дефектов. Главный приоритет — не «ещё одна модель», а корректный капитал, живой рынок, журнал исходов, post-only/OCO acceptance и EdgeGate по net R.
