# План доработок AITradingSystem — трекер выполнения

Источник: план пользователя (P0 / P1 / P2), скопирован дословно; добавлены статусы, ссылки на файлы и критерии приёмки.
Аудит выполнен: **24.11.2026**, по коду из `AITradingSystem.zip` (репозиторий `QuasarHeartofOmega/AITradingSystem`).

## Легенда

| Метка | Значение |
|---|---|
| ✅ | Выполнено — обязательно перечислены **изменённые файлы** |
| 🟡 | Частично — есть фундамент, но критерий пункта не выполнен |
| ⬜ | Не сделано — указаны файлы, которые предстоит менять |

**Правило ведения файла:** пункт переводится в ✅ только вместе со списком изменённых файлов (и, при наличии, коммитом). Пока кода нет — статус не меняется. Промежуточное состояние фиксируется как 🟡 с описанием, чего именно не хватает.

## Сводка на 24.11.2026 (после прохода 23 — P0 закрыт 7/7, P1 закрыт 8/8; выполнен P1.7; остался 1 шаг очереди — P2.6)

| Блок | Готово | Частично | Не сделано |
|---|---|---|---|
| P0 — защита капитала | 7 / 7 | 0 | 0 |
| P1 — деньги и данные | 8 / 8 | 0 | 0 |
| P2 — сигнал и HOLD | 5 / 6 | 0 | 1 |

> **Проход 1 (аудит):** код не менялся, выполнен P2.1 — диагностика по логам (артефакт: [`docs/signal-funnel-20261124.md`](docs/signal-funnel-20261124.md)).
> **Проход 2:** выполнен **P0.1 — биржевые стоп-ордера OCO**.
> **Проход 3:** выполнен **P0.2 — независимый контур мониторинга на тик WS**.
> **Проход 4:** по команде заказчика сделан **фундамент P0.3 — очередь исполнения с приоритетами и идемпотентность по `clientOrderId`**; пункт был переведён в 🟡.
> **Проход 5:** **P0.3 доведён до ✅** — durable-журнал намерений (идемпотентность переживает рестарт), детерминированные `clientOrderId`, ретрай операции с backoff/jitter и лимитом попыток, метрики очереди в дашборде. Остаток, не входящий в P0.3, явно передан P0.5 / P1.6 / P0.6.
> **Проход 7 (ранее не записанный):** обнаружен и зафиксирован фундамент **P0.5–P0.7**: `ReconciliationService`, `ExecutionReport`, listenKey/user-data WS, авто/ручной kill-switch, двухшаговый Live и отдельные Live-ключи в конфиге. UI/DI были не доведены.
> **Проход 8:** фундамент учтён в трекере; доведены DI-регистрация `ReconciliationService`, ключи `appsettings.json`, UI подтверждения Live/отдельных Live-ключей, несмываемый баннер REAL MONEY, сброс kill-switch и панель реконсиляции в дашборде. **P0.6 и P0.7 закрыты**.
> **Проход 9:** проверка завершённости **P0.5** по коду — все четыре пункта остатка (применение филлов без повторного MARKET, подхват сирот, пересоздание listenKey + реконсиляция, частичные филлы из stream) реализованы в дереве, но не были записаны в трекер. **P0.5 → ✅**, блок **P0 закрыт полностью (7/7)**. Код в этом проходе не менялся — только SECUPD.md. Следующий шаг — **P1.6** (бюджет весов), либо по порядку работ — фикс `volume_ratio` → P2.2.
> **Проход 10:** по команде заказчика оставшиеся работы упорядочены от лёгких/мелких к тяжёлым/большим с сохранением зависимостей. Код не менялся; изменён только `SECUPD.md`. Первый шаг очереди — фикс `volume_ratio`, последний — P2.6.
 > **Проход 23 (этот):** выполнен **шаг №13 очереди — P1.7: портфельный риск**. Новый `PortfolioRiskManager`: кап суммарной экспозиции, кап коррелированного BTC-кластера по Pearson на закрытых свечах, дневной лимит убытка на символ, пауза после серии убытков; состояние восстанавливается из полной истории сделок. Гейты стоят до AI и перед отправкой ордера, объём может урезаться портфельным капом. Метрики и дашборд-панель добавлены. Следующий шаг — **№14, P2.6: валидация весов скоринга**.
 > **Проход 22:** выполнен **шаг №12 очереди — P2.4: порог MinConfidence из данных**. Уверенность входа и начальный риск теперь доживают до истории сделок (`Position` → `TradeRecord`, включая закрытия биржевой ногой после рестарта). Новый чистый `ConfidenceExpectancyAnalyzer` считает net expectancy после комиссий в R-мультипликаторах по сетке порогов и выбирает максимум при достаточной выборке; контур `ConfidenceThresholdTuner` разбирает полную историю (`GetAllTradesAsync`), логирует `CONFIDENCE-TUNE`, пишет метрики и по умолчанию ТОЛЬКО рекомендует — авто-применение выключено и ограничено коридором [0.45; 0.80]. Добавлен оффлайн-инструмент `tools/confidence_threshold.py` с теми же правилами. Следующий шаг — **№13, P1.7: портфельный риск**.
 > **Проход 21:** выполнен **шаг №11 очереди — P1.4: целостность данных после реконнекта WS**. Рыночный WS отдаёт события потери/восстановления соединения; после реконнекта входы блокируются, стакан сбрасывается, свечи дозагружаются через REST с проверкой непрерывности и повторным прогревом индикаторов. Разрыв закрытой свечи в WS не маскируется, а отправляет символ на REST-восстановление. Watchdog протухания kline/стакана и fail-closed гейт перед AI и перед отправкой ордера запрещают входы на непригодных данных. Сопровождение открытых позиций и биржевые OCO не блокируются. Следующий шаг — **№12, P2.4: порог MinConfidence из данных**.
 > **Проход 20:** выполнен **шаг №10 очереди — P1.3: dust и аудит остатка после отмены**. Новый чистый `DustPolicy`: маршрут закрытия выбирается по фильтрам (IOC/MARKET/dust-MARKET-SELL/отказ), dust-остаток ниже stepSize/minQty/minNotional закрывается MARKET SELL точного объёма (full-balance exemption) вместо вечного зависания; аудит «отменённый остаток vs фактический филл» после отмены неисполненной части (метрика расхождений); «хвост» защиты (OCO < объёма позиции после округления) логируется и считается в метриках. Следующий шаг — **№11, P1.4: целостность данных после реконнекта WS**.
 > **Проход 17:** выполнен **шаг №7 очереди — P1.6: 429/-1003 и бюджет весов**. Новый `RequestWeightBudget`: учёт `X-MBX-USED-WEIGHT-1M` + локальные оценки веса между ответами; soft-limit (80% лимита) притормаживает только polling/данные — постановка/снятие ордеров и OCO проходит без задержки; явная ветка 429/-1003/418 регистрирует бан (Retry-After) для ВСЕХ запросов. Метрики, дашборд-панель (вместе с отложенными из P1.5 счётчиками ликвидности), конфигурация. Следующий шаг — **№8, P1.2: комиссии в R/R и безубытке**.
 > **Проход 18:** выполнен **шаг №8 очереди — P1.2: комиссии в R/R и безубытке + ограничение проскальзывания на market-выходе**. Net R/R и TP считаются после комиссий обеих ног; сайзинг использует net риск; безубыток переносит стоп после 1R net прибыли; обычный выход идёт marketable LIMIT IOC с cap, аварийный может обойти cap; история/метрики используют NetPnL. Следующий шаг — **№9, P1.1: slippage-модель в paper**.
 > **Проход 19:** выполнен **шаг №9 очереди — P1.1: slippage-модель в paper-режиме**. Paper taker-вход/выход симулируется проходом по стакану (`OrderBook.SimulateMarketFill`), с частичным филлом, средней ценой, проскальзыванием и IOC-cap обычного выхода; комиссии обеих ног сохраняются. Следующий шаг — **№10, P1.3: dust и аудит остатка после отмены**.
 > **Проход 16:** выполнен **шаг №6 очереди — P1.5: фильтр ликвидности**. Вход теперь блокируется по спреду / глубине стороны входа / среднему объёму свечей (чистый гейт `LiquidityFilter`, пороги глобальные + точечные из `symbols.json` для BTTC/STRAX/FLR); post-only-отказы агрегируются в метрику.
 > **Проход 15:** выполнен **шаг №5 очереди — P2.5: расширить детект «дегенерата»**. Пересмотр HOLD→вход больше не ограничен окном 0.45±0.02: любой HOLD с сильным согласованным prior (prior ≥ MinConfidence, направление не-флэт, short только при AllowShort) в политике Blend уходит в prior (`source=Hold→Prior`) и пересматривается на вход по детерминированным ценам. Guards сохранены: нет позиции, modelConf>0, отказ инференса → HOLD, ModelOnly не пересматривает.
> **Проход 14:** выполнен **шаг №4 очереди — P2.3: политика уверенности 0.7/0.3**. Blend больше не 50/50: `final = w·model + (1−w)·prior`, `w = AI:BlendModelWeight` (по умолчанию 0.7 — независимая оценка модели после P2.2 весит больше prior). Дегенерат HOLD≈0.45, отказ инференса и ModelOnly/EngineOverride не тронуты; порог `MinConfidence` не менялся (P2.4). Следующий шаг — **№5, P2.5: расширить детект «дегенерата»**.
> **Проход 13:** выполнен **шаг №3 очереди — P2.2: убрать двойной штраф в промпте**. Рубрика модели стала независимой: не стартует с `engine_prior_confidence` и не вычитает повторно за ADX/объём/BB/волатильность; в промпт идут нейтральные факты `EngineScore.Facts`, а аудиторские `Reasons` со штрафами остаются для лога. Следующий шаг — **№4, P2.3: политика уверенности 0.7/0.3**.
> **Проход 12:** выполнен **шаг №2 очереди — P1.8: мониторинг дрейфа часов**. Добавлен независимый периодический контур `ClockDriftMonitor`, метрики дрейфа/RTT/ошибок/алертов, дашборд-панель, конфигурация и Telegram-алерты только при смене состояния. Следующий шаг — **№3, P2.2: убрать двойной штраф в промпте**.
> **Проход 11:** исходники распакованы из архива в корень клона; выполнен **шаг №1 очереди — фикс `volume_ratio`**. Числитель отношения теперь берётся из последней закрытой свечи калькулятора, а не из внутриинтервального WS-тика. Добавлены регрессионные тесты, не запускались. Следующий шаг — **№2, P1.8: мониторинг дрейфа часов**.
> **Проход 6:** **P0.4 выполнен** — полный персист риск-состояния (`risk_state.json`: пик, дневной старт, дата дня, макс. просадка, баланс), граница дня по часам биржи, восстановление в `StartAsync` до первого `UpdateBalance`, персист после каждого изменения баланса, миграция с legacy `peak_balance.json`. Следующий по плану — **P0.6** (kill-switch), не начат.
> Счётчики «частично/не сделано» пересчитаны по фактическим статусам пунктов (в проходе 2 сводка P0/P1 расходилась с телом документа).
>
> **Замечания к рабочему дереву:**
> - **Проход 24, подготовка:** свежий клон снова содержал только корневой `AITradingSystem.zip` и `README.md`. Из вложенной папки архива в корень клона перенесены 156 файлов исходников, тестов, инструментов и трекера (правки до прохода 23 включительно) по конвенции прохода 6; `bin/`, `obj/`, `.vs/`, `.git` и вложенный zip не переносились, вложенный каталог удалён. Корневой `AITradingSystem.zip` сохранён без изменений, `README.md` заменён версией из архива. Подготовка зафиксирована до начала реализации P2.6.
> - **Проход 22, подготовка:** свежий клон снова содержал только корневой `AITradingSystem.zip` и `README.md`. Из вложенной папки архива в корень клона перенесены 150 файлов исходников, тестов, инструментов и трекера (правки до прохода 21 включительно) по конвенции прохода 6; `bin/`, `obj/`, `.vs/`, `.git` и вложенный zip не переносились, вложенный каталог удалён. Корневой `AITradingSystem.zip` сохранён без изменений, `README.md` заменён версией из архива. Подготовка зафиксирована до начала реализации P2.4.
> - **Проход 21, подготовка:** свежий клон содержал только корневой `AITradingSystem.zip` и `README.md`. Из вложенной папки архива восстановлены 280 файлов исходников, тестов, инструментов и трекера в корень клона по конвенции прохода 6; `.vs/`, вложенный zip и `.git` из архива не переносились. Корневой `AITradingSystem.zip` сохранён без изменений, `README.md` заменён версией из архива. Восстановлены правки до прохода 20 включительно. Этот подготовительный список файлов записан до начала реализации P1.4; статус P1.4 пока не менялся.
> - В git-репозитории лежит только `AITradingSystem.zip`; исходники распакованы. **С прохода 6 корень решения — сам клон `AITradingSystem/`** (без подкаталога `extracted/`, которого в снапшоте репозитория не было); все пути ниже относительны его. Правки внесены в распакованные файлы, zip не пересобирался. Проверено: zip уже содержит правки проходов 2–5 (`ExecutionQueue`, `PositionMonitor`, OCO-модели, журнал намерений на месте). В проходе 14 свежий клон: zip в этом снапшоте содержал вложенную папку `AITradingSystem/` (с правками до прохода 13 включительно и этим трекером) — её содержимое перенесено в корень клона, чтобы сохранить конвенцию путей; вложенный каталог удалён. В проходе 16 снова свежий клон: вложенная папка из zip содержала правки до прохода 15 включительно — перенесена в корень клона по той же конвенции; вложенный каталог удалён вместе со старым внутренним zip, zip самого репозитория сохранён без изменений. В проходе 17 снова свежий клон: вложенная папка содержала правки до прохода 16 включительно — перенесена по той же конвенции, вложенный каталог и внутренний zip удалены, zip репозитория сохранён. В проходе 20 снова свежий клон: вложенная папка из zip содержала правки до прохода 19 включительно — перенесена в корень клона по той же конвенции (включая `.gitignore`), вложенный каталог удалён вместе со старым внутренним zip и `.vs/` (как в проходе 18), zip репозитория сохранён без изменений.
> - Распаковка zip перезаписала `README.md` репозитория (в клоне был 1-байтовый, в zip — свой); прочее дерево клона не тронуто.
> - Артефакт `docs/signal-funnel-20261124.md`, на который ссылается P2.1, **в снапшоте репозитория отсутствует** — каталога `docs/` нет. Выводы диагностики сохранены в тексте P2.1.
> - Сборки/компиляции в этом проходе не выполнялись (по требованию заказчика); тесты добавлены, но не запускались.

---

## P0 — защита капитала

### ✅ P0.1 Биржевые стоп-ордера
> Биржевые стоп-ордера: OCO (`STOP_LOSS_LIMIT` + `TAKE_PROFIT_LIMIT`) сразу после филла входа; трейлинг через cancel/replace (`OrderExecutor`, `BinanceRestClient`). Локальный SL — только дублёр. **Критерий: убить процесс с позицией → биржа закрывает сама.**

**Выполнено 24.11.2026 (проход 2).** Защита теперь ставится на самой бирже связкой OCO и переживает смерть процесса: ноги `STOP_LOSS_LIMIT` + `TAKE_PROFIT_LIMIT` остаются в стакане Binance, активация одной отменяет другую.

**Что сделано:**
1. **REST-слой:** `POST /api/v3/orderList/oco` (новый эндпоинт Binance, не legacy `/api/v3/order/oco`), `DELETE /api/v3/orderList`, `GET /api/v3/openOrderList`. Для SELL-защиты long: `aboveType=TAKE_PROFIT_LIMIT`, `belowType=STOP_LOSS_LIMIT`; для BUY — зеркально. У каждой связки/ноги свой `listClientOrderId`/`clientOrderId` (`oco-`/`sl-`/`tp-` + GUID, ≤36 символов).
2. **Постановка сразу после филла:** `TradingEngine.ProcessSymbolAsync` → `EnsureExchangeProtectionAsync` → `OrderExecutor.EnsureProtectionAsync` сразу после `AddPositionAsync`. Ориентир цены для правила OCO — mid стакана (актуальнее закрытия свечи).
3. **Fail-closed:** если OCO не встала в live — позиция закрывается market'ом (`Trading:ClosePositionIfProtectionFails=true`), правило «нет биржевой защиты → нет позиции». Тот же контроль применяется на каждом круге сопровождения, т.е. позиции, восстановленные из `positions.json` без защиты, либо получают OCO, либо закрываются.
4. **Трейлинг через cancel/replace:** `OrderExecutor.UpdateStopLossAsync` снимает старую связку и ставит новую с подтянутым стопом; локальный SL двигается **только после подтверждённой замены**. Троттлинг: `TrailingReplaceMinIntervalSeconds=30`, `TrailingReplaceMinStepPercent=0.15`. Если новая связка не встала — восстанавливается прежний стоп; если и это не удалось — позиция помечается незащищённой и закрывается движком.
5. **Закрытие:** перед MARKET-выходом OCO снимается (`CancelProtectionAsync`), иначе базовый актив заблокирован ногами и биржа отвечает -2010. Ответ «unknown order/list» (-2011) трактуется как «связки уже нет» (вероятно, сработала нога) и логируется `PROTECTION-GONE`.
6. **Округления защитных ног (задел под P1.3):** `ProtectiveOrderPricing.TryBuild` округляет объём к `stepSize`, обе цены к `tickSize` в сторону исполнения, проверяет `minQty` и `minNotional` по худшей ноге и правило Binance `TP.stopPrice > LastPrice > SL.stopPrice`. Лимитная цена отступает от триггера на `ProtectionLimitSlippage=0.3%`, чтобы сработавшая нога налилась.
7. **Персист:** `Position.Protection` (`ProtectiveOrderList`: orderListId, id/clientOrderId обеих ног, триггеры и лимитные цены, объём, статус) сохраняется в `positions.json` сразу после постановки/замены.
8. **Paper-режим:** защита симулируется (`IsSimulated=true`, на биржу ничего не отправляется), выходы по-прежнему считает локальный мониторинг.

**Изменённые файлы:**
- **новые:** `AITradingSystem.Core/Models/Trading/ProtectiveOrderList.cs`, `AITradingSystem.Core/Models/Trading/ProtectiveOrderPricing.cs`
- `AITradingSystem.Core/Models/Trading/Position.cs` (`Protection`, `HasExchangeProtection`, `SetProtection`)
- `AITradingSystem.Data/Binance/IBinanceRestClient.cs` (`PlaceOcoAsync`, `CancelOcoAsync`, `GetOpenOrderListsAsync`)
- `AITradingSystem.Data/Binance/BinanceRestClient.cs` (реализация OCO + `MapToProtection`; `BinanceApiException.IsUnknownOrderOrList`, `IsWouldImmediatelyTrigger`)
- `AITradingSystem.Data/Binance/BinanceModels.cs` (`BinanceOrderListResponse`, `BinanceOrderListRef`, `BinanceOrderResponse.StopPrice`)
- `AITradingSystem.Services/Trading/IOrderExecutor.cs` (`EnsureProtectionAsync`, `CancelProtectionAsync`, `UpdateStopLossAsync` → `Task<bool>`)
- `AITradingSystem.Services/Trading/OrderExecutor.cs` (постановка/замена/снятие OCO, cancel перед market-закрытием)
- `AITradingSystem.Services/Trading/TradingEngine.cs` (`EnsureExchangeProtectionAsync`, вызов после филла и в `MonitorPositionAsync`, трейлинг через executor, mid стакана как ориентир цены)
- `AITradingSystem.Common/Configuration/TradingConfig.cs` (`ExchangeProtectionEnabled`, `ProtectionLimitSlippage`, `ClosePositionIfProtectionFails`, `TrailingReplaceMinIntervalSeconds`, `TrailingReplaceMinStepPercent` + валидация)
- `AITradingSystem.UI/appsettings.json` (те же 5 ключей)
- `AITradingSystem.Tests/TradingLogicTests.cs` (5 тестов: правило above/below, отказ при стопе за рынком, округление/minNotional, порог значимости трейлинга, `HasExchangeProtection`)

**Критерий приёмки:** выполнен на уровне механики — ноги OCO живут на бирже независимо от процесса. Проверка «убить процесс» в живом окружении не проводилась (сборки/запуски запрещены в этом проходе).

**Остаточные ограничения (не входят в P0.1):**
- если биржа исполнила стоп, пока процесс был мёртв, после рестарта бот увидит позицию из `positions.json` и попытается закрыть её повторно — ~~распознавание филлов через user-data stream/реконсиляцию делает **P0.5**~~ **закрыто проходом 9 (P0.5 ✅)**: филл применяется через `ApplyExchangeFillAsync` с дедуп-леджером, повторный MARKET не шлётся;
- ~~accompanied-мониторинг по-прежнему ходит раз в круг анализа (~7 мин на 12 пар)~~ — **закрыто проходом 3 (P0.2)**: сопровождение вынесено в тик-контур `PositionMonitor`;
- `GetOpenOrderListsAsync` возвращает только id связки/ног (Binance не отдаёт типы и цены в `openOrderList`) — ~~разбор ног оставлен реконсиляции P0.5~~ **закрыто проходом 9**: реконсиляция запрашивает ноги по `origClientOrderId`.

---

### ✅ P0.2 Независимый контур мониторинга
> Независимый контур мониторинга: сопровождение позиций на каждый тик WS (≤1–2 с), без `await` LLM (`TradingEngine`).

**Выполнено 24.11.2026 (проход 3).** Сопровождение позиций больше не стоит в очереди за инференсом модели: отдельный контур `PositionMonitor` реагирует на тики WebSocket и проверяет SL/TP, биржевую защиту и трейлинг независимо от цикла анализа.

