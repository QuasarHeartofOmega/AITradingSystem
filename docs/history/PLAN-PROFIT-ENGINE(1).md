# PLAN-PROFIT-ENGINE — исправленный план превращения AITradingSystem в проверяемый торговый движок

**Дата редакции:** 2026-11-26  
**Статус:** активный план восстановлен в корне репозитория; P0.1–P0.6 код-изменения присутствуют в checkout, без сборок/тестовых прогонов; runtime-приёмка P0.1–P0.6 не выполнена; добавлены запланированные исправления P0.4a и P0.6a; следующий шаг — P0.4a, затем P0.6a, затем E1  
**Базовый документ:** `docs/history/PLAN-PROFIT-ENGINE.md` сохранён как история  
**Ограничение аудита:** без сборок, тестовых прогонов и новых ордеров  
**Главный вывод:** за 6 ч 47 м торговых решений получено 501 AI-предложение, 20 BUY, 0 отправленных ордеров, 0 филлов, 0 закрытых сделок. Прибыльность не доказана; сначала нужно починить учёт капитала, данные, журнал исходов и исполняемость.

---

## Ход выполнения в текущей сессии

- **Шаг 1 — восстановление плана: файлы изменены.** Из коммита `6825706fde16514ffe49bfb500005ef9b49f8f66` восстановлены `docs/history/PLAN-PROFIT-ENGINE.md` (историческая база) и `PLAN-PROFIT-ENGINE.md` (активная рабочая копия). В текущем `main` до восстановления отсутствовали оба файла; исходный текст сохранён без изменений в истории. Затем обновлён статус активной копии и добавлена эта запись.
- Сборки, компиляции и тестовые прогоны не выполнялись. P0.6 и последующие пункты ещё не начаты в этой сессии.
- **Шаг 2 — P0.6: файлы изменены.** Введены явные классы символов и единая политика исключений; stablecoin-пары и benchmark-пары исключены из трендовых входов, сохранены для наблюдения/контекста. Runtime проверяет доступность и пишет причины исключений вместе с active universe и config hash. Добавлена защита от входа против направления движка. Тесты добавлены, но не запускались; сборки/компиляции не выполнялись. Затем обновлён этот план.
- **Шаг 3 — план обновлён по команде пользователя.** Добавлены разделы P0.4a (реконсиляция не отменяет активный post-only entry) и P0.6a (PromptBuilder/AI-контекст согласованы с ролью LLM и вселенной символов). Код в этом шаге не изменялся; сборки/тесты не выполнялись.
- После каждого следующего шага: сначала изменения файлов, затем актуализация этого плана; продолжение — только по команде пользователя.

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
| Market-data fail-closed | `[~]` | 71 отказ, но recovery loops | код P0.3 внесён (history_reloaded ≠ stream_live, resubscribe/reconnect/карантин); runtime-приёмка не выполнена |
| Order submit / fill | `[ ]` | 0 наблюдаемых | Demo smoke-test post-only и market |
| OCO SL/TP | `[ ]` | нет постановки/срабатывания | Demo smoke-test обеих ног |
| Independent protection monitor | `[~]` | MONITOR-START есть | тест во время занятого LLM/паузы/сети |
| Idempotency | `[ ]` | нет повторной отправки | тест duplicate clientOrderId |
| Risk-state persistence | `[ ]` | нет RISK-STATE-RESTORED | тест рестарта после убытка/просадки |
| User-data stream | `[~]` | 410 POST userDataStream | код P0.4 внесён (USER-DATA-PROBE, POLLING-FALLBACK, fail-closed); поддержка Demo не проверена runtime |
| Reconciliation | `[~]` | нет RECON-SUMMARY | код P0.4 внесён (RECON-START/SUMMARY/FAIL/ADOPT/CANCEL, unresolved → freeze); orphan order/OCO/position tests не выполнены |
| 429 / -1003 | `[ ]` | не наблюдались | контролируемый weight-budget test |
| Tick/step/minNotional | `[ ]` | до filters не дошло | тесты на BTTC/STRAX/FLR и dust |
| Portfolio/symbol/cash caps | `[ ]` | до sizing не дошло | логировать requested/capped quantity |
| PnL / fees / slippage | `[ ]` | нет закрытых сделок | журнал филлов и net R |

**E0 нельзя считать закрытым.** Сначала закрыть P0-дефекты ниже, затем повторить Demo-приёмку.

---

## 3. P0: исправить до любой статистики и Live

### P0.1. Разделить equity, free cash, locked и position notional

**Проблема:** `AvailableUSDT` используется как баланс для просадки, а затем из него вычитается капитал в позициях. При открытой позиции это может выглядеть как потеря денег.

**Статус после локальных правок:** код внесён; сборки/тесты не запускались; runtime-критерий ещё не подтверждён.

**Задачи:**

