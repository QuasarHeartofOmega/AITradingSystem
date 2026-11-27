# AITradingSystem — единый мастер-план

**Дата консолидации:** 2026-11-27  
**Область:** все планы/трекеры/журналы из `docs/history/`, плюс связанные операционные документы `docs/SETUP-WINDOWS.md`, `docs/LOGGING.md`, `docs/E1-LEDGER.md`.  
**Статус документа:** это единый читаемый план и сводка состояния. Сам факт консолидации не является сборкой, тестом, runtime-приёмкой или доказательством торговой готовности.

---

## 0. Как читать этот документ

Этот файл заменяет несколько параллельных очередей одним планом:

1. **Часть I — предыдущие планы:** что уже внесено в код/документацию или подтверждено логами. Это baseline, а не отдельная активная очередь.
2. **Часть II — текущий baseline и блокеры:** что мешает считать систему наблюдаемой, исполняемой и статистически проверяемой.
3. **Часть III — единый активный план:** один порядок работ от ближайших safety-блокеров до Live ramp-up.
4. **Часть IV — сквозные критерии готовности, метрики и правила ведения.**

Исходные документы сохранены в `docs/history/` как источник деталей. Если старая очередь противоречит этому мастер-плану, приоритет имеет **Часть III**.

### Легенда статусов

| Метка | Значение |
|---|---|
| `[code]` | Файловая правка внесена; сборка/тесты/runtime не подтверждены в этом проходе |
| `[runtime]` | Подтверждено предоставленным runtime-логом или явной приёмкой |
| `[partial]` | Есть код/частичное подтверждение, но критерий блока не закрыт |
| `[todo]` | Не сделано или не подключено |
| `[accept]` | Требуется внешняя Demo/Windows/GPU/Binance-приёмка |

### Главное правило

Сначала сделать систему **наблюдаемой и исполняемой**, затем **статистически проверяемой**, и только потом масштабировать капитал. Не переходить к Live и не ослаблять risk/confidence gates ради увеличения числа сделок.

---

## 1. Карта исходных документов

| Источник | Роль | Куда перенесено в мастер-плане |
|---|---|---|
| `docs/history/plan.md` | Исторический план A/B/C/D: GPU/native, confidence, account/UI, symbols | Часть I, блоки A–D |
| `docs/history/changes.md` | Журнал изменений, включая P0.8 transport/liveness | Часть I, блок H; Часть III, Phase 1/10 |
| `docs/history/ACTION-PLAN.md` | Понятные итоговые строки решений, TP-FIX, релевантность новостей | Часть I, блок E |
| `docs/history/SECUPD.md` | Трекер P0/P1/P2: капитал, деньги/данные, сигнал/HOLD | Часть I, блоки F–G; P2.6 перенесён в Phase 4 |
| `docs/history/trdupd.md` | Qwen3/Vulkan/KV: повторы, think-бюджет, деградация, ускорение | Часть I, блок B; native DLL acceptance в Phase 10 |
| `docs/history/LOGGING-PLAN.md` | Аудит и реформа логирования | Часть I, блок I; runtime-приёмка логов в Phase 10 |
| `docs/history/PLAN-PROFIT-ENGINE.md` | Полный план проверяемого торгового движка E0–E9 | Части II–III, Phases 1–11 |
| `docs/history/PLAN-PROFIT-ENGINE-ext.md` | Активная редакция по логам 2026-11-26: P0.9/P0.3a/E1 | Часть II; Phases 1–3 |
| `docs/E1-LEDGER.md` | Контракт E1 ledger + runtime wiring | Phase 2 |
| `docs/LOGGING.md` | Операционная карта каналов/формата/настроек логов | Phase 10, операционные правила |
| `docs/SETUP-WINDOWS.md` | Сборка/запуск/ключи/GPU/native | Phase 10, операционные правила |
| `docs/README.md` | Индекс документации | Обновлён: мастер-план указан как основной сводный документ |

В `docs/README.md` исторически упоминались `PLAN-10-10-LIVE-PROFIT.md` и `PLAN-ML-LLM-INTEGRATION.md`, но в текущем checkout эти файлы отсутствуют. Их темы покрыты здесь как E9/Live ramp-up и E5/LLM-роли; отдельные невыполненные обещания из отсутствующих файлов не переносятся как подтверждённый baseline.

---

# Часть I. Предыдущие планы: единый baseline

## A. GPU / native / загрузка модели

Источник: `plan.md`, `changes.md`, `trdupd.md`.

| Блок | Статус | Что сделано | Что остаётся проверять |
|---|---|---|---|
| A1–A3: `GgmlNative`, `GgufInspector`, `NativeStaging` | `[code]` | Нативные экспорты, GGUF-инспектор, плоский стейджинг, CUDA-runtime проверки, отклонение GPU без offload | Реальный offload на целевой машине |
| A4: `LlamaNativeLoader` | `[code]` | Строгий набор DLL, staged-резолвер, освобождение пробных хэндлов, диагностика каталога | Смена бэкенда без перезапуска на Windows |
| A5: `QwenModel` | `[code]` | Конвейер стейдж→проба→GGUF→AutoFit→OOM-ретраи→проверка offload; CPU fallback | Фактический CPU/GPU fallback и latency |
| A6–A7: `GpuProbe`, `AIConfig` | `[code]` | VRAM/probe/legacy AMD, AutoFit, VRAM reserve, CPU fallback, FlashAttention, native log level | RX590/RTX3060 acceptance |
| Qwen3/Vulkan/KV пакет | `[code]` | `DegenerateOutputDetector`, `KvManagedExecutor`, жёсткий think-бюджет, KV-reuse, сэмплеры Qwen3, эскалация KV→reset→reload→CPU | Нет `????`/циклов, think ≤ бюджета, KV reuse, latency |
| Native DLL обновление | `[todo]`/`[accept]` | Документировано: llama.cpp ≥ b9892 для Vulkan/RX590 issue #24812 | Оператор обновляет DLL и подтверждает по логам |

