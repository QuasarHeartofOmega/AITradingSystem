# PLAN-PROFIT-ENGINE — план проверяемого торгового движка

**Редакция:** 2026-11-27, по предоставленным runtime-логам **2026-11-26, 17:28–20:22 (+03:00)**.  
**Проанализированный checkout:** `c9b12c2`, commit от 2026-11-26 17:17:45 +03:00. Соответствие этого checkout бинарникам, создавшим логи, по хэшу сборки не подтверждено.  
**Статус:** активный рабочий план; P0.9 код-изменения внесены одним шагом, сборки/тесты не запускались, runtime-приёмка P0.9 не выполнена. По P0.3a внесены шаги 1–4 (per-stream диагностика; раздельная свежесть kline/depth; изоляция общего reconnect и backoff карантина; метрики/итоговый entries_allowed) — без сборки и без runtime-подтверждения. Полная предыдущая редакция сохранена в [docs/history/PLAN-PROFIT-ENGINE.md](docs/history/PLAN-PROFIT-ENGINE.md); её исторические счётчики не заменяют новый baseline.  
**Ограничение этой правки:** P0.3a шаги 3–4 — изменены `MarketAnalyzer` (серия карантинов `QuarantineStreak` со сбросом только по live-подтверждению обоих стримов, фактические секунды карантина, `TryResumeAfterReconnect` для здоровых пар с непрерывной сеткой свечей), `TradingConfig` (`MarketDataQuarantineMaxSeconds` + валидация), `BinanceWebSocketClient` (single-flight принудительного реконнекта), `TradingEngine` (экспоненциальный backoff карантина, операторский алерт `DATA-QUARANTINE-ESCALATED` при серии ≥3, jitter reconnect-cooldown, сохранение здоровых пар при WS-UP без REST-перезагрузки, итоговый `entries_allowed` в `DATA-LIVE-CONFIRMED`/`DATA-QUARANTINE-LIFTED`), `MetricsCollector` (причины реконнекта, kept/reloads, серия/эскалации/секунды карантина, recovery-циклы по символам), `LogChannels`, контракты `MarketDataIntegrityTests`, `docs/LOGGING.md` и этот план. Шаги 1–2 внесены ранее. Сборки, компиляции, тесты, GPU-инференс и новые ордера не выполнялись.

## Главный вывод

Новый прогон подтвердил работу детерминированного пути без ожидания LLM, постановку и замену OCO, polling-реконсиляцию и восстановление части рыночных данных. Но **исполнение биржевого выхода не было своевременно применено к локальной позиции**: после исчезновения DOGE OCO и продажи 5333 DOGE бот продолжал считать открытыми 5333,661 DOGE. Последующие SL/manual-close пытались закрыть уже проданный объём, упирались в остаток 0,661 DOGE и оставляли фантомную позицию до конца сессии.

Второй блокер — **неустойчивый recovery ENJUSDT**: один молчащий символ многократно вызывает переподключение общего рыночного WS, затрагивающее остальные пары. Третий — отсутствие подтверждённого журнала филлов/net outcomes: изменение USDT-баланса нельзя выдавать за рассчитанную прибыль сделки.

**Приоритет:** P0.9 (закрытие жизненного цикла позиции) → P0.3a (изоляция молчащего символа) → E1 (журнал филлов/исходов) → повторная Demo-приёмка. Не переходить к Live и не ослаблять risk/confidence gates ради увеличения числа сделок.

---

## 1. Новый baseline: только факты предоставленных логов

Источники: `trading-20261126.txt`, `trades-20261126.txt`, `exchange-20261126.txt`, `ai-20261126.txt`. Все времена ниже — **+03:00**, не локальное время пользователя.