**Что сделано:**
1. **Новый контур:** `PositionMonitor` — собственный цикл с периодом `PositionMonitorIntervalMs=500` мс. В нём нет ни одного обращения к LLM/новостям: только цена, SL/TP, OCO-защита и трейлинг.
2. **Тики без блокировки WS:** `OnKlineReceived`/`OnOrderBookReceived` пишут последний тик в `ConcurrentDictionary` (latest-wins) и сразу возвращаются — receive-loop не ждёт REST. Источники: kline (~2 с) и partial-depth `@depth20@1000ms` (1 с).
3. **Цена выхода — исполнимая:** для long берётся best **bid**, для short — best **ask** (`ResolveExitPrice`), а не закрытие свечи. Нет стакана — last, нет last — mid.
4. **Параллелизм по символам:** обработка каждой позиции — отдельная задача с single-flight (`_inFlight`), поэтому медленный REST по одной паре не задерживает остальные 11. Принудительный обход раз в `PositionMonitorSweepSeconds=5` с работает даже если WS молчит.
5. **SL/TP:** проверка по тику плюс экстремумы текущей свечи, **только** если свеча началась после входа (`CandleStartedBeforeEntry`) — защита от «мгновенного стопа» сохранена (`EvaluateExits`).
6. **Защита и трейлинг переехали в контур:** `EnsureProtectionAsync` для позиций без OCO (в т.ч. восстановленных из `positions.json`) с троттлингом попыток `ProtectionRetryIntervalSeconds=15`; fail-closed «нет защиты → нет позиции» сохранён. Трейлинг — по-прежнему cancel/replace биржевой связки через executor (`TrailingReplaceMinIntervalSeconds`/`MinStepPercent`), локальный SL двигается только после подтверждённой замены, персист сразу.
7. **Без гонок с циклом анализа:** посимвольный шлюз `PositionMonitor.LockSymbolAsync`. Критические секции короткие (вход+постановка защиты, закрытие); инференс LLM идёт **вне** шлюза. `ClosePositionAsync` берёт шлюз и перепроверяет, что позиция ещё открыта (`PositionManager.GetPositionById`) — двойной MARKET-выход из тик-контура и ИИ-пересмотра исключён.
8. **Цикл анализа разгружен:** `ProcessSymbolAsync` для открытой позиции теперь только спрашивает модель «держать/закрыть»; методы `MonitorPositionAsync` и `EnsureExchangeProtectionAsync` из `TradingEngine` удалены. Сопровождение продолжает работать и на `Pause` (пауза останавливает только входы/анализ).
9. **ATR для трейлинга:** кэш `_atrAbsolute` обновляется на закрытых свечах и при прогреве историей — индикаторы не пересчитываются на каждом тике.
10. **Телеметрия задержки:** `MetricsCollector.RecordPositionCheck` (счётчик, средняя/худшая задержка «тик → проверка»), предупреждение `MONITOR-LAG` при превышении `PositionMonitorMaxLatencyMs=2000` (троттлинг 30 с на символ), `MONITOR-START` при старте контура.
11. **Жизненный цикл:** контур стартует в `StartAsync` до основного цикла, гасится в `StopAsync`/`CleanupAfterFailedStartAsync` раньше отключения WS; зарегистрирован в DI (`App.xaml.cs`).

**Изменённые файлы:**
- **новый:** `AITradingSystem.Services/Trading/PositionMonitor.cs`
- `AITradingSystem.Services/Trading/TradingEngine.cs` (приём тиков, шлюзы входа/закрытия, `ClosePositionCoreAsync`, кэш ATR, старт/остановка контура, удаление `MonitorPositionAsync`/`EnsureExchangeProtectionAsync`)
- `AITradingSystem.Services/Trading/PositionManager.cs` (`GetPositionById`)
- `AITradingSystem.Common/Configuration/TradingConfig.cs` (`PositionMonitorIntervalMs`, `PositionMonitorSweepSeconds`, `PositionMonitorMaxLatencyMs`, `ProtectionRetryIntervalSeconds` + валидация)
- `AITradingSystem.UI/appsettings.json` (те же 4 ключа)
- `AITradingSystem.Services/Monitoring/MetricsCollector.cs` (`RecordPositionCheck`, `PositionChecks`, `AveragePositionCheckLatencyMs`, `MaxPositionCheckLatencyMs`, сводка)
- `AITradingSystem.UI/App.xaml.cs` (регистрация `PositionMonitor` в DI)
- `AITradingSystem.Tests/TradingLogicTests.cs` (5 тестов: исполнимая цена выхода bid/ask, срабатывание по тику, экстремумы свечи после входа, отсутствие тика ≠ выход, валидация новых ключей)

**Критерий приёмки:** сопровождение привязано к тику (kline ~2 с / depth 1 с) и не содержит `await` LLM; худшая задержка измеряется и алертится порогом 2 с. Живой замер задержки не проводился (сборки/запуски запрещены в этом проходе).

**Остаточные ограничения (не входят в P0.2):**
- если процесс мёртв, сопровождение делает только биржевая OCO-связка (P0.1); ~~распознавание её филлов после рестарта — **P0.5**~~ **закрыто проходом 9 (P0.5 ✅)**;
- «протухшие» данные после реконнекта WS теперь блокируют входы — **P1.4 выполнен проходом 21**;
- ~~очередь исполнения с приоритетами (закрытие раньше входов)~~ — **закрыто проходом 5 (P0.3)**: сетевые отправки идут через `ExecutionQueue` с приоритетами, durable-журналом и ретраями.

---

### ✅ P0.3 Очередь исполнения с приоритетами
> Очередь исполнения с приоритетами: закрытие/защита раньше входов; идемпотентность через `clientOrderId`.

**Выполнено 24.11.2026: фундамент — проход 4, закрытие пункта — проход 5.** Сетевые отправки ордеров больше не идут напрямую из цикла анализа: они проходят через очередь с приоритетами и ключом идемпотентности.

**Что сделано:**
1. **Новый `ExecutionQueue`** — диспетчер с приоритетами `Protection(0) → Close(1) → Trailing(2) → Entry(3)`, FIFO внутри приоритета. Защита и закрытие обгоняют вход, даже если вход запрошен раньше.
2. **Параллелизм и сериализация:** `ExecutionQueueMaxParallel=2` одновременных отправок, но **по одному символу — не более одной операции в полёте** (нет гонок «вход vs закрытие» на одной паре).
3. **В очереди только сетевая операция**, а не ожидание филла: 60-секундный опрос лимитного входа (`WaitForFillAsync`) выполняется вне очереди и не блокирует закрытие другой позиции.
4. **Идемпотентность по `clientOrderId`:** повтор с тем же ключом либо присоединяется к уже идущей отправке (`IDEMPOTENT-JOIN`), либо получает сохранённый результат (`IDEMPOTENT-REPLAY`, TTL `ExecutionIdempotencyTtlSeconds=600`) — второй ордер не создаётся. Кэшируется только успех: отказ можно повторить тем же id.
5. **`newClientOrderId` в REST:** `PlaceOrderAsync`/`PlaceMarketOrderAsync` принимают `clientOrderId` и шлют его бирже. Ретраи внутри `SendRequestAsync` (5xx, -1000/-1001/-1007, сетевой сбой) переиспользуют тот же id — **закрыта дыра «ретрай POST /order создаёт дубль»**.
6. **Ответ «Duplicate order sent»** (`BinanceApiException.IsDuplicateOrder`) трактуется как «ордер уже принят»: новый метод `GetOrderAsync(symbol, clientOrderId)` (`origClientOrderId`) забирает его состояние вместо повторной отправки.
7. **OCO тоже идемпотентна:** `listClientOrderId` генерируется в `OrderExecutor` (`oco-…`) и передаётся в `PlaceOcoAsync`, он же — ключ очереди; повтор не создаёт вторую пару ног.
8. **Приоритеты расставлены по местам:** постановка/снятие защиты — `Protection`, market-закрытие и отмена ордера — `Close`, cancel/replace трейлинга — `Trailing`, вход (limit/market) — `Entry`.
9. **Отмена вызывающего:** токен stopping-а снимает ещё не отправленную операцию с очереди — ордер не уйдёт на биржу после остановки движка.
10. **Телеметрия:** `MetricsCollector.RecordExecutionWait` / `RecordExecutionQueueDepth` (среднее/худшее ожидание, макс. глубина), предупреждение `QUEUE-WAIT` при ожидании > `ExecutionQueueWarnWaitMs=3000`.

**Изменённые файлы:**
- **новый:** `AITradingSystem.Services/Trading/ExecutionQueue.cs` (`ExecutionPriority` + очередь)
- `AITradingSystem.Services/Trading/OrderExecutor.cs` (очередь во всех отправках, `PlaceOrderIdempotentAsync`, `NewClientOrderId`, генерация `listClientOrderId`, обработка дубля)
- `AITradingSystem.Data/Binance/IBinanceRestClient.cs` (`clientOrderId` в `PlaceOrderAsync`/`PlaceMarketOrderAsync`, перегрузка `GetOrderAsync(symbol, clientOrderId)`)
- `AITradingSystem.Data/Binance/BinanceRestClient.cs` (`newClientOrderId`, `origClientOrderId`, `BinanceApiException.IsDuplicateOrder`)
- `AITradingSystem.Common/Configuration/TradingConfig.cs` (`ExecutionQueueMaxParallel`, `ExecutionIdempotencyTtlSeconds`, `ExecutionQueueWarnWaitMs` + валидация)
- `AITradingSystem.UI/appsettings.json` (те же 3 ключа)
- `AITradingSystem.Services/Monitoring/MetricsCollector.cs` (`RecordExecutionWait`, `RecordExecutionQueueDepth`, метрики в сводке)
- `AITradingSystem.UI/App.xaml.cs` (регистрация `ExecutionQueue` в DI)
- `AITradingSystem.Tests/TradingLogicTests.cs` (3 теста: закрытие обгоняет ранее поставленный вход, повтор по тому же `clientOrderId` не отправляет второй ордер, одна операция на символ в полёте)

**Доведено до ✅ 24.11.2026 (проход 5).** Закрыты три из пяти пунктов остатка; два оставшихся не входят в P0.3 и переданы соседним пунктам.

**Что добавлено в проходе 5:**
1. **Durable-журнал намерений** (`IExecutionJournal` / `JsonExecutionJournal`, файл `data/state/execution_journal.json`): запись `Pending` фиксируется **до** сетевого вызова (write-ahead, атомарно tmp+rename), после ответа — `Confirmed`/`Failed`. Кэш идемпотентности больше не живёт только в памяти.
2. **Восстановление при старте:** `ExecutionQueue.RestoreAsync` вызывается в `TradingEngine.StartAsync` **до** первой торговой операции. Подтверждённые ключи в окне TTL блокируют повтор; неподтверждённые (`Pending`) не вычищаются по TTL и попадают в `UnresolvedIntents` + лог `EXEC-INTENT-UNRESOLVED`.
3. **Сверка с биржей вместо слепой отправки:** `OrderExecutor.ResolveRestoredIntentAsync` (регистрируется через `SetIntentResolver`) забирает ордер по `origClientOrderId`. Нет ордера → ключ снимается (`EXEC-INTENT-CLEARED`), отправка разрешена; есть → берётся его состояние (`EXEC-INTENT-RESOLVED`), второй ордер не шлётся. Сетевая ошибка при сверке не глотается — повтор запрещён (fail-closed). Связку OCO целиком восстановить нельзя (Binance не отдаёт ноги в `openOrderList`), поэтому незавершённая связка снимается и ставится заново.
4. **Детерминированные `clientOrderId`** (`StableClientOrderId`, префикс + 32 hex SHA-256, ≤36 символов) вместо GUID: вход — от `symbol|side|qty|price|signal.Timestamp`, закрытие — от `position.Id|qty`, OCO — от `position.Id|qty|SL|TP`. Ключ одинаков после рестарта, поэтому журнал реально работает; закрытие остатка после частичного филла получает новый ключ (другой объём).
5. **Снятие ключа при отмене:** `ExecutionQueue.ForgetKeyAsync` вызывается после успешного снятия OCO (и при «связки уже нет») — cancel/replace трейлинга и повторная постановка той же защиты не блокируются собственным кэшем.
6. **Ретрай на уровне операции:** `ExecutionRetryMaxAttempts=3`, экспоненциальный backoff `ExecutionRetryBaseDelayMs=300` → потолок `ExecutionRetryMaxDelayMs=5000` с **полным jitter**. Ретраится только транзиент (`BinanceApiException.IsTransient`: 5xx/429/418/−1000/−1001/−1003/−1006/−1007, сетевые/таймаут), отказ по существу (−2010 и т.п.) — нет. Повтор идёт **с тем же `clientOrderId`**, журнал пишет номер попытки.
7. **Backoff не держит символ:** на время ожидания слот символа и общий слот освобождаются, операция возвращается в очередь с прежним FIFO-номером (`NotBeforeUtc`), диспетчер будится таймером. Закрытие по той же паре не стоит за ретраем входа.
8. **Наблюдаемость в UI:** панель «Очередь исполнения (P0.3)» в дашборде — текущая/максимальная глубина, ожидание avg/max, ретраи, склеенные запросы, пропущенные повторы, восстановленные ключи; жёлтый алерт при превышении `ExecutionQueueWarnWaitMs` и при наличии неподтверждённых намерений.

**Изменённые файлы (проход 5):**
- **новые:** `AITradingSystem.Core/Models/Trading/ExecutionIntent.cs` (`ExecutionIntentStatus` + запись намерения), `AITradingSystem.Data/Storage/IExecutionJournal.cs`, `AITradingSystem.Data/Storage/JsonExecutionJournal.cs`
- `AITradingSystem.Services/Trading/ExecutionQueue.cs` (`RestoredExecution`, журнал в ctor, `RestoreAsync`/`SetIntentResolver`/`ForgetKeyAsync`, ретрай с backoff/jitter и освобождением слота, `UnresolvedIntents`, счётчики)
- `AITradingSystem.Services/Trading/OrderExecutor.cs` (`StableClientOrderId` вместо GUID, `ResolveRestoredIntentAsync`, снятие ключа при cancel OCO; `NewClientOrderId` удалён как неиспользуемый)
- `AITradingSystem.Services/Trading/TradingEngine.cs` (ctor + `ExecutionQueue`, `RestoreAsync` и алерт по неподтверждённым намерениям в `StartAsync`)
- `AITradingSystem.Data/Binance/BinanceRestClient.cs` (`BinanceApiException.IsTransient`)
- `AITradingSystem.Common/Configuration/TradingConfig.cs` (`ExecutionRetryMaxAttempts`, `ExecutionRetryBaseDelayMs`, `ExecutionRetryMaxDelayMs` + валидация, в т.ч. потолок ≥ базы)
- `AITradingSystem.UI/appsettings.json` (те же 3 ключа)
- `AITradingSystem.Services/Monitoring/MetricsCollector.cs` (`ExecutionQueueDepth`, `ExecutionRetries`, `RecordExecutionRetry`, сводка)
- `AITradingSystem.UI/App.xaml.cs` (DI: `IExecutionJournal` → `JsonExecutionJournal` в `data/state`)
- `AITradingSystem.UI/ViewModels/DashboardViewModel.cs` (ctor + `ExecutionQueue`/`TradingConfig`, `ExecutionQueueText`, `ExecutionQueueAlertText`)
- `AITradingSystem.UI/Views/DashboardView.xaml` (панель метрик очереди)
- `AITradingSystem.Tests/TradingLogicTests.cs` (7 тестов: подтверждённый ключ переживает рестарт и блокирует второй ордер; неподтверждённое намерение сверяется с биржей — «нет ордера → отправка», «есть ордер → повтора нет»; ретрай транзиента тем же ключом; лимит попыток; отказ по существу не ретраится; валидация новых ключей)

**Критерий приёмки:** выполнен — «закрытие/защита раньше входов» и «идемпотентность через `clientOrderId`» реализованы, идемпотентность переживает рестарт, покрыто тестами. Сквозная проверка на живом окружении не проводилась (сборки/запуски запрещены).

**Остаток вне P0.3 (передан по назначению):**
- ~~**бюджет весов `X-MBX-USED-WEIGHT`** — **P1.6** (очередь ограничивает только параллелизм)~~ **закрыто проходом 17 (P1.6 ✅)**: `RequestWeightBudget` учитывает вес и притормаживает polling; критичные отправки обходят soft-limit;
- **приоритетное вытеснение в полёте** (cancel уже отправленного входа при пробитии риска) — решается вместе с **P0.6**;
- **подхват сиротских позиций/ордеров и филлов, случившихся пока процесс был мёртв** — ~~**P0.5**: журнал даёт список `UnresolvedIntents`, но полной реконсиляции по user-data stream не заменяет~~ **закрыто проходом 9 (P0.5 ✅)**: реконсиляция применяет филлы по кумулятиву с дедуп-леджером `Position.AppliedExchangeFills` — повторный MARKET по уже закрытой биржей позиции не шлётся и для нового ключа.

---

### ✅ P0.4 Персист DrawdownTracker
> Персист `DrawdownTracker` (пик, дневной старт) в `JsonStateRepository`, сброс по UTC биржи, восстановление при старте.

**Выполнено 24.11.2026 (проход 6).** Дневной убыток и пик больше не обнуляются рестартом: состояние трекера целиком живёт в `data/state/risk_state.json`, граница торгового дня считается по часам биржи.

**Что сделано:**
1. **Снимок состояния:** `DrawdownState` (`DrawdownTracker.cs`) — пик, дневной старт, дата дня (`DayStartUtc`), максимальная просадка, текущий баланс, время сохранения. Методы `CaptureState()` / `RestoreState()`.
2. **Персист:** `IStateRepository.SaveRiskStateAsync` / `LoadRiskStateAsync`; `JsonStateRepository` пишет `risk_state.json` атомарно (tmp + rename, как журнал намерений P0.3). Legacy `SavePeakBalanceAsync`/`LoadPeakBalanceAsync` сохранены; если `risk_state.json` нет, а `peak_balance.json` есть — миграция «только пик» (`CurrentBalance=0`), дневной старт берётся из первого фактического баланса.
3. **Часы биржи:** `DrawdownTracker.SetExchangeClock(Func<DateTime>)` — смена дня считается по времени биржи, не `DateTime.UtcNow.Date` процесса. `TradingEngine.ConfigureExchangeClockAsync` в `StartAsync` оценивает смещение через `GET /api/v3/time` (полудлина RTT, как в синхронизации подписи); при недоступности `/time` — предупреждение и локальные UTC-часы. Опциональный параметр `exchangeUtcNow` добавлен в конструкторы `DrawdownTracker`/`RiskManager` (старые вызовы совместимы).
4. **Восстановление при старте:** `StartAsync` — сначала часы биржи, затем `LoadRiskStateAsync` + `RestoreState` **до** первого `UpdateBalance`, лог `RISK-STATE-RESTORED`. Если сохранённый день уже прошёл по часам биржи — дневной PnL обнуляется, пик/просадка остаются (ролловер внутри `RestoreState` → `UpdateBalance`).
5. **Персист после каждого изменения баланса:** helper `UpdateRiskBalanceAsync` (update + save) заменил прямые `_riskManager.UpdateBalance` в комиссии входа (paper) и в обновлении баланса после закрытия (paper и live); состояние фиксируется и сразу после стартового баланса.
6. **Paper-баланс переживает рестарт:** при восстановлении виртуальный баланс берётся из `risk_state.json`, а не сбрасывается к `PaperTradingInitialBalance` (иначе дневной старт восстанавливался бы несогласованно). Первый запуск — по-прежнему из конфига.

**Изменённые файлы:**
- `AITradingSystem.Core/RiskManagement/DrawdownTracker.cs` (`DrawdownState`, `SetExchangeClock`, `CaptureState`, `RestoreState`, день по часам биржи)
- `AITradingSystem.Core/RiskManagement/RiskManager.cs` (опциональный `exchangeUtcNow` в ctor)
- `AITradingSystem.Data/Storage/IStateRepository.cs` (`SaveRiskStateAsync`, `LoadRiskStateAsync`)
- `AITradingSystem.Data/Storage/JsonStateRepository.cs` (`risk_state.json`, атомарная запись, миграция с legacy пика)
- `AITradingSystem.Services/Trading/TradingEngine.cs` (ctor + `IStateRepository`, `ConfigureExchangeClockAsync`/`PersistRiskStateAsync`/`UpdateRiskBalanceAsync`, восстановление в `StartAsync`, персист на обоих путях обновления баланса)
- `AITradingSystem.Tests/TradingLogicTests.cs` (6 тестов: дневной лимит переживает рестарт; пик переживает рестарт; ролловер по часам биржи, а не локальным; протухший день сбрасывает дневной PnL, но не пик; roundtrip репозитория; миграция с legacy `peak_balance.json`)

**Критерий приёмки:** выполнен на уровне механики — после рестарта дневной убыток/пик восстанавливаются, новый день начинается по UTC биржи. Живой прогон «убить процесс» не проводился (сборки/запуски запрещены в этом проходе).

**Остаточные ограничения (не входят в P0.4):**
- пробитие лимита по-прежнему только запрещает новые входы — авто-закрытие позиций и паническая кнопка — **P0.6**;
- в live баланс между сделками не опрашивается — дневной PnL фиксируется по событиям сделок (как и раньше); после применённого биржевого филла баланс теперь запрашивается (P0.5, проход 9), непрерывная переоценка equity — **P1.8**;
- смещение часов биржи оценивается один раз при старте; мониторинг дрейфа — **P1.8**.

---

### ✅ P0.5 Реконсиляция + user-data stream
> Реконсиляция + user-data stream (`listenKey`/`executionReport`) вместо polling; подхват сиротских позиций/ордеров; учёт частичных филлов.

**Выполнено 24.11.2026: фундамент — проход 7, UI/DI — проход 8, завершение — проход 9 (проверка завершённости + учёт в трекере).** Все четыре пункта остатка закрыты в коде; проход 9 проверил их наличие и согласованность и внёс запись в SECUPD.md (код в проходе 9 не менялся).