- `[~]` Ввести `AccountSnapshot`: `equity_quote`, `free_cash_quote`, `locked_cash_quote`, `position_notional_quote`, `unrealized_pnl_quote`, `account_id`, `environment`, `as_of_utc`.
  - Добавлен `AITradingSystem.Core/Models/Trading/AccountSnapshot.cs` и `AITradingSystem.Services/Trading/AccountSnapshotService.cs`.
  - Live/demo equity оценивает все положительные активы счёта; неполная оценка помечается `IsValuationComplete=false` и блокирует входы.
- `[~]` `DrawdownTracker` обновлять только от mark-to-market equity, не от свободного USDT.
  - Добавлен `UpdateEquity`; `TradingEngine` обновляет риск от `AccountSnapshot.EquityQuote`.
- `[~]` `GetFreeCash` брать из биржевого free/locked или из отдельного cash ledger, не вычитать notional из equity повторно.
  - `GetFreeCash()` теперь берёт `FreeCashQuote` из снимка; paper использует отдельный `_paperFreeCash` ledger.
- `[~]` Депозиты/выводы/трансферы корректировать baseline, а не считать торговым PnL.
  - Добавлены `DrawdownTracker.RegisterExternalCashFlow` и `TradingEngine.RegisterExternalCashFlowAsync`; автоматическое распознавание биржевых депозитов/выводов ещё не подключено.
- `[~]` Risk-state хранить по `(environment, account_id, quote_asset)`; не переносить пик Live в Demo и наоборот.
  - `JsonStateRepository` получил scoped save/load; legacy unscoped state не мигрируется автоматически в scoped.
  - `IBinanceRestClient.AccountId` / `BinanceRestClient.AccountId` даёт не-секретный SHA-256 fingerprint API-ключа; paper account id задаётся `TradingConfig.PaperTradingAccountId`.
- `[~]` Логировать `RISK-SNAPSHOT`: equity, free, locked, peak, day_start, drawdown, daily_pnl, source.
  - Добавлен `TradeLog.RiskSnapshot`; вызывается при обновлении снимка капитала.

**Изменённые файлы P0.1:**

- `AITradingSystem.Core/Models/Trading/AccountSnapshot.cs` — новый снимок капитала.
- `AITradingSystem.Services/Trading/AccountSnapshotService.cs` — построение live/demo и paper снимков.
- `AITradingSystem.Services/Trading/TradingEngine.cs` — equity/free-cash интеграция, scoped risk-state, paper cash ledger, `RISK-SNAPSHOT`.
- `AITradingSystem.Core/RiskManagement/DrawdownTracker.cs` — equity semantics, external cash flow baseline, scoped state fields.
- `AITradingSystem.Core/RiskManagement/RiskManager.cs` — `UpdateEquity` passthrough.
- `AITradingSystem.Data/Storage/IStateRepository.cs`, `AITradingSystem.Data/Storage/JsonStateRepository.cs` — scoped risk-state API/реализация.
- `AITradingSystem.Data/Binance/IBinanceRestClient.cs`, `AITradingSystem.Data/Binance/BinanceRestClient.cs` — `AccountId` fingerprint.
- `AITradingSystem.Common/Configuration/TradingConfig.cs` — `PaperTradingAccountId`.
- `AITradingSystem.Common/Logging/LogChannels.cs` — `TradeLog.RiskSnapshot`.

**Критерий:** открытие/закрытие позиции без изменения цены не создаёт ложную просадку; депозит не создаёт ложную прибыль.
**Осталось для закрытия P0.1:** runtime-проверка критерия, автоматический учёт депозитов/выводов/трансферов из биржевого источника, проверка scoped-миграции и неполной оценки активов.

### P0.2. Kill-switch: персист, причина, сброс

**Статус после локальных правок:** код внесён; сборки/тесты не запускались; runtime-критерий ещё не подтверждён.

**Задачи:**

- `[~]` Персистить `kill_switch_engaged`, `reason`, `engaged_at`, `equity_at_engage`, `requires_manual_reset`.
  - `DrawdownState` расширен полями kill-switch; `TradingEngine.PersistRiskStateAsync` сохраняет их в scoped risk-state.
- `[~]` При старте, если лимит всё ещё пробит, не разрешать входы до явного reset.
  - `StartAsync` восстанавливает engaged-состояние из scoped risk-state и логирует `KILL-SWITCH-RESTORED`.
  - `ResetKillSwitchAsync` запрещает сброс, если лимит риска всё ещё пробит или остались открытые позиции.
- `[~]` Логировать `KILL-SWITCH-ENGAGED`, `KILL-SWITCH-CLOSE-RESULT`, `KILL-SWITCH-RESET`, `KILL-SWITCH-RESET-DENIED`.
  - Добавлены методы `TradeLog`; engage/close/reset/denied/restored пишутся в trade-log.
- `[~]` Reset не должен обнулять peak/day_start автоматически.
  - `ResetKillSwitchAsync` меняет только kill-switch состояние и персистит risk-state; `DrawdownTracker.ResetDaily` не вызывается.