| Показатель | Наблюдение |
|---|---|
| Окно `trading` | 17:28:42.209–20:22:24.431, около 2 ч 53 м 42 с |
| Окружение | Binance Demo, REAL ORDERS; это не Paper |
| Active universe | DOGEUSDT, DASHUSDT, XRPUSDT, ENJUSDT, BNBUSDT — 5 пар |
| Исключены | FLRUSDT: `exchange_not_trading`; BTC/ETH: benchmark; USDC/FDUSD: stablecoin; BTTC/STRAX: registry disabled |
| LLM | Role=Shadow; `ENGINE-SIGNAL source=EngineOnly`; в AI-файле только загрузка модели, нет записей inference-запросов/ответов |
| GPU | RX590 GME, Vulkan, 23/41 фактически оффлоажено, CPU fallback; предупреждение о llama.cpp >= b9892 остаётся |
| Решения | 189 строк PRE-SCORE, 162 ENGINE-SIGNAL, 189 HOLD; это сырые строки, не число уникальных decision_id |
| Market-data отказы | 339 сырых строк `stage=market-data`; повторы агрегированы через `repeats` |
| Новые входы | 0 наблюдаемых отправок обычного entry-ордера |
| OCO | 2 POST `/orderList/oco` → 2 ACCEPTED-OCO; 2 успешные отмены предыдущих связок |
| User-data | POST `/userDataStream` → 410; explicit POLLING-FALLBACK, интервал 20 с |
| Реконсиляция | 511 RECON-START; 33 строки RECON-SUMMARY, с учётом repeats — 496; `unresolved=0`, `entries_allowed=True` во всех summary |
| HTTP | 4072 response-строки: 4070×200, 1×410, 1×400/-2011; явных 429/-1003 нет |
| WS recovery | 126 resubscribe, 37 reconnect-request, 37 WS-DOWN/UP, 30 quarantine/lift событий |
| Risk | RISK-STATE-RESTORED; первый equity 10016,26, последний 10009,96; peak 10020,85, day_start 10000,00; valuation_complete=True |
| Сделки для статистики | Закрытый bot outcome с entry/exit/fees/net R не восстановлен; trades=0 в стартовой калибровке |

**Как читать счётчики:** `trading` и `trades` пересекаются; их нельзя складывать. Строки с `repeats=N` обозначают дополнительные повторения. Например, 44 строки DATA-STALE соответствуют 114 событиям с учётом repeats. Отсутствие строк не доказывает отсутствие скрытого состояния вне окна логов.

### 1.1. Хронология DOGE: главный дефект

```text
17:28:44  DOGE free=0,661 + 5333 locked; локальная позиция=5333,661
17:29:04  OCO #4786653 принята на 5333; хвост 0,661 не покрыт
17:32:12  OCO #4786683 принята после trailing replacement
17:42:00  CLOSE-TRIGGER Stop-Loss, CLOSE-START qty=5333,661
17:42:00  DELETE OCO #4786683 → 400/-2011 Unknown order list
17:42:00  PROTECTION-GONE; локальная ссылка на защиту снята
17:42:00  GET account: USDT 4013,95942063 → 4317,47660962;
          DOGE остаётся free=0,661, locked больше нет
17:42:00  LIVE-BALANCE-CAP: sellable=0,661 → dust reject
17:42:00  защита не восстановлена; close сообщает «позиция остаётся открытой»
17:47:30  Manual close повторяет тот же путь с qty=5333,661
17:47:30  dust reject + protection qty=0 < minQty=1
до 20:22  RECON-START positions=1; summary protection_missing=0, unresolved=0
```

Разница USDT-баланса **+303,51718899** согласуется с продажей 5333 DOGE примерно по 0,0569. Это **не net PnL**: исходная цена входа, комиссии и точные execution fills в логах не восстановлены.

Подтверждённая последовательность: связка исчезла, баланс изменился, локальная позиция не была уменьшена/закрыта. Наиболее вероятно исполнение защитной ноги между последним account snapshot и отменой. Точный orderId/price/fee/time ноги неизвестен: нужен запрос истории ордеров/сделок, а не предположение.

### 1.2. Почему текущая реконсиляция пропустила проблему

Чтение checkout показывает механизм, согласующийся с логом:

1. `OrderExecutor.CancelProtectionAsync` при `-2011` пишет PROTECTION-GONE, вызывает `position.SetProtection(null)` и возвращает `true`.
2. Закрытие затем использует live free balance, но не уменьшает `position.Quantity` на уже исполненный биржевой выход.
3. `ReconciliationService.Classify` проверяет missing protection только у позиций с ненулевой `Protection`. После её очистки исчезнувшая OCO больше не классифицируется.
4. `RemediateAsync` ищет пропавшие ноги через сохранённые clientOrderId в `pos.Protection`; после очистки этот путь также недоступен.
5. Polling успешно читает open orders/lists/account, но это не заменяет lookup terminal protective orders и применение cumulative fill к позиции.
6. Dust 0,661 требует отдельного terminal dust-review состояния. Он не должен поддерживать фантомную позицию 5333,661 или бесконечный retry закрытия всего объёма.

Это вывод по коду + логам, **не подтверждение исправления**. Необходим регрессионный тест именно гонки `exchange fill → cancel -2011 → SetProtection(null) → polling recon`.

### 1.3. ENJ recovery и влияние на весь рынок

| Символ | REST reload | Live confirmed | Resubscribe | Quarantine |
|---|---:|---:|---:|---:|
| DOGEUSDT | 38 | 38 | 0 | 0 |
| DASHUSDT | 95 | 86 | 37 | 2 |
| XRPUSDT | 38 | 38 | 0 | 0 |
| BNBUSDT | 38 | 38 | 0 | 0 |
| ENJUSDT | 98 | 35 | 89 | 28 |

Счётчики — сырые строки. ENJ даёт основную часть recovery-шума; DASH тоже требует проверки. 37 запросов reconnect сопровождаются 37 парами WS-DOWN/UP. Это не доказывает 37 физических разрывов сокета, но показывает масштаб воздействия на общий сокет.

Карантин не является стабильным терминальным состоянием: 30 постановок и 30 снятий, после чего recovery продолжается. В 17:47:41 видно `DATA-QUARANTINE-LIFTED`, сразу PRE-SCORE/ENGINE-SIGNAL/HOLD при `entries_allowed=false` в событии снятия. Это не доказывает разрешённый вход, но диагностика должна явно разделять «данные можно анализировать» и «вход разрешён».

Нельзя по этим логам объявить виновником только биржу, конкретный stream parser или подписку. Нужны per-stream message counters, последние kline/depth timestamps и идентификаторы SUBSCRIBE/ack. Отдельно проверить, не сбрасывает ли общий reconnect прогресс других символов.

### 1.4. Что улучшилось относительно старого baseline

- LLM больше не наблюдается в критическом пути: EngineOnly/Shadow вместо последовательных ~47-секундных AI-предложений.
- Runtime явно исключает stable/benchmark/unavailable пары и пишет active universe/config hash.
- User-data failure больше не выдаётся за работающий stream: виден polling fallback.
- Risk-state восстанавливается; equity/free/locked разделены в snapshot.
- OCO submit/accept и trailing replacement подтверждены реальными Demo REST-ответами.
- REST-прогрев отделён от live confirmation; прежнего бесконечного зависания всего цикла в этом окне не видно.

Эти улучшения **не закрывают E0**: lifecycle exit/reconciliation и recovery остаются незавершёнными.

---

## 2. Статус прежних P0 по новому runtime

| Блок | Статус | Основание и оставшаяся работа |
|---|---|---|
| P0.1 equity/free/locked | `[~]` | Snapshots и restore подтверждены; ложной 50% просадки нет. Deposit/withdrawal accounting и рестарт после убытка ещё не проверены |
| P0.2 kill-switch | `[ ]` | В этом окне не срабатывал; персист аварии/reset не подтверждён |
| P0.3 data recovery | `[~]` | REST≠live работает, но ENJ/DASH повторяют recovery; код P0.3a (шаги 1–4) внесён, runtime-приёмка не выполнена |
| P0.4 user-data/recon | `[~]` | 410 → polling подтверждён; terminal protective fill не применён, нужен P0.9 |
| P0.4a active entry protection | `[ ]` | Новых entry не было; гонка с recon не проверена |
| P0.5 LLM off critical path | `[~]` | EngineOnly подтверждён; Off/VetoOnly/late-veto/runtime latency не проверены |
| P0.6 symbol universe | `[~]` | 5 активных пар и причины исключений подтверждены; direction_mismatch и UI persistence не проверены |
| P0.6a prompt roles | `[ ]` | Нет inference-трассы; соответствие runtime-промптов не подтверждено |
| P0.8 transport/liveness | `[~]` | Нет прежнего многочасового залипания в этом окне; orphan requests, watchdog и 24 ч soak не подтверждены |
| P0.9 exit lifecycle | `[ ]` | **Новый блокер; наблюдаемый дефект** |

