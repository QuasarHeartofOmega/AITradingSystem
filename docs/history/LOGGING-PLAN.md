# Аудит логов AITradingSystem (шаг 1)

**Статус плана:** шаги 2–7 внесены в код/документацию. Основной пакет — commit `a847563` (`2611252`); завершение — текущие правки поверх него (см. «Завершение плана» ниже).
**Проверка:** компиляция, сборки, тестовые прогоны, GPU-инференс и новые ордера не выполнялись. Runtime-приёмка на Demo и новый замер объёма логов остаются внешними шагами.

Репозиторий: `QuasarHeartofOmega/AITradingSystem`. Исходный аудит: `0c53c0a`.
Материал: реальные логи из `docs/history/logs.zip` (26.11.2026, ~1 час работы, Demo, 8 символов, AI role=Shadow).

## Замеры

| Файл | Размер | Записей | Что внутри |
|---|---|---|---|
| `exchange-*.log` | **45.6 МБ** | 8 833 | 18.7 МБ — тела `/api/v3/account`, 18.6 МБ — `/api/v3/ticker/price`, 7.5 МБ — `/api/v3/klines` |
| `trading-*.log` | 1.68 МБ | 7 412 | `ИТОГ` 2699, `DECISION` 1139, `RECON-START/SUMMARY` 616+616, `AI BACKEND` 819 |
| `trades-*.log` | 1.39 МБ | 6 363 | те же события, что в `trading`, но второй формулировкой |
| `ai-*.log` | 0.086 МБ | 819 | **100% `AI BACKEND`** (нативный лог llama.cpp). Промптов/ответов/сигналов — 0 |

## Найденный мусор и дефекты

1. **81% объёма exchange-лога — бесполезные тела.** Каждый опрос пишется полный `/account` (сотни активов с нулями) и полный `/ticker/price` (все символы биржи), плюс сырые klines. Полезной информации — строка на запрос.
2. **Двойное логирование одного события.** Структурный хелпер канала + дублирующий `_logger.Log*` той же сути:
   `ReconciliationService.cs:226-227` (`EXECUTION-MODE`), `:282-283` (`USER-DATA-PROBE`), `:361-366` (`RECON-SUMMARY`), аналогично в `ExecutionQueue`, `PositionMonitor`, `OrderExecutor`, `TradingEngine`. Отсюда `trades` ≈ копия `trading`.
3. **Спам одинаковых состояний.** `ИТОГ … ОТКАЗ [market-data] awaiting-live-tick` — 2699 строк уровня **WRN** за час нормального прогрева; `RECON-START/SUMMARY` каждые 20 с даже при нулевых изменениях.
4. **Неверные уровни.** Ожидаемое состояние (прогрев, ожидание тика, отсутствие позиции) пишется как Warning; штатный отказ входа неотличим от аварии.
5. **Разнобой форматов.** Основной файл: `… .fff zzz [LVL] [ThreadId] [Channel] {SourceContext} …`; каналы: `… .fff [LVL] …` без таймзоны, без источника. Итог — события одного часа нельзя сопоставить, склейка логов ломается.
6. **Артефакт шаблона:** `[trade]  ИТОГ` — двойной пробел, т.к. `{SourceContext}` пуст у статических каналов.
7. **Два языка и две схемы в одной строке:** `RECON-CANCEL DASHUSDT kind=order id=…` рядом с `RECON-CANCEL: снят сиротский ордер бота …`. Часть полей — `snake_case`, часть — `PascalCase`, часть — проза.
8. **Служебные маркеры разработки в проде:** `P0.3/P0.4/P0.5`, `WARN:` внутри текста сообщения вместо уровня, `DECISION -`, `qty=0 price=0`, пустой `detail=`.
9. **AI-канал не выполняет свою задачу.** Он занят нативным логом ggml/llama.cpp (пути стейджа, Vulkan-дампы), а промпт/рассуждение/ответ/распарсенный сигнал в файл не попадают.
10. **Настройки по умолчанию противоречат цели:** `ExcludeDiagnosticsFromMainLog=false`, `LogHttpBodies=true`, `DiagnosticsLevel=Debug`, `MaxTextLength=20000`, `RetainedFileCount=10` при 50 МБ на файл.

## План работ (по одному шагу на сообщение)

- **Шаг 2. Единый контракт вывода.** Один шаблон для всех файлов (время с таймзоной, уровень, канал, источник), убрать пустой `SourceContext`/двойные пробелы, единая схема `EVENT-CODE key=value` + понятная русская расшифровка, инвариантная культура чисел, маскирование секретов.
- **Шаг 3. Устранение дублей.** Одно событие — одна запись. Убрать параллельные `_logger.Log*`-дубли каналов (~40 мест), оставить структурные хелперы единственным источником; `trades` перестаёт копировать `trading`.
- **Шаг 4. Чистка exchange-канала.** Вместо тел `/account`, `/ticker/price`, `/klines` — компактная сводка (кол-во ненулевых балансов, запрошенные символы, latency, weight, код ответа). Полные тела — только `Verbose`/по флагу, с обрезкой и счётчиком повторов.
- **Шаг 5. Подавление шума и уровни.** Логировать смену состояния, а не каждый цикл (`ИТОГ`, `DECISION`, `RECON-*`), периодический heartbeat-итог; ожидаемые состояния → `Debug`, реальные отказы → `Warning`.
- **Шаг 6. AI-канал по назначению.** Нативный лог llama.cpp → отдельный `ai-native-*.log` на `Debug`; в `ai-*.log` — читаемые блоки PROMPT / THINKING / RESPONSE / PARSED-SIGNAL с разделителями, метаданными (модель, токены, latency) и обрезкой.
- **Шаг 7. Документация и настройки.** `docs/LOGGING.md`: карта каналов, словарь кодов событий, уровни, примеры строк; дефолты `LoggingConfig` и `appsettings.json`, приведение `tools/watch_logs.py` к новому формату.