**Что сделано (поверх фундамента проходов 7–8):**
1. **Применение филлов без повторного MARKET:** `TradingEngine.ApplyExchangeFillAsync` применяет биржевой филл (защитная нога / close из журнала) к позиции, истории и балансу под посимвольным шлюзом. Дедуп — durable-леджер `Position.AppliedExchangeFills` (кумулятив по `clientOrderId`, персистится в `positions.json`): повторный `executionReport`, устаревший кумулятив и стартовая реконсиляция не закрывают объём дважды. Частичный филл защитной ноги уменьшает позицию (`ReduceQuantity`), старая OCO снимается — контур сопровождения ставит новую на остаток. После применения — обновление баланса с биржи и `UpdateRiskBalanceAsync` (kill-switch P0.6 взводится по факту).
2. **Подхват сиротских позиций/ордеров:** `ReconciliationService.RemediateAsync` — (а) пропавшая OCO: ноги запрашиваются по `origClientOrderId`, исполненный объём применяется; если филлов нет — локальная защита сбрасывается, тик-контур переставит OCO или fail-closed закроет остаток; (б) журнал намерений P0.3: entry с филлом при отсутствии позиции → `AdoptOrphanPositionAsync` (SL/TP от ATR, постановка OCO сразу, fail-closed «нет защиты → нет позиции»), close с филлом → применение к позиции; (в) открытые ордера: сиротский entry с исполненным объёмом подхватывается даже без записи в журнале; сиротские ордера/OCO бота (префиксы `mkt-/ent-/cls-/oco-/sl-/tp-`) снимаются через очередь с приоритетами Close/Protection; **внешние ордера не трогаются**. Переключатели: `ReconciliationAdoptOrphanPositions`, `ReconciliationCancelOrphanBotOrders`.
3. **Живучесть listenKey:** `KeepAliveLoopAsync` (PUT каждые `UserDataKeepAliveMinutes`), `HealthLoopAsync` (проверка socket каждые `UserDataStreamHealthCheckSeconds=15`), при сбое продления/обрыве — `RecreateListenKeyAsync`: переподключение user-data WS, метрика `ListenKeyRecreations` и **полная реконсиляция после пересоздания**. Фоновые ошибки переподключения user-data WS не глотаются молча — health-check видит `IsUserDataStreamConnected=false` и поднимает контур заново.
4. **Частичные филлы из stream вместо polling:** `OrderExecutor.WaitForFillAsync` при включённом и подключённом user-data stream ждёт `executionReport` (`_latestReports` latest-wins + `_fillWaiters` TCS по ключам `clientOrderId`/`id:<orderId>`), кумулятив ордера обновляется из report; REST-polling остаётся только fallback (stream выключен/не подключён). Финальное состояние и комиссии забираются **одним** GET, не циклом опроса. Неисполненный остаток по таймауту отменяется, фактический филл добирается после отмены.
5. **Наблюдаемость:** метрики `ReconciliationFillsApplied`, `OrphanPositionsAdopted`, `OrphanOrdersCanceled`, `ListenKeyRecreations` + сводка; панель дашборда показывает их вместе с отчётом реконсиляции. Логи: `EXCHANGE-FILL-APPLIED`, `PROTECTION-FILLED-APPLIED`, `RECON-ADOPT`, `RECON-CANCEL`, `USER-DATA-RECREATE`.

**Изменённые файлы фундамента (проход 7):** новые `AITradingSystem.Core/Models/Trading/ExecutionReport.cs`, `AITradingSystem.Services/Trading/ReconciliationService.cs`; изменены `AITradingSystem.Common/Configuration/TradingConfig.cs`, `AITradingSystem.Core/RiskManagement/RiskManager.cs`, `AITradingSystem.Data/Binance/BinanceModels.cs`, `BinanceRestClient.cs`, `IBinanceRestClient.cs`, `BinanceWebSocketClient.cs`, `IBinanceWebSocketClient.cs`, `AITradingSystem.Services/Monitoring/MetricsCollector.cs`, `AITradingSystem.Services/Trading/TradingEngine.cs`, `AITradingSystem.UI/ViewModels/TradingViewModel.cs`, `AITradingSystem.UI/Views/TradingView.xaml`, `AITradingSystem.UI/ViewModels/SettingsViewModel.cs`, `AITradingSystem.Common/Configuration/BinanceConfig.cs`.
**Изменённые файлы прохода 8:** `AITradingSystem.UI/App.xaml.cs`, `AITradingSystem.UI/appsettings.json`, `AITradingSystem.UI/ViewModels/DashboardViewModel.cs`, `AITradingSystem.UI/Views/DashboardView.xaml`, `AITradingSystem.Tests/TradingLogicTests.cs`.
**Файлы завершения (проход 9 — учтены в трекере, код уже был в дереве):** `AITradingSystem.Services/Trading/ReconciliationService.cs` (remediate/adopt/listenKey-recreate/применение филлов), `AITradingSystem.Services/Trading/TradingEngine.cs` (`ApplyExchangeFillAsync`, `AdoptOrphanPositionAsync`, `Configure` колбэков), `AITradingSystem.Services/Trading/OrderExecutor.cs` (stream-first `WaitForFillAsync`, `_latestReports`/`_fillWaiters`, `IDisposable`), `AITradingSystem.Core/Models/Trading/Position.cs` (`AppliedExchangeFills`, `GetUnappliedExchangeFill`, `RecordExchangeFill`, `ReduceQuantity`), `AITradingSystem.Core/Models/Trading/Order.cs` (`FilledQuoteQuantity`, `Commission`, `AverageFillPrice`, `UpdatedAt`), `AITradingSystem.Common/Configuration/TradingConfig.cs` + `AITradingSystem.UI/appsettings.json` (`UserDataStreamHealthCheckSeconds`, `ReconciliationAdoptOrphanPositions`, `ReconciliationCancelOrphanBotOrders`), `AITradingSystem.Services/Monitoring/MetricsCollector.cs`, `AITradingSystem.UI/ViewModels/DashboardViewModel.cs`, `AITradingSystem.Data/Binance/BinanceRestClient.cs` (`MapToOrder`: комиссии/quote из fills), `AITradingSystem.Tests/TradingLogicTests.cs` (3 теста: дедуп-леджер по кумулятиву, `ToReport` берёт кумулятив и среднюю цену филла, валидация health-check).

**Критерий приёмки:** выполнен на уровне механики — филлы биржи применяются к позиции/истории/балансу без повторного MARKET и без двойного учёта после рестарта; сироты подхватываются/снимаются; listenKey пересоздаётся с реконсиляцией; ожидание филлов идёт через stream. Живой прогон не проводился (сборки/запуски запрещены).

**Остаточные ограничения (не входят в P0.5):**
- `outboundAccountPosition`/`balanceUpdate` из user-data stream не обрабатываются — баланс обновляется REST-запросом после применённого филла; непрерывная переоценка equity — **P1.8**;
- реконсиляция выполняется при старте и после пересоздания listenKey; периодической фоновой реконсиляции по таймеру нет (при живом stream события приходят сами);
- ~~бюджет весов REST-запросов реконсиляции — **P1.6**~~ **закрыто проходом 17 (P1.6 ✅)**: все REST-запросы клиента, включая реконсиляцию, идут через бюджет весов.

---

### ✅ P0.6 Kill-switch
> Kill-switch: авто-закрытие всех позиций при пробитии лимита просадки/дня (сейчас лимиты только запрещают новые входы) + паническая кнопка в UI.

**Выполнено 24.11.2026: фундамент — проход 7, UI-хвост — проход 8.** `RiskManager.ShouldEngageKillSwitch` даёт чистый предикат по лимитам просадки/дня и флагу `KillSwitchAutoCloseOnRiskBreach`. `TradingEngine.UpdateRiskBalanceAsync` после каждого обновления баланса взводит kill-switch: блокирует новые входы, ставит Pause и вызывает `CloseAllPositionsAsync` через штатный пайплайн (снятие OCO → market → история → баланс → события). Ручная паника в UI двухшаговая (MessageBox Yes/No) и вызывает `EngageKillSwitchAsync("ManualPanic")`.

**Доведено проходом 8:** кнопка «Сброс kill-switch» с подтверждением вызывает `ResetKillSwitch`; баннер оболочки показывает состояние kill-switch; дашборд выводит счётчик активаций; добавлен тест предиката `ShouldEngageKillSwitch` (не запускался).

**Изменённые файлы:** `AITradingSystem.Core/RiskManagement/RiskManager.cs`, `AITradingSystem.Common/Configuration/TradingConfig.cs`, `AITradingSystem.Services/Trading/TradingEngine.cs`, `AITradingSystem.Services/Monitoring/MetricsCollector.cs`, `AITradingSystem.UI/ViewModels/TradingViewModel.cs`, `AITradingSystem.UI/Views/TradingView.xaml`, `AITradingSystem.UI/ViewModels/MainViewModel.cs`, `AITradingSystem.UI/ViewModels/DashboardViewModel.cs`, `AITradingSystem.UI/Views/DashboardView.xaml`, `AITradingSystem.UI/appsettings.json`, `AITradingSystem.Tests/TradingLogicTests.cs`.

**Критерий приёмки:** выполнен на уровне механики — пробитие лимита после обновления баланса закрывает все позиции и блокирует входы; паническая кнопка и явный сброс есть в UI. Живой прогон не проводился (сборки/запуски запрещены). Непрерывная переоценка equity между сделками относится к P0.5/P1.8, а не к критерию P0.6.

---

### ✅ P0.7 Защита от случайного Live
> Защита от случайного Live: двухшаговое подтверждение, отдельные ключи/конфиги, несмываемый индикатор «REAL MONEY».

**Выполнено 24.11.2026: фундамент — проход 7, UI-хвост — проход 8.** Переход в Live требует второго шага: выбор Live показывает блок подтверждения, сохранение заблокировано до ввода точной фразы `LIVE` и команды «Подтвердить Live». `BinanceConfig` поддерживает отдельные `EncryptedLiveApiKey`/`EncryptedLiveApiSecret`; `BinanceRestClient` в Live использует их через `EffectiveEncrypted*`, общие ключи остаются для Demo/Testnet. В оболочке постоянный баннер `REAL MONEY — LIVE` с красным фоном обновляется таймером независимо от статуса бота.

**Доведено проходом 8:** добавлены отсутствовавшие команда `ConfirmLive`, сброс подтверждения при смене окружения, поля ввода отдельных Live-ключей (Secret через PasswordBox), шифрование через `ApiKeyEncryptor` при сохранении, ключи `EncryptedLive*` в `appsettings.json`, привязки фона/цвета баннера в `MainWindow.xaml`; добавлен тест выбора Live-ключей (не запускался).

**Изменённые файлы:** `AITradingSystem.Common/Configuration/BinanceConfig.cs`, `AITradingSystem.Data/Binance/BinanceRestClient.cs`, `AITradingSystem.UI/ViewModels/SettingsViewModel.cs`, `AITradingSystem.UI/Views/SettingsView.xaml`, `AITradingSystem.UI/Views/SettingsView.xaml.cs`, `AITradingSystem.UI/ViewModels/MainViewModel.cs`, `AITradingSystem.UI/MainWindow.xaml`, `AITradingSystem.UI/appsettings.json`, `AITradingSystem.Tests/TradingLogicTests.cs`.

**Критерий приёмки:** выполнен на уровне механики — одно кликовое переключение в Live не сохраняется, отдельные Live-ключи поддержаны и вводятся из UI, индикатор REAL MONEY постоянный. Живой прогон UI не проводился (сборки/запуски запрещены).

---

## P1 — корректность денег и данных

### ✅ P1.1 Slippage-модель в paper-режиме
> Slippage-модель в paper-режиме (филл по стакану, комиссии обеих ног).

**Выполнено 24.11.2026 (проход 19).** Paper taker-исполнение больше не считается по цене сигнала: оно проходит по уровням стакана и возвращает фактический объём, среднюю цену и проскальзывание. Комиссии входа и выхода остаются в модели сделки и истории.

**Что сделано:**
1. **Симуляция стакана:** `OrderBook.SimulateMarketFill` для BUY ест asks, для SELL — bids; поддерживает `worstPrice` (cap выхода) и `maxLevels`; возвращает `OrderBookFill` с filled/quote/average/slippage/fullyFilled.
2. **Paper market-вход:** при `PaperSlippageEnabled=true` и `UseMarketEntry=true` вход симулируется по стакану; нет стакана или нулевой филл → отказ `paper-entry`. Частичный филл открывает позицию на исполненный объём.
3. **Paper post-only вход:** maker не пересекает спред, филл считается по лимитной цене с комиссией; это отдельное допущение maker-модели, не taker-slippage.
4. **Paper выход:** обычный выход симулируется по стакану с cap `MarketExitMaxSlippage`; аварийный (`emergency=true`) обходит cap при `EmergencyMarketExitBypassSlippageCap=true`. Нулевой филл оставляет позицию открытой; частичный филл возвращается движку для уменьшения позиции и защиты остатка.
5. **Fallback:** если стакан недоступен или `PaperSlippageEnabled=false`, выход идёт по текущей цене (старое поведение), вход market при включённой модели без стакана отклоняется.
6. **Комиссии/история:** `CreatePaperOrder` считает комиссию от фактического нотариала исполненной части; `TradingEngine`/`TradeRecord.NetPnL` из P1.2 учитывают комиссии обеих ног.

| Действие | Файлы |
|---|---|
| Создан до трекера | `AITradingSystem.Tests/PaperSlippageTests.cs` |
| Изменены до трекера | `AITradingSystem.Core/Models/MarketData/OrderBook.cs`, `AITradingSystem.Common/Configuration/TradingConfig.cs`, `AITradingSystem.Services/Trading/OrderExecutor.cs`, `AITradingSystem.UI/appsettings.json` |
| Изменён сразу после кода | `SECUPD.md` (P1.1 → ✅, сводка P1 = 5 ✅ / 1 🟡 / 2 ⬜, очередь: осталось 5 шагов, этот журнал) |

**Новые ключи конфигурации (`Trading`):** `PaperSlippageEnabled=true`, `PaperSlippageMaxLevels=0` (0 — все доступные уровни; валидация [0; 1000]).

**Побочные эффекты интерфейса:** `OrderBook` получил `SimulateMarketFill`; добавлен record `OrderBookFill`; приватный `CreatePaperOrder` теперь принимает requested/filled quantity и average price. Поведенческое изменение: paper market-вход без стакана отклоняется; paper-выход может быть частичным/нулевым из-за глубины или cap. Тесты добавлены (7 кейсов), но не запускались. Сборки/компиляции и коммит не выполнялись; zip не пересобирался.

### ✅ P1.2 Комиссии в R/R и безубытке
> Комиссии в R/R и безубытке; ограничение проскальзывания на market-выходе.

**Выполнено 24.11.2026 (проход 18).** Денежная математика сделки переведена на net-величины после комиссий входа и выхода; обычный market-выход ограничен по проскальзыванию через marketable LIMIT IOC.

**Что сделано:**
1. **Единая модель издержек:** `TradeCostModel` считает effective entry/exit, net risk/reward, TP для заданного net R/R, break-even и net PnL на единицу. Long/short зеркальны.
2. **R/R и TP:** `RiskManager.ValidateSignal` проверяет net R/R после комиссий; `CalculateTakeProfit` решает целевой net R/R. `TradingEngine` после фактического филла пересчитывает TP по net R/R, а не по gross-ценам.
3. **Сайзинг:** `PositionSizer` делит риск-бюджет на net risk per unit (комиссии входа+выхода), а ограничение по балансу учитывает комиссию входа.
4. **Безубыток:** `RiskManager.IsBreakEvenActivated` активируется при net прибыли ≥ `BreakEvenActivationRR` × начальный net risk. `PositionMonitor` подтягивает стоп не ниже break-even + буфер (для short — не выше), не двигает стоп назад и не ставит его за текущую цену.
5. **Выход с cap проскальзывания:** `IBinanceRestClient.PlaceIocOrderAsync` / `BinanceRestClient` отправляют LIMIT IOC. `OrderExecutor.ClosePositionAsync` для обычного выхода считает худшую допустимую цену от best bid/ask (fallback current/entry), округляет к tick и отправляет IOC. Аварийные причины (`Stop-Loss`, `NoExchangeProtection`, `KillSwitch*`) при `EmergencyMarketExitBypassSlippageCap=true` идут MARKET без cap — приоритет закрытия риска.
6. **Защита остатка:** если IOC/MARKET не исполнился после снятия OCO, executor восстанавливает защиту; при частичном закрытии `TradingEngine` уменьшает позицию, пропорционально списывает комиссию входа и сразу ставит защиту на остаток.
7. **История/метрики:** `TradeRecord.NetPnL = PnL - Commission`; `MetricsCollector.TotalPnL` и логи закрытия используют net. Частичное закрытие больше не обнуляет всю комиссию входа.

| Действие | Файлы |
|---|---|
| Созданы до трекера | `AITradingSystem.Core/RiskManagement/TradeCostModel.cs`, `AITradingSystem.Tests/TradeCostModelTests.cs` |
| Изменены до трекера | `AITradingSystem.Common/Configuration/TradingConfig.cs`, `AITradingSystem.Core/RiskManagement/PositionSizer.cs`, `AITradingSystem.Core/RiskManagement/RiskManager.cs`, `AITradingSystem.Core/Models/Trading/TradeRecord.cs`, `AITradingSystem.Data/Binance/IBinanceRestClient.cs`, `AITradingSystem.Data/Binance/BinanceRestClient.cs`, `AITradingSystem.Services/Trading/IOrderExecutor.cs`, `AITradingSystem.Services/Trading/OrderExecutor.cs`, `AITradingSystem.Services/Trading/TradingEngine.cs`, `AITradingSystem.Services/Trading/PositionMonitor.cs`, `AITradingSystem.Services/Monitoring/MetricsCollector.cs`, `AITradingSystem.UI/appsettings.json`, `AITradingSystem.Tests/RiskTests.cs` |
| Изменён сразу после кода | `SECUPD.md` (P1.2 → ✅, сводка P1 = 4 ✅ / 1 🟡 / 3 ⬜, очередь: осталось 6 шагов, этот журнал) |

**Новые ключи конфигурации (`Trading`):** `MakerCommissionRate=0.001`, `TakerCommissionRate=0.001`, `MarketExitSlippageGuardEnabled=true`, `MarketExitMaxSlippage=0.003`, `EmergencyMarketExitBypassSlippageCap=true`, `BreakEvenEnabled=true`, `BreakEvenActivationRR=1.0`, `BreakEvenBufferPercent=0.05`.

**Побочные эффекты интерфейса:** `IBinanceRestClient` получил `PlaceIocOrderAsync`; `IOrderExecutor.ClosePositionAsync` получил опциональные `currentOrderBook` и `emergency`; `RiskManager` получил `CalculateNetRiskReward`, `CalculateBreakEvenPrice`, `IsBreakEvenActivated`; `TradeRecord` получил `NetPnL`. Старые вызовы с двумя аргументами совместимы. Поведенческое изменение: сделки с gross R/R ≥ порога, но net R/R < порога, отклоняются; обычный выход может исполниться частично или не исполниться из-за cap, остаток защищается и дозакрыется контуром. Тесты добавлены (10 кейсов), но не запускались. Сборки/компиляции и коммит не выполнялись; zip не пересобирался.

### ✅ P1.3 Округления tick/step/minNotional для всех ног
> Округления tick/step/minNotional для всех ног включая OCO; dust; аудит частичного филла после отмены остатка.

**Выполнено 24.11.2026 (проход 20, шаг №10 очереди).** Закрыт остаток пункта: dust-обработка, аудит «отменённый остаток vs фактический филл» и метрика «хвоста» защиты. Округления ног были готовы ранее: входная нога — с исходного аудита, стоп/тейк-ноги OCO — `ProtectiveOrderPricing.TryBuild` (проход 2, вместе с P0.1).

**Что сделано:**
1. **Чистый `DustPolicy` (Core, без I/O):** выбор маршрута закрытия `PlanClose` → `IocLimit | Market | DustMarketSell | Reject` по фильтрам символа (stepSize/minQty/minNotional) и стороне. Dust — остаток, который округляется в ноль (< stepSize) либо ниже minQty/minNotional: обычный LIMIT/MARKET ордер биржа отклонит.
2. **Dust-закрытие вместо зависания:** SELL-остаток (закрытие long) ниже фильтров уходит **MARKET точного объёма** под full-balance exemption Binance (продажа всего остатка базового актива проходит мимо LOT_SIZE/MIN_NOTIONAL) — раньше `quantity <= 0` после округления просто отклонял закрытие, и dust-позиция висела вечно в цикле fail-closed. Точный (не округлённый) объём обязателен: exemption срабатывает только при продаже всего остатка. Переключатель `Trading:DustMarketCloseEnabled=true`; при выключении — отказ с причиной «нужен ручной аудит».
3. **BUY-dust отклоняется честно:** full-balance exemption на споте только для SELL; dust-покупка (покрытие шорта) невозможна — Reject с причиной в лог/метрики (на споте шорты и так запрещены `AllowShort=false`).
4. **Dust-маршрут согласован с P1.2:** для dust IOC-cap не применяется (LIMIT невозможен физически), cap-ветка P1.2 для нормальных объёмов не тронута; аварийные выходы не менялись. При отказе dust-закрытия защита НЕ восстанавливается (ноги OCO не пройдут minQty/minNotional — попытка восстановления лишь шумела бы в логе), факт уходит в `DustCloseFailures` и `DUST-*`/`close` логи; отказ биржи (-2010 и т.п.) тоже регистрируется метрикой через catch-ветки.
5. **Аудит остатка после отмены:** `WaitForFillAsync` после cancel + финального GET вызывает `AuditCancelResidual` — классификация `DustPolicy.ClassifyCancelResidual(status, executed, original)`: терминальный статус и `executed + residual == original` → чистый аудит (`CANCEL-AUDIT`, счётчик `CancelAudits`, суммарный отменённый остаток); статус НЕ терминальный после отмены или отрицательный остаток → расхождение (`CancelAuditMismatches`, лог `cancel-audit`): остаток мог остаться живым на бирже и продолжить наливаться — факт для реконсиляции P0.5. Аудит работает для входов и закрытий (общий путь ожидания филла). Прежний лог «Остаток отменён» больше не утверждает отмену при не-терминальном статусе — выводит фактический статус.
6. **«Хвост» защиты — отдельная метрика:** после `TryBuild` в `PlaceProtectionAsync` считается `DustPolicy.ProtectionTail(position.Quantity, prices.Quantity)` — объём OCO после округления к stepSize может быть меньше объёма позиции. Хвост > 0 → `PROTECTION-TAIL` в лог решений + счётчики `ProtectionTails`/`ProtectionTailQuantity`. После срабатывания ноги хвост становится dust-остатком и закрывается маршрутом п.2 — цепочка «округление защиты → частичный филл ноги → dust-закрытие» замкнута.
7. **Метрики:** `MetricsCollector` — `CancelAudits`, `CancelAuditMismatches`, `TotalCanceledResidual`, `DustMarketCloses`, `DustCloseFailures`, `ProtectionTails`, `ProtectionTailQuantity`; сводка `GetSummary()` дополнена (дашборд-панель не добавлялась — счётчики видны в сводке, как было принято для P1.5 до прохода 17).