- `[~]` Если автозакрытие не удалось, статус `UNPROTECTED_OR_OPEN` и алерт, а не тихое продолжение.
  - После `CloseAllPositionsAsync` статус становится `UNPROTECTED_OR_OPEN` при остатке позиций и пишется critical alert.
  - UI-баннер показывает причину и статус закрытия.

**Изменённые файлы P0.2:**

- `AITradingSystem.Core/RiskManagement/DrawdownTracker.cs` — поля kill-switch в `DrawdownState`.
- `AITradingSystem.Common/Logging/LogChannels.cs` — аудит-события kill-switch.
- `AITradingSystem.Services/Trading/TradingEngine.cs` — persistence/restore/engage/close-result/guarded reset; snapshot-lock больше не удерживается во время аварийного закрытия.
- `AITradingSystem.UI/ViewModels/TradingViewModel.cs` — UI reset вызывает guarded async reset.
- `AITradingSystem.UI/ViewModels/MainViewModel.cs` — баннер показывает причину/статус kill-switch.

**Критерий:** рестарт не «забывает» аварию; оператор видит причину и остаточные позиции.
**Осталось для закрытия P0.2:** runtime-проверка рестарта после аварии, частичного/неудачного автозакрытия, denied reset и успешного reset без обнуления peak/day_start.

### P0.3. Market-data recovery: REST-прогрев ≠ живой WS

**Проблема:** `DATA-RECOVERED` повторяется 101 раз, но `stale-kline` продолжается для ENJ/STRAX/FDUSD/DASH.
Причина в коде: `TryReloadHistory` выставлял `LastKlineUtc = nowUtc`, то есть REST-прогрев сам себе подделывал
«свежий тик», снимал `RecoveryRequired` и писал «входы разрешены». Через `MarketDataMaxAgeSeconds` watchdog
снова видел `stale-kline` и заново запускал тот же REST-прогрев — цикл recovered → stale.

**Статус после локальных правок:** код внесён; сборки/тесты не запускались; runtime-критерий ещё не подтверждён.

**Задачи:**

- `[~]` Разделить `history_reloaded` и `stream_live`.
  - `MarketAnalyzer.SymbolState` получил `LastRestReloadUtc` (REST) отдельно от `LastKlineUtc` (только WS-тик).
  - `TryReloadHistory` больше не трогает `LastKlineUtc`; выставляет `LiveTickPending` и окно ожидания.
  - Единственный источник факта «стрим жив» — `NoteLiveTickLocked`, вызываемый из `NoteKline`/`UpdateIndicators`.
- `[~]` После REST recovery требовать свежий WS tick по символу в течение `N` секунд.
  - Новый `TradingConfig.MarketDataLiveConfirmSeconds` (по умолчанию 20, диапазон [5; 300]).
  - `IsDataFresh` возвращает `awaiting-live-tick`, пока живой тик не пришёл; вход запрещён.
- `[~]` Если WS молчит: resubscribe symbol/stream, затем reconnect, затем quarantine.
  - `IBinanceWebSocketClient.ResubscribeAsync` (UNSUBSCRIBE + SUBSCRIBE без разрыва сокета).
  - `IBinanceWebSocketClient.ReconnectMarketStreamsAsync` — реконнект только рыночного сокета, user-data не гасится.
  - `TradingEngine.EscalateSilentStreamAsync`: `MarketDataMaxResubscribeAttempts` (2) → принудительный реконнект
    с кулдауном `MarketDataReconnectCooldownSeconds` (60) → карантин `MarketDataQuarantineSeconds` (300).
  - Карантин снимается живым тиком или по истечении срока; после истечения символ идёт в REST-прогрев, не в торговлю.
- `[~]` Не писать `DATA-RECOVERED` как «входы разрешены», пока не подтверждён live tick и стакан.
  - Событие `DATA-RECOVERED` удалено из кода; вместо него `DATA-REST-RELOAD` (`entries_allowed=false`)
    и отдельное `DATA-LIVE-CONFIRMED` (`entries_allowed_after_fresh_book=true`).
- `[~]` Логировать `DATA-STALE`, `DATA-REST-RELOAD`, `DATA-WS-RESUBSCRIBE`, `DATA-LIVE-CONFIRMED`, `DATA-QUARANTINE`.
  - Добавлены `TradeLog.DataStale/DataRestReload/DataWsResubscribe/DataWsReconnect/DataLiveConfirmed/DataQuarantine/DataQuarantineLifted`.
- `[~]` Счётчики: stale_blocks, recovery_attempts, recovery_success, mean_time_to_live.
  - `MetricsCollector`: `MarketDataRecoveryAttempts`, `MarketDataLiveConfirmations`, `MarketDataMeanTimeToLiveSeconds`,
    `MarketDataMaxTimeToLiveSeconds`, `MarketDataResubscribes`, `MarketDataReconnectRequests`, `MarketDataQuarantines`,
    `MarketDataQuarantineLifts`; всё выведено в `GetSummary()`.