Оценка эффекта: объём логов ~48 МБ/час → ~1–2 МБ/час без потери диагностической информации; каждое торговое решение читается одной строкой с причиной.

---

## Завершение плана

### Что уже было в последнем commit `a847563`

Проверены `git log -1 --stat` и diff `HEAD^..HEAD`. Commit уже изменил 16 файлов:

- `Common/Configuration/LoggingConfig.cs` — новые ключи `HttpBodyMode`, `StateHeartbeatSeconds`, `LogNativeAiOutput`, дефолт `ExcludeDiagnosticsFromMainLog=true`.
- `Common/Logging/LogChannels.cs` — структурные каналы, уровни, подавление повторов, сводки, отдельный `ai-native`.
- `Common/Logging/LogFormat.cs` — `UnifiedLogFormatter` и `LogGate`.
- `Common/Logging/LogSummary.cs` — компактные выжимки HTTP-тел.
- `Common/Logging/Logger.cs` — единые приёмники, маршрутизация каналов, тихий `ai-native`.
- `Data/Binance/BinanceRestClient.cs`, `BinanceWebSocketClient.cs` — перевод биржевых событий в `ExchangeLog`, вес лимитов в ответе.
- `Services/AI/GgmlNative.cs`, `QwenModel.cs` — нативный лог отдельно, AI-блоки.
- `Services/Trading/ExecutionQueue.cs`, `OrderExecutor.cs`, `PositionMonitor.cs`, `ReconciliationService.cs`, `TradingEngine.cs` — устранение дублей и структурные торговые события.
- `UI/appsettings.json` — новые ключи и ротация 25 МБ / 14 файлов.
- `docs/history/LOGGING-PLAN.md` — сам аудит.

### Что завершено поверх commit

- **Шаг 2, контракт:** маскирование секретов исправлено для JSON (`"signature":"..."`), query и заголовков; все текстовые блоки проходят маскирование до обрезки. Режим `HttpBodyMode` нормализуется без учёта регистра. HTTP-ответ получил код `HTTP-RESP`, новостной ответ — `NEWS-RESP`.
- **Шаг 3, дубли:** удалён пустой остаточный блок после `KILL-SWITCH-CLOSE-RESULT`; из сообщения async-veto убран служебный маркер `P0.5`.
- **Шаг 4, exchange:** сводки тел и вес лимитов уже внесены commit; режимы `Summary|Full|Off` документированы и согласованы с `LogHttpBodies`.
- **Шаг 5, шум:** `LogGate` и heartbeat уже внесены commit; `trades` теперь уважает `MinimumLevel` не ниже `Information`.
- **Шаг 6, AI:** в `AI-PROMPT/RESPONSE/THINKING/SIGNAL` добавлены метаданные модели, backend, роли, контекста и токенов; отдельный код `AI-GENERATION` вместо прозы `AI GEN` внутри `AI-BACKEND`. Нативные строки маскируются и обрезаются.
- **Шаг 7, документация и настройки:** создан `docs/LOGGING.md` (карта каналов, формат, уровни, словарь кодов, настройки, примеры, просмотр и приватность). Дефолты `LoggingConfig` приведены к `appsettings.json`: 25 МБ, 14 файлов, `MaxTextLength=8000`. `tools/watch_logs.py` понимает новый формат, многострочные события, `ai-native`, суточную/размерную ротацию и `NO_COLOR`.
- **Маршрутизация:** при `SeparateChannelFiles=false` диагностика больше не может быть одновременно исключена из общего лога и потеряна; `ai-native` остаётся тихим. `LOGGER-START` перечисляет реальные каналы.

### Изменённые файлы завершения

```text
AITradingSystem.Common/Configuration/LoggingConfig.cs
AITradingSystem.Common/Logging/LogChannels.cs
AITradingSystem.Common/Logging/Logger.cs
AITradingSystem.Services/AI/QwenModel.cs
AITradingSystem.Services/Trading/LlmVetoService.cs
AITradingSystem.Services/Trading/TradingEngine.cs
AITradingSystem.UI/appsettings.json
docs/LOGGING.md
docs/README.md
docs/history/LOGGING-PLAN.md
README.md
tools/watch_logs.py
```

### Runtime-приёмка, не выполнявшаяся здесь

- [ ] Demo-прогон: объём `exchange` и `trading` за час, отсутствие полных тел при `Summary`.
- [ ] Прогрев/ожидание тика: одна строка состояния и `repeats=N` по heartbeat, без WRN-спама.
- [ ] Реконсиляция: нет дублей `trades`/`trading`, summary только при изменении или heartbeat.
- [ ] AI: в `ai-*.log` видны PROMPT/THINKING/RESPONSE/GENERATION/SIGNAL; нативный вывод только в `ai-native-*.log`.
- [ ] Секреты: подписи, API-ключи, токены и listenKey замаскированы в URL/JSON/блоках.
- [ ] Ротация: суточная и размерная, `watch_logs.py` переходит на новый файл.
- [ ] `SeparateChannelFiles=false`: диагностика не теряется, `trades` остаётся отдельным.