`[x]` ниже используется только для конкретного подтверждённого факта, не для закрытия всего блока. `[~]` означает частичное подтверждение или наличие кода без полной приёмки. `[ ]` — нет runtime-приёмки.

---

## 3. P0.9: достоверное закрытие позиции и защита от фантомного остатка

**Статус после правки:** код и регрессионные контракты внесены; сборки/компиляции/тесты не выполнялись; Demo runtime-приёмка остаётся обязательной. Реализованы durable pending identity, общий terminal resolver, fail-closed pre-close/trailing/ensure-protection, кумулятивная комиссия/идемпотентность, dust-review отделение и метрики/журнал. Не подтверждено runtime: реальный DOGE-like SL/TP fill, restart, недоступность истории, partial protective fill.

**Цель:** любое исчезновение OCO разрешается через биржевую историю до изменения локального lifecycle; уже проданный объём никогда не предлагается к продаже повторно.

### Задачи

- `[~]` Сохранять durable protection identity отдельно от текущей ссылки `Position.Protection`: `Position.PendingProtection`/`ProtectionResolution` хранит list_id, client ids, order ids, covered qty, reason, attempts, timestamps. Персистится в positions.json.
- `[~]` На cancel `-2011`/unknown list: `PROTECTION-RESOLUTION-PENDING`, а не успешная отмена. Resolver запрашивает обе ноги через REST order history (комиссии обогащаются myTrades) и применяет cumulative executed qty/avg price/fee/time.
- `[~]` Не очищать identity до terminal-resolution. Если история недоступна/нога unknown/active — pending остаётся, `IsExecutionStateTrusted=false`, operator alert; пустой open lists не даёт entries_allowed.
- `[~]` Общий resolver `ReconciliationService.ResolveProtectionAsync` используется polling/recon/startup missing list и pre-close manual/SL. Идемпотентность по clientOrderId + cumulative qty/commission в `Position.AppliedExchangeFills/AppliedExchangeCommissions`.
- `[~]` После covered protective exit: основная позиция закрывается/уменьшается; хвост, который OCO не покрывала, переводится в `DUST_REVIEW` (`Position.IsDustOnly`), ordinary close/OCO не отправляются.
- `[~]` После частичного выхода quantity уменьшается; остаток защищается штатным контуром, dust-only остаток не считается основной позицией.
- `[~]` Manual/SL close до posymbol lock разрешает pending protective exit; blind MARKET по устаревшему quantity не отправляется.
- `[~]` Журнал различает close-deferred/pending, dust-review и реальное открытие позиции; добавлены PROTECTION-RESOLUTION-* и DUST-REVIEW события.
- `[~]` RECON-SUMMARY дополнен `pending_protection_resolutions` и `dust_reviews`; pending identity участвует в known protections, чтобы orphan-cleanup не снимал ноги до resolution.
- `[ ]` Автоматический учёт депозитов/выводов и полноценный net outcome ledger остаются E1/E7; изменение USDT после продажи не выдаётся за прибыль стратегии.

### Минимальная Demo-приёмка

1. Entry fill → OCO accepted → SL trigger → polling без user-data stream → позиция закрыта один раз, dust отдельно.
2. То же для TP и partial protective fill.
3. Гонка: нога исполняется непосредственно перед cancel; cancel возвращает `-2011`; resolver находит fill.
4. Повторный polling/restart не применяет fill второй раз.
5. Lookup истории временно недоступен → entries frozen, alert; после восстановления состояние разрешено.
6. Manual close после уже исполненного SL не создаёт новый sell-order и не пишет ложное «позиция открыта» для проданного объёма.

**Критерий закрытия:** нет фантомного DOGE-подобного остатка; каждая защитная нога имеет конечный статус; сумма применённых fills согласована с биржей; net outcome содержит fees и связан с исходным entry.

---

## 4. P0.3a: карантин должен изолировать символ, а не раскачивать весь WS