**Изменённые файлы P0.3:**

- `AITradingSystem.Services/Analysis/MarketAnalyzer.cs` — разделение history_reloaded/stream_live, окно live-подтверждения, счётчик resubscribe, карантин/снятие карантина, `awaiting-live-tick` в `IsDataFresh`.
- `AITradingSystem.Services/Trading/TradingEngine.cs` — `RecoverMarketDataAsync` переписан: снятие карантина → подтверждение live → watchdog → REST-прогрев (`ReloadHistoryAsync`) → эскалация (`EscalateSilentStreamAsync`); startup-прогрев тоже требует live-тик; freshness-гейт не гоняет REST по карантину/ожиданию тика.
- `AITradingSystem.Data/Binance/IBinanceWebSocketClient.cs`, `AITradingSystem.Data/Binance/BinanceWebSocketClient.cs` — `ResubscribeAsync`, `ReconnectMarketStreamsAsync`, send-lock и id управляющих фреймов.
- `AITradingSystem.Common/Configuration/TradingConfig.cs` — `MarketDataLiveConfirmSeconds`, `MarketDataMaxResubscribeAttempts`, `MarketDataQuarantineSeconds`, `MarketDataReconnectCooldownSeconds` + валидация диапазонов.
- `AITradingSystem.Common/Logging/LogChannels.cs` — аудит-события `DATA-*`.
- `AITradingSystem.Services/Monitoring/MetricsCollector.cs` — счётчики recovery/live/resubscribe/quarantine и mean/max time-to-live.
- `AITradingSystem.Tests/MarketDataIntegrityTests.cs` — контракты обновлены под «прогрев ≠ live», добавлены тесты окна ожидания, resubscribe, карантина, конфигурации и метрик (не запускались).

**Критерий:** нет бесконечного цикла recovered → stale; символ либо жив, либо явно карантинирован.
**Осталось для закрытия P0.3:** runtime-проверка на Demo: молчащий символ проходит resubscribe → reconnect → карантин и возвращается только после живого тика; `mean_time_to_live` и `stale_blocks` собираются; отсутствует рост `DATA-REST-RELOAD` без `DATA-LIVE-CONFIRMED`; проверить, что принудительный реконнект не рвёт user-data stream.

### P0.4. User-data stream и реконсиляция fail-closed

**Проблема:** `410 POST /userDataStream`; дальнейшая работа stream/recon не подтверждена.
В коде отказ поднятия stream глотался в `catch` с сообщением «остаётся polling», но самого polling-контура не было,
а входы продолжали разрешаться без какого-либо подтверждения состояния ордеров.

**Статус после локальных правок:** код внесён; сборки/тесты не запускались; runtime-критерий ещё не подтверждён.

**Задачи:**

- `[~]` Явно проверить поддержку user-data stream в Demo/Testnet/Live.
  - `ConnectUserDataAsync` логирует `USER-DATA-PROBE env=… supported=… http=… code=…` и при `BinanceApiException`
    помечает окружение как не поддержавшее listenKey; результат виден в `IsUserDataStreamSupported` / `UserDataStreamProbeCompleted`.
- `[~]` Если stream обязателен для режима, при недоступности запретить новые входы.
  - Новый `TradingConfig.UserDataStreamRequiredForEntries` (по умолчанию true).
  - `ReconciliationService.IsExecutionStateTrusted` + `ExecutionStateBlockReason`; чистая политика `EvaluateExecutionTrust`.
  - `TradingEngine.ProcessSymbolAsync` гейт `execution-state` до AI и повторный `pre-exec` гейт перед отправкой ордера.
- `[~]` Если разрешён fallback polling, задать интервал и логировать `EXECUTION-MODE=POLLING-FALLBACK`.
  - `UserDataPollingFallbackEnabled` (true), `UserDataPollingIntervalSeconds` (20, [5; 300]), `ExecutionStateMaxStalenessSeconds` (90, [15; 900]).
  - `PollingLoopAsync` вызывает реконсиляцию по таймеру; при восстановлении сокета режим возвращается в `STREAM`.
  - Режимы: `STREAM | POLLING-FALLBACK | UNAVAILABLE | PAPER | DISABLED | STOPPED`, каждый пишется как `EXECUTION-MODE=…`.
- `[~]` Реконсиляция должна писать `RECON-START`, `RECON-SUMMARY`, `RECON-FAIL`, `RECON-ADOPT`, `RECON-CANCEL`, `PROTECTION-FILL-APPLIED`.
  - Добавлены методы `TradeLog.ReconStart/ReconSummary/ReconFail/ReconAdopt/ReconCancel/ProtectionFillApplied`;
    `RECON-SUMMARY` содержит `unresolved` и `entries_allowed`, вызовы принимают `trigger` (startup/polling-fallback/listenKey-recreated/manual).
- `[~]` При неизвестном состоянии ордера/позиции — freeze entries, не угадывать.
  - Счётчик `_unresolvedStateIssues` растёт при RECON-FAIL и при отказе adopt/close/cancel; успешной реконсиляция
    считается только при `unresolved == 0`, иначе `LastSuccessfulReconcileUtc` не обновляется.
  - Просроченная сверка блокирует входы даже при формально живом сокете; аккаунт без ни одной успешной сверки не торгует.

**Изменённые файлы P0.4:**

- `AITradingSystem.Services/Trading/ReconciliationService.cs` — probe окружения, режимы исполнения, polling-контур, trust-политика, unresolved-счётчик, журнал `RECON-*`, остановка polling-цикла.
- `AITradingSystem.Services/Trading/TradingEngine.cs` — гейты `execution-state` до AI и перед отправкой ордера.
- `AITradingSystem.Common/Configuration/TradingConfig.cs` — `UserDataStreamRequiredForEntries`, `UserDataPollingFallbackEnabled`, `UserDataPollingIntervalSeconds`, `ExecutionStateMaxStalenessSeconds` + валидация.
- `AITradingSystem.Common/Logging/LogChannels.cs` — `USER-DATA-PROBE`, `EXECUTION-MODE`, `RECON-*`, `PROTECTION-FILL-APPLIED`, `EXECUTION-STATE-BLOCKED`.
- `AITradingSystem.Services/Monitoring/MetricsCollector.cs` — probes/failures, polling reconciles, recon failures, execution-state blocks (+ summary).
- `AITradingSystem.Tests/ExecutionStateTrustTests.cs` — новый файл: политика доверия (stream down без polling, устаревшая сверка, отсутствие сверки, unresolved, paper/keyless), валидация конфига, метрики (не запускались).

**Критерий:** бот не считает ордер исполненным только потому, что REST-запрос был отправлен.
**Осталось для закрытия P0.4:** runtime-проверка на Demo: поддерживает ли окружение listenKey (410), реально ли идёт polling-fallback и обновляет `LastSuccessfulReconcileUtc`, блокируются ли входы при `UNAVAILABLE`/протухшей сверке, применяются ли филлы защитных ног из `executionReport` и из REST без повторного MARKET.

### P0.4a. Реконсиляция не отменяет активный post-only entry

**Статус:** запланировано; код ещё не изменён. Основание — runtime-лог DASHUSDT: `LIMIT_MAKER` принят как `New` с `filled=0`, затем через ~16 с снят как `RECON-CANCEL kind=order`, хотя `OrderFillTimeoutSeconds=60`. Это гонка polling-реконсиляции и ещё живого entry-intent, а не штатный таймаут филла.

**Проблема:** `ReconciliationService` считает известными только ордера открытой позиции/защиты. Активный entry без позиции и без филла ошибочно классифицируется как сиротский и отменяется до истечения окна входа. Это искажает `fill_probability`, создаёт ложные `NO_FILL` и маскируется под `unresolved=0 entries_allowed=true`.

**Задачи:**

- `[ ]` Реконсиляция должна видеть активные entry-intents текущего процесса: `clientOrderId`, символ, операция `Entry`, статус отправки, время создания/обновления, дедлайн `OrderFillTimeoutSeconds + grace`.
- `[ ]` Не отменять entry-ордер, если intent активен, принадлежит текущему владельцу и дедлайн ещё не истёк; логировать `RECON-SKIP-ACTIVE-ENTRY` с причиной и остатком окна.
- `[ ]` В `RECON-SUMMARY` добавить `active_entries` и `skipped_active_entry_cancels`; такие ордера не учитываются в `orphan_orders` и не должны давать ложный `entries_allowed=true` после преждевременной отмены.
- `[ ]` Отмена entry разрешена только после истечения дедлайна, потери владельца/процесса, терминального статуса или явно unresolved-состояния; причину писать в `RECON-CANCEL`/`RECON-EXPIRE-ENTRY`.
- `[ ]` Частичный филл активного entry: зафиксировать исполненный объём и необходимость защиты; остаток отменять только по lifecycle входа/дедлайну, не как сироту.
- `[ ]` Добавить настройку grace, например `ReconciliationEntryGraceSeconds`, с валидацией диапазона; значение по умолчанию консервативное.
- `[ ]` Метрики: преждевременные отмены предотвращены, активные entry пропущены реконсиляцией, entry отменён по дедлайну/сиротству.
- `[ ]` Тесты без прогонов: активный zero-fill entry не отменяется; истёкший entry отменяется; partial-fill не считается сиротой; внешний/неботовский ордер не трогается; summary содержит active_entries.

**Изменённые файлы P0.4a:** пока нет; список будет заполнен сразу после правок.