| Действие | Файлы |
|---|---|
| Созданы до трекера | `AITradingSystem.Core/Models/Trading/DustPolicy.cs`, `AITradingSystem.Tests/DustPolicyTests.cs` |
| Изменены до трекера | `AITradingSystem.Services/Trading/OrderExecutor.cs`, `AITradingSystem.Services/Monitoring/MetricsCollector.cs`, `AITradingSystem.Common/Configuration/TradingConfig.cs`, `AITradingSystem.UI/appsettings.json` |
| Изменён сразу после кода | `SECUPD.md` (P1.3 → ✅, сводка P1 = 6 ✅ / 0 🟡 / 2 ⬜, очередь: осталось 4 шага, этот журнал) |

**Новый ключ конфигурации (`Trading`):** `DustMarketCloseEnabled=true`.

**Побочные эффекты интерфейса:** новых публичных сигнатур в executor нет; `DustPolicy` — новый статический класс Core (`ClosePlan`, `CancelAudit`, `DustCloseRoute`). `ProtectiveOrderPricing.TryBuild`, `BinanceRestClient` и `TradingEngine` не менялись (оценка аудита перечисляла их как кандидатов — округления ног и `SymbolPrecision` уже были достаточны). Поведенческие изменения: dust-остаток SELL закрывается MARKET вместо бесконечного отклонения; после отмены неисполненной части всегда пишется аудит (чистый или расхождение). Тесты добавлены (17 кейсов: маршруты PlanClose incl. dust/BUY/отключённый dust/неизвестные фильтры, классификация аудита отмены, хвост защиты + интеграция с TryBuild, дефолт конфига), но не запускались. Сборки/компиляции и коммит не выполнялись; zip не пересобирался.

**Критерий приёмки:** выполнен на уровне механики — округления всех ног (вход/закрытие/OCO) на месте, dust имеет определённый маршрут закрытия и метрики, расхождение «отменённый остаток vs фактический филл» детектируется. Живой прогон с реальным dust на бирже не проводился (сборки/запуски запрещены).

**Остаточные ограничения (не входят в P1.3):**
- расхождение аудита отмены (mismatch) только регистрируется — фактический подхват «живого» остатка делает реконсиляция P0.5;
- dust-остатки, которые биржа всё же отклонила (например, учёт расходится с free-балансом), остаются открытыми с локальным SL и ждут ручного аудита/реконсиляции — авто-сверки с балансом счёта в этом шаге нет;
- протухшие данные после реконнекта WS блокируются — **P1.4 выполнен проходом 21**.

### ✅ P1.4 Целостность данных после реконнекта WS
> Целостность данных после реконнекта WS: REST-дозагрузка свечей, детект разрывов, повторный прогрев; запрет входов на протухших данных.

**Выполнено 24.11.2026 (проход 21, шаг №11 очереди).** Реконнект WS больше не оставляет индикаторы с дырами и не разрешает входы на протухших данных.

**Что сделано:**
1. **События рыночного WS:** `IBinanceWebSocketClient`/`BinanceWebSocketClient` получили `OnDisconnected` и `OnReconnected`. Событие потери соединения приходит до задержки переподключения, событие восстановления — сразу после успешного reconnect. Движок сбрасывает кэш стакана и помечает все активные символы как требующие восстановления.
2. **Детект разрывов свечей:** новый `CandleSchedule` парсит интервал Binance и считает сетку `OpenTime`; `IndicatorCalculator.TryAppendClosed` возвращает `Accepted | Duplicate | OutOfOrder | Gap`. Разрыв не добавляется в индикаторы: `MarketAnalyzer` помечает символ `candle-gap` и блокирует снимок/вход до REST-восстановления.
3. **REST-дозагрузка и повторный прогрев:** `MarketAnalyzer.TryReloadHistory` атомарно проверяет закрытую REST-историю на достаточность, непрерывность, отсутствие дублей и свежесть последней закрытой свечи, затем пересобирает индикаторы. `TradingEngine.RecoverMarketDataAsync` выполняет дозагрузку для символов в очереди восстановления, обновляет текущую свечу/ATR и снимает блокировку только после успешного прогрева.
4. **Fail-closed на протухших данных:** `MarketAnalyzer.IsDataFresh` требует отсутствия восстановления, прогрева индикаторов, свежего kline и свежего стакана (`Trading:MarketDataMaxAgeSeconds`, по умолчанию 30 с). Гейт стоит до AI и повторно перед отправкой ордера; отказ пишется как `Rejected/market-data` и в метрику. Watchdog основного цикла также ставит символ на восстановление при `stale-kline`, `stale-book`, `no-kline`, `warmup-incomplete`, `candle-gap`.
5. **Наблюдаемость и конфигурация:** метрики `MarketDataDisconnects/Reconnects/Gaps/Recoveries/RecoveryFailures/StaleEntryBlocks` и сводка; ключи `Trading:MarketDataMaxAgeSeconds=30`, `Trading:MarketDataRecoveryRetrySeconds=15` с валидацией.

| Действие | Файлы |
|---|---|
| Созданы до трекера | `AITradingSystem.Core/Models/MarketData/CandleSchedule.cs`, `AITradingSystem.Tests/MarketDataIntegrityTests.cs` |
| Изменены до трекера | `AITradingSystem.Core/Indicators/IndicatorCalculator.cs`, `AITradingSystem.Services/Analysis/MarketAnalyzer.cs`, `AITradingSystem.Data/Binance/IBinanceWebSocketClient.cs`, `AITradingSystem.Data/Binance/BinanceWebSocketClient.cs`, `AITradingSystem.Services/Trading/TradingEngine.cs`, `AITradingSystem.Common/Configuration/TradingConfig.cs`, `AITradingSystem.UI/appsettings.json`, `AITradingSystem.Services/Monitoring/MetricsCollector.cs` |
| Изменён сразу после кода | `SECUPD.md` (P1.4 → ✅, сводка P1 = 7 ✅ / 0 🟡 / 1 ⬜, очередь: осталось 3 шага, этот журнал) |

**Критерий приёмки:** выполнен на уровне механики — после реконнекта WS входы запрещены до непрерывной REST-истории, повторного прогрева и свежего стакана; разрывы закрытых свечей детектируются и не попадают в индикаторы. Живой прогон с реальным обрывом WS не проводился (сборки/запуски запрещены).

**Остаточные ограничения (не входят в P1.4):**
- если WS подключён, но стакан не приходит (`no-book`/`stale-book`), вход блокируется; REST-дозагрузка свечей сама по себе стакан не заменяет;
- сопровождение открытых позиций продолжает использовать `PositionMonitor` и биржевые OCO; протухание рыночных данных блокирует новые входы и AI-снимок, но не отменяет защитные ордера;
- ~~подбор порога `MinConfidence` на чистых данных — **P2.4**~~ **закрыто проходом 22 (P2.4 ✅)**: порог подбирается по expectancy после издержек на истории, авто-применение выключено по умолчанию.

**Файлы:** `AITradingSystem.Data/Binance/BinanceWebSocketClient.cs`, `AITradingSystem.Services/Analysis/MarketAnalyzer.cs`, `AITradingSystem.Core/Indicators/IndicatorCalculator.cs`, `AITradingSystem.Services/Trading/TradingEngine.cs`, тесты.

### ✅ P1.5 Фильтр ликвидности
> Фильтр ликвидности: спред/глубина/объём перед входом (BTTC/STRAX/FLR); метрика post-only-отказов.

**Выполнено 24.11.2026 (проход 16, шаг №6 очереди).** Широкий спред больше не просто логируется: вход блокируется гейтом ликвидности, а post-only-отказы биржи агрегируются в метрику.

**Что сделано:**
1. **Чистый гейт `LiquidityFilter`** (новый файл в Core, без I/O): `Evaluate(orderBook, side, quantity, fallbackPrice, avgCandleQuoteVolume, thresholds)` → pass/fail с категорией `spread | depth | volume | no-book` и деталями для лога. Проверки: средний объём закрытой свечи в котировке ≥ минимума; спред (ask−bid)/mid ≤ порога; глубина **стороны исполнения** (BUY съедает asks, SELL — bids) ≥ max(абсолютный минимум, множитель × нотариал ордера). Нотариал считается по исполнимой цене стакана (ask для BUY / bid для SELL), не по закрытию свечи.
2. **Fail-closed:** нет стакана (или пустой) → вход не разрешён, категория `no-book`. Протухшесть данных после реконнекта WS закрыта отдельным гейтом P1.4 (проход 21), здесь не смешивается.
3. **Гейт в движке:** `TradingEngine.ProcessSymbolAsync` шаг 7b — после сайзинга (объём уже финальный, с учётом SYMBOL-CAP/SIZE-CAP), перед `ExecuteSignalAsync`. Отказ пишется в воронку как `Rejected/liquidity [категория] детали` и в метрику. Работает и в paper-режиме — «бумажная» статистика не должна врать там, где реальная биржа вход не пропустил бы. Логирование аномального спреда >0.5% в `OnOrderBookReceived` сохранено как есть.
4. **Объём без бага незакрытой свечи:** средний объём = `IndicatorCalculator.VolumeSma20 × текущая цена` (SMA20 закрытых свечей, контракт фикса `volume_ratio` прохода 11); внутриинтервальный WS-объём в оценку не попадает.
5. **Точечные пороги из `symbols.json`:** `SymbolConfig` получил `MaxSpreadPercent?`, `MinDepthQuote?`, `MinCandleQuoteVolume?` (+ валидация); `ConfigLoader` переносит их в `TradingConfig.SymbolLiquidityOverrides`. null-поле — глобальное значение. Для BTTC/STRAX/FLR заданы более строгие пороги: спред ≤0.25%, глубина ≥2000 USDT, объём свечи ≥25000 USDT. Реестр настроек (`SettingsViewModel`) сериализует ту же модель `SymbolsRegistry`, поэтому новые поля переживают сохранение экрана настроек.
6. **Метрика post-only-отказов:** `OrderExecutor` в catch `IsPostOnlyRejection` вызывает `MetricsCollector.RecordPostOnlyRejection()` (раньше признак только писался в лог сделки). Конструктор получил **опциональный** `MetricsCollector? metrics = null` — DI разрешает автоматически (синглтон уже зарегистрирован), старые вызовы совместимы.
7. **Метрики ликвидности:** `LiquidityRejections` всего + по причинам (`Spread/Depth/Volume/NoBook`), `PostOnlyRejections`; сводка `GetSummary()` дополнена.
8. **Конфигурация:** `LiquidityFilterEnabled=true`, `MaxEntrySpreadPercent=0.35`, `MinEntryDepthQuote=1000`, `MinDepthNotionalRatio=5`, `LiquidityDepthLevels=10` (≤20 — partial-depth stream отдаёт 20 уровней), `MinCandleQuoteVolume=15000` (0 — проверка выключена); валидация диапазонов в `TradingConfig.Validate`, ключи в `appsettings.json`.

**Изменённые файлы:**
- **новые:** `AITradingSystem.Core/Models/MarketData/LiquidityFilter.cs` (`LiquidityThresholds`, `LiquidityCheckResult`, гейт), `AITradingSystem.Tests/LiquidityFilterTests.cs`
- `AITradingSystem.Common/Models/Config/SymbolConfig.cs` (3 точечных порога, `HasLiquidityOverrides`/`ToLiquidityOverride`, валидация; новый класс `SymbolLiquidityOverride`)
- `AITradingSystem.Common/Configuration/TradingConfig.cs` (6 ключей + `SymbolLiquidityOverrides` + валидация)
- `AITradingSystem.Common/Configuration/ConfigLoader.cs` (перенос порогов из реестра в конфиг)
- `AITradingSystem.Services/Trading/TradingEngine.cs` (шаг 7b + `PassesLiquidityFilter`)
- `AITradingSystem.Services/Trading/OrderExecutor.cs` (опциональный `MetricsCollector`, запись post-only-отказа)
- `AITradingSystem.Services/Monitoring/MetricsCollector.cs` (`RecordLiquidityRejection`, `RecordPostOnlyRejection`, счётчики, сводка)
- `AITradingSystem.UI/appsettings.json` (6 ключей)
- `AITradingSystem.UI/symbols.json` (пороги BTTC/STRAX/FLR, Version 1.2)

Тесты добавлены (17 кейсов: спред, глубина стороны входа, абсолютный пол глубины, объём, fail-closed без стакана, точечный порог строже глобального, roundtrip/валидация `SymbolConfig`, дефолты и валидация конфига, метрики по причинам и post-only), но не запускались; сборки/компиляции не выполнялись.

**Критерий приёмки:** выполнен на уровне механики — вход блокируется по спреду/глубине/объёму с точечными порогами для тонких пар, post-only-отказы агрегируются. Живой прогон не проводился (сборки/запуски запрещены).

**Остаточные ограничения (не входят в P1.5):**
- протухшие данные после реконнекта WS блокируют входы — **P1.4 выполнен проходом 21**;
- slippage-модель paper-филла по стакану — **P1.1** (гейт только отказывает, не моделирует проскальзывание);
- ~~панель дашборда для новых метрик не добавлялась — счётчики видны в сводке метрик; UI-панель может быть добавлена с P1.6~~ **закрыто проходом 17**: счётчики ликвидности/post-only выведены в панель «Бюджет весов REST / rate-limit (P1.6) + ликвидность (P1.5)».

### ✅ P1.6 Обработка 429/-1003 и бюджет весов
> Обработка 429/-1003 c `Retry-After`, бюджет весов запросов.

**Выполнено 24.11.2026 (проход 17, шаг №7 очереди).** Биржевые лимиты больше не обрабатываются только ретраем отдельного запроса: клиент ведёт бюджет веса минутного окна, притормаживает polling при приближении к лимиту и приостанавливает ВСЕ запросы при 429/-1003/418.

**Что сделано:**
1. **Новый `RequestWeightBudget`** (Data, чистый потокобезопасный класс, время передаётся параметром — тестируется без сети): окно минуты аппроксимируется от первого наблюдения (консервативно — локальное окно начинается не раньше серверного); израсходованный вес = максимум сэмплов заголовка `X-MBX-USED-WEIGHT-1M` в окне + локальные оценки запросов после последнего сэмпла (`EstimateWeight` — таблица весов spot-эндпоинтов: klines 2, exchangeInfo 20, account 20, openOrders 6/80, GET order 4, POST/DELETE order 1 и т.д.). Свежий заголовок замещает локальные оценки (без двойного учёта); ролловер окна обнуляет вес.
2. **Двухуровневое притормаживание в `SendRequestAsync`:** (а) **soft-limit** — вес ≥ `RequestWeightSoftLimitPercent` (80% от `RequestWeightLimitPerMinute=6000`) → **некритичные** запросы (polling статусов, свечи, баланс, openOrders) ждут конца окна + guard 0.25 с; **критичные** — POST/DELETE `/api/v3/order*` (постановка/снятие ордеров и OCO) — проходят без задержки: защита/закрытие не стоят за исчерпанным бюджетом polling'а (приоритеты P0.3 сохранены); (б) **бан** — действует для ВСЕХ запросов, включая критичные: отправка во время бана грозит IP-баном 418. Ожидание выполняется **до** построения timestamp подписи (не создаёт -1021).
3. **Явная ветка 429/-1003/418:** backoff из `Retry-After` (легаси-имя заголовка веса тоже читается); для 418 фактическая длительность IP-бана из `Retry-After` (потолок регистрации 1 час, обычный ретрайный потолок 120 с не обрезает бан). Бан регистрируется в бюджете — параллельные запросы ждут его окончания, а не бьют по бирже независимыми ретраями; ретрай текущего запроса — не раньше окончания бана (бан мог быть продлён параллельным ответом). Классификация транзиентности не менялась: 429/418/-1003 ретраятся с тем же `clientOrderId` (P0.3), идемпотентность не тронута.
4. **Метрики:** `MetricsCollector` реализует новый интерфейс `IRestRateLimitMetrics` (объявлен в Data — Data не ссылается на Services): вес тек./макс./лимит, счётчики 429 / -1003 / 418, последний backoff, число/макс./суммарное время притормаживаний; сводка `GetSummary()` дополнена. Конструктор `BinanceRestClient` получил **опциональный** `IRestRateLimitMetrics? rateLimitMetrics = null` (DI разрешает автоматически; старые вызовы совместимы).
5. **Дашборд:** панель «Бюджет весов REST / rate-limit (P1.6) + ликвидность (P1.5)» — вес минуты, счётчики лимитов/банов, притормаживания; жёлтый алерт при 429/-1003/418 и при весе ≥80% лимита. В ту же панель выведены отложенные из P1.5 счётчики ликвидности/post-only (остаток P1.5 закрыт).
6. **Конфигурация:** `Binance:RequestWeightLimitPerMinute=6000`, `Binance:RequestWeightSoftLimitPercent=80` (валидация [1; 100]), `Binance:WeightThrottleEnabled=true` (false — только учёт/метрики и жёсткие баны; баны действуют всегда). Валидация лимита [100; 1000000] в `BinanceConfig.Validate`, ключи в `appsettings.json`.
7. **DI:** `App.xaml.cs` — `IRestRateLimitMetrics` → тот же синглтон `MetricsCollector`.

**Изменённые файлы:**
- **новые:** `AITradingSystem.Data/Binance/RequestWeightBudget.cs` (бюджет + `IRestRateLimitMetrics`), `AITradingSystem.Tests/RequestWeightBudgetTests.cs`
- `AITradingSystem.Data/Binance/BinanceRestClient.cs` (бюджет в ctor, троттлинг перед отправкой, локальные оценки, чтение заголовка веса, явная ветка 429/-1003/418 с баном, `TryReadUsedWeight`/`TryReadRetryAfterSeconds`, свойство `WeightBudget`)
- `AITradingSystem.Common/Configuration/BinanceConfig.cs` (3 ключа + валидация)
- `AITradingSystem.Services/Monitoring/MetricsCollector.cs` (реализация `IRestRateLimitMetrics`, счётчики, сводка)
- `AITradingSystem.UI/appsettings.json` (3 ключа в секции Binance)
- `AITradingSystem.UI/App.xaml.cs` (DI-маппинг `IRestRateLimitMetrics`)
- `AITradingSystem.UI/ViewModels/DashboardViewModel.cs` (`RateLimitText`/`RateLimitAlertText`, `UpdateRateLimits`)
- `AITradingSystem.UI/Views/DashboardView.xaml` (панель)

Тесты добавлены (23 кейса: учёт заголовка/локальных оценок без двойного счёта, ролловер окна, soft-limit только для некритичных, бан для всех и продление максимумом, отключённый soft-throttle не отключает баны, таблица весов, классификация критичных операций, разбор заголовков, дефолты/валидация конфига, метрики), но не запускались; сборки/компиляции не выполнялись.

**Критерий приёмки:** выполнен на уровне механики — `Retry-After` читается и останавливает все запросы, вес учитывается по заголовку биржи и локальным оценкам, polling притормаживается у лимита, ордера/защита не задерживаются soft-limit'ом. Живой прогон с реальными 429 не проводился (сборки/запуски запрещены).

**Остаточные ограничения (не входят в P1.6):**
- окно минуты аппроксимируется локально (без привязки к серверной минуте) — консервативно в сторону более раннего троттлинга; лёгкий недоучёт веса запросов «в полёте» между сэмплами компенсируется запасом soft-limit 20%;
- лимиты числа ордеров (`X-MBX-ORDER-COUNT-*`) отдельно не бюджетируются — очередь исполнения P0.3 уже ограничивает параллелизм отправок;
- WS-переподключения весов не расходуют; REST-дозагрузка свечей после реконнекта (P1.4, проход 21) идёт через этот же бюджет автоматически.

### ✅ P1.7 Портфельный риск
> Портфельный риск: кап суммарной экспозиции и корреляции (12 альтов ≈ ставка на BTC), дневной лимит на символ, пауза после серии убытков.

**Выполнено 24.11.2026 (проход 23, шаг №13 очереди).** Поверх поштучных лимитов появился портфельный контур: суммарная экспозиция, коррелированный BTC-кластер, дневной убыток по символу и пауза после серии убытков.

**Что сделано:**
1. **Новый `PortfolioRiskManager` (Core, без I/O):** считает экспозицию открытых позиций по marked price (`CurrentPrice`, fallback `EntryPrice`), сравнивает с `MaxPortfolioExposurePercent` от equity и возвращает допустимый дополнительный нотариал. Движок урезает объём до капа, а не только отказывает.
2. **Корреляция BTC-кластера:** Pearson по лог-доходностям закрытых свечей, выровненным по `OpenTime`. Бенчмарк `BTCUSDT` держится свежим отдельным REST-обновлением, даже если не входит в торговые пары. Кластер = кандидат + открытые позиции, сильно коррелированные с кандидатом или входящие в тот же BTC-кластер. Нет истории/бенчмарка/дисперсии → fail-closed отказ. Кап кластера — `MaxCorrelatedExposurePercent` от equity.
3. **Дневной лимит символа:** реализованный `NetPnL` закрытых сделок за риск-день (часы биржи) сравнивается с `SymbolDailyLossLimitPercent` от `DayStartBalance`. Дешёвый гейт стоит до AI; полный — перед отправкой.
4. **Пауза после серии убытков:** `LossStreakPauseThreshold` подряд убыточных закрытий взводит паузу входов на `LossStreakPauseMinutes`. Прибыль/безубыток прерывает серию; истёкшая пауза сбрасывает серию. Состояние восстанавливается из полной истории (`GetAllTradesAsync`), дедуп по `TradeRecord.Id`.
5. **Интеграция:** `RiskManager.ValidateSignal` вызывает портфельный гейт паузы/дня; `TradingEngine` применяет полный кап после сайзинга и повторно перед исполнением. Equity = баланс бота + нереализованный PnL открытых позиций. Сопровождение/закрытие позиций не блокируется портфельным гейтом.
6. **Наблюдаемость:** метрики экспозиции/equity/корр. кластера/max ρ/отказов/серии/паузы + сводка; дашборд-панель «Портфельный риск (P1.7)».
7. **История сделок:** `JsonTradeHistoryRepository.SaveTradesAsync` переведён на атомарную запись tmp+rename — история питает восстановление портфельного риска после рестарта.