**Статус после правки:** внесены шаги 1–4 (код). Шаг 1 — per-stream диагностика (счётчики kline/depth, возраст последнего payload, пустые payload и ошибки разбора, id/ack SUBSCRIBE-запросов) и событие `DATA-STREAM-DIAG` перед каждой эскалацией; причина карантина дополнена `silent-{kline|depth|both|none}`. Шаг 2 — live-подтверждение и досрочное снятие карантина требуют оба типа сообщений. Шаг 3 — backoff карантина/операторский алерт, single-flight+jitter общего реконнекта, сохранение здоровых пар без REST-перезагрузки. Шаг 4 — метрики recovery и итоговый `entries_allowed` в событиях подтверждения/снятия. Сборки/компиляции/тесты не выполнялись; runtime-подтверждение причин ENJ/DASH и приёмка нового поведения на Demo не выполнены.

- `[~]` Зафиксировать причины ENJ: наличие kline/depth messages, время последнего сообщения каждого типа, subscribe ids/ack, состояние парсера и принятых свечей. Код внесён (`MarketStreamDiagnostics`, `GetStreamDiagnostics`, `WS-SUBSCRIBE-SENT/ACK/ERROR`, `DATA-STREAM-DIAG`); фактические значения по ENJ/DASH появятся только в следующем Demo-прогоне.
- `[~]` Разделить freshness candle stream и order book; не объявлять весь символ живым только по одному типу сообщений, если вход требует оба. Код внесён: `MarketAnalyzer.NoteStreamMessageLocked` подтверждает live и снимает карантин только при kline И depth после начала ожидания/постановки карантина; `NoteOrderBook` участвует в подтверждении; `IsDataFresh` возвращает `awaiting-live-tick:{kline|book|both}`; стакан, полученный до нового REST-прогрева, не засчитывается. `IsDataFresh` и раньше требовал оба типа для входа; теперь и `DATA-LIVE-CONFIRMED`/quarantine-lift не выдаются по одному kline. Runtime-подтверждение на Demo не выполнено.
- `[~]` Проверить путь `quarantine expiry → REST reload → live confirmation`: снятие срока не равно разрешению входа; не запускать новый reconnect cascade без отдельного бюджета. Код: expiry-lift ставит `RecoveryRequired=quarantine-expired` → REST-прогрев → ожидание обоих стримов; входы запрещены на всём пути (регрессионный контракт дополнен). Новый цикл после expiry получает обычный бюджет resubscribe, но повторный карантин длится вдвое дольше (backoff ниже), а общий reconnect ограничен cooldown+jitter/single-flight. Runtime-проверка не выполнена.
- `[~]` Для persistently silent symbol — длительная изоляция/exponential backoff и операторский алерт. Не повторять одинаковые две resubscribe + reconnect каждые несколько минут без ограничения общего бюджета. Код внесён: `QuarantineStreak` растёт с каждым карантином без live-подтверждения обоих стримов; длительность = `MarketDataQuarantineSeconds` × 2^streak до `MarketDataQuarantineMaxSeconds` (новый параметр, по умолчанию 3600 с, валидация); при серии ≥3 — `DATA-QUARANTINE-ESCALATED` (Error) + метрика эскалаций. Серия сбрасывается только реальным live-подтверждением. Runtime-приёмка не выполнена.
- `[~]` Per-symbol resubscribe предпочтительнее reconnect общего сокета. Если reconnect необходим — cooldown/jitter/single-flight на весь процесс, сохранение прогресса здоровых пар, минимизация повторного REST reload всех символов. Код внесён: эскалация сначала per-symbol resubscribe (как раньше); принудительный reconnect защищён semaphore single-flight в `BinanceWebSocketClient` и cooldown+jitter в движке; на WS-UP `TryResumeAfterReconnect` сохраняет индикаторы пар с непрерывной закрытой сеткой свечей без REST-перезагрузки, но требует свежие kline И depth после реконнекта; при пропущенной закрытой свече/недогреве/карантине — прежний REST-путь. Runtime-приёмка не выполнена.
- `[ ]` Отдельно расследовать DASH: 95 reload/37 resubscribe не должны объясняться только шумом ENJ. Требует нового Demo-прогона с per-stream диагностикой.
- `[~]` В каждом DATA-LIVE-CONFIRMED и quarantine-lift писать окончательный `entries_allowed`, а не только промежуточный флаг fresh book. Код внесён: оба события получают фактический вердикт data-гейта (WS подключён + `IsDataFresh` обоих стримов) на момент записи; хардкод `entries_allowed_after_fresh_book=true`/`=false` убран. Runtime-подтверждение не выполнено.
- `[~]` Метрики: recovery cycles per symbol, reconnect causes, healthy-symbol collateral reloads, quarantined seconds, time-to-live, repeated failures after quarantine. Код внесён в `MetricsCollector`/METRICS-сводку: cycles/failed по символам, причины reconnect-запросов, kept/reloads при WS-UP, max streak/эскалации/фактические секунды карантина; time-to-live существовал ранее. Runtime-значения не собраны.