**Вывод:** файловая база GPU/native внесена, но это не доказательство успешной сборки, GPU-инференса или отсутствия Vulkan-деградации.

## B. Confidence / сигнал / HOLD

Источник: `plan.md`, `SECUPD.md`, `ACTION-PLAN.md`.

| Блок | Статус | Что сделано | Остаток |
|---|---|---|---|
| B8: `PromptBuilder` | `[code]` | Исправлен компиляционный блокер, нейтральные факты, tick-точность, роль LLM | Runtime-трасса промптов/ответов |
| B9: `SignalCalibrator` | `[code]` | PreScore/Calibrate, ModelOnly/Blend/EngineOverride, отказ инференса → HOLD | Калибровка на достоверных outcomes |
| B10: сырые индикаторы | `[code]` | `rsi_raw/adx_raw/atr_abs/...`, volume SMA20 | Проверка `ema_align`/feature coverage |
| B11: `TradingEngine` | `[code]` | DI, явный symbol, tick-aware context, activeSymbols, SYMBOL-CAP | Интеграция с E1 decision journal |
| P2.1: диагностика signal funnel | `[code]`/analysis | Диагностика по логам, выявлены структурные причины низкого prior | Использовать как вход для P2.6/E2 |
| Фикс `volume_ratio` | `[code]` | Объём из последней закрытой свечи, не из WS-тика | Runtime coverage |
| P2.2: убрать двойной штраф | `[code]` | Независимая рубрика модели, нейтральные факты | Проверка на новых логах |
| P2.3: Blend 0.7/0.3 | `[code]` | `AI:BlendModelWeight`, валидация | Не заменяет edge gate |
| P2.5: Hold→Prior | `[code]` | Сильный согласованный prior может пересматриваться; guards сохранены | Измерять avoided/false veto cost |
| P2.4: MinConfidence из данных | `[code]` | Expectancy после издержек в R, recommendation-only tuner, offline tool | Пересчитать только на E1 net outcomes |
| P2.6: валидация весов скоринга | `[todo]` | Единственный незакрытый пункт старой SECUPD-очереди | Перенесён в Phase 4/E2 |

**Вывод:** старая проблема «ничего не покупает» диагностирована и частично исправлена на уровне сигнала, но частоту входов нельзя увеличивать вручную. Сначала E1/E2/E3.

## C. Биржевой аккаунт / UI / режимы

Источник: `plan.md`, `changes.md`, `SETUP-WINDOWS.md`.

| Блок | Статус | Что сделано |
|---|---|---|
| C12–C14: account service / REST / balances | `[code]` | Read-only опрос, free/locked, open orders, WS+REST цены, equity/partial |
| C15–C18: Dashboard/Trading/Main UI | `[code]` | Раздельные панели бота и биржи, режим/ключи, Dispatcher, ручное обновление |
| C19: DI/preflight | `[code]` | SignalCalibrator/ExchangeAccountService, GPU/config preflight |
| C20: appsettings | `[code]` | Demo REAL ORDERS по умолчанию; Paper отдельно; Live отдельно |
| Режимы | `[runtime]`/`[code]` | Demo + `PaperTradingMode=false` = ордера на Demo, не бумажная симуляция |

**Остаток:** DPAPI-ключи, реальные балансы Demo, UI-поведение и сохранение настроек проверяются только на целевой Windows-машине.

## D. Символы / реестр / новости

Источник: `plan.md`, `ACTION-PLAN.md`, `PLAN-PROFIT-ENGINE.md`.

| Блок | Статус | Что сделано |
|---|---|---|
| D21–D26: symbols registry | `[code]` | `symbols.json`, лимиты, BASE/QUOTE, tick/step, PriceDecimals, settings UI |
| Symbol policy | `[code]` | Stable/benchmark/unavailable исключаются из трендовых входов; active universe/config hash |
| News relevance | `[code]` | `AllowGeneralNewsFallback=false`, crypto-relevant GENERAL bucket, absence-of-news honesty |
| NewsSymbolMapper | `[code]` | Словари/синонимы, ambiguous tickers, исправления компиляции |

**Остаток:** direction mismatch, UI persistence, event catalog и depeg-контур относятся к Phase 6/E4.

## E. Понятность торговых решений

Источник: `ACTION-PLAN.md`.

| Блок | Статус | Что сделано |
|---|---|---|
| TP-FIX | `[code]` | `TryFixSignalTakeProfit`, `AutoFixSignalTakeProfit`, net R/R правило |
| Единый финал `ИТОГ` | `[code]` | AI proposal ≠ ордер; ОТКАЗ/ПАУЗА/КУПЛЕНО/ПРОДАНО/ЗАКРЫТО/HOLD |
| News relevance | `[code]` | См. блок D |

**Вывод:** human-readable логи улучшены, но они не заменяют machine-readable E1 ledger.

## F. SECUPD P0 — защита капитала

Источник: `SECUPD.md`.

| Пункт | Статус | Суть |
|---|---|---|
| P0.1 биржевые стоп-ордера OCO | `[code]` | OCO SL/TP, замена trailing |
| P0.2 независимый мониторинг | `[code]` | Tick/WS контур сопровождения позиций |
| P0.3 очередь исполнения | `[code]` | Приоритеты, durable execution intent, clientOrderId idempotency, retry/backoff |
| P0.4 персист DrawdownTracker | `[code]` | `risk_state.json`, peak/day start/max drawdown, restore |
| P0.5 recon + user-data | `[code]` | listenKey/user-data WS, polling fallback, adoption/cancel, partial fills |
| P0.6 kill-switch | `[code]` | Auto/manual, persist/reset, UI/DI |
| P0.7 защита от случайного Live | `[code]` | Двухшаговый Live, отдельные Live-ключи, баннер REAL MONEY |

**Важно:** эти `[code]`-статусы не закрывают runtime-приёмку. Новый baseline PLAN-PROFIT-ext показал, что terminal protective fill может не применяться к локальной позиции; это Phase 1/P0.9.

## G. SECUPD P1 — деньги и данные

Источник: `SECUPD.md`.

| Пункт | Статус | Суть |
|---|---|---|
| P1.1 paper slippage | `[code]` | Проход по стакану, partial fill, avg price, IOC-cap |
| P1.2 комиссии в R/R/BE | `[code]` | Net R/R, TP, sizing, breakeven, NetPnL |
| P1.3 tick/step/dust | `[code]` | DustPolicy, cancel audit, OCO tail metrics |
| P1.4 WS integrity | `[code]` | REST reload, gap detection, warmup, fail-closed freshness |
| P1.5 liquidity filter | `[code]` | Spread/depth/volume gates, per-symbol thresholds |
| P1.6 429/-1003 weight budget | `[code]` | Used-weight, soft limit для polling, ban handling |
| P1.7 portfolio risk | `[code]` | Exposure/correlation/daily loss/loss-streak, restore from history |
| P1.8 clock drift | `[code]` | ClockDriftMonitor, metrics/alerts |

**Остаток:** runtime fault-injection, Demo acceptance, deposit/withdrawal accounting и связь с E1 net outcomes.

## H. Transport / liveness / P0.8

Источник: `changes.md`, `PLAN-PROFIT-ENGINE.md`, `PLAN-PROFIT-ENGINE-ext.md`.

| Блок | Статус | Что сделано | Остаток |
|---|---|---|---|
| Жёсткий дедлайн REST-попытки | `[code]` | `WithHardDeadlineAsync`, транзиентный timeout, retry/backoff | Fault-injection |
| MainLoop не умирает от timeout | `[code]` | Выход только по реальной отмене | Soak |
| Phase budgets | `[code]` | `LOOP-PHASE-TIMEOUT`, `LOOP-STALL`, recovery lock budget | Watchdog/heartbeat acceptance |
| Orphan HTTP/handler hygiene | `[todo]` | Учёт abandoned requests, oldest orphan, ConnectTimeout/PooledConnectionLifetime | Phase 10 |
| LOOP-HEARTBEAT/watchdog | `[todo]`/`[accept]` | Отсутствие итераций не должно маскироваться polling-таймером | Phase 10 |

## I. Логирование

Источник: `LOGGING-PLAN.md`, `docs/LOGGING.md`.

| Блок | Статус | Что сделано |
|---|---|---|
| Единый формат/каналы | `[code]` | Unified formatter, channel routing, masking, invariant culture |
| Устранение дублей | `[code]` | Одно событие — одна запись; trades не копирует trading |
| Exchange summary | `[code]` | Compact summaries вместо полных account/ticker/klines тел |
| Noise/levels | `[code]` | LogGate, heartbeat, expected states → Debug |
| AI channel | `[code]` | PROMPT/THINKING/RESPONSE/SIGNAL отдельно от ai-native |
| Docs/settings/tools | `[code]` | `docs/LOGGING.md`, defaults, `watch_logs.py` |

**Остаток:** Demo-замер объёма, отсутствие WRN-спама, masking, rotation, `SeparateChannelFiles=false` acceptance.

## J. PLAN-PROFIT P0: код внесён, runtime не закрыт

Источник: `PLAN-PROFIT-ENGINE.md`, `PLAN-PROFIT-ENGINE-ext.md`.