| Действие | Файлы |
|---|---|
| Созданы до трекера | `AITradingSystem.Core/RiskManagement/PortfolioRiskManager.cs`, `AITradingSystem.Tests/PortfolioRiskTests.cs` |
| Изменены до трекера | `AITradingSystem.Common/Configuration/TradingConfig.cs`, `AITradingSystem.Core/Indicators/IndicatorCalculator.cs`, `AITradingSystem.Services/Analysis/MarketAnalyzer.cs`, `AITradingSystem.Core/RiskManagement/DrawdownTracker.cs`, `AITradingSystem.Core/RiskManagement/RiskManager.cs`, `AITradingSystem.Data/Storage/JsonTradeHistoryRepository.cs`, `AITradingSystem.Services/Trading/TradingEngine.cs`, `AITradingSystem.Services/Monitoring/MetricsCollector.cs`, `AITradingSystem.UI/App.xaml.cs`, `AITradingSystem.UI/appsettings.json`, `AITradingSystem.UI/ViewModels/DashboardViewModel.cs`, `AITradingSystem.UI/Views/DashboardView.xaml` |
| Изменён сразу после кода | `SECUPD.md` (P1.7 → ✅, сводка P1 = 8 ✅ / 0 🟡 / 0 ⬜, очередь: остался 1 шаг, этот журнал) |

**Новые ключи конфигурации (`Trading`):** `PortfolioRiskEnabled=true`, `MaxPortfolioExposurePercent=60`, `MaxCorrelatedExposurePercent=35`, `CorrelationRiskEnabled=true`, `CorrelationBenchmarkSymbol="BTCUSDT"`, `CorrelationLookbackCandles=96`, `MinCorrelationSamples=48`, `MaxPairwiseCorrelation=0.80`, `SymbolDailyLossLimitPercent=1`, `LossStreakPauseThreshold=3`, `LossStreakPauseMinutes=60` (валидация диапазонов и согласованности коридоров в `TradingConfig.Validate`).

**Побочные эффекты интерфейса:** конструкторы `RiskManager`, `TradingEngine`, `DashboardViewModel` получили `PortfolioRiskManager` (DI-регистрация добавлена); `IndicatorCalculator` хранит окно закрытых свечей и отдаёт `GetClosedCandles`; `MarketAnalyzer.GetClosedCandles` потокобезопасен; `DrawdownTracker.DayStartBalance` публичный; `PortfolioRiskManager.LogReturns/Pearson` публичные статические helpers. Поведенческие изменения: входы могут урезаться/отклоняться по экспозиции, корреляции, дневному лимиту символа и серии убытков; при включённой корреляции отсутствие свежей истории бенчмарка/символа блокирует вход. Тесты добавлены (12 кейсов), но не запускались. Сборки/компиляции и коммит не выполнялись; zip не пересобирался.

**Критерий приёмки:** выполнен на уровне механики — суммарная экспозиция и коррелированный кластер каппятся, дневной лимит символа и пауза после серии убытков работают и переживают рестарт через историю сделок. Живой прогон с реальной корреляцией/убытками не проводился (сборки/запуски запрещены).

**Остаточные ограничения (не входят в P1.7):**
- корреляция считается только по закрытым свечам текущего интервала; внутриинтервальные режимы/хвостовые зависимости не моделируются;
- портфельный гейт блокирует только новые входы; принудительное сокращение уже открытых позиций при превышении капа из-за роста цен не делается (кроме существующих SL/OCO/kill-switch);
- equity для капа — баланс бота + нереализованный PnL открытых позиций, не полная переоценка всех активов счёта (как и в P0.4/P0.6).

### ✅ P1.8 Мониторинг дрейфа часов
> Мониторинг дрейфа часов с алертом.

**Выполнено 24.11.2026 (проход 12, шаг №2 очереди).** Дрейф локальных часов относительно Binance теперь измеряется периодически, отображается в метрика/дашборде и алертится через существующий `TelegramAlertService`.

**Что сделано:**
1. **Независимый контур:** новый `ClockDriftMonitor` стартует/останавливается вместе с `TradingEngine`, работает отдельно от цикла анализа и подписанных запросов, не требует API-ключей и применим в Paper/Live. Интервал и порог берутся из `TradingConfig`.
2. **Измерение с компенсацией RTT:** `ClockDriftSample.Measure` берёт середину окна `localBefore/localAfter`; дрейф = `serverTime - midpoint`, RTT = `localAfter - localBefore`. Односторонняя сетевая задержка не принимается за дрейф часов.
3. **Алерты без спама:** состояние `Normal/Warning/Unavailable`; Telegram отправляется только при переходе в предупреждение, восстановлении или недоступности проверки. Повторные одинаковые состояния не алертятся каждый интервал. Логи: `CLOCK-DRIFT`, `CLOCK-DRIFT-OK`, `CLOCK-DRIFT-CHECK-FAILED`.
4. **Метрики:** `MetricsCollector` хранит число проверок/ошибок/алертов, последний дрейф и RTT, максимальный абсолютный дрейф, флаг предупреждения, время последней проверки и текст ошибки; сводка метрик дополнена.
5. **Дашборд:** панель «Дрейф часов относительно Binance (P1.8)» показывает текущий дрейф, RTT, max |дрейф|, порог, ошибки/алерты и предупреждение о возможных отказах подписи `-1021`.
6. **Конфигурация:** `ClockDriftMonitorEnabled=true`, `ClockDriftCheckIntervalSeconds=60`, `ClockDriftWarningThresholdMs=1000`; валидация диапазонов добавлена в `TradingConfig.Validate` и ключи вынесены в `appsettings.json`.
7. **Жизненный цикл:** DI-регистрация `ClockDriftMonitor`; остановка при штатном stop и при откате неудачного старта движка.

**Изменённые файлы:**
- **новый:** `AITradingSystem.Services/Monitoring/ClockDriftMonitor.cs`
- `AITradingSystem.Services/Monitoring/MetricsCollector.cs`
- `AITradingSystem.Common/Configuration/TradingConfig.cs`
- `AITradingSystem.UI/appsettings.json`
- `AITradingSystem.Services/Trading/TradingEngine.cs`
- `AITradingSystem.UI/App.xaml.cs`
- `AITradingSystem.UI/ViewModels/DashboardViewModel.cs`
- `AITradingSystem.UI/Views/DashboardView.xaml`
- **новый:** `AITradingSystem.Tests/ClockDriftTests.cs`

`BinanceRestClient.GetServerTimeAsync` и `TelegramAlertService.SendAlertAsync` переиспользованы без изменения. Сборки/компиляции и запуск тестов не выполнялись; тесты добавлены, но не запускались.

**Критерий приёмки:** выполнен на уровне механики — дрейф измеряется, алертится и виден в метриках/дашборде. Живой прогон с реальным Telegram/Binance не проводился.

---

## P2 — 🆕 сигнал и HOLD: убрать тройную осторожность

### ✅ P2.1 Диагностика по логам CALIBRATE/Rejected
> 🆕 Диагностика по логам CALIBRATE/Rejected: посчитать, где умирают сигналы — модель сама HOLD / BUY режется порогом / post-only не налился. Лечить причину, а не всё сразу.

**Выполнено 24.11.2026** по логу `trades-20261124.txt` (29 решений, 11 символов, 02:14–02:38).
**Артефакт:** [`docs/signal-funnel-20261124.md`](docs/signal-funnel-20261124.md) (новый файл). Исходный лог: `uploads/trades-20261124.txt` (вне репозитория).
**Изменённые файлы кода:** нет — пункт аналитический.

**Вывод (куда лечить):** все 29 сигналов умерли **на выходе модели**, а не на пороге и не на исполнении: `model_conf` avg **0.157**, median **0.10**, 16/29 залипли на нижней отсечке 0.10; `engine_prior` max **0.42**; `final` max **0.35** при пороге 0.55. Ни одного `Rejected`/ордера в логе нет — до исполнения дело не доходит.
Две причины, обе подтверждаются кодом:
1. **Двойной штраф** (P2.2): 28/29 обоснований HOLD ссылаются на слабый объём, 29/29 — на «конфликт» индикаторов, за которые prior уже умножен на ×0.8/×0.6.
2. **🆕 Баг данных, найденный при диагностике:** `volume_ratio` считается по **незакрытой** свече. `OnKlineReceived` кладёт в кэш каждый тик (`TradingEngine.cs:1028–1033`), снапшот берёт `candle.Volume` (`NormalizedIndicatorSnapshot.cs:109`) и делит на SMA20 закрытых свечей (`IndicatorCalculator.cs:44–48`). Отсюда `volume_ratio` 0.005–0.49 одновременно у **всех** 12 пар, включая BTC/ETH, и вечный штраф ×0.8 + рубрика −0.1…−0.2. Это надо чинить **до** перенастройки весов/порогов, иначе подбор пойдёт по испорченным данным.

**✅ Исправление `volume_ratio` выполнено 24.11.2026 (проход 11, шаг №1 очереди).** `IndicatorCalculator` фиксирует `LastClosedVolume` при обновлении закрытой свечой и предоставляет `VolumeRatio = LastClosedVolume / VolumeSma20`. `NormalizedIndicatorSnapshot` использует это значение вместо объёма переданной текущей свечи. Текущая WS-свеча по-прежнему обновляет цену/PnL, но её неполный объём больше не попадает в числитель отношения. Знаменатель сохраняет прежнее определение — SMA20 закрытых свечей, включая последнюю закрытую. При отсутствии данных и после `Reset()` отношение равно нулю. Двойной штраф в рубрике и веса/пороги в этом шаге не менялись — это следующие пункты очереди.

**Изменённые файлы исправления:** `AITradingSystem.Core/Indicators/IndicatorCalculator.cs`, `AITradingSystem.Core/Indicators/NormalizedIndicatorSnapshot.cs`, `AITradingSystem.Services/Trading/TradingEngine.cs` (контракт кэша текущей свечи), `AITradingSystem.Tests/IndicatorTests.cs` (3 теста: частичный тик не меняет отношение, скользящее окно закрытых объёмов, очистка при сбросе). Тесты добавлены, но не запускались; сборки/компиляции не выполнялись.

---

### ✅ P2.2 Убрать двойной штраф в промпте
> 🆕 Убрать двойной штраф в промпте (`PromptBuilder`): рубрика вычитает 0.1–0.2 за слабые ADX/объём, за которые prior уже умножен на 0.6–0.8. Модель должна давать независимую оценку (новости, структура рынка), а не пересчитывать prior.

**Выполнено 24.11.2026 (проход 13, шаг №3 очереди).** Модель больше не пересчитывает prior и не получает второй штраф за те же факторы.

**Что сделано:**
1. **Независимая рубрика:** `PromptBuilder` больше не велит стартовать с `engine_prior_confidence` и вычитать 0.10–0.20 за conflicts / `adx_raw < 20` / `volume_ratio < 0.6` / BB. Новая рубрика стартует с нейтральных 0.50 и корректирует только за независимые качественные факторы: asset-specific news, структура рынка, event risk. Явно запрещено повторно штрафовать за EMA/Supertrend/MACD conflicts, слабый ADX, низкий объём, BB-pinning и низкую/высокую ATR — эти модификаторы уже учтены в prior движка.
2. **Факты вместо аудиторских штрафов в промпте:** `EngineScore` получил `Facts` — нейтральные значения (`ema_align`, `supertrend`, `adx_raw`, `volume_ratio`, `atr_pct`, `bb_pos`, `rsi_raw`, `macd_hist`) без слов «штраф/бонус». `AiRequestContext.EngineFacts` передаёт их в промпт; `EngineReasons` с формулировками штрафов остаётся для лога/аудита и в промпт не попадает.
3. **Pre-score как справка:** блок engine pre-score помечен как context only/reference only; модель не должна использовать его как стартовую точку рубрики. Repair-prompt также требует независимую рубрику без повторного применения штрафов.
4. **Смешение остаётся за движком:** `SignalCalibrator.Calibrate` по-прежнему смешивает уверенность модели и prior по политике; веса/пороги в этом шаге не менялись — это P2.3/P2.4.

**Изменённые файлы:**
- `AITradingSystem.Services/AI/PromptBuilder.cs`
- `AITradingSystem.Services/AI/AiRequestContext.cs`
- `AITradingSystem.Services/Analysis/SignalCalibrator.cs`
- `AITradingSystem.Services/Trading/TradingEngine.cs`
- **новый:** `AITradingSystem.Tests/PromptBuilderTests.cs`

Тесты добавлены, но не запускались; сборки/компиляции не выполнялись.

**Критерий приёмки:** выполнен на уровне механики — промпт требует независимую оценку и не содержит инструкции повторно штрафовать за факторы, уже учтённые в prior. Живой прогон модели не проводился.

### ✅ P2.3 Политика уверенности 0.7 / 0.3
> 🆕 Политика уверенности (`ConfidencePolicy`): уйти от Blend 50/50 — веса 0.7/0.3.

**Выполнено 24.11.2026 (проход 14, шаг №4 очереди).** Blend больше не смешивает 50/50: `final = w·modelConf + (1−w)·prior`, где `w = AI:BlendModelWeight`, по умолчанию **0.7** (модель) / **0.3** (prior движка).

**Что сделано:**
1. **Направление весов обосновано диагностикой P2.1:** «при 0.7/0.3 максимум final = 0.42» достижимо только при весе модели 0.7 (при весе prior 0.7 верхняя граница — 0.378, т.к. prior ≤ 0.42). Это согласуется с P2.2: рубрика модели стала независимой (новости, структура рынка), поэтому её мнение весит больше, а prior — якорь 0.3.
2. **Конфигурируемый вес:** `AIConfig.BlendModelWeight` (decimal, дефолт 0.7, валидация [0; 1], ключ `AI:BlendModelWeight` в `appsettings.json`). Значение 0.5 воспроизводит прежнее поведение 50/50; 0/1 — чистый prior/чистая модель в рамках Blend.
3. **Калибратор:** `SignalCalibrator.Calibrate` получил необязательный параметр `blendModelWeight = 0.7m` (существующие вызовы совместимы), вес зажимается в [0; 1]; ветка Blend считает по весам. В лог `CALIBRATE` добавлено поле `blend_w` для аудита.
4. **Оба вызова в движке** (анализ без позиции и сопровождение позиции) передают `_config.AI.BlendModelWeight`.
5. **Не тронуто (осознанно):** дегенерат HOLD conf≈0.45 по-прежнему уходит в prior при любых весах (расширение детекта — P2.5); отказ инференса остаётся HOLD с confidence 0; `ModelOnly`/`EngineOverride` игнорируют вес; порог `MinConfidence=0.55` не менялся (подбор — P2.4); веса direction-скоринга не менялись (P2.6).

**Замечание из диагностики (остаётся в силе):** на старом логе смена весов сама по себе не даёт входов (максимум final = 0.42 против порога 0.55) — эффект ожидается только вместе с уже выполненными P2.2/фиксом `volume_ratio` и последующими P2.4/P2.5.

**Изменённые файлы:**
- `AITradingSystem.Common/Configuration/AIConfig.cs` (`BlendModelWeight` + валидация, комментарии политики/enum обновлены)
- `AITradingSystem.Services/Analysis/SignalCalibrator.cs` (параметр `blendModelWeight`, взвешенный Blend, `blend_w` в логе CALIBRATE)
- `AITradingSystem.Services/Trading/TradingEngine.cs` (оба вызова `Calibrate` передают вес из конфига)
- `AITradingSystem.UI/appsettings.json` (`AI:BlendModelWeight=0.7`)
- **новый:** `AITradingSystem.Tests/SignalCalibratorTests.cs` (7 тестов: дефолт 0.7/0.3; 0.5 воспроизводит 50/50; края 0/1 и кламп вне диапазона; дегенерат → prior при любом весе; отказ инференса → HOLD; ModelOnly/EngineOverride игнорируют вес; дефолт конфига и валидация)

Тесты добавлены, но не запускались; сборки/компиляции не выполнялись.

**Критерий приёмки:** выполнен на уровне механики — Blend взвешивает 0.7/0.3 по умолчанию, вес конфигурируется и валидируется, поведение дегенерата/отказа/альтернативных политик сохранено. Живой прогон модели не проводился.

### ✅ P2.4 Порог MinConfidence из данных
> 🆕 Порог `MinConfidence` из данных: 0.55 сейчас взято с потолка; подобрать по expectancy после издержек на истории.

**Выполнено 24.11.2026 (проход 22, шаг №12 очереди).** Порог перестал быть числом «с потолка»: он проверяется expectancy после издержек на полной истории закрытых сделок. 0.55 остаётся документированным fallback, когда данных не хватает.

**Что сделано:**
1. **Данные для подбора доживают до истории.** Раньше `TradeRecord` не знал ни уверенности входа, ни принятого риска — подбирать порог было не по чему. Теперь `Position` хранит `SignalConfidence`/`ModelConfidence`/`EngineConfidence`/`ConfidenceSource` (персист в `positions.json`, переживает рестарт), а `TradeRecord` получает их вместе с `InitialStopLoss` на **обоих** путях закрытия: штатный `ClosePositionCoreAsync` и биржевой филл `ApplyExchangeFillAsync` (защитная нога / close из журнала). Сиротские позиции P0.5 остаются без метки (conf=0) и в подбор не попадают.
2. **Чистый `ConfidenceExpectancyAnalyzer` (Core, без I/O).** Для каждой сделки: net = `NetPnL` (комиссии обеих ног уже вычтены, P1.2), риск = `|entry − initialSL| × объём`, R = net / риск. Основная метрика — **средний net R**, а не USDT: 12 альтов торгуются разными нотариалами, средняя прибыль в деньгах смешивала бы размер позиции с качеством сигнала. Строятся корзины уверенности и sweep порогов «сделки с conf ≥ порога».
3. **Правила выбора (fail-safe).** В подбор идут только сделки с меткой уверенности и ненулевым риском (старая история отсеивается и считается отдельно: `UnlabeledTrades`, `NoRiskTrades`). Кандидат значим при выборке ≥ `MinBucketTrades` и expectancy > 0; берётся максимум expectancy, при равенстве — **более высокий** порог. Мало истории (< `MinTrades`) или нет значимых положительных кандидатов → рекомендации нет, порог не меняется. «Понизим порог, чтобы хоть что-то торговало» невозможно by design.
4. **Коридор порога.** Sweep ограничен [`MinConfidenceFloor`=0.45; `MinConfidenceCeiling`=0.80] с шагом 0.05 — подбор не может ни обвалить порог, ни задрать его до бесконечности; верхняя граница всегда присутствует в сетке, даже если шаг её не делит.
5. **Контур `ConfidenceThresholdTuner` (Services/Analysis).** Стартует/гасится вместе с движком (как `ClockDriftMonitor`), работает вне цикла анализа, не ходит на биржу и к модели. Берёт **полную** историю новым `ITradeHistoryRepository.GetAllTradesAsync` (прежний `GetTradesAsync` резал выборку до 100 — на ней подбор был бы фикцией), считает отчёт, пишет лог `CONFIDENCE-TUNE` и метрики.
6. **Авто-применение выключено по умолчанию.** `MinConfidenceAutoTuneEnabled=false`: контур только рекомендует, решение за оператором. При включении порог меняется в пределах коридора, с лог-предупреждением `CONFIDENCE-TUNE-APPLIED` и Telegram-алертом (только на факт изменения, не каждый интервал). Все места движка читают `AI:MinConfidence` вживую, поэтому применённое значение сразу действует и на гейт входа, и на контекст калибратора/AI.
7. **Оффлайн-инструмент `tools/confidence_threshold.py`.** Те же правила на Python: читает `data/trades.json`, печатает корзины и sweep с expectancy R / USDT, рекомендацию и причину; `--json` сохраняет машинный отчёт, флаги `--current/--floor/--ceiling/--step/--min-trades/--min-bucket`. Позволяет подобрать порог по истории без запуска приложения.
8. **Метрики:** `ConfidenceTuneRuns`, `ConfidenceTuneApplications`, `ConfidenceTuneNoRecommendation`, `LastRecommendedMinConfidence`, `LastConfidenceExpectancyR`, `LastConfidenceSampleTrades`, `ActiveMinConfidence`, `LastConfidenceTuneUtc`, `ConfidenceTuneReason` + сводка `GetSummary()`.
9. **Единый fallback.** `CalibrationContext.MinConfidence` больше не хранит собственную копию 0.55 — берёт `ConfidenceExpectancyOptions.FallbackMinConfidence` с комментарием, что рабочее значение приходит из данных.

| Действие | Файлы |
|---|---|
| Созданы до трекера | `AITradingSystem.Core/Models/Trading/ConfidenceExpectancy.cs`, `AITradingSystem.Services/Analysis/ConfidenceThresholdTuner.cs`, `AITradingSystem.Tests/ConfidenceThresholdTests.cs`, `tools/confidence_threshold.py` |
| Изменены до трекера | `AITradingSystem.Core/Models/Trading/TradeRecord.cs`, `AITradingSystem.Core/Models/Trading/Position.cs`, `AITradingSystem.Services/Trading/TradingEngine.cs`, `AITradingSystem.Data/Storage/ITradeHistoryRepository.cs`, `AITradingSystem.Data/Storage/JsonTradeHistoryRepository.cs`, `AITradingSystem.Common/Configuration/AIConfig.cs`, `AITradingSystem.Services/Analysis/SignalCalibrator.cs`, `AITradingSystem.Services/Monitoring/MetricsCollector.cs`, `AITradingSystem.UI/App.xaml.cs`, `AITradingSystem.UI/appsettings.json` |
| Изменён сразу после кода | `SECUPD.md` (P2.4 → ✅, сводка P2 = 5 ✅ / 0 🟡 / 1 ⬜, очередь: осталось 2 шага, этот журнал) |

**Новые ключи конфигурации (`AI`):** `MinConfidenceTuneEnabled=true`, `MinConfidenceAutoTuneEnabled=false`, `MinConfidenceTuneIntervalMinutes=60`, `MinConfidenceFloor=0.45`, `MinConfidenceCeiling=0.80`, `MinConfidenceTuneStep=0.05`, `MinConfidenceTuneMinTrades=40`, `MinConfidenceTuneMinBucketTrades=15` (валидация диапазонов и согласованности коридора/выборки в `AIConfig.Validate`).

**Побочные эффекты интерфейса:** конструктор `TradingEngine` получил параметр `ConfidenceThresholdTuner` (DI-регистрация добавлена); `ITradeHistoryRepository` получил `GetAllTradesAsync`; `TradeRecord`/`Position` получили поля метки уверенности и начального риска (старые JSON-записи читаются: поля отсутствуют → 0 → в подбор не идут). Поведенческие изменения: по умолчанию — только рекомендация в логе/метриках, порог не меняется; при включённом авто-режиме порог может измениться в пределах коридора после разбора истории. Тесты добавлены (24 метода / 25 кейсов с Theory), но не запускались. Сборки/компиляции и коммит не выполнялись; zip не пересобирался.