**Критерий:** ENJ может оставаться недоступным, но DOGE/XRP/BNB не теряют подтверждённую свежесть из-за его бесконечного recovery; нет повторяющегося одинакового цикла без эскалации оператору.

---

## 5. E1: журнал решений, fills и outcomes — следующий после P0.9/P0.3a

**Статус E1 после шага 1:** добавлены machine-readable контракты `DecisionLedgerRecord`/`FillLedgerRecord`/`OutcomeLedgerRecord`, durable JSONL-хранилище `JsonE1Ledger` с идемпотентностью по identity, fail-closed при повреждении строки/партиции, разделением source/environment/account и проверкой fee/outcome-арифметики; добавлены регрессионные контракты `E1LedgerTests` и описание `docs/E1-LEDGER.md`. Runtime-продюсеры decision/entry/protection/exit/outcome ещё не подключены; сборки/компиляции/тесты не выполнялись; Demo-приёмка E1 не выполнена.

Сохранить требования полной исторической редакции, но начать с минимально проверяемого сквозного пути:

```text
decision_id → entry_intent_id → entry_fill(s)
→ protection_identity → protective_fill(s)
→ position_close / dust_review → outcome_net_R
```

Обязательные поля:

```text
run_id, config_hash, build_hash, environment, account_fingerprint,
symbol, decision_id, candle_close_utc, gate_stage, reason_code,
client_order_id, exchange_order_id, list_id,
requested_qty, rounded_qty, cumulative_filled_qty,
avg_fill_price, fee, fee_asset, exchange_trade_id, exchange_time_utc,
entry_basis, initial_risk_quote, realized_gross/fees/slippage/net_R,
exit_reason, protection_resolution, dust_qty, unresolved_reason
```

Требования:

- `[~]` Один финальный outcome на entry/position lifecycle; повторная доставка события идемпотентна. Контракт/hранилище проверяют immutable identity и однократную финализацию `Unresolved`; runtime-продюсеры lifecycle ещё не подключены.
- `[~]` Сырые human logs не заменяют machine-readable ledger; repeats не используются как статистическая выборка. Добавлен отдельный JSONL ledger; runtime-запись решений/fills/outcomes ещё не подключена.
- `[~]` HOLD/NO_FILL не считаются сделками; отсутствие AI-вызова не считается AI-отказом. Контракты `DecisionOutcome`/`AiDisposition`/`OutcomeStatus.NoFill` и проверки хранилища добавлены; runtime-классификация ещё не подключена.
- `[~]` Demo, Paper, Shadow и Live имеют разный source; нет смешивания симулированных и биржевых fills. Партиции source/environment/account и evidence-проверки добавлены; runtime-источники ещё не подключены.
- `[~]` Комиссия учитывается один раз, в том числе fee в базовом активе/BNB; balance delta не заменяет fee ledger. Контракты fee delta/valuation/conversion и сумма outcome fees добавлены; runtime-получение myTrades/конвертация ещё не подключены.
- `[~]` Сохранять point-in-time признаки и версии gates для последующего walk-forward. Поля features/gate versions/data snapshot hash добавлены в контракты; runtime-снимок на момент сигнала ещё не подключён.

---

## 6. Остальная дорожная карта