| Блок | Статус | Суть |
|---|---|---|
| P0.1 equity/free/locked | `[partial]` | Snapshots/restore есть; deposit/withdrawal и рестарт после убытка не подтверждены |
| P0.2 kill-switch | `[partial]` | Код есть; persist/reset runtime не подтверждён |
| P0.3 data recovery | `[partial]` | REST≠live, quarantine/reconnect код есть; ENJ/DASH loops не закрыты |
| P0.4 user-data/recon | `[partial]` | Polling fallback есть; terminal protective fill defect открыт |
| P0.4a active entry protection | `[partial]` | ActiveEntryRegistry/grace/deadline есть; гонка с recon не проверена |
| P0.5 LLM off critical path | `[partial]` | EngineOnly/Shadow подтверждён логами; Off/VetoOnly/late-veto не проверены |
| P0.6 symbol universe | `[partial]` | Active universe/exclusions подтверждены; direction/UI persistence не проверены |
| P0.6a prompt roles | `[partial]` | Код ролей есть; inference-трасса не подтверждена |
| P0.8 transport/liveness | `[partial]` | Hard deadlines есть; orphan/watchdog/soak не подтверждены |
| P0.9 exit lifecycle | `[code]`/`[accept]` | Durable pending identity, terminal resolver, dust-review, metrics; Demo acceptance обязательна |
| P0.3a quarantine isolation | `[code]`/`[accept]` | Per-stream diagnostics, dual freshness, backoff/single-flight, metrics; Demo acceptance обязательна |

## K. E1 шаг 1: ledger contracts/storage

Источник: `docs/E1-LEDGER.md`, текущая правка.

| Блок | Статус | Что сделано |
|---|---|---|
| `DecisionLedgerRecord` / `FillLedgerRecord` / `OutcomeLedgerRecord` | `[code]` | Machine-readable контракты, source/environment/account, fee valuation, point-in-time features |
| `IE1Ledger` / `JsonE1Ledger` | `[code]` | JSONL, durable flush, idempotency, immutable final outcomes, fail-closed corruption |
| `E1LedgerTests` | `[code]` | Регрессионные контракты добавлены, не запускались |
| Runtime producers | `[code]` | Decision/entry/protection/exit/outcome подключены к `E1Runtime`; рестарт/polling recovery добавлен |
| Demo acceptance | `[accept]` | Restart/polling/fees/net R не подтверждены |

---

# Часть II. Текущий baseline и блокеры

Источник: `PLAN-PROFIT-ENGINE-ext.md`, логи 2026-11-26 17:28–20:22 (+03:00).

## 1. Что подтверждено

- Demo-окружение, REAL ORDERS, не Paper.
- EngineOnly/Shadow: LLM не наблюдался в критическом пути.
- Active universe: DOGE/DASH/XRP/ENJ/BNB; stable/benchmark/unavailable исключены.
- OCO submit/accept и trailing replacement подтверждены REST-ответами.
- User-data stream 410 → explicit polling fallback.
- Risk-state restore и equity/free/locked snapshots наблюдались.
- REST-прогрев отделён от live confirmation.

## 2. Главные блокеры

### Blocker 1: фантомная позиция после биржевого выхода

DOGE OCO исчезла, USDT-баланс изменился, но локальная позиция не была уменьшена/закрыта. Последующие close пытались продать уже проданный объём. Это блокирует E0/E1.

**Требование:** любое исчезновение OCO разрешается через биржевую историю до изменения локального lifecycle; уже проданный объём не предлагается к продаже повторно.

### Blocker 2: recovery одного символа раскачивает общий WS

ENJ/DASH recovery вызывает repeated resubscribe/reconnect и затрагивает здоровые пары.

**Требование:** per-symbol isolation, dual kline/depth freshness, bounded reconnect/backoff, operator escalation, healthy-symbol progress preserved.

### Blocker 3: нет достоверного net outcome ledger

Изменение USDT-баланса не является net PnL. Нет связанной цепочки entry/protection/exit/fees/net R.

**Требование:** E1 machine-readable ledger с реальными fills, комиссиями, protection resolution и idempotent outcomes.

### Blocker 4: E0 не закрыт

Нет подтверждённого сквозного Demo acceptance: entry fill → OCO → SL/TP/partial → recon → restart → risk-state → kill-switch → fees/net R.

---

# Часть III. Единый активный план

## Phase 0. Консолидация документации

**Статус:** `[code]` этот документ.

Задачи:

- `[x]` Собрать предыдущие/текущие/будущие планы в один мастер-план.
- `[x]` Сохранить исходные документы как исторический источник.
- `[x]` Указать один активный порядок работ.
- `[todo]` После каждого следующего рабочего шага обновлять статус в этом файле, а не создавать новую параллельную очередь.

## Phase 1. Закрыть safety-блокеры до статистики

**Цель:** система не создаёт фантомные позиции, не раскачивает общий WS из-за одного символа и не останавливается навсегда из-за внешнего I/O.

### 1.1 P0.9 runtime: восстановить фактический DOGE exit

- `[todo]` Через Demo order/trade history восстановить orderId, fills, fees, причину расхождения.
- `[todo]` Не отправлять новые ордера в рамках документационного аудита.
- `[accept]` Зафиксировать факт: биржевой exit был применён к локальной позиции один раз.

### 1.2 P0.9 acceptance

- `[accept]` Entry fill → OCO accepted → SL trigger → polling без user-data stream → позиция закрыта один раз, dust отдельно.
- `[accept]` То же для TP и partial protective fill.
- `[accept]` Гонка: fill перед cancel, cancel `-2011`, resolver находит fill.
- `[accept]` Повторный polling/restart не применяет fill второй раз.
- `[accept]` Lookup истории недоступен → entries frozen, alert; после восстановления состояние разрешено.
- `[accept]` Manual close после уже исполненного SL не создаёт новый sell-order.

### 1.3 P0.3a acceptance

- `[accept]` Собрать per-stream диагностику ENJ/DASH: kline/depth counters, last payload, subscribe ids/ack.
- `[accept]` Healthy пары не теряют freshness из-за quarantine одного символа.
- `[accept]` Quarantine expiry → REST reload → live confirmation обоих стримов; entries запрещены на всём пути.
- `[accept]` Persistent silent symbol → exponential backoff + operator alert, без бесконечного одинакового цикла.
- `[accept]` Общий reconnect single-flight/cooldown/jitter; healthy progress preserved.

### 1.4 Остальные P0 runtime

- `[accept]` Equity/free/locked/deposit/withdrawal: нет ложного PnL/risk.
- `[accept]` Kill-switch persist/engage/reset audit.
- `[accept]` Active entry не отменяется recon преждевременно.
- `[accept]` LLM роли Off/Shadow/VetoOnly/Explainer и late-veto не блокируют защиту.
- `[accept]` Symbol policy/direction mismatch/UI persistence.
- `[accept]` Transport fault-injection: timeout не останавливает цикл; orphan/watchdog видны.

**Критерий выхода из Phase 1:** нет фантомных позиций; исчезнувшая OCO имеет terminal resolution или explicit unresolved freeze; карантин одного символа не вызывает бесконечный reconnect общего рынка.

## Phase 2. E1: журнал решений, fills и outcomes

**Цель:** сквозная цепочка:

```text
decision_id → entry_intent_id → entry_fill(s)
→ protection_identity → protective_fill(s)
→ position_close / dust_review → outcome_net_R
```

### 2.1 Шаг 1 — contracts/storage

- `[code]` `DecisionLedgerRecord`, `FillLedgerRecord`, `OutcomeLedgerRecord`.
- `[code]` `JsonE1Ledger`: JSONL, durable flush, idempotency, fail-closed corruption.
- `[code]` Source/environment/account partitioning.
- `[code]` Fee valuation/conversion contracts.
- `[code]` `E1LedgerTests` добавлены, не запускались.

### 2.2 Шаг 2 — runtime decision/entry wiring

- `[code]` `IE1Ledger` подключён в DI/runtime через `E1Runtime`.
- `[code]` Каждый финальный gate decision получает `decision_id`, `run_id`, stage, reason_code, features, gate versions.
- `[code]` Ранние return пишут причину: cooldown, max positions, no-book, warmup, market-data, risk, confidence, liquidity, kill-switch.
- `[code]` HOLD/NO_FILL не считаются сделками.
- `[code]` Entry intent создаётся до отправки и связан с decision; write-ahead journal fail-closed.
- `[code]` Demo/Paper/Live source не смешивается; Shadow не создаёт fills.

### 2.3 Шаг 3 — fills/protection wiring

- `[code]` Entry/exit/protective fills пишутся как deltas, не cumulative duplicates.
- `[code]` ExchangeTrade evidence требует реальный exchange trade id и fee (`myTrades` / executionReport trade id).
- `[code]` OrderCumulativeDelta не выдаётся за myTrades.
- `[code]` Fee в base конвертируется по цене того же филла с evidence; BNB/прочее остаётся Unvalued до отдельного conversion evidence.
- `[code]` Protection identity/resolution связан с fills и outcome.
- `[code]` Balance delta не заменяет fee ledger.

### 2.4 Шаг 4 — outcomes/restart/polling

- `[code]` Один финальный outcome на position lifecycle.
- `[code]` NoFill outcome на entry intent без fills.
- `[code]` Unresolved outcome может финализироваться один раз.
- `[code]` Restart/polling recovery переиспользует lifecycle/intent ids и дедуплицирует fills/fees.
- `[code]` Dust отделён от realized exit quantity.
- `[code]` Net R считается от initial quote risk и actual fills/fees.

### 2.5 Шаг 5 — E1 acceptance

- `[accept]` 100% решений имеют decision_id/stage/reason.
- `[accept]` Повторная доставка события идемпотентна.
- `[accept]` Human logs не заменяют ledger; repeats не используются как статистическая выборка.
- `[accept]` Demo/Paper/Shadow/Live partitions раздельны.
- `[accept]` Комиссия учитывается один раз, включая base/BNB.
- `[accept]` Point-in-time features/gate versions сохранены для walk-forward.

**Критерий выхода из Phase 2:** каждый statistically usable outcome связан с реальными entry/exit fills, комиссиями, protection resolution и initial risk.

## Phase 3. E0: Demo infrastructure acceptance

**Цель:** подтвердить, что система исполняема и наблюдаема на Demo, а не только в коде.

Checklist:

- `[accept]` Demo environment/account fingerprint/config hash/build hash зафиксированы в run.
- `[accept]` Market WS: per-symbol freshness, resubscribe, gap metrics.
- `[accept]` Order submit/fill: post-only и market smoke-test.
- `[accept]` OCO SL/TP/trailing/emergency close.
- `[accept]` Independent protection monitor работает при занятом LLM/UI/сети.
- `[accept]` Idempotency duplicate clientOrderId.
- `[accept]` Risk-state persistence после убытка/просадки/restart.
- `[accept]` User-data stream или explicit polling fallback.
- `[accept]` Reconciliation orphan order/OCO/position tests.
- `[accept]` 429/-1003 weight-budget test.
- `[accept]` Tick/step/minNotional/dust на проблемных символах.
- `[accept]` Portfolio/symbol/cash caps логируют requested/capped quantity.
- `[accept]` PnL/fees/slippage подтверждены через E1 ledger.