**Критерий приёмки:** выполнен на уровне механики — порог выводится из expectancy после издержек на истории сделок, а не из константы; выбор защищён требованиями к выборке, коридором и запретом понижать порог при отсутствии положительной expectancy. Подбор на реальном боевом логе не проводился: в снапшоте нет истории сделок с меткой уверенности (метка введена этим проходом), а сборки/запуски запрещены.

**Остаточные ограничения (не входят в P2.4):**
- история, накопленная до прохода 22, не содержит уверенности и начального стопа — подбор начнёт работать по мере накопления новых сделок (или после ручного разбора оффлайн-инструментом по внешним данным);
- панель дашборда для метрик подбора не добавлялась — счётчики видны в сводке метрик (как было принято для P1.5 до прохода 17);
- подбор не учитывает режим рынка/символ (один глобальный порог на все пары); пер-символьные пороги и reliability diagram — **P2.6**;
- веса direction-скоринга не менялись — **P2.6**.

### ✅ P2.5 Расширить детект «дегенерата»
> 🆕 Расширить детект «дегенерата» (`SignalCalibrator`): пересмотр HOLD→вход при любом HOLD с сильным согласованным prior, а не только conf=0.45±0.02.

**Выполнено 24.11.2026 (проход 15, шаг №5 очереди).** Пересмотр HOLD→вход больше не заперт в узком окне conf≈0.45: любой HOLD с сильным согласованным prior пересматривается движком.

**Что сделано:**
1. **Новый триггер `holdOverride`:** в политике Blend при `Action=HOLD`, `prior >= MinConfidence` и явно не-флэт направлении (`Direction > 0.05` для long; `< −0.05` для short **только** при `AllowShort`) движок берёт уверенность из своего prior (`final = prior`, новый `source=Hold→Prior`) и пересматривает HOLD→вход через существующий `TryUpgrade` — цены входа/SL/TP считает математика (entry ± k·ATR, TP по R/R), не модель. Без этого blend с низким conf модели (типичные 0.10 из лога) убивал бы пересмотренный вход на пороге.
2. **Согласованность = явное направление фактов:** «сильный согласованный prior» = prior ≥ порога **и** направление вне флэт-зоны ±0.05, совпадающее со стороной входа; флэт (`|Direction| ≤ 0.05`) пересмотр не запускает — как и раньше.
3. **Guards сохранены полностью:** позиции нет (`!HasPosition`), модель «жива» (`modelConf > 0` — отказ/пустой ответ запрещает пересмотр), `IsInferenceFailure` → HOLD при любой политике, `ModelOnly` не пересматривает никогда, `EngineOverride` работает как прежде.
4. **Узкое окно 0.45±0.02 сохранено с приоритетом:** дегенерат по-прежнему помечается `IsDegenerateConfidence` и получает `source=Degenerate→Prior` (проверка окна идёт первой); новый массовый случай отличим в логе по `Hold→Prior`. Флаг `IsDegenerateConfidence` для прочих HOLD **не** взводится — его смысл («якорный» ответ) не размывается.
5. **Без новых ключей конфигурации:** триггер использует существующие `MinConfidence` и пороги направления; веса Blend (P2.3) для всех прочих случаев не тронуты.
6. **Тесты:** 6 новых кейсов. Размещены в `SignalCalibratorTests.cs` (где живут кейсы калибратора с прохода 14), а не в `TradingLogicTests.cs`, как предполагал план.

**Поведенческое изменение (принято по спеке «при любом HOLD»):** уверенный HOLD модели (в т.ч. с высоким conf, например по новостному риску) теперь может быть пересмотрен сильным prior движка — prior новостей не видит. Это и есть цель пункта: на логе P2.1 все 29 сигналов умерли на выходе модели, а окно ловило 1/29. Защита капитала при этом не ослабляется: вход всё равно проходит RiskManager, биржевую OCO-защиту (P0.1) и fail-closed.

**Замечание из диагностики (остаётся в силе):** на старом логе `prior >= 0.55` не было ни разу — новый триггер, как и P2.3, даст эффект только на исправленных данных (фикс `volume_ratio`) и в связке с P2.4/P2.6.

**Изменённые файлы:**
- `AITradingSystem.Services/Analysis/SignalCalibrator.cs` (`strongAlignedPrior`/`holdOverride`, ветка `Hold→Prior` в Blend, расширенный `mayReconsider`, doc-комментарии)
- `AITradingSystem.Core/Models/Trading/TradingSignal.cs` (комментарий `ConfidenceSource` дополнен `Hold→Prior`)
- `AITradingSystem.Tests/SignalCalibratorTests.cs` (6 тестов: HOLD 0.10 + prior 0.60 → BUY с ценами 98/104; слабый prior 0.42 → Blend/HOLD; флэт-направление → без пересмотра; short только при AllowShort; guards позиция/нулевой conf/ModelOnly; окно 0.45 сохраняет приоритет и source)

Тесты добавлены, но не запускались; сборки/компиляции не выполнялись.

**Критерий приёмки:** выполнен на уровне механики — пересмотр срабатывает при любом HOLD с сильным согласованным prior, узкое окно и все guards сохранены, покрыто тестами. Живой прогон модели не проводился.

### ⬜ P2.6 Валидация весов скоринга
> 🆕 Валидация весов скоринга (EMA 0.30, Supertrend 0.20, штрафы ×0.6/0.8…): подтвердить/подогнать на истории; reliability diagram — уверенность должна соответствовать реальной частоте успеха.

**Сейчас:** веса зашиты константами (`SignalCalibrator.cs:78–84`: EMA 0.30 / Supertrend 0.20 / EMA-pos 0.15 / RSI 0.15 / MACD 0.10 / Stoch 0.05 / OBV 0.05), prior = `0.2 + 0.7·|score|` (`:168`). В логе `ema_align = 0` в 28/30 случаев → крупнейший вес не contributes, |score| ≤ 0.32 и prior структурно не превышает 0.42 < 0.55. Бэктеста/калибровочной кривой в решении нет.

**Файлы:** `AITradingSystem.Services/Analysis/SignalCalibrator.cs`, `AITradingSystem.Core/Indicators/NormalizedIndicatorSnapshot.cs:70–71` (`ema_align`), новый бэктест/отчёт (`tools/`), `AITradingSystem.Tests/`.

---

## Порядок работ (очередь от лёгких к тяжёлым; обновлена проходом 11)

**Принцип:** сортировка по объёму и связности правок, но жёсткие зависимости важнее размера. P0 закрыт полностью (7/7). Фикс `volume_ratio` выполнен проходом 11, P1.8 — проходом 12, P2.2 — проходом 13, P2.3 — проходом 14, P2.5 — проходом 15, P1.5 — проходом 16, P1.6 — проходом 17, P1.2 — проходом 18, P1.1 — проходом 19, P1.3 — проходом 20, P1.4 — проходом 21, P2.4 — проходом 22. Остался **1 рабочий шаг**: 1 пункт P2. Размеры ориентировочные: **XS** — точечная правка; **S** — локальная правка/тесты; **M** — несколько связанных файлов; **L** — сквозной контур/подсистема; **XL** — исследование + инструменты + калибровка.

Диагностика P2.1 остаётся источником зависимостей: сначала фикс `volume_ratio`, затем P2.2, затем P2.3/P2.5, и только потом P2.4/P2.6. Подбор порога и весов не делается на испорченных данных и без учёта издержек.

| № | Шаг | Размер | Почему здесь / зависимости | Основные файлы |
|---|---|---|---|---|
| 1 | **✅ Фикс `volume_ratio` — выполнен, проход 11** | S | Объём для отношения берётся из последней закрытой свечи; блокировка для последующей калибровки снята. Изменённые файлы перечислены в P2.1 и журнале прохода 11. | `TradingEngine.cs`, `NormalizedIndicatorSnapshot.cs`, `IndicatorCalculator.cs`, `IndicatorTests.cs` |
| 2 | **✅ P1.8 Мониторинг дрейфа часов — выполнен, проход 12** | S | Независимый контур, метрики, дашборд и Telegram-алерты по смене состояния. Изменённые файлы перечислены в P1.8 и журнале прохода 12. | `ClockDriftMonitor.cs`, `MetricsCollector.cs`, `TradingConfig.cs`, `TradingEngine.cs`, дашборд, `ClockDriftTests.cs` |
| 3 | **✅ P2.2 Убрать двойной штраф в промпте — выполнен, проход 13** | S | Независимая рубрика, нейтральные факты вместо аудиторских штрафов в промпте. Изменённые файлы перечислены в P2.2 и журнале прохода 13. | `PromptBuilder.cs`, `AiRequestContext.cs`, `SignalCalibrator.cs`, `TradingEngine.cs`, `PromptBuilderTests.cs` |
| 4 | **✅ P2.3 Политика уверенности 0.7/0.3 — выполнен, проход 14** | XS | Blend взвешен: `AI:BlendModelWeight=0.7` (модель) / 0.3 (prior), валидация и тесты. Изменённые файлы перечислены в P2.3 и журнале прохода 14. | `SignalCalibrator.cs`, `AIConfig.cs`, `TradingEngine.cs`, `appsettings.json`, `SignalCalibratorTests.cs` |
| 5 | **✅ P2.5 Расширить детект «дегенерата» — выполнен, проход 15** | S–M | Любой HOLD с сильным согласованным prior → пересмотр на вход по детерминированным ценам (`Hold→Prior`); окно 0.45 и guards сохранены. Изменённые файлы перечислены в P2.5 и журнале прохода 15. | `SignalCalibrator.cs`, `TradingSignal.cs`, `SignalCalibratorTests.cs` |
| 6 | **✅ P1.5 Фильтр ликвидности — выполнен, проход 16** | M | Гейт спреда/глубины/объёма перед входом (точечные пороги BTTC/STRAX/FLR из symbols.json) + метрики ликвидности и post-only-отказов. Изменённые файлы перечислены в P1.5 и журнале прохода 16. | `LiquidityFilter.cs`, `TradingEngine.cs`, `OrderExecutor.cs`, `MetricsCollector.cs`, `TradingConfig.cs`, `SymbolConfig.cs`, `ConfigLoader.cs`, `symbols.json`, `LiquidityFilterTests.cs` |
| 7 | **✅ P1.6 429/-1003 и бюджет весов — выполнен, проход 17** | M | Бюджет весов (`X-MBX-USED-WEIGHT-1M` + локальные оценки), soft-limit только для polling, баны 429/-1003/418 для всех запросов; критичные отправки не задерживаются. Изменённые файлы перечислены в P1.6 и журнале прохода 17. | `BinanceRestClient.cs`, `RequestWeightBudget.cs`, `MetricsCollector.cs`, `BinanceConfig.cs`, дашборд, `RequestWeightBudgetTests.cs` |
| 8 | **✅ P1.2 Комиссии в R/R и безубытке — выполнен, проход 18** | M | Net R/R/TP/сайзинг после комиссий, безубыток, IOC-cap обычного выхода, NetPnL истории. Изменённые файлы перечислены в P1.2 и журнале прохода 18. | `TradeCostModel.cs`, `RiskManager.cs`, `PositionSizer.cs`, `OrderExecutor.cs`, `BinanceRestClient.cs`, `TradingEngine.cs`, `PositionMonitor.cs`, конфиг/тесты |
| 9 | **✅ P1.1 Slippage-модель в paper — выполнен, проход 19** | M | Paper taker-филл по стакану, частичное исполнение, slippage и IOC-cap выхода; комиссии обеих ног сохранены. Изменённые файлы перечислены в P1.1 и журнале прохода 19. | `OrderBook.cs`, `OrderExecutor.cs`, `TradingConfig.cs`, `appsettings.json`, `PaperSlippageTests.cs` |
| 10 | **✅ P1.3 Dust и аудит остатка после отмены — выполнен, проход 20** | M–L | Dust-остаток ниже фильтров закрывается MARKET SELL точного объёма (full-balance exemption); аудит «отменённый остаток vs фактический филл» с метрикой расхождений; «хвост» защиты после округления OCO к stepSize логируется и считается. Изменённые файлы перечислены в P1.3 и журнале прохода 20. | `DustPolicy.cs`, `OrderExecutor.cs`, `MetricsCollector.cs`, `TradingConfig.cs`, `appsettings.json`, `DustPolicyTests.cs` |
| 11 | **✅ P1.4 Целостность данных после реконнекта WS — выполнен, проход 21** | L | События потери/восстановления WS, REST-дозагрузка свечей, детект разрывов, повторный прогрев, fail-closed запрет входов на протухших данных. Изменённые файлы перечислены в P1.4 и журнале прохода 21. | `CandleSchedule.cs`, `BinanceWebSocketClient.cs`, `MarketAnalyzer.cs`, `IndicatorCalculator.cs`, `TradingEngine.cs`, `MetricsCollector.cs`, `TradingConfig.cs`, `appsettings.json`, `MarketDataIntegrityTests.cs` |
| 12 | **✅ P2.4 Порог MinConfidence из данных — выполнен, проход 22** | L | Expectancy после издержек в R по сетке порогов, guards выборки и коридор [0.45; 0.80]; метка уверенности доведена до истории сделок, контур рекомендует (авто-режим выключен), оффлайн-инструмент подбора. Изменённые файлы перечислены в P2.4 и журнале прохода 22. | `ConfidenceExpectancy.cs`, `ConfidenceThresholdTuner.cs`, `TradeRecord.cs`, `Position.cs`, `TradingEngine.cs`, `JsonTradeHistoryRepository.cs`, `AIConfig.cs`, `SignalCalibrator.cs`, `MetricsCollector.cs`, `appsettings.json`, `tools/confidence_threshold.py`, `ConfidenceThresholdTests.cs` |
| 13 | **✅ P1.7 Портфельный риск — выполнен, проход 23** | L–XL | Экспозиция, корреляция BTC-кластера, дневной лимит символа, пауза после серии убытков; восстановление из истории, метрики/дашборд. Изменённые файлы перечислены в P1.7 и журнале прохода 23. | `PortfolioRiskManager.cs`, `RiskManager.cs`, `TradingEngine.cs`, `TradingConfig.cs`, `appsettings.json`, `JsonTradeHistoryRepository.cs`, `PortfolioRiskTests.cs` |
| 14 | **P2.6 Валидация весов скоринга** | XL | Самый тяжёлый: бэктест/калибровочная кривая, reliability diagram, подтверждение весов на истории; после чистых данных, издержек и порога. | `SignalCalibrator.cs`, `NormalizedIndicatorSnapshot.cs`, `tools/`, тесты |

**Ближайший шаг:** №14 — P2.6, валидация весов скоринга. Ожидается команда заказчика.  
**Правило очереди:** один шаг = один проход = отдельное сообщение заказчику; после шага трекер обновляется сразу, затем ожидается команда на продолжение.

---

## Журнал проходов

### Проход 23 — 24.11.2026 — шаг №13: P1.7 «Портфельный риск»

Клон получен; `SECUPD.md` прочитан из `AITradingSystem.zip`. Подготовка рабочего дерева: 154 файла исходников, тестов, инструментов и трекера из вложенной папки архива восстановлены в корень клона по конвенции прохода 6 (правки до прохода 22 включительно). `bin/`, `obj/`, `.vs/`, вложенный `.git` и вложенный zip не сохранялись. Корневой `AITradingSystem.zip` восстановлен из git без изменений после распаковки; `README.md` заменён версией архива. Временная копия архива удалена. Подготовка зафиксирована в трекере до начала реализации.

Выполнен тринадцатый пункт очереди. Новый `PortfolioRiskManager` (Core) считает суммарную экспозицию открытых позиций по marked price и каппит её процентом от equity; коррелированный BTC-кластер считается Pearson по лог-доходностям закрытых свечей, выровненным по `OpenTime`, с fail-closed при недостатке истории/бенчмарка/дисперсии. Дневной лимит убытка на символ считается по реализованному `NetPnL` за риск-день (часы биржи) от `DayStartBalance`; пауза после серии убытков взводится на `LossStreakPauseMinutes` и переживает рестарт через полную историю сделок (`GetAllTradesAsync`, дедуп по `TradeRecord.Id`). Гейты стоят до AI и перед отправкой ордера; объём может урезаться портфельным капом. Бенчмарк `BTCUSDT` обновляется отдельным REST-контуром даже вне торговых пар. `JsonTradeHistoryRepository` пишет историю атомарно (tmp+rename). Метрики и дашборд-панель добавлены.

| Действие | Файлы |
|---|---|
| Созданы до трекера | `AITradingSystem.Core/RiskManagement/PortfolioRiskManager.cs`, `AITradingSystem.Tests/PortfolioRiskTests.cs` |
| Изменены до трекера | `AITradingSystem.Common/Configuration/TradingConfig.cs`, `AITradingSystem.Core/Indicators/IndicatorCalculator.cs`, `AITradingSystem.Services/Analysis/MarketAnalyzer.cs`, `AITradingSystem.Core/RiskManagement/DrawdownTracker.cs`, `AITradingSystem.Core/RiskManagement/RiskManager.cs`, `AITradingSystem.Data/Storage/JsonTradeHistoryRepository.cs`, `AITradingSystem.Services/Trading/TradingEngine.cs`, `AITradingSystem.Services/Monitoring/MetricsCollector.cs`, `AITradingSystem.UI/App.xaml.cs`, `AITradingSystem.UI/appsettings.json`, `AITradingSystem.UI/ViewModels/DashboardViewModel.cs`, `AITradingSystem.UI/Views/DashboardView.xaml` |
| Изменён сразу после кода | `SECUPD.md` (P1.7 → ✅, сводка P1 = 8 ✅ / 0 🟡 / 0 ⬜, очередь: остался 1 шаг, этот журнал) |

Новые ключи конфигурации (`Trading`): `PortfolioRiskEnabled=true`, `MaxPortfolioExposurePercent=60`, `MaxCorrelatedExposurePercent=35`, `CorrelationRiskEnabled=true`, `CorrelationBenchmarkSymbol="BTCUSDT"`, `CorrelationLookbackCandles=96`, `MinCorrelationSamples=48`, `MaxPairwiseCorrelation=0.80`, `SymbolDailyLossLimitPercent=1`, `LossStreakPauseThreshold=3`, `LossStreakPauseMinutes=60`. Побочные эффекты интерфейса: конструкторы `RiskManager`, `TradingEngine`, `DashboardViewModel` получили `PortfolioRiskManager`; DI-регистрация добавлена; `IndicatorCalculator` хранит окно закрытых свечей; `MarketAnalyzer.GetClosedCandles` потокобезопасен; `DrawdownTracker.DayStartBalance` публичный. Поведенческие изменения: входы урезаются/отклоняются по экспозиции, корреляции, дневному лимиту символа и серии убытков; при включённой корреляции отсутствие свежей истории бенчмарка/символа блокирует вход. Тесты добавлены (12 кейсов), но не запускались. Сборки/компиляции и коммит не выполнялись; zip не пересобирался. **Шаг №13 завершён. Следующий шаг — №14, P2.6 «Валидация весов скоринга»; ожидается команда заказчика на продолжение.**

### Проход 2 — 24.11.2026 — P0.1 «Биржевые стоп-ордера»

| Действие | Файлы |
|---|---|
| Созданы | `AITradingSystem.Core/Models/Trading/ProtectiveOrderList.cs`, `AITradingSystem.Core/Models/Trading/ProtectiveOrderPricing.cs` |
| Изменены | `AITradingSystem.Core/Models/Trading/Position.cs`, `AITradingSystem.Data/Binance/IBinanceRestClient.cs`, `AITradingSystem.Data/Binance/BinanceRestClient.cs`, `AITradingSystem.Data/Binance/BinanceModels.cs`, `AITradingSystem.Services/Trading/IOrderExecutor.cs`, `AITradingSystem.Services/Trading/OrderExecutor.cs`, `AITradingSystem.Services/Trading/TradingEngine.cs`, `AITradingSystem.Common/Configuration/TradingConfig.cs`, `AITradingSystem.UI/appsettings.json`, `AITradingSystem.Tests/TradingLogicTests.cs` |
| Трекер | `SECUPD.md` (P0.1 → ✅, сводка P0 = 1/7, обновлён P1.3, добавлен журнал) |

Новые ключи конфигурации (`Trading`): `ExchangeProtectionEnabled=true`, `ProtectionLimitSlippage=0.003`, `ClosePositionIfProtectionFails=true`, `TrailingReplaceMinIntervalSeconds=30`, `TrailingReplaceMinStepPercent=0.15`.

Побочные эффекты интерфейса: `IOrderExecutor.UpdateStopLossAsync` теперь `Task<bool>` (было `Task<Order?>`) и реально ходит на биржу; добавлены `EnsureProtectionAsync`/`CancelProtectionAsync`. Внешних вызовов старого метода вне `TradingEngine` не было.

Не делалось: сборки/компиляции, запуск тестов, коммит (по требованию заказчика).

### Проход 3 — 24.11.2026 — P0.2 «Независимый контур мониторинга»

| Действие | Файлы |
|---|---|
| Создан | `AITradingSystem.Services/Trading/PositionMonitor.cs` |
| Изменены | `AITradingSystem.Services/Trading/TradingEngine.cs`, `AITradingSystem.Services/Trading/PositionManager.cs`, `AITradingSystem.Common/Configuration/TradingConfig.cs`, `AITradingSystem.UI/appsettings.json`, `AITradingSystem.Services/Monitoring/MetricsCollector.cs`, `AITradingSystem.UI/App.xaml.cs`, `AITradingSystem.Tests/TradingLogicTests.cs` |
| Трекер | `SECUPD.md` (P0.2 → ✅, сводка P0 = 2/7 и пересчёт P0/P1, обновлён остаток P0.1, порядок работ → P0.6, добавлен журнал) |

Новые ключи конфигурации (`Trading`): `PositionMonitorIntervalMs=500`, `PositionMonitorSweepSeconds=5`, `PositionMonitorMaxLatencyMs=2000`, `ProtectionRetryIntervalSeconds=15`.

Побочные эффекты интерфейса: конструктор `TradingEngine` получил параметр `PositionMonitor` (DI-регистрация добавлена в `App.xaml.cs`); `PositionManager` получил `GetPositionById`; из `TradingEngine` удалены `MonitorPositionAsync`/`EnsureExchangeProtectionAsync` — внешних вызовов у них не было. Поведенческое изменение: на `Pause` сопровождение позиций продолжается (раньше замирало вместе с циклом анализа).

Не делалось: сборки/компиляции, запуск тестов, коммит (по требованию заказчика).

### Проход 4 — 24.11.2026 — фундамент P0.3 «Очередь исполнения + идемпотентность»

| Действие | Файлы |
|---|---|
| Создан | `AITradingSystem.Services/Trading/ExecutionQueue.cs` |
| Изменены | `AITradingSystem.Services/Trading/OrderExecutor.cs`, `AITradingSystem.Data/Binance/IBinanceRestClient.cs`, `AITradingSystem.Data/Binance/BinanceRestClient.cs`, `AITradingSystem.Common/Configuration/TradingConfig.cs`, `AITradingSystem.UI/appsettings.json`, `AITradingSystem.Services/Monitoring/MetricsCollector.cs`, `AITradingSystem.UI/App.xaml.cs`, `AITradingSystem.Tests/TradingLogicTests.cs` |
| Трекер | `SECUPD.md` (P0.3 → 🟡 с перечнем остатка, сводка P0 = 2 ✅ / 2 🟡 / 3 ⬜, обновлён остаток P0.2 и порядок работ, добавлен журнал) |

Новые ключи конфигурации (`Trading`): `ExecutionQueueMaxParallel=2`, `ExecutionIdempotencyTtlSeconds=600`, `ExecutionQueueWarnWaitMs=3000`.

Побочные эффекты интерфейса: конструктор `OrderExecutor` получил параметр `ExecutionQueue` (DI-регистрация добавлена); в `IBinanceRestClient` у `PlaceOrderAsync`/`PlaceMarketOrderAsync` появился необязательный `clientOrderId` **перед** `ct` — все вызовы (только в `OrderExecutor`) переведены на именованные аргументы; добавлена перегрузка `GetOrderAsync(symbol, string clientOrderId)`.

Найдено попутно: `SendRequestAsync` ретраит POST `/api/v3/order` при 5xx/-1000/-1001/-1007 и сетевых сбоях **без** `newClientOrderId` — это и был источник возможных дублей; закрыто п.5 пункта.

Не делалось: сборки/компиляции, запуск тестов, коммит (по требованию заказчика).

### Проход 5 — 24.11.2026 — P0.3 «Очередь исполнения» доведён до ✅

| Действие | Файлы |
|---|---|
| Созданы | `AITradingSystem.Core/Models/Trading/ExecutionIntent.cs`, `AITradingSystem.Data/Storage/IExecutionJournal.cs`, `AITradingSystem.Data/Storage/JsonExecutionJournal.cs` |
| Изменены | `AITradingSystem.Services/Trading/ExecutionQueue.cs`, `AITradingSystem.Services/Trading/OrderExecutor.cs`, `AITradingSystem.Services/Trading/TradingEngine.cs`, `AITradingSystem.Data/Binance/BinanceRestClient.cs`, `AITradingSystem.Common/Configuration/TradingConfig.cs`, `AITradingSystem.UI/appsettings.json`, `AITradingSystem.Services/Monitoring/MetricsCollector.cs`, `AITradingSystem.UI/App.xaml.cs`, `AITradingSystem.UI/ViewModels/DashboardViewModel.cs`, `AITradingSystem.UI/Views/DashboardView.xaml`, `AITradingSystem.Tests/TradingLogicTests.cs` |
| Трекер | `SECUPD.md` (P0.3 → ✅, сводка P0 = 3 ✅ / 1 🟡 / 3 ⬜, закрыт остаток P0.2, обновлён порядок работ, добавлен журнал) |

Новые ключи конфигурации (`Trading`): `ExecutionRetryMaxAttempts=3`, `ExecutionRetryBaseDelayMs=300`, `ExecutionRetryMaxDelayMs=5000`. Новый файл состояния: `data/state/execution_journal.json` (≤500 записей, незавершённые не вычищаются по TTL).

Побочные эффекты интерфейса: конструктор `ExecutionQueue` получил необязательный `IExecutionJournal`; конструкторы `TradingEngine` и `DashboardViewModel` получили `ExecutionQueue` (и `TradingConfig` для дашборда) — DI-регистрация добавлена в `App.xaml.cs`. Поведенческое изменение: `clientOrderId` больше не случайный, а детерминированный от параметров операции — повтор того же входа/закрытия/защиты в окне TTL не создаёт второй ордер.

Не делалось: сборки/компиляции, запуск тестов, коммит (по требованию заказчика). P0.4 не начинался — ждёт команды заказчика.

### Проход 6 — 24.11.2026 — P0.4 «Персист DrawdownTracker»

| Действие | Файлы |
|---|---|
| Изменены | `AITradingSystem.Core/RiskManagement/DrawdownTracker.cs`, `AITradingSystem.Core/RiskManagement/RiskManager.cs`, `AITradingSystem.Data/Storage/IStateRepository.cs`, `AITradingSystem.Data/Storage/JsonStateRepository.cs`, `AITradingSystem.Services/Trading/TradingEngine.cs`, `AITradingSystem.Tests/TradingLogicTests.cs` |
| Трекер | `SECUPD.md` (P0.4 → ✅, сводка P0 = 4 ✅ / 0 🟡 / 3 ⬜, обновлены замечания к рабочему дереву и порядок работ, добавлен журнал) |

Новых ключей конфигурации нет. Новый файл состояния: `data/state/risk_state.json` (атомарная запись tmp+rename; при отсутствии — миграция с legacy `peak_balance.json`).

Побочные эффекты интерфейса: конструктор `TradingEngine` получил параметр `IStateRepository` (DI разрешает автоматически — регистрация `JsonStateRepository` уже была); конструкторы `RiskManager`/`DrawdownTracker` получили **опциональный** `exchangeUtcNow` (существующие вызовы, в т.ч. `RiskTests.cs`, совместимы); в `IStateRepository` добавлены `SaveRiskStateAsync`/`LoadRiskStateAsync`. Поведенческое изменение: paper-баланс при рестарте восстанавливается из `risk_state.json` вместо сброса к `PaperTradingInitialBalance`.

Распаковка в этом проходе: zip из репозитория уже содержал правки проходов 2–5; дерево распаковано прямо в корень клона `AITradingSystem/` (корень решения), `README.md` клона перезаписан версией из zip.

Не делалось: сборки/компиляции, запуск тестов, коммит (по требованию заказчика). P0.6 не начинался — ждёт команды заказчика.

### Проход 7 — 24.11.2026 — фундамент P0.5–P0.7 (ранее не записан в SECUPD)

Обнаружен в коде при проверке заказчиком. Код уже содержал: `ReconciliationService` (инвентаризация + listenKey + executionReport), `ExecutionReport`, user-data WS/REST, kill-switch движка и паническую кнопку, двухшаговую блокировку сохранения Live и отдельные Live-ключи в `BinanceConfig`. Не было: DI-регистрации `ReconciliationService`, ключей в `appsettings.json`, команды/UI подтверждения Live, полей Live-ключей, сброса kill-switch, постоянного баннера REAL MONEY и панели реконсиляции.

| Действие | Файлы |
|---|---|
| Созданы | `AITradingSystem.Core/Models/Trading/ExecutionReport.cs`, `AITradingSystem.Services/Trading/ReconciliationService.cs` |
| Изменены | `AITradingSystem.Common/Configuration/TradingConfig.cs`, `BinanceConfig.cs`, `AITradingSystem.Core/RiskManagement/RiskManager.cs`, `AITradingSystem.Data/Binance/BinanceModels.cs`, `BinanceRestClient.cs`, `IBinanceRestClient.cs`, `BinanceWebSocketClient.cs`, `IBinanceWebSocketClient.cs`, `AITradingSystem.Services/Monitoring/MetricsCollector.cs`, `AITradingSystem.Services/Trading/TradingEngine.cs`, `AITradingSystem.UI/ViewModels/TradingViewModel.cs`, `SettingsViewModel.cs`, `AITradingSystem.UI/Views/TradingView.xaml` |

Не делалось: сборки/компиляции, запуск тестов, коммит.

### Проход 8 — 24.11.2026 — учёт фундамента P0.5–P0.7 + доведение UI/DI

| Действие | Файлы |
|---|---|
| Изменены | `AITradingSystem.UI/App.xaml.cs` (DI `ReconciliationService`), `AITradingSystem.UI/appsettings.json` (`UserDataStreamEnabled`, `UserDataKeepAliveMinutes`, `KillSwitchAutoCloseOnRiskBreach`, `EncryptedLiveApiKey`, `EncryptedLiveApiSecret`), `AITradingSystem.UI/ViewModels/SettingsViewModel.cs` (`ConfirmLive`, сброс подтверждения, ввод/шифрование Live-ключей), `AITradingSystem.UI/Views/SettingsView.xaml` (+ `.xaml.cs`: блок подтверждения Live, поля ключей, PasswordBox), `AITradingSystem.UI/ViewModels/MainViewModel.cs` (постоянный REAL MONEY баннер, статус kill-switch), `AITradingSystem.UI/MainWindow.xaml` (привязки фона/цвета баннера), `AITradingSystem.UI/ViewModels/TradingViewModel.cs` (сброс kill-switch), `AITradingSystem.UI/Views/TradingView.xaml` (кнопка сброса), `AITradingSystem.UI/ViewModels/DashboardViewModel.cs` + `Views/DashboardView.xaml` (панель реконсиляции/user-data/kill-switch), `AITradingSystem.Tests/TradingLogicTests.cs` (4 теста фундамента) |
| Трекер | `SECUPD.md` (зафиксирован проход 7; P0.5 → 🟡 с остатком, P0.6/P0.7 → ✅, сводка P0 = 6 ✅ / 1 🟡 / 0 ⬜, порядок работ → завершение P0.5, добавлены журналы проходов 7–8) |

Новые ключи конфигурации, вынесенные в `appsettings.json`: `Trading:UserDataStreamEnabled=true`, `Trading:UserDataKeepAliveMinutes=30`, `Trading:KillSwitchAutoCloseOnRiskBreach=true`, `Binance:EncryptedLiveApiKey=""`, `Binance:EncryptedLiveApiSecret=""`.

Побочные эффекты интерфейса: `DashboardViewModel` получил параметр `ReconciliationService`; `SettingsViewModel` получил команду `ConfirmLive` и свойства ввода Live-ключей; `TradingViewModel` получил команду `ResetKillSwitch`. Поведенческое изменение: баннер REAL MONEY обновляется и на паузе/остановке; сохранение Live без второго шага заблокировано.

Не делалось: сборки/компиляции, запуск тестов, коммит (по требованию заказчика). P0.5 не завершён — ждёт команды заказчика на следующий шаг.

### Проход 9 — 24.11.2026 — P0.5 проверка завершённости + учёт в трекере

По команде заказчика: P0.5 был завершён в коде, но запись в SECUPD.md не вносилась. Выполнена проверка всех четырёх пунктов остатка по фактическому коду — все реализованы и согласованы (колбэки зарегистрированы до старта, DI разрешается, ключи в appsettings, метрики в дашборде, тесты добавлены). **Код в этом проходе не менялся.**

| Действие | Файлы |
|---|---|
| Изменены | только `SECUPD.md` (P0.5 → ✅ с полным описанием и списком файлов завершения; сводка P0 = 7 ✅ / 0 🟡 / 0 ⬜; порядок работ → P1.6 / фикс `volume_ratio`; этот журнал) |
| Проверены (без правок) | `ReconciliationService.cs`, `TradingEngine.cs` (`ApplyExchangeFillAsync`/`AdoptOrphanPositionAsync`), `OrderExecutor.cs` (stream-first `WaitForFillAsync`), `Position.cs` (леджер `AppliedExchangeFills`), `TradingConfig.cs`/`appsettings.json` (3 ключа P0.5), `MetricsCollector.cs`, `DashboardViewModel.cs`, `BinanceWebSocketClient.cs`, `BinanceRestClient.cs`, `TradingLogicTests.cs` |

Ключи конфигурации P0.5, подтверждённые в `appsettings.json`: `UserDataStreamEnabled=true`, `UserDataKeepAliveMinutes=30`, `UserDataStreamHealthCheckSeconds=15`, `ReconciliationAdoptOrphanPositions=true`, `ReconciliationCancelOrphanBotOrders=true`.

Не делалось: сборки/компиляции, запуск тестов, коммит (по требованию заказчика). Следующий шаг ждёт команды заказчика.

### Проход 10 — 24.11.2026 — очередь оставшихся работ от лёгких к тяжёлым

По команде заказчика оставшиеся работы упорядочены от лёгких/мелких к тяжёлым/большим. Учтены жёсткие зависимости из диагностики P2.1: фикс `volume_ratio` → P2.2 → P2.3/P2.5 → P2.4/P2.6; подбор порога/весов только после учёта издержек. **Код в этом проходе не менялся.**

| Действие | Файлы |
|---|---|
| Изменены | только `SECUPD.md` (сводка/замечание прохода 10, новый раздел «Порядок работ», этот журнал) |

Очередь: 1) фикс `volume_ratio`; 2) P1.8; 3) P2.2; 4) P2.3; 5) P2.5; 6) P1.5; 7) P1.6; 8) P1.2; 9) P1.1; 10) P1.3; 11) P1.4; 12) P2.4; 13) P1.7; 14) P2.6.

Не делалось: сборки/компиляции, запуск тестов, коммит (по требованию заказчика). Первый шаг очереди ждёт команды заказчика.

### Проход 11 — 24.11.2026 — шаг №1: фикс `volume_ratio`

Клон репозитория получен; исходники из `AITradingSystem.zip` распакованы в корень клона (корень решения), `README.md` перезаписан версией из архива. Сам архив не пересобирался. SECUPD.md прочитан; выполнен первый пункт очереди из прохода 10 без изменения весов, рубрики и порогов.

**Изменения кода:** `IndicatorCalculator.LastClosedVolume` запоминает объём при `Update`, `IndicatorCalculator.VolumeRatio` делит его на `VolumeSma20`; окно и числитель очищаются при `Reset()`. Снапшот использует `calc.VolumeRatio`, а не `currentCandle.Volume`. В `TradingEngine.OnKlineReceived` зафиксирован контракт: текущая свеча нужна для цены/PnL, индикаторы и объём для отношения обновляются только закрытой свечой. Добавлены 3 регрессионных теста; не запускались.

| Действие | Файлы |
|---|---|
| Изменены до трекера | `AITradingSystem.Core/Indicators/IndicatorCalculator.cs`, `AITradingSystem.Core/Indicators/NormalizedIndicatorSnapshot.cs`, `AITradingSystem.Services/Trading/TradingEngine.cs`, `AITradingSystem.Tests/IndicatorTests.cs` |
| Изменён сразу после кода | `SECUPD.md` (результат и файлы фикса в P2.1, сводка, очередь: осталось 13 шагов, этот журнал) |

Сборки/компиляции, запуск тестов и коммит не выполнялись. Кодовые правки сделаны в распакованном дереве; zip не пересобирался. **Шаг №1 завершён. Следующий шаг — №2, P1.8; ожидается команда заказчика на продолжение.**

### Проход 12 — 24.11.2026 — шаг №2: P1.8 «Мониторинг дрейфа часов»

Выполнен второй пункт очереди. Добавлен независимый периодический мониторинг дрейфа часов относительно Binance с компенсацией RTT, метриками, дашборд-панелью и Telegram-алертами только при смене состояния. Комментарий метода измерения приведён к русскому тексту без изменения поведения. Существующие `GetServerTimeAsync` и `TelegramAlertService` переиспользованы без изменения.

| Действие | Файлы |
|---|---|
| Созданы до трекера | `AITradingSystem.Services/Monitoring/ClockDriftMonitor.cs`, `AITradingSystem.Tests/ClockDriftTests.cs` |
| Изменены до трекера | `AITradingSystem.Services/Monitoring/MetricsCollector.cs`, `AITradingSystem.Common/Configuration/TradingConfig.cs`, `AITradingSystem.UI/appsettings.json`, `AITradingSystem.Services/Trading/TradingEngine.cs`, `AITradingSystem.UI/App.xaml.cs`, `AITradingSystem.UI/ViewModels/DashboardViewModel.cs`, `AITradingSystem.UI/Views/DashboardView.xaml` |
| Изменён сразу после кода | `SECUPD.md` (P1.8 → ✅, сводка P1 = 1 ✅ / 2 🟡 / 5 ⬜, очередь: осталось 12 шагов, этот журнал) |

Новые ключи конфигурации (`Trading`): `ClockDriftMonitorEnabled=true`, `ClockDriftCheckIntervalSeconds=60`, `ClockDriftWarningThresholdMs=1000`. Побочные эффекты интерфейса: конструктор `TradingEngine` получил параметр `ClockDriftMonitor`; DI-регистрация добавлена. Тесты добавлены, но не запускались. Сборки/компиляции и коммит не выполнялись; zip не пересобирался. **Шаг №2 завершён. Следующий шаг — №3, P2.2; ожидается команда заказчика на продолжение.**

### Проход 13 — 24.11.2026 — шаг №3: P2.2 «Убрать двойной штраф в промпте»

Выполнен третий пункт очереди. Рубрика модели стала независимой: не стартует с prior и не вычитает повторно за ADX/объём/BB/волатильность. В промпт передаются нейтральные факты `EngineScore.Facts`; аудиторские `Reasons` со штрафами остаются для лога. Веса смешения и порог уверенности не менялись.

| Действие | Файлы |
|---|---|
| Создан до трекера | `AITradingSystem.Tests/PromptBuilderTests.cs` |
| Изменены до трекера | `AITradingSystem.Services/AI/PromptBuilder.cs`, `AITradingSystem.Services/AI/AiRequestContext.cs`, `AITradingSystem.Services/Analysis/SignalCalibrator.cs`, `AITradingSystem.Services/Trading/TradingEngine.cs` |
| Изменён сразу после кода | `SECUPD.md` (P2.2 → ✅, сводка P2 = 2 ✅ / 1 🟡 / 3 ⬜, очередь: осталось 11 шагов, этот журнал) |

Побочные эффекты интерфейса: `EngineScore` получил `Facts`; `AiRequestContext` получил `EngineFacts`; `TradingEngine.BuildAiContext` передаёт факты в контекст модели. Тесты добавлены, но не запускались. Сборки/компиляции и коммит не выполнялись; zip не пересобирался. **Шаг №3 завершён. Следующий шаг — №4, P2.3; ожидается команда заказчика на продолжение.**

### Проход 14 — 24.11.2026 — шаг №4: P2.3 «Политика уверенности 0.7/0.3»

Выполнен четвёртый пункт очереди. Blend ушёл от 50/50: `final = w·model + (1−w)·prior`, `w = AI:BlendModelWeight` (дефолт 0.7). Направление весов (0.7 — модели) выведено из диагностики P2.1: максимум final 0.42 при 0.7/0.3 достижим только при весе модели 0.7; после P2.2 независимая оценка модели весит больше prior. Дегенерат HOLD≈0.45, отказ инференса, ModelOnly/EngineOverride и порог MinConfidence не менялись.

Свежий клон репозитория: zip в этом снапшоте содержал вложенную папку `AITradingSystem/` (правки до прохода 13 включительно); содержимое перенесено в корень клона по конвенции прохода 6, `README.md` клона перезаписан версией из zip. Сам архив не пересобирался.

| Действие | Файлы |
|---|---|
| Создан до трекера | `AITradingSystem.Tests/SignalCalibratorTests.cs` |
| Изменены до трекера | `AITradingSystem.Common/Configuration/AIConfig.cs`, `AITradingSystem.Services/Analysis/SignalCalibrator.cs`, `AITradingSystem.Services/Trading/TradingEngine.cs`, `AITradingSystem.UI/appsettings.json` |
| Изменён сразу после кода | `SECUPD.md` (P2.3 → ✅, сводка P2 = 3 ✅ / 1 🟡 / 2 ⬜, очередь: осталось 10 шагов, этот журнал) |

Новый ключ конфигурации: `AI:BlendModelWeight=0.7` (валидация [0; 1]; 0.5 — прежнее 50/50). Побочные эффекты интерфейса: `SignalCalibrator.Calibrate` получил **опциональный** `blendModelWeight = 0.7m` (старые вызовы совместимы); в лог `CALIBRATE` добавлено поле `blend_w`. Тесты добавлены (7 шт.), но не запускались. Сборки/компиляции и коммит не выполнялись; zip не пересобирался. **Шаг №4 завершён. Следующий шаг — №5, P2.5; ожидается команда заказчика на продолжение.**

### Проход 15 — 24.11.2026 — шаг №5: P2.5 «Расширить детект „дегенерата"»

Выполнен пятый пункт очереди. Пересмотр HOLD→вход расширен с узкого окна conf=0.45±0.02 на любой HOLD с сильным согласованным prior: в политике Blend при `prior >= MinConfidence` и направлении вне флэт-зоны ±0.05 (short — только при AllowShort) уверенность берётся из prior (`source=Hold→Prior`), действие пересматривается существующим `TryUpgrade` с детерминированными ценами. Окно 0.45 сохраняет приоритет и source `Degenerate→Prior`; guards (нет позиции, modelConf>0, отказ инференса → HOLD, ModelOnly не пересматривает) не тронуты. Новых ключей конфигурации нет; веса Blend из P2.3 для прочих случаев не менялись.

| Действие | Файлы |
|---|---|
| Изменены до трекера | `AITradingSystem.Services/Analysis/SignalCalibrator.cs`, `AITradingSystem.Core/Models/Trading/TradingSignal.cs` (комментарий `ConfidenceSource`), `AITradingSystem.Tests/SignalCalibratorTests.cs` (6 новых кейсов P2.5) |
| Изменён сразу после кода | `SECUPD.md` (P2.5 → ✅, сводка P2 = 4 ✅ / 0 🟡 / 2 ⬜, очередь: осталось 9 шагов, этот журнал) |

Побочные эффекты интерфейса: нет (сигнатуры не менялись; новый `source=Hold→Prior` documentирован в `TradingSignal.ConfidenceSource`). Поведенческое изменение: уверенный HOLD модели может быть пересмотрен сильным prior движка — принято по спеке «при любом HOLD»; вход по-прежнему проходит RiskManager и биржевую защиту P0.1. Тесты добавлены, но не запускались. Сборки/компиляции и коммит не выполнялись; zip не пересобирался. **Шаг №5 завершён. Следующий шаг — №6, P1.5; ожидается команда заказчика на продолжение.**

### Проход 16 — 24.11.2026 — шаг №6: P1.5 «Фильтр ликвидности»

Выполнен шестой пункт очереди. Вход блокируется гейтом ликвидности: спред (ask−bid)/mid, глубина стороны исполнения (BUY — asks, SELL — bids) против max(абсолютный минимум, множитель × нотариал), средний объём закрытой свечи в котировке (SMA20 × цена — без бага незакрытой свечи). Нет стакана → fail-closed отказ `no-book`. Пороги глобальные из `TradingConfig` + точечные из `symbols.json` (BTTC/STRAX/FLR строже: 0.25% / 2000 / 25000). Отказы попадают в воронку `Rejected/liquidity` и метрики по причинам; post-only-отказы биржи агрегируются `RecordPostOnlyRejection`. Гейт работает и в paper-режиме. Логирование аномального спреда >0.5% сохранено.

Свежий клон репозитория: zip содержал вложенную папку `AITradingSystem/` с правками до прохода 15 включительно; содержимое перенесено в корень клона по конвенции прохода 6, вложенный каталог удалён (старый внутренний zip удалён вместе с ним, zip репозитория сохранён). `README.md` клона перезаписан версией из zip. Сам архив не пересобирался.

| Действие | Файлы |
|---|---|
| Созданы до трекера | `AITradingSystem.Core/Models/MarketData/LiquidityFilter.cs`, `AITradingSystem.Tests/LiquidityFilterTests.cs` |
| Изменены до трекера | `AITradingSystem.Common/Models/Config/SymbolConfig.cs`, `AITradingSystem.Common/Configuration/TradingConfig.cs`, `AITradingSystem.Common/Configuration/ConfigLoader.cs`, `AITradingSystem.Services/Trading/TradingEngine.cs`, `AITradingSystem.Services/Trading/OrderExecutor.cs`, `AITradingSystem.Services/Monitoring/MetricsCollector.cs`, `AITradingSystem.UI/appsettings.json`, `AITradingSystem.UI/symbols.json` |
| Изменён сразу после кода | `SECUPD.md` (P1.5 → ✅, сводка P1 = 2 ✅ / 2 🟡 / 4 ⬜, очередь: осталось 8 шагов, этот журнал) |

Новые ключи конфигурации (`Trading`): `LiquidityFilterEnabled=true`, `MaxEntrySpreadPercent=0.35`, `MinEntryDepthQuote=1000`, `MinDepthNotionalRatio=5`, `LiquidityDepthLevels=10`, `MinCandleQuoteVolume=15000`. Новые поля реестра символов: `MaxSpreadPercent?`, `MinDepthQuote?`, `MinCandleQuoteVolume?` (null — глобальный порог; версия symbols.json 1.2).

Побочные эффекты интерфейса: конструктор `OrderExecutor` получил **опциональный** `MetricsCollector? metrics = null` (DI разрешает автоматически, старые вызовы совместимы); `SymbolConfig`/`TradingConfig` получили новые поля (существующие вызовы совместимы). Поведенческое изменение: входы на тонких парах/широком спреде/пустом стакане теперь отклоняются до отправки ордера (в т.ч. в paper). Тесты добавлены (17 кейсов), но не запускались. Сборки/компиляции и коммит не выполнялись; zip не пересобирался. **Шаг №6 завершён. Следующий шаг — №7, P1.6; ожидается команда заказчика на продолжение.**

### Проход 17 — 24.11.2026 — шаг №7: P1.6 «429/-1003 и бюджет весов»

Выполнен седьмой пункт очереди. Новый `RequestWeightBudget` ведёт бюджет веса минутного окна: факт из `X-MBX-USED-WEIGHT-1M` (легаси `X-MBX-USED-WEIGHT` тоже читается) + локальные оценки веса запросов между сэмплами. При весе ≥80% лимита (6000) **некритичные** запросы ждут конца окна; постановка/снятие ордеров и OCO (POST/DELETE `/api/v3/order*`) обходит soft-limit — конкуренция с очередью исполнения P0.3 решена в пользу защиты/закрытия. Явная ветка 429/-1003/418 регистрирует бан по `Retry-After` для **всех** запросов (для 418 — фактическая длительность IP-бана, потолок 1 час); ретрай не раньше окончания бана; timestamp подписи строится после ожидания. Метрики (`IRestRateLimitMetrics` в Data, реализация — `MetricsCollector`), дашборд-панель вместе с отложенными из P1.5 счётчиками ликвидности/post-only, 3 ключа конфигурации.

Свежий клон репозитория: zip содержал вложенную папку `AITradingSystem/` с правками до прохода 16 включительно; содержимое перенесено в корень клона по конвенции прохода 6, вложенный каталог удалён вместе со старым внутренним zip, zip репозитория сохранён без изменений. `README.md` клона перезаписан версией из zip. Сам архив не пересобирался.

| Действие | Файлы |
|---|---|
| Созданы до трекера | `AITradingSystem.Data/Binance/RequestWeightBudget.cs`, `AITradingSystem.Tests/RequestWeightBudgetTests.cs` |
| Изменены до трекера | `AITradingSystem.Data/Binance/BinanceRestClient.cs`, `AITradingSystem.Common/Configuration/BinanceConfig.cs`, `AITradingSystem.Services/Monitoring/MetricsCollector.cs`, `AITradingSystem.UI/appsettings.json`, `AITradingSystem.UI/App.xaml.cs`, `AITradingSystem.UI/ViewModels/DashboardViewModel.cs`, `AITradingSystem.UI/Views/DashboardView.xaml` |
| Изменён сразу после кода | `SECUPD.md` (P1.6 → ✅, сводка P1 = 3 ✅ / 1 🟡 / 4 ⬜, закрыты остатки P0.3/P0.5/P1.5 про бюджет весов и панель, очередь: осталось 7 шагов, этот журнал) |

Новые ключи конфигурации (`Binance`): `RequestWeightLimitPerMinute=6000`, `RequestWeightSoftLimitPercent=80`, `WeightThrottleEnabled=true`.

Побочные эффекты интерфейса: конструктор `BinanceRestClient` получил **опциональный** `IRestRateLimitMetrics? rateLimitMetrics = null` (DI-маппинг на синглтон `MetricsCollector` добавлен в `App.xaml.cs`, старые вызовы совместимы); `MetricsCollector` реализует `IRestRateLimitMetrics`; клиент получил публичное свойство `WeightBudget` и статические `TryReadUsedWeight`/`TryReadRetryAfterSeconds`. Поведенческое изменение: polling притормаживается у лимита веса; при 429/-1003/418 приостанавливаются все запросы, включая закрытие (отправка во время бана грозит IP-баном). Классификация транзиентных ошибок и идемпотентность `clientOrderId` не менялись. Тесты добавлены (23 кейса), но не запускались. Сборки/компиляции и коммит не выполнялись; zip не пересобирался. **Шаг №7 завершён. Следующий шаг — №8, P1.2; ожидается команда заказчика на продолжение.**
### Проход 18 — 24.11.2026 — шаг №8: P1.2 «Комиссии в R/R и безубытке»

Выполнен восьмой пункт очереди. Сначала восстановлено рабочее дерево: свежий клон содержал только корневой `AITradingSystem.zip` и `README.md`; из архива восстановлены 275 файлов исходников, тестов, инструментов и трекера в корень клона по конвенции прохода 6 (без `.vs/` и вложенного архива). Корневой `AITradingSystem.zip` сохранён без изменений; `README.md` заменён версией из архива. Восстановлены правки до прохода 17 включительно.

Затем реализован P1.2: net R/R и TP после комиссий обеих ног, net-сайзинг, безубыток после 1R net прибыли, обычный выход marketable LIMIT IOC с cap проскальзывания, аварийный MARKET-bypass, восстановление защиты остатка и NetPnL в истории/метриках. Подробный список изменённых файлов и новые ключи конфигурации — в карточке P1.2.

| Действие | Файлы |
|---|---|
| Созданы до трекера | `AITradingSystem.Core/RiskManagement/TradeCostModel.cs`, `AITradingSystem.Tests/TradeCostModelTests.cs` |
| Изменены до трекера | `AITradingSystem.Common/Configuration/TradingConfig.cs`, `AITradingSystem.Core/RiskManagement/PositionSizer.cs`, `AITradingSystem.Core/RiskManagement/RiskManager.cs`, `AITradingSystem.Core/Models/Trading/TradeRecord.cs`, `AITradingSystem.Data/Binance/IBinanceRestClient.cs`, `AITradingSystem.Data/Binance/BinanceRestClient.cs`, `AITradingSystem.Services/Trading/IOrderExecutor.cs`, `AITradingSystem.Services/Trading/OrderExecutor.cs`, `AITradingSystem.Services/Trading/TradingEngine.cs`, `AITradingSystem.Services/Trading/PositionMonitor.cs`, `AITradingSystem.Services/Monitoring/MetricsCollector.cs`, `AITradingSystem.UI/appsettings.json`, `AITradingSystem.Tests/RiskTests.cs` |
| Изменён сразу после кода | `SECUPD.md` (P1.2 → ✅, сводка, очередь, этот журнал) |

Тесты добавлены (10 кейсов), но не запускались. Сборки/компиляции и коммит не выполнялись; zip не пересобирался. **Шаг №8 завершён. Следующий шаг — №9, P1.1; ожидается команда заказчика на продолжение.**

### Проход 19 — 24.11.2026 — шаг №9: P1.1 «Slippage-модель в paper-режиме»

Выполнен девятый пункт очереди. Paper taker-вход и выход симулируются проходом по стакану: BUY ест asks, SELL ест bids; результат содержит фактический объём, среднюю цену, проскальзывание и признак полного исполнения. Обычный paper-выход уважает cap `MarketExitMaxSlippage`, аварийный может обойти cap; частичный филл уменьшается движком и остаток защищается. Комиссии обеих ног остаются в `Order.Commission`, `TradeRecord.Commission` и `NetPnL`.

| Действие | Файлы |
|---|---|
| Создан до трекера | `AITradingSystem.Tests/PaperSlippageTests.cs` |
| Изменены до трекера | `AITradingSystem.Core/Models/MarketData/OrderBook.cs`, `AITradingSystem.Common/Configuration/TradingConfig.cs`, `AITradingSystem.Services/Trading/OrderExecutor.cs`, `AITradingSystem.UI/appsettings.json` |
| Изменён сразу после кода | `SECUPD.md` (P1.1 → ✅, сводка, очередь, этот журнал) |

Новые ключи конфигурации: `Trading:PaperSlippageEnabled=true`, `Trading:PaperSlippageMaxLevels=0`. Побочные эффекты интерфейса: `OrderBook.SimulateMarketFill`, record `OrderBookFill`, изменённая приватная сигнатура `CreatePaperOrder`. Поведенческое изменение: paper market-вход без стакана отклоняется; paper-выход может быть частичным или нулевым из-за глубины/cap. Тесты добавлены (7 кейсов), но не запускались. Сборки/компиляции и коммит не выполнялись; zip не пересобирался. **Шаг №9 завершён. Следующий шаг — №10, P1.3; ожидается команда заказчика на продолжение.**

### Проход 20 — 24.11.2026 — шаг №10: P1.3 «Dust и аудит остатка после отмены»

Свежий клон репозитория: zip содержал вложенную папку `AITradingSystem/` с правками до прохода 19 включительно; содержимое перенесено в корень клона по конвенции прохода 6 (включая `.gitignore`), вложенный каталог удалён вместе со старым внутренним zip и `.vs/` (как в проходе 18), zip репозитория сохранён без изменений. `README.md` клона перезаписан версией из zip.

Выполнен десятый пункт очереди. Новый чистый `DustPolicy` (Core) выбирает маршрут закрытия по фильтрам символа: обычный IOC-cap (P1.2) / MARKET / dust-MARKET-SELL / отказ. Dust-остаток (ниже stepSize/minQty/minNotional) закрывается MARKET SELL точного объёма под full-balance exemption Binance — раньше округление в ноль отклоняло закрытие и позиция зависала в цикле fail-closed; BUY-dust отклоняется с причиной (exemption только для SELL). `WaitForFillAsync` после отмены неисполненной части всегда пишет аудит «отменённый остаток vs фактический филл»: терминальный статус + сходимость объёмов → чистый аудит; не-терминальный статус/отрицательный остаток → расхождение в метрику `CancelAuditMismatches` (подхват живого остатка — реконсиляция P0.5). «Хвост» защиты (OCO < объёма позиции после округления к stepSize) считается `ProtectionTail`, логируется `PROTECTION-TAIL` и агрегируется в метриках; после срабатывания ноги хвост закрывается dust-маршрутом. 7 новых счётчиков `MetricsCollector` + сводка.

| Действие | Файлы |
|---|---|
| Созданы до трекера | `AITradingSystem.Core/Models/Trading/DustPolicy.cs`, `AITradingSystem.Tests/DustPolicyTests.cs` |
| Изменены до трекера | `AITradingSystem.Services/Trading/OrderExecutor.cs`, `AITradingSystem.Services/Monitoring/MetricsCollector.cs`, `AITradingSystem.Common/Configuration/TradingConfig.cs`, `AITradingSystem.UI/appsettings.json` |
| Изменён сразу после кода | `SECUPD.md` (P1.3 → ✅, сводка P1 = 6 ✅ / 0 🟡 / 2 ⬜, очередь: осталось 4 шага, этот журнал) |

Новый ключ конфигурации: `Trading:DustMarketCloseEnabled=true`. Побочные эффекты интерфейса: новый статический класс `DustPolicy` (`ClosePlan`, `CancelAudit`, `DustCloseRoute`); публичные сигнатуры `OrderExecutor`/`ProtectiveOrderPricing`/`BinanceRestClient`/`TradingEngine` не менялись. Поведенческие изменения: dust-остаток SELL закрывается MARKET вместо бесконечного отклонения; при отказе dust-закрытия защита не восстанавливается (ноги не прошли бы фильтры); после каждой отмены остатка пишется аудит; лог ожидания филла больше не утверждает «остаток отменён» при не-терминальном статусе. Тесты добавлены (17 кейсов), но не запускались. Сборки/компиляции и коммит не выполнялись; zip не пересобирался. **Шаг №10 завершён. Следующий шаг — №11, P1.4 «Целостность данных после реконнекта WS»; ожидается команда заказчика на продолжение.**

### Проход 21 — 24.11.2026 — шаг №11: P1.4 «Целостность данных после реконнекта WS»

Свежий клон репозитория: zip содержал вложенную папку `AITradingSystem/` с правками до прохода 20 включительно; содержимое восстановлено в корень клона по конвенции прохода 6 (280 файлов, включая `.gitignore`), `.vs/`, вложенный zip и `.git` из архива не переносились. Корневой `AITradingSystem.zip` сохранён без изменений; `README.md` клона перезаписан версией из архива. Подготовка зафиксирована в трекере до начала реализации.

Выполнен одиннадцатый пункт очереди. Рыночный WS теперь сообщает о потере и восстановлении соединения (`OnDisconnected`/`OnReconnected`). При потере/реконнекте движок сбрасывает кэш стакана, помечает активные символы как требующие восстановления и блокирует входы. Закрытая свеча из WS добавляется в индикаторы только по непрерывной сетке интервала (`CandleSchedule`, `IndicatorCalculator.TryAppendClosed`): дубль/порядок/разрыв классифицируются, разрыв не маскируется и отправляет символ на REST-дозагрузку. `MarketAnalyzer.TryReloadHistory` атомарно проверяет REST-историю (достаточность, непрерывность, отсутствие дублей, свежесть последней закрытой свечи) и повторно прогревает индикаторы. `TradingEngine.RecoverMarketDataAsync` обслуживает очередь восстановления и watchdog протухания; гейт свежести стоит перед AI и повторно перед отправкой ордера. Сопровождение открытых позиций (`PositionMonitor`) и биржевые OCO не блокируются.

| Действие | Файлы |
|---|---|
| Созданы до трекера | `AITradingSystem.Core/Models/MarketData/CandleSchedule.cs`, `AITradingSystem.Tests/MarketDataIntegrityTests.cs` |
| Изменены до трекера | `AITradingSystem.Core/Indicators/IndicatorCalculator.cs`, `AITradingSystem.Services/Analysis/MarketAnalyzer.cs`, `AITradingSystem.Data/Binance/IBinanceWebSocketClient.cs`, `AITradingSystem.Data/Binance/BinanceWebSocketClient.cs`, `AITradingSystem.Services/Trading/TradingEngine.cs`, `AITradingSystem.Common/Configuration/TradingConfig.cs`, `AITradingSystem.UI/appsettings.json`, `AITradingSystem.Services/Monitoring/MetricsCollector.cs` |
| Изменён сразу после кода | `SECUPD.md` (P1.4 → ✅, сводка P1 = 7 ✅ / 0 🟡 / 1 ⬜, очередь: осталось 3 шага, этот журнал) |

Новые ключи конфигурации: `Trading:MarketDataMaxAgeSeconds=30`, `Trading:MarketDataRecoveryRetrySeconds=15`. Побочные эффекты интерфейса: `IBinanceWebSocketClient` получил события `OnDisconnected`/`OnReconnected`; `IndicatorCalculator` получил `LastClosedOpenTime`/`TryAppendClosed`; `MarketAnalyzer` получил посимвольные отметки свежести, `MarkRecoveryRequired`, `TryReloadHistory`, `IsDataFresh`, `GetVolumeSma20`; сигнатура `UpdateIndicators` теперь принимает интервал и время получения. Поведенческие изменения: после реконнекта WS входы запрещены до непрерывной REST-истории, повторного прогрева и свежего стакана; разрыв закрытой свечи не попадает в индикаторы; протухший kline/стакан блокирует вход и ставит символ на восстановление. Тесты добавлены (17 методов / 22 кейса с Theory), но не запускались. Сборки/компиляции и коммит не выполнялись; zip не пересобирался. **Шаг №11 завершён. Следующий шаг — №12, P2.4 «Порог MinConfidence из данных»; ожидается команда заказчика на продолжение.**

### Проход 22 — 24.11.2026 — шаг №12: P2.4 «Порог MinConfidence из данных»

Свежий клон репозитория: zip содержал вложенную папку `AITradingSystem/` с правками до прохода 21 включительно; 150 файлов исходников, тестов, инструментов и трекера перенесены в корень клона по конвенции прохода 6, вложенный каталог удалён; `bin/`, `obj/`, `.vs/`, `.git` и вложенный zip не переносились. Корневой `AITradingSystem.zip` сохранён без изменений, `README.md` клона перезаписан версией из архива. Подготовка зафиксирована в трекере до начала реализации.

Выполнен двенадцатый пункт очереди. Порог `MinConfidence` больше не берётся с потолка: он подбирается по expectancy **после издержек** на истории закрытых сделок. Для этого уверенность входа (`SignalConfidence`/`ModelConfidence`/`EngineConfidence`/`ConfidenceSource`) и начальный стоп доведены до истории: `Position` хранит и персистит метку, `TradeRecord` получает её и `InitialStopLoss` на обоих путях закрытия (штатный выход и биржевой филл `ApplyExchangeFillAsync`). Новый чистый `ConfidenceExpectancyAnalyzer` нормирует каждую сделку на принятый риск (net R), строит корзины уверенности и sweep порогов, выбирает максимум expectancy при выборке ≥ порога значимости; при равенстве берётся более высокий порог. Отбор fail-safe: сделки без метки/риска исключаются и считаются отдельно, мало истории или отсутствие положительной expectancy → рекомендации нет и порог не меняется, sweep ограничен коридором [0.45; 0.80]. Контур `ConfidenceThresholdTuner` работает вне цикла анализа, берёт полную историю новым `GetAllTradesAsync` (старый лимит 100 записей сделал бы подбор фикцией), пишет `CONFIDENCE-TUNE` и метрики; авто-применение выключено по умолчанию, при включении ограничено коридором и сопровождается логом/алертом. Добавлен оффлайн-инструмент `tools/confidence_threshold.py` с идентичными правилами (корзины, sweep, рекомендация, `--json`-отчёт) — проверен на синтетической истории, реальных сделок с меткой в снапшоте нет. `CalibrationContext.MinConfidence` теперь ссылается на единый документированный fallback вместо собственной константы.

| Действие | Файлы |
|---|---|
| Созданы до трекера | `AITradingSystem.Core/Models/Trading/ConfidenceExpectancy.cs`, `AITradingSystem.Services/Analysis/ConfidenceThresholdTuner.cs`, `AITradingSystem.Tests/ConfidenceThresholdTests.cs`, `tools/confidence_threshold.py` |
| Изменены до трекера | `AITradingSystem.Core/Models/Trading/TradeRecord.cs`, `AITradingSystem.Core/Models/Trading/Position.cs`, `AITradingSystem.Services/Trading/TradingEngine.cs`, `AITradingSystem.Data/Storage/ITradeHistoryRepository.cs`, `AITradingSystem.Data/Storage/JsonTradeHistoryRepository.cs`, `AITradingSystem.Common/Configuration/AIConfig.cs`, `AITradingSystem.Services/Analysis/SignalCalibrator.cs`, `AITradingSystem.Services/Monitoring/MetricsCollector.cs`, `AITradingSystem.UI/App.xaml.cs`, `AITradingSystem.UI/appsettings.json` |
| Изменён сразу после кода | `SECUPD.md` (P2.4 → ✅, сводка P2 = 5 ✅ / 0 🟡 / 1 ⬜, очередь: осталось 2 шага, этот журнал) |

Новые ключи конфигурации (`AI`): `MinConfidenceTuneEnabled=true`, `MinConfidenceAutoTuneEnabled=false`, `MinConfidenceTuneIntervalMinutes=60`, `MinConfidenceFloor=0.45`, `MinConfidenceCeiling=0.80`, `MinConfidenceTuneStep=0.05`, `MinConfidenceTuneMinTrades=40`, `MinConfidenceTuneMinBucketTrades=15`. Побочные эффекты интерфейса: конструктор `TradingEngine` получил `ConfidenceThresholdTuner` (DI-регистрация в `App.xaml.cs`); `ITradeHistoryRepository` получил `GetAllTradesAsync`; `TradeRecord`/`Position` — новые поля (старая история читается, поля отсутствуют → 0 → в подбор не идут). Поведенческие изменения: по умолчанию контур только рекомендует; при `MinConfidenceAutoTuneEnabled=true` порог может измениться в пределах коридора после разбора истории. Панель дашборда не добавлялась — счётчики в сводке метрик. Тесты добавлены (24 метода / 25 кейсов с Theory), но не запускались. Сборки/компиляции и коммит не выполнялись; zip не пересобирался. **Шаг №12 завершён. Следующий шаг — №13, P1.7 «Портфельный риск»; ожидается команда заказчика на продолжение.**