| Этап | Содержание | Условие перехода |
|---|---|---|
| E0 | Demo infrastructure acceptance | P0.9/P0.3a закрыты; entry/exit/OCO/recon/risk/restart проверены |
| E2 | BucketTable + EmpiricalEdgeEstimator | E1 даёт достоверные net outcomes и покрытие бакетов |
| E3 | EdgeGate по нижней границе net expectancy | Не только win rate; фиксированные границы, embargo/walk-forward |
| E4 | Новости, события, stablecoin policy | Событийный запрет работает без LLM |
| E5 | Off/Shadow/VetoOnly/Explainer | Польза LLM измеряется avoided_R против false_veto_cost |
| E6 | Post-only fill probability, adverse selection, slippage | Реальные maker fills/cancels/partials, не мгновенная Paper-симуляция |
| E7 | Equity/risk/sizing/capital degradation | Реальные equity/deposits/fees; лимиты и хвостовой риск проверены |
| E8 | Надёжность, DLL, безопасность, поставка | Heartbeat/watchdog, orphan HTTP budget, 24 ч soak, версии/откат |
| E9 | Shadow → Demo edge → минимальный Live | Только после закрытого E0 и устойчивого положительного edge |

Не менять порядок ради демонстрации прибыли. Нулевая частота входов сама по себе не дефект: сначала выяснить, насколько gates обоснованы на достоверных outcomes. Отсутствие новых входов в этом прогоне не доказывает ни хорошую, ни плохую стратегию.

### Сохраняющиеся ограничения E8

- `[ ]` Жёсткие дедлайны внешнего I/O уже присутствовали в коде предыдущей редакции; их runtime fault-injection не выполнен в этом окне.
- `[ ]` Учёт abandoned HTTP requests, возраст oldest orphan, рецикл handler, ConnectTimeout/PooledConnectionLifetime.
- `[ ]` LOOP-HEARTBEAT/watchdog; отсутствие итераций не должно маскироваться живым polling-таймером.
- `[ ]` Soak ≥24 ч с непрерывностью решений, памятью/sockets/handles и сетевыми сбоями.
- `[ ]` Зафиксировать native DLL/model/build hashes; предупреждение Vulkan для RX590 не считать устранённым без проверки версии.
- `[ ]` Не публиковать полные балансы/account dumps; текущие логи содержат inventory Demo-активов. Перед передачей логов наружу применять masking.

---

## 7. Ближайший исполнимый порядок

1. **P0.9 runtime:** восстановить фактический DOGE exit через Demo order/trade history; зафиксировать orderId, fills, fees и причину расхождения. Не отправлять новые ордера в рамках документационного аудита.
2. **P0.9 acceptance:** проверить внесённый resolver и регрессию описанной гонки на Demo SL/TP/partial/restart; только затем считать P0.9 закрытым.
3. **P0.3a:** собрать per-stream диагностику ENJ/DASH; ограничить общий reconnect и исправить подтверждённую причину молчания. Шаги 1–4 (диагностика; раздельная свежесть kline/depth; backoff карантина/изоляция reconnect; метрики/итоговый entries_allowed) внесены в код; остались runtime-приёмка на Demo и расследование причин ENJ/DASH по новым полям.
4. **E1:** связать entry/protection/exit/outcome и проверить идемпотентность на restart/polling.
5. Повторить E0 и суточный soak; обновить README фактическими результатами, не обещая доходность.

## 8. Критерии готовности

```text
Нет фантомных позиций после биржевого SL/TP.
Исчезнувшая OCO всегда имеет конечный resolution или явный unresolved freeze.
Dust отделён от основной позиции и не вызывает повторную продажу проданного объёма.
Polling fallback восстанавливает terminal fills, а не только открытые ордера.
Карантин одного символа не вызывает бесконечный reconnect общего рынка.
REST reload, live candle/book freshness и entries_allowed различаются.
Equity/free/locked и external cash flows не создают ложный PnL/risk.
Каждый outcome связан с реальными entry/exit fills и комиссиями.
LLM не создаёт входы и не задерживает защиту.
EV_lower считается по net R; coverage и стабильность проверены.
Live запрещён до закрытия E0 и подтверждения edge.
Нет обещаний гарантированной прибыли или нулевого риска разорения.
```