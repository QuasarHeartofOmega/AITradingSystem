# Логирование AITradingSystem

Документ описывает текущий контракт вывода после аудита `docs/history/LOGGING-PLAN.md`.
Исторические логи и старый формат `ИТОГ`/`AI BACKEND` относятся к прежнему baseline и не являются примерами текущего формата.

## Карта каналов

| Канал | Файл | Содержимое |
|---|---|---|
| `system` | `trading-*.log` | Жизненный цикл приложения, инфраструктура, ошибки без явного канала. Общий лог также принимает недиагностические события других каналов, если они не исключены настройкой. |
| `trade` | `trades-*.log` | Решения, финальные исходы, ордера, позиции, риск, kill-switch, реконсиляция, роль LLM. |
| `exchange` | `exchange-*.log` | REST/WS Binance: запросы, ответы, сводки тел, вес лимитов, ошибки API, баланс. |
| `ai` | `ai-*.log` | PROMPT / THINKING / RESPONSE / GENERATION / PARSED-SIGNAL, загрузка модели и ошибки инференса. |
| `ai-native` | `ai-native-*.log` | Сырой вывод llama.cpp/ggml. Только этот файл; не попадает в консоль и общий лог. |
| `news` | `news-*.log` | Новостные запросы, ответы, пропуски провайдеров и контекст для модели. |

`SeparateChannelFiles=false` оставляет общий `trading-*.log` и `trades-*.log`; отдельные диагностические файлы не создаются.
В этом режиме `ExcludeDiagnosticsFromMainLog` не применяется к общему логу, иначе диагностика потерялась бы полностью.
`ai-native` остаётся тихим каналом и не выводится в консоль/общий лог.

## Формат строки

Все файловые приёмники используют `UnifiedLogFormatter`:

```text
yyyy-MM-dd HH:mm:ss.fff zzz [LVL] [канал] Источник: EVENT-CODE key=value — расшифровка
```

- Время с таймзоной во всех файлах; консоль использует короткое время без даты/зоны и без источника.
- Уровень: `VRB`, `DBG`, `INF`, `WRN`, `ERR`, `FTL`.
- Источник печатается только при наличии `SourceContext`; пустого поля и двойных пробелов нет.
- Числа рендерятся в инвариантной культуре: десятичная точка, независимо от локали Windows.
- Многострочные блоки и исключения получают отступ 4 пробела.
- Поля — `snake_case`; код события — `UPPER-KEBAB`. Русская расшифровка идёт после `—`.
- Повторы подавляются по ключу состояния; при возврате после паузы добавляется `repeats=N`.

Примеры:

```text
2026-11-26 14:16:47.338 +03:00 [INF] [trade] OUTCOME symbol=BTCUSDT verdict=ОТКАЗ stage=market-data repeats=12 — awaiting-live-tick
2026-11-26 14:16:48.001 +03:00 [DBG] [exchange] HTTP-RESP GET /api/v3/klines?symbol=BTCUSDT&interval=1m status=200 elapsed_ms=84 weight=31/6000 candles=500 last_close=97123.45 closed=true
2026-11-26 14:16:49.120 +03:00 [INF] [ai] AI-SIGNAL symbol=BTCUSDT action=Hold confidence=0.50 elapsed_ms=47250 meta=model=qwen.gguf, backend=Vulkan, prompt_tokens=1200, generated_tokens=96 — мнение модели, не ордер. reason=...
```

## Уровни

| Уровень | Назначение |
|---|---|
| `Verbose` | GET-запросы биржи до ответа; глубокая трассировка. |
| `Debug` | Диагностика каналов: HTTP-ответы, промпты/ответы/счётчики AI, нативный llama.cpp, новостные блоки. |
| `Information` | Штатные решения, ожидания, открытия/закрытия, сигналы модели, сводки риска и реконсиляции. |
| `Warning` | Реальные отказы и деградации: потеря защиты, устаревшие данные, ошибки API/сети, поздний veto, неудачная реконсиляция. |
| `Error` | Ошибки исполнения/инференса/сопровождения, требующие разбора. |
| `Fatal` | Kill-switch и состояния, требующие немедленного вмешательства оператора. |

Ожидаемый прогрев, ожидание живого тика, cooldown и штатный отказ входа — не `Warning`.
Уровень файла канала задаётся `DiagnosticsLevel` для `ai`/`exchange`/`news`/`ai-native`;
`trades` использует максимум из `MinimumLevel` и `Information`.

## Настройки