**Критерий:** polling-реконсиляция не убивает живой post-only вход раньше его дедлайна; сиротская отмена отличается от штатного истечения окна входа.
**Осталось для закрытия P0.4a:** runtime-проверка сценария DASHUSDT: `New/filled=0` переживает polling до `OrderFillTimeoutSeconds + grace`; `RECON-CANCEL` появляется только по истечении/сиротству; `active_entries` виден в summary; fill/NO_FILL статистика не искажается преждевременной отменой.

### P0.5. LLM вне последовательного критического пути

**Проблема:** 14B на RX590 с частичным оффлоадом даёт ~47 с на решение и почти полностью занимает цикл.

**Статус после локальных правок:** код внесён; сборки/тесты не запускались; runtime-критерий ещё не подтверждён.

**Задачи:**

- `[~]` Добавить `AI.Enabled` и `AI.Role = Off | Shadow | VetoOnly | Explainer`.
  - `AIConfig.Enabled` (true), `AIConfig.Role` (по умолчанию "Shadow"), enum `AiRole`, `ResolvedRole` (Enabled=false → Off), валидация Role и `VetoBudgetSeconds` [1; 60].
- `[~]` Для baseline допустить `Role=Off` или `Shadow`; вход только из engine setup + EdgeGate.
  - `SignalCalibrator.BuildEngineSignal`: детерминированный сигнал из PreScore (направление/prior), цены entry/SL/TP — та же математика, что ENGINE-ENTRY; `ConfidenceSource=EngineOnly`, `ModelConfidence=0`.
  - `TradingEngine.ProcessSymbolAsync` больше не вызывает LLM на входе; EdgeGate — E3, ещё не реализован.
- `[~]` Deterministic path не ждёт LLM.
  - Вход считается до любого обращения к модели; veto-вердикт берётся из кэша, отсутствующий вердикт в VetoOnly блокирует вход без ожидания (fail-closed).
  - `ReviewOpenPositionAsync` стал fire-and-forget: один in-flight на символ, бюджет `VetoBudgetSeconds`, таймаут логируется как `LLM-REVIEW-TIMEOUT`; защита остаётся в PositionMonitor.
- `[~]` Veto вызывается асинхронно на закрытой свече, кэш на `(symbol, candle_close, config_hash)`.
  - Новый `LlmVetoService`: `RequestOpinion` (fire-and-forget, один in-flight на символ), кэш вердиктов (лимит 512, вытеснение старых), ключ `(symbol, candle_close_utc, config_hash)`; `candle_close_utc = OpenTime` текущей свечи.
  - `TradingEngine.ComputeConfigHash`: SHA-256 сериализованного AppConfig, первые 16 hex; логируется при старте.
- `[~]` Жёсткий бюджет, например 5–15 с; опоздал → veto не применён к уже принятому решению, но событие логируется.
  - `AI.VetoBudgetSeconds` (по умолчанию 10); linked CTS с CancelAfter; опоздавший вердикт → `LLM-VETO-LATE`, вход по этой свече не разрешается (fail-closed), постфактум решение не меняется.
- `[~]` Для VetoOnly отключить thinking или использовать меньшую модель; 14B explainer — отдельно.
  - `AiRequestContext.DisableThinking`; `QwenModel.EffectiveThinking` — per-request отключение thinking (промпт, repair, сэмплеры, think-бюджет); veto/shadow-запросы всегда с `DisableThinking=true`. Отдельная модель для explainer не подключена.
- `[~]` Timeout/parse fail/degenerate не разрешают вход и не блокируют защиту.
  - `LlmVetoService.EvaluateVeto`: inference failure/degenerate/HOLD/CLOSE/противоположное направление → veto; timeout/исключение → fail-closed veto. Сервис не создаёт ордеров и не закрывает позиций; SL/TP/трейлинг в PositionMonitor не зависят от LLM.
- `[~]` Метрики: `llm_latency`, `veto_coverage`, `unique_veto`, `avoided_R`, `false_veto_cost`.
  - `MetricsCollector`: LlmOpinionRequests/Results, LlmVetoes, LlmVetoesApplied, LlmOpinionsLate/Timeouts/Failures, LlmVetoPendingBlocks, LlmReviewCloseRequests + `GetSummary()`; latency через существующий `RecordInferenceTime`.
  - `avoided_R`/`false_veto_cost` требуют журнала исходов (E1) — не реализованы, отложены до OutcomeLedger.
  - События журнала: `LLM-ROLE`, `LLM-VETO-RESULT`, `LLM-VETO-APPLIED`, `LLM-VETO-LATE`, `LLM-VETO-PENDING` (`TradeLog`).

**Изменённые файлы P0.5:**