**Критерий выхода из Phase 3:** E0 закрыт на Demo. Без этого нельзя строить EdgeGate как основание для Live.

## Phase 4. E2: BucketTable + статистическое ядро + P2.6

**Цель:** статистика по достоверным net outcomes, а не по human logs или сырым сигналам.

### 4.1 BucketKey

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

Задачи:

- `[todo]` Границы корзин фиксируются по train-периоду.
- `[todo]` Признак, дробящий бакеты ниже минимального покрытия, отклоняется.
- `[todo]` Fallback: полный ключ → убрать слабый признак → direction×regime → direction.
- `[todo]` Ниже минимального покрытия → `INSUFFICIENT_COVERAGE`, вход запрещён.
- `[todo]` Версионировать `bucket_definition_hash`, `artifact_hash`, `train_window`, `embargo`.

### 4.2 EmpiricalEdgeEstimator

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

- `[todo]` Реализовать `EmpiricalEdgeEstimator` по net R.
- `[todo]` Wilson оставить для диагностики win rate, не как единственный EV gate.
- `[todo]` Walk-forward, purged split, embargo.
- `[todo]` Проверять ширину интервала и стабильность по окнам.
- `[todo]` `min_samples` задавать по мощности/ширине интервала, не магическим 200.
- `[todo]` Reliability/Brier/ECE отдельно для engine prior, model confidence и bucket probability.

### 4.3 P2.6: валидация весов скоринга

Источник: единственный незакрытый пункт старой SECUPD-очереди.

- `[todo]` Подтвердить/подогнать веса EMA/Supertrend/RSI/MACD/Stoch/OBV и штрафы на истории.
- `[todo]` Reliability diagram: уверенность должна соответствовать реальной частоте успеха.
- `[todo]` `Dead/Contradicts` веса исключаются только через версионированный артефакт.
- `[todo]` Не подбирать веса на испорченных данных, без издержек и без E1 outcomes.

**Критерий выхода из Phase 4:** BucketTable версионирована, coverage известна, EV_lower считается по net R, веса/пороги имеют воспроизводимый артефакт.

## Phase 5. E3: EdgeGate и запрет недоказанного Live

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

- `[todo]` `ProfitabilityGate` до сайзинга.
- `[todo]` Запрет входа при insufficient coverage, stale table, широком интервале, `EV_lower <= threshold`.
- `[todo]` `AI.MinConfidence` вторичен и не заменяет edge.
- `[todo]` Live запрещён, пока E0 не закрыт и edge не доказан на Demo/shadow.
- `[todo]` Каждый отказ имеет `reason_code` и числовые поля.
- `[todo]` Сравнивать old gate (`MinConfidence`) и new gate (`EV_lower`) в shadow.

**Критерий выхода из Phase 5:** ни один Live-вход не проходит без coverage и положительного edge.

## Phase 6. E4: новости, события и режимы

Каталог событий:

```text
BLOCK: delisting, trading halt, exchange outage, depeg, hack/exploit
REDUCE: regulatory enforcement, macro window, unknown risk
IGNORE: listing, partnership, upgrade — не является разрешением на вход
```

Задачи:

- `[todo]` `config/event-catalog.json`: типы, severity, keywords, negation, actions.
- `[todo]` Дедуп по нормализованному заголовку.
- `[todo]` Окно свежести и credibility.
- `[todo]` Критичное событие: 2 независимых источника или официальный анонс.
- `[todo]` Макро-календарь и blackout-окна без парсинга новостей.
- `[todo]` Рыночная аномалия: движение + объём + спред.
- `[todo]` Stablecoin pairs: отдельный depeg-контур.
- `[todo]` LLM не является единственным детектором критичного события.
- `[todo]` Prompt injection: новости — недоверенный текст, не инструкции.

**Критерий выхода из Phase 6:** событийный запрет работает без LLM и объясним одной строкой.

## Phase 7. E5: LLM VetoOnly / Shadow / Off

Задачи:

- `[todo]` `AI.Role` и `AI.Enabled`.
- `[todo]` Вход только из engine setup; LLM BUY не создаёт ордер.
- `[todo]` `Hold→Prior` и `EngineOverride` не являются источником входа в Live.
- `[todo]` Контракт veto: `veto`, `veto_reason`, `event_type`, `risk_flags`, `invalidation`.
- `[todo]` Строгая JSON-схема; битый JSON/дегенерат/timeout не разрешают вход.
- `[todo]` Асинхронный вызов, кэш, бюджет, отмена по устареванию свечи.
- `[todo]` SL/TP/protection никогда не ждут LLM.
- `[todo]` Explainer вызывается после решения.
- `[todo]` Тень отклонённых: считать исходы veto-сигналов.
- `[todo]` Метрики: `veto_precision`, `avoided_R`, `unique_veto`, `false_veto_cost`, `llm_latency`.
- `[todo]` Если `avoided_R <= false_veto_cost` на достаточной выборке — роль сжимается до explainer/off.