Секция `Logging` в `AITradingSystem.UI/appsettings.json` и значения по умолчанию `LoggingConfig` совпадают:

| Ключ | По умолчанию | Смысл |
|---|---:|---|
| `MinimumLevel` | `Information` | Уровень общего лога и консоли. |
| `DiagnosticsLevel` | `Debug` | Уровень диагностических файлов каналов. |
| `LogPath` | `logs` | Каталог относительно папки запуска. |
| `WriteToConsole` / `WriteToFile` | `true` | Приёмники. |
| `SeparateChannelFiles` | `true` | Отдельные файлы каналов. |
| `ExcludeDiagnosticsFromMainLog` | `true` | Не дублировать диагностику в общий лог при включённом разделении. |
| `HttpBodyMode` | `Summary` | `Summary` — выжимка; `Full` — тело с обрезкой; `Off` — только размер. |
| `LogHttpBodies` | `true` | Совместимость: `false` принудительно даёт режим `Off`. |
| `LogHttpTraffic` | `true` | REST-запросы/ответы биржи. |
| `LogPrompts` / `LogAiResponses` / `LogThinking` | `true` | Блоки AI-канала. |
| `LogNativeAiOutput` | `true` | Отдельный нативный лог llama.cpp/ggml. |
| `LogNews` | `true` | Новостной канал. |
| `MaxTextLength` | `8000` | Обрезка одного текстового блока; `0` — без обрезки. |
| `MaskSecrets` | `true` | Маскирование подписей, ключей, токенов и listenKey в URL/JSON/заголовках/блоках. |
| `StateHeartbeatSeconds` | `300` | Повтор неизменившегося состояния со счётчиком `repeats`; `0` — только изменения; `-1` — без подавления. |
| `MaxFileSizeMb` | `25` | Размерный предел одного файла. |
| `RetainedFileCount` | `14` | Число хранимых файлов на приёмник, включая суточные и размерные роли. |

`AI:NativeLogLevel` отдельно управляет порогом перехвата нативного вывода llama.cpp (`Off|Error|Warn|Info|Debug`).
`Logging:LogNativeAiOutput=false` отключает запись уже перехваченных строк в `ai-native`.

### Приватность и объём

- Полные тела `/account`, `/ticker/price`, `/klines` по умолчанию не пишутся; вместо них — сводка.
- Промпты, ответы и thinking могут содержать чувствительный контекст. Для публичного обмена логами отключите соответствующие флаги или проверьте файлы вручную.
- Маскирование снижает риск утечки, но не заменяет проверку логов перед публикацией.
- `Full` и `MaxTextLength=0` предназначены только для локальной глубокой отладки.
- Оценка эффекта аудита (~48 МБ/час → ~1–2 МБ/час) является расчётной целью; новый runtime-замер не выполнялся.

## Словарь основных кодов

### Trade / system

| Код | Значение |
|---|---|
| `LOGGER-START` / `LOGGER-STOP` | Инициализация/завершение логгера. |
| `OUTCOME` | Финал по символу: куплено/продано/закрыто/пауза/отказ с причиной. |
| `DECISION` | Промежуточное решение контура: калибровка, капы, коррекции. |
| `ORDER` | Этап ордера: SUBMIT / ACCEPTED / FILLED / CANCELED / CANCELED-OCO. |
| `BALANCE` / `RISK-SNAPSHOT` | Баланс и риск-снимок. |
| `SYMBOL-UNIVERSE` | Активные символы и причины исключений. |
| `KILL-SWITCH-*` | Аварийная блокировка, закрытие, сброс/отказ сброса, восстановление. |
| `LLM-ROLE`, `LLM-VETO-*` | Роль модели и результат veto-контура. |
| `DATA-STALE`, `DATA-REST-RELOAD`, `DATA-LIVE-CONFIRMED`, `DATA-QUARANTINE*`, `DATA-WS-*` | Здоровье рыночных данных. |
| `DATA-STREAM-DIAG` | P0.3a: per-stream факты молчащего символа — счётчики kline/depth, возраст последнего сообщения, пустые payload, состояние и ack подписки, прогрев свечей. Пишется перед каждой эскалацией recovery. |
| `RECON-START`, `RECON-SUMMARY`, `RECON-FAIL`, `RECON-FINDING`, `RECON-ADOPT`, `RECON-CANCEL` | Реконсиляция. |
| `RECON-SKIP-ACTIVE-ENTRY`, `RECON-EXPIRE-ENTRY` | Защита активного входа от преждевременной отмены. |
| `EXECUTION-MODE`, `EXECUTION-STATE-BLOCKED`, `USER-DATA-*` | Режим исполнения и user-data stream. |
| `PROTECTION-FILL-APPLIED`, `EXCHANGE-FILL-APPLIED` | Применение филла без повторного MARKET. |