- `AITradingSystem.Common/Configuration/AIConfig.cs` — `Enabled`, `Role`, `VetoBudgetSeconds`, enum `AiRole`, `ResolvedRole`, валидация.
- `AITradingSystem.Services/AI/AiRequestContext.cs` — `DisableThinking` (per-request).
- `AITradingSystem.Services/AI/QwenModel.cs` — `EffectiveThinking`: per-request отключение thinking под `_inferenceLock`.
- `AITradingSystem.Services/Analysis/SignalCalibrator.cs` — `BuildEngineSignal`: детерминированный вход без модели.
- `AITradingSystem.Services/Trading/LlmVetoService.cs` — новый: асинхронный veto/shadow, кэш, бюджет, чистая политика `EvaluateVeto`.
- `AITradingSystem.Services/Trading/TradingEngine.cs` — вход без ожидания LLM, роль-гейты (Off/Shadow/VetoOnly/Explainer), config hash, асинхронный пересмотр позиции, `LLM-ROLE` при старте, условная загрузка модели (Off → не загружается).
- `AITradingSystem.Services/Monitoring/MetricsCollector.cs` — счётчики LLM-мнений/veto (+ summary).
- `AITradingSystem.Common/Logging/LogChannels.cs` — `LLM-ROLE`, `LLM-VETO-*`.
- `AITradingSystem.UI/App.xaml.cs` — DI-регистрация `LlmVetoService`.
- `AITradingSystem.UI/appsettings.json` — `AI.Enabled=true`, `AI.Role="Shadow"`, `AI.VetoBudgetSeconds=10`.
- `AITradingSystem.Tests/LlmRoleTests.cs` — новый файл: парсинг/валидация роли, политика veto, детерминированный сигнал (не запускались).

**Критерий:** выключение LLM не ломает торговый контур; польза LLM измеряется в R.
**Осталось для закрытия P0.5:** runtime-проверка: Role=Off торгует без модели; VetoOnly блокирует вход без вердикта и применяет готовый veto; опоздавшие вердикты только логируются; пересмотр позиции не занимает цикл; `avoided_R`/`false_veto_cost` после E1; отдельная лёгкая модель/explainer-контур.

### P0.6. Символы и стабильные пары

**Статус после правок текущего шага:** код и конфигурация внесены; сборки/тесты не запускались; runtime-критерий ещё не подтверждён.

**Принятые границы:** `USDCUSDT`/`FDUSDUSDT` остаются в реестре наблюдения, но исключены из входов до отдельной depeg/liquidity стратегии. `BTCUSDT`/`ETHUSDT` — benchmark/корреляционный контекст, не торговые кандидаты. `FLRUSDT` остаётся volatile-кандидатом и проходит проверку доступности окружения; его доступность не предполагается. Лимиты исключённых пар не перераспределяются автоматически.

**Задачи:**

- `[~]` Явно разделить `volatile_symbols`, `stablecoin_pairs`, `benchmark_symbols`.
  - В `symbols.json` и fallback `appsettings.json` заданы `VolatileSymbols`, `StablecoinPairs`, `BenchmarkSymbols`; версия реестра повышена до `1.3`.
  - `SymbolUniversePolicy` нормализует списки, запрещает пересечение классов и требует класс символа при непустом явном volatile-списке. Пустой volatile-список сохраняет legacy fallback; поставляемая конфигурация использует явный список.
  - `ConfigLoader` передаёт только volatile-кандидаты в `Trading.Symbols`; причины исключений сохраняет в `Trading.SymbolExclusions`. Реестр/UI сохраняют классы при загрузке и сохранении настроек.
- `[~]` `USDCUSDT`/`FDUSDUSDT` не анализировать как обычный трендовый актив; либо исключить, либо отдельная depeg/liquidity strategy.
  - Выбрано исключение из active universe с причиной `stablecoin_no_depeg_strategy`; отдельная стратегия не реализовывалась. Пары остаются в `WatchAssets` и реестре.
- `[~]` `FLRUSDT` и другие unavailable symbols логировать с причиной и не включать в active universe.
  - Runtime-валидация различает `exchange_not_trading`, `exchange_info_missing`, `spot_trading_disabled`, `exchange_status_not_trading`, `exchange_info_error:<type>`; отключённые в реестре пары получают `registry_disabled`.
  - Исключённые пары не запрашивают фильтры и не попадают в `_activeSymbols`; причины доступны через `TradingEngine.ExcludedSymbols`. Если кандидатов после исключений нет, старт завершается ошибкой.
- `[~]` Запретить BUY, если engine direction = SHORT и нет отдельного reversal setup.
  - `SignalCalibrator.IsEntryDirectionAllowed` требует LONG для BUY и SHORT для SELL; FLAT не разрешает вход. Проверка стоит в `TryUpgrade` и повторно в `TradingEngine` перед публикацией сигнала/LLM/исполнением. Отказ: `symbol-policy`, `direction_mismatch`. Отдельный reversal setup не добавлен.