**Критерий выхода из Phase 7:** LLM может только запретить и объяснить; её польза измерена в R.

## Phase 8. E6: исполнение, post-only и защита

### 8.1 Post-only lifecycle

- `[todo]` Demo smoke-test: maker fill, post-only reject, timeout cancel, partial fill, cancel race.
- `[todo]` Отмена при уходе цены/спреда за коридор, не только по таймауту.
- `[todo]` Запрет догоняющего market-входа после отмены.
- `[todo]` Частичный fill защищается немедленно; остаток отменяется и аудируется.
- `[todo]` `fill_probability` по submitted intents, не по AI-сигналам.
- `[todo]` Paper post-only моделирует очередь/вероятность fill, не мгновенное исполнение.
- `[todo]` Метрики: `time_to_fill`, `fill_ratio`, `post_only_reject_rate`, `cancel_rate`, `expired_rate`.

### 8.2 Adverse selection и slippage

Для каждого maker-fill:

```text
arrival_mid
fill_price
mid_after_1m / 5m / 15m
adverse_selection_R = side_sign * (mid_after - fill_price) / initial_risk
slippage_vs_arrival = side_sign * (fill_price - arrival_mid) / initial_risk
```

Задачи:

- `[todo]` Таблицы `fill_probability`, `expected_slippage`, `adverse_selection` по bucket `size×depth×vol×spread`.
- `[todo]` EdgeGate использует ожидаемые издержки исполнения, а не только комиссию.
- `[todo]` Серия плохих fills → пауза symbol/execution mode.

### 8.3 Защита

- `[todo]` Вход не считается состоявшимся для статистики, пока OCO не подтверждена.
- `[todo]` Если OCO не поставлена: аварийное закрытие или quarantine по режиму.
- `[todo]` Crash-safe intent journal: восстановить незавершённый entry/protection после падения.
- `[todo]` Учитывать гэп через stop-limit: stop может не исполниться по limit.
- `[todo]` `unprotected_seconds` и `protection_missing` — метрики и алерты.
- `[todo]` Тест: LLM занята, UI закрыт, сеть моргнула, процесс упал — защита/recon восстанавливают состояние.

**Критерий выхода из Phase 8:** позиция не остаётся неуправляемой; издержки в EV соответствуют фактическим.

## Phase 9. E7: капитал, сайзинг и риск разорения

Задачи:

- `[todo]` Сайзинг от equity, initial risk, ATR и edge; не от свободного USDT как equity.
- `[todo]` Явный журнал: `requested_qty`, `risk_qty`, `cash_cap_qty`, `symbol_cap_qty`, `portfolio_cap_qty`, `correlation_cap_qty`, `rounded_qty`, `final_qty`.
- `[todo]` Лимиты: символ, актив, коррелированная группа, суммарная экспозиция, max open positions.
- `[todo]` Throttle и kill-switch по просадке и дневному убытку.
- `[todo]` Loss-streak pause и symbol daily loss limit подтверждаются runtime.
- `[todo]` Оценка риска разорения по распределению net R, ставке и хвостовым сценариям.
- `[todo]` Ступени капитала: минимальная → следующая только после N сделок и удержания expectancy.
- `[todo]` Запрет увеличения риска после убытков и ручного снятия лимитов в Live без audit flow.

**Критерий выхода из Phase 9:** система снижает риск и останавливается при деградации; нулевой риск разорения не обещается.

## Phase 10. E8: надёжность, поставка, безопасность, логирование

Задачи:

- `[todo]` Headless-режим: торговля не зависит от окна WPF.
- `[todo]` Fail-closed для критических зависимостей.
- `[todo]` LOOP-HEARTBEAT/watchdog: отсутствие итераций видно, алерт и контролируемый перезапуск.
- `[todo]` Транспортная гигиена: жёсткие дедлайны всех внешних вызовов, abandoned requests, oldest orphan, handler recycle, `ConnectTimeout`, `PooledConnectionLifetime`.
- `[accept]` Soak ≥ 24 ч на Demo: непрерывность решений, память/handles/sockets, timeout/orphan, сетевые штормы.
- `[todo]` Секреты: DPAPI, masking, запрет полных account dumps в логах/промптах.
- `[accept]` Native DLL/model/build hashes зафиксированы; AMD Polaris/RX590 обновлён до llama.cpp с фиксом кластерного мусора.
- `[todo]` Воспроизводимая поставка и откат версии.
- `[todo]` Метрики гейтов, отказов, latency, деградации; Telegram alerts.
- `[todo]` Effective config hash в каждом run.
- `[accept]` Logging acceptance: объём, отсутствие полных тел при Summary, no WRN spam, masking, rotation, AI/native separation.
- `[todo]` README/assessment обновляются после каждой runtime-приёмки.

Операционные документы:

- `docs/SETUP-WINDOWS.md`: Windows x64, .NET 8, GPU backends, DPAPI, Demo/Paper/Live, model/native paths.
- `docs/LOGGING.md`: каналы, формат, уровни, настройки, словарь событий, просмотр.
- `docs/E1-LEDGER.md`: контракт machine-readable ledger.

**Критерий выхода из Phase 10:** поставка воспроизводима, версии зафиксированы, откат возможен, soak и logging acceptance закрыты.

## Phase 11. E9: Shadow → Demo edge → минимальный Live

Этапы:

1. **Replay/shadow:** engine setup + BucketTable + EdgeGate без ордеров.
2. **Demo execution acceptance:** post-only, OCO, cancel, partial, recon, kill-switch, risk-state, 429-budget.
3. **Demo edge validation:** достаточное покрытие, положительный `EV_lower`, стабильность walk-forward.
4. **Live minimum capital:** только если Demo acceptance закрыт и edge не деградировал.
5. **Auto-degradation:** снижение риска/запрет входов при потере edge, росте slippage/adverse selection, ошибках исполнения.

Задачи:

- `[todo]` Shadow сравнивает old/new gate по net expectancy и частоте.
- `[todo]` Измерять долю отказов coverage и влияние на frequency.
- `[todo]` Измерять расхождение replay-издержек и фактических.
- `[todo]` Мониторить наполненность бакетов, расхождение p и факта, expectancy decay.
- `[todo]` Пересчёт таблиц по расписанию, approval по хешу, откат версии.
- `[todo]` Переход на следующую ступень только по факту удержания expectancy.

**Критерий выхода из Phase 11:** Live разрешён только после закрытого E0 и устойчивого положительного edge; нет обещаний гарантированной прибыли.

---

# Часть IV. Сквозные требования

## 1. Целевые метрики

| Метрика | Цель | Где считается |
|---|---|---|
| `net_expectancy_R` | Нижняя граница > 0 после всех издержек | `EdgeEngine` + `BucketTable` |
| `expectancy_lower_bound` | Bootstrap/t-интервал по net R, не только Wilson win rate | `EmpiricalEdgeEstimator` |
| `fill_probability` | Отдельно по post-only bucket, горизонт и режим | E1 execution ledger |
| `adverse_selection` | Движение цены против maker-fill через 1/5/15 мин | `ExecutionQualityMonitor` |
| `slippage_R` | Фактическое отклонение от reference mid/arrival price | `ExecutionQualityMonitor` |
| `max_drawdown` | В пределах лимита, дальше kill-switch | `DrawdownTracker` |
| `risk_of_ruin` | Оценка по распределению net R и ставке, не гарантия нуля | `CapitalRiskModel` |
| `decision_latency` | Детерминированный путь без LLM; бюджет задаётся конфигом | `DecisionMetrics` |
| `journal_coverage` | 100% решений имеют decision_id, стадию и причину | E1 ledger |
| `live_gate` | Live запрещён без runtime-приёмки и положительного edge | `ProfitabilityGate` |

Рост капитала = `expectancy × frequency × sizing × time`. Если expectancy не доказана, увеличение частоты или сайзинга только ускоряет убыток.

## 2. Минимальные события для BucketTable / EdgeGate

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

Без этих событий нельзя честно посчитать coverage, fill probability, expected slippage, adverse selection, net expectancy, вклад LLM, частоту/причины отказов и деградацию преимущества.

## 3. Критерии готовности

```text
E0 закрыт на Demo, а не только в коде.
Equity/free-cash/deposit accounting проверен.
Kill-switch переживает рестарт и имеет audit trail.
Market-data recovery не зацикливается.
Ни один внешний вызов не может остановить торговый контур навсегда.
Пауза контура видна в логе/метриках; watchdog поднимает или глушит входы.
Soak-прогон ≥ 24 ч без тихой тишины решений, без роста памяти/сокетов.
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

## 4. Правила ведения плана

1. Один рабочий шаг = одно сообщение заказчику; после шага обновляется этот мастер-план.
2. Не создавать новую параллельную очередь; новые задачи добавляются в соответствующую Phase.
3. `[code]` не означает `[runtime]` и не закрывает `[accept]`.
4. Сборки/компиляции/тесты/GPU-инференс/новые ордера выполняются только по явной команде заказчика.
5. Исторические документы не удаляются; они остаются источником деталей и аудита.
6. Перед передачей логов наружу применять masking; не публиковать полные балансы/account dumps/ключи/модели.
7. Demo с `PaperTradingMode=false` — это реальные ордера на Demo, не бумажная симуляция.
8. Live запрещён до закрытия E0 и подтверждения edge.

---

## Ближайший исполнимый порядок

1. **Phase 1 / P0.9 runtime:** восстановить фактический DOGE exit через Demo order/trade history; не отправлять новые ордера в рамках аудита.
2. **Phase 1 / P0.9 acceptance:** проверить resolver и регрессию гонки на Demo SL/TP/partial/restart.
3. **Phase 1 / P0.3a acceptance:** собрать per-stream диагностику ENJ/DASH и подтвердить isolation/backoff.
4. **Phase 2 / E1 acceptance:** Demo-приёмка decision/fill/protection/outcome цепочки, restart/polling, fees/net R.
5. **Phase 3 / E0:** повторить Demo infrastructure acceptance.
6. **Phase 3 / E0:** повторить Demo infrastructure acceptance.
7. **Phase 4–11:** статистика, EdgeGate, события, LLM-роли, исполнение, капитал, надёжность, Live ramp-up.