### Exchange

| Код | Значение |
|---|---|
| `HTTP-REQ` / `HTTP-RESP` | Запрос/ответ REST; ответ содержит статус, latency, вес и сводку тела. |
| `BINANCE-API-ERROR` | Ошибка API с кодом и телом. |
| `HTTP-RETRY` | Повтор запроса. |
| `RATE-THROTTLE` | Ожидание бюджета веса/бана. |
| `TIME-SYNC` / `TIME-SYNC-FAIL` | Синхронизация времени. |
| `ACCOUNT-BALANCE` | Доступный баланс quote-актива. |
| `WS`, `WS-PARSE-ERROR` | WebSocket-события и ошибки разбора. |
| `WS-SUBSCRIBE-SENT`, `WS-SUBSCRIBE-ACK`, `WS-SUBSCRIBE-ERROR` | P0.3a: контрольные кадры SUBSCRIBE/UNSUBSCRIBE с id, подтверждением сервера и latency. Подтверждение подписки не равно живому потоку данных. |
| `BINANCE-ENV` | REST/stream URL окружения. |
| `SYMBOL-NOT-FOUND`, `KLINES-SKIPPED` | Проблемы справочника/свечей. |

### AI / AI-native

| Код | Значение |
|---|---|
| `AI-PROMPT` | Промпт с метаданными модели, роли, контекста и бюджета токенов. |
| `AI-RESPONSE` | Сырой ответ до парсинга. |
| `AI-THINKING` | Блок рассуждений. |
| `AI-GENERATION` | Счётчики токенов, reuse KV, бюджеты, таймаут. |
| `AI-SIGNAL` | Разобранное мнение модели; ещё не ордер. |
| `AI-PARSE-FAIL` / `AI-RETRY` | Невалидный вывод и повтор/repair. |
| `AI-BACKEND` / `AI-KV` | Загрузка модели и состояние KV. |
| `AI-ERROR` / `AI-EXHAUSTED` | Ошибка инференса / исчерпаны попытки. |
| `LLAMA` | Нативная строка llama.cpp/ggml в канале `ai-native`. |

### News

| Код | Значение |
|---|---|
| `NEWS-REQ` / `NEWS-RESP` | Запрос/ответ провайдера. |
| `NEWS-SKIP` | Провайдер пропущен. |
| `NEWS-ERROR` | Ошибка провайдера. |
| `NEWS-CONTEXT` | Итоговый новостной контекст для модели. |

## Просмотр логов

```powershell
python tools/watch_logs.py --channel trades --grep OUTCOME
python tools/watch_logs.py --channel ai --grep AI-PROMPT --lines 20
python tools/watch_logs.py --channel exchange --grep HTTP-RESP
python tools/watch_logs.py --channel ai-native --grep LLAMA
python tools/watch_logs.py --channel trading --dir AITradingSystem.UI/bin/Debug/net8.0-windows/logs
```

Скрипт понимает многострочные блоки: `--grep` фильтрует событие целиком вместе с продолжением,
`--lines` считает события, а не строки. Поддерживаются суточная и размерная ротация, `--once`, `--no-color` и `NO_COLOR`.

## Диагностика проблем

- **Нет `ai-*.log` с промптами:** проверьте `DiagnosticsLevel=Debug`, `LogPrompts=true`, `SeparateChannelFiles=true` и роль AI не `Off`.
- **Нет HTTP-тел:** это штатный `Summary`; для глубокой отладки временно задайте `HttpBodyMode=Full`.
- **Мало строк `OUTCOME`/`RECON-SUMMARY`:** работает подавление повторов; ищите `repeats=` или задайте `StateHeartbeatSeconds=-1` локально.
- **Нативный лог не виден в `ai`:** так и задумано; смотрите `ai-native-*.log` и `AI:NativeLogLevel`.
- **События разных файлов не совпадают по времени:** все файловые строки содержат дату и таймзону; сравнивайте с учётом смещения.

## Статус проверки

Контракт внесён в код и документацию. Компиляция, тестовые прогоны, GPU-инференс и новые ордера в среде подготовки не выполнялись.
Runtime-приёмка объёма логов и поведения на Demo остаётся отдельным шагом.