- `[~]` В начале сессии логировать effective config hash и active/excluded symbols.
  - Хэш эффективного конфига вычисляется до валидации символов и торговых действий; классы и причины исключений входят в конфигурацию.
  - `TradeLog.SymbolUniverse` пишет `SYMBOL-UNIVERSE env=… config_hash=… active=… excluded=SYM:reason,… volatile=… stablecoin_pairs=… benchmark_symbols=…` на каждом старте.

**Изменённые файлы P0.6:**

- `AITradingSystem.Common/Configuration/SymbolUniversePolicy.cs` — новый: классы, нормализация, пересечения, причины исключений.
- `AITradingSystem.Common/Configuration/TradingConfig.cs` — списки классов, карта исключений, нормализация/валидация.
- `AITradingSystem.Common/Models/Config/SymbolsRegistry.cs` — классы в реестре и проверка принадлежности каждого символа.
- `AITradingSystem.Common/Configuration/ConfigLoader.cs` — фильтрация торговых кандидатов и сохранение причин исключений.
- `AITradingSystem.UI/symbols.json`, `AITradingSystem.UI/appsettings.json` — явная вселенная и пояснения неторговых классов.
- `AITradingSystem.UI/ViewModels/SettingsViewModel.cs` — сохранение классов/исключений и согласованный fallback-список.
- `AITradingSystem.Services/Trading/TradingEngine.cs` — runtime-политика, причины недоступности, стартовый журнал вселенной, повторный direction-гейт.
- `AITradingSystem.Services/Analysis/SignalCalibrator.cs` — запрет противоположного направления при генерации/пересмотре сигнала.
- `AITradingSystem.Common/Logging/LogChannels.cs` — `SYMBOL-UNIVERSE`.
- `AITradingSystem.Tests/SymbolUniverseTests.cs` — новый: классы/исключения/валидация/направление; не запускался.
- `PLAN-PROFIT-ENGINE.md` — статус и ход выполнения обновлены после изменения файлов.

**Критерий:** список анализируемых символов совпадает с торговым смыслом стратегии.
**Осталось для закрытия P0.6:** runtime-проверка `SYMBOL-UNIVERSE`: ожидаемые восемь volatile-кандидатов с учётом доступности окружения; отсутствие stable/benchmark/unavailable пар в анализе и ордерах; причины исключений и config hash в стартовом логе; проверка сохранения классов через UI; отказ `direction_mismatch` на противоположном направлении. Depeg-стратегия и reversal setup остаются отдельными будущими задачами.

### P0.6a. PromptBuilder и AI-контекст согласованы с ролью LLM и вселенной символов

**Статус:** запланировано; код ещё не изменён. Основание — после P0.5/P0.6 `PromptBuilder` всё ещё строит «торговый» промпт: предлагает BUY/SELL, цены входа и старую confidence-логику, тогда как детерминированный движок уже принимает решение, а LLM должна только запрещать/объяснять. Это рассинхрон контракта, не дыра в ордерах: ордера защищает engine-gate.

**Задачи:**

- `[ ]` `AiRequestContext` передаёт `AI.Role`, класс символа (`volatile/stablecoin/benchmark`), причину исключения, направление движка и флаг «отдельного reversal setup нет».
- `[ ]` Prompt не строится для excluded/unavailable символов; для stable/benchmark допустим только отдельный контекстный/наблюдательный запрос, если он вообще нужен, без торговых действий.
- `[ ]` `Role=Off`: промпт не строится и модель не вызывается.
- `[ ]` `Role=Shadow`: промпт наблюдательный; ответ не разрешает вход и не смешивается с engine confidence как источник ордера.
- `[ ]` `Role=VetoOnly`: промпт требует только veto-контракт (`veto`, `veto_reason`, `event_type`, `risk_flags`, `invalidation`); BUY/SELL/entry/SL/TP не являются разрешением на ордер; противоположное направление движка = veto; отсутствие/просрочка вердикта блокирует вход.
- `[ ]` `Role=Explainer`: объяснение после принятого решения, без права открыть/закрыть позицию.
- `[ ]` Убрать из veto/shadow-промпта инструкции «execution threshold», генерацию цен и blending confidence как основание входа; новости помечать как недоверенный текст, не инструкции.
- `[ ]` Repair-prompt и parser align с тем же role-контрактом; битый JSON/degenerate/timeout не разрешают вход.
- `[ ]` Тесты без прогонов: Off не строит промпт; VetoOnly содержит veto-схему и не содержит торговых цен; Shadow не даёт ордерного разрешения; excluded symbol не получает торговый промпт; opposite direction даёт veto.

**Изменённые файлы P0.6a:** пока нет; список будет заполнен сразу после правок.

**Критерий:** промпт соответствует роли LLM: модель не может создать вход, а только запретить/наблюдать/объяснить; excluded символы не получают торговый промпт.
**Осталось для закрытия P0.6a:** runtime-проверка логов промптов по ролям; VetoOnly блокирует без вердикта; Shadow не влияет на вход; Explainer не создаёт ордеров; excluded/unavailable символы не попадают в LLM.

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
