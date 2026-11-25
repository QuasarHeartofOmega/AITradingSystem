
## Текущий этап — 2026-11-24
Исходники восстановлены из архива. Ниже сохранён исходный план; фактический прогресс фиксируется после каждой правки.

### Прогресс текущего прохода — 2026-11-24 (пакет «остатки аудита»)
- Закрыты оставшиеся ⚠️/❌ аудита: **B8** (CS1503 в PromptBuilder — блокер компиляции Services), **C15** (equity по всем активам), **D23** (реестр вместо ручного разбора, одна валидация, перенос SymbolConfig/SymbolsRegistry в Common), **D25** (словарь новостей).
- Подтверждено наличие правок прошлого пакета: компиляция NewsSymbolMapper, A2 (KV), A3 (CUDA-рантайм), A4 (хэндлы), A5 (CPU-фолбэк), B9 (отказ→HOLD), C12 (таймер).
- C19 остаётся ✅ с отклонением: ранний стейджинг выполняется в QwenModel.ActivateBackend до загрузки весов, а не в App вслепую.
- Сборки/компиляции/прогоны не выполнялись. Детали — в `changes.md`.

### Прогресс текущего прохода — 2026-11-24
- Исходники восстановлены из ZIP без bin/obj. Документы приведены к `plan.md`/`changes.md`.
- D21–D26: реализованы реестр 12 пар, лимит 32, BASE+QUOTE, загрузка symbols.json, точность цен, новостные синонимы, таблица настроек и предупреждения режима.
- B11/D24: движок проверяет доступность пар выбранной биржи до загрузки модели, кэширует tick/step, передаёт точность в промпт и калибратор для входов и сопровождения.
- C12–C14: read-only сервис аккаунта, полные балансы, открытые ордера, WS+REST цены, оценка USDT, частичная оценка без маршрута, ошибки без падения фонового цикла.
- C15–C18: Dashboard/Trading показывают биржевые данные отдельно от бота; ручное обновление; MainViewModel обновляет UI через Dispatcher и показывает режим/ключи/недоступные пары.
- C19: DI SignalCalibrator и ExchangeAccountService добавлен; опрос аккаунта стартует независимо от торговли; GPU preflight и сводка конфигурации логируются. Стейджинг native остаётся в активации выбранного бэкенда QwenModel (до загрузки весов), чтобы не смешивать CPU/CUDA/Vulkan до выбора.
- C20: appsettings обновлён; **PaperTradingMode=false + Environment=Demo означает реальные ордера на Demo**, не бумажную симуляцию. Live требует отдельного явного выбора и реальных ключей.
- Однократный статический просмотр интеграций выполнен. Исправлены: сохранение настроек без изменения активного торгового режима; отдельные activeSymbols вместо потери исходного списка при недоступности Demo; фактическое применение MaxPositionPercent к сайзингу; проверка суммы лимитов при загрузке; KnownQuotes не сериализуется; корректный приоритет WS-цен; остановка таймера аккаунта при ошибке первого запуска.
- Все файловые правки текущего плана внесены; без компиляции/сборки/запусков.
- Приёмка на Windows/GPU/Binance остаётся внешней: реальный offload, доступность Demo-пар, ключи DPAPI, балансы/ордера и UI не проверялись в песочнице.

# План доработки AITradingSystem — статус выполнения

Репозиторий: `QuasarHeartofOmega/AITradingSystem`. Рабочие исходники восстановлены в корень клона.
Пути ниже — относительно корня клона. Исторический ZIP не перезаписывался.

**Легенда:** ✅ файловая правка внесена. Это не означает успешную сборку или runtime-приёмку.
**Обновлено:** 2026-11-24

## Сводка прогресса

| Задача | Статус | Комментарий |
|---|---|---|
| A1. GgmlNative.cs | ✅ | Создан; + минимальные `TryGetExport`/`GetBackendDirectory` в LlamaNativeLoader (предпосылка из A4) |
| A2. GgufInspector.cs | ✅ | Парсер GGUF v2/v3, размеры весов, KV; **KV учитывается ровно один раз** (kvPerLayer в делителе, полный KV из бюджета не вычитается) |
| A3. NativeStaging.cs | ✅ | Плоский стейдж, GGML_BACKEND_PATH, SetDllDirectory; строгий CUDA-рантайм (nvcuda/libcuda/nvidia-smi, не CUDA_PATH), GPU-бэкенд отклоняется при llama_supports_gpu_offload()=false |
| A4. LlamaNativeLoader.cs | ✅ | Строгий набор в одном каталоге, staged-резолвер, освобождение пробных хэндлов при смене стейджа (переключение бэкенда между пробами), защита от Free после P/Invoke, ResetLogCapture, диагностика каталога |
| A5. QwenModel.cs | ✅ | LoadAsync: стейдж→проба→лог llama→GGUF→AutoFit→OOM-ретраи→проверка оффлоада; ActivateCpuFallback (чистый CPU-стейдж, иначе GpuLayers=0 на загруженной llama) во всех точках отказа; явный Cpu не подменяется GPU |
| A6. GpuProbe.cs | ✅ | Проверено в коде: nvidia-smi VRAM, vulkaninfo, IsLegacyAmd, WMI-фолбэк, EnrichFromNative |
| A7. AIConfig.cs | ✅ | Проверено в коде: AutoFitGpu/VramReserveMb/MinGpuLayers/AllowCpuFallback/FlashAttention/NativeLogLevel/ConfidencePolicy, thinking=384 |
| B8. PromptBuilder.cs | ✅ | Правило 6 убрано; легенда индикаторов, рубрика, engine pre-score, tick-точность, без think-префилла; **компиляция исправлена** — string.Create вместо AppendLine(IFormatProvider, string) |
| B9. SignalCalibrator.cs | ✅ | PreScore + Calibrate (ModelOnly/Blend/EngineOverride, дегенерат→prior, ENGINE-ENTRY); отказ инференса (IsInferenceFailure) → HOLD conf=0 при любой политике, пересмотр запрещён при modelConf=0 |
| B10. NormalizedIndicatorSnapshot.cs | ✅ | Сырые ключи (rsi_raw/adx_raw/atr_abs/ema*/bb_*/vwap/macd_hist_abs/volume_ratio); volume SMA20 в IndicatorCalculator |
| B11. TradingEngine.cs | ✅ | DI, явный symbol, tick-aware контекст, activeSymbols и SYMBOL-CAP добавлены |
| C12. ExchangeAccountService.cs | ✅ | Read-only опрос (PeriodicTimer.WaitForNextTickAsync), снимки, ошибки, WS+REST цены, equity/partial |
| C13. BinanceRestClient.cs | ✅ | HasApiKeys, GetAllPricesAsync, exchangeInfo выбранного окружения |
| C14. AccountBalance.cs | ✅ | Free/Locked по активам, совместимость со старым словарём |
| C15. DashboardViewModel.cs | ✅ | Биржевые балансы, режим, ключи, GPU и ошибки; **equity — сумма ВСЕХ активов аккаунта**, EquityIsPartial при любом положительном активе без маршрута в USDT (в т.ч. вне watch-list); таблица позиций остаётся по watch-list |
| C16. TradingViewModel.cs | ✅ | Биржевые позиции/ордера и ручное обновление |
| C17. DashboardView/TradingView.xaml | ✅ | Раздельные панели и прокручиваемые таблицы |
| C18. MainViewModel.cs | ✅ | Dispatcher-подписки, баннер режима, отписка при освобождении |
| C19. App.xaml.cs | ✅ | DI калибратора/аккаунта, запуск опроса, GPU/config preflight |
| C20. appsettings.json | ✅ | Demo REAL ORDERS, AI/AutoFit/confidence, интервалы аккаунта |
| D21. symbols.json | ✅ | 12 пар, сумма активных лимитов 100%, CopyToOutputDirectory |
| D22. TradingConfig/SymbolsRegistry | ✅ | 32 пары, WatchAssets/QuoteAssets, нормализация и лимиты |
| D23. ConfigLoader.cs | ✅ | symbols.json → SymbolsRegistryLoader → SymbolsRegistry.Validate() (одна проверка, без дублирующей суммы) → TradingConfig; реестр перенесён в Common с сохранением namespace; тот же загрузчик использует экран настроек; legacy-строки поддерживаются; без сети |
| D24. SymbolConfig.cs | ✅ | Quote-aware активы, PriceDecimals; tick/step в движке |
| D25. NewsSymbolMapper.cs | ✅ | BTTC/DASH/FLR/ENJ/STRAX/USDT/USDC/FDUSD в словаре, $BTT → BTTC, DASH/FLR в AmbiguousTickers (только капсом); фигурные скобки цикла $TICKER (CS1023/CS0103) |
| D26. SettingsViewModel/View | ✅ | Таблица пар, активы, режим, GPU-сводка, сохранение для следующего старта |

---

Принципы (по требованиям заказчика):
- **Математика — детерминированным алгоритмам** (скоринг, сайзинг, SL/TP, R/R считает движок), ИИ — качественная оценка сетапа и решение.
- **Модель ДУМАЕТ всегда**: `EnableThinking=true` остаётся дефолтом, `/think` остаётся в промпте. Урезаем только бюджет раздумий (1024 → 384–512 токенов), `/no_think` — лишь опция конфига, НЕ дефолт.
- Без компиляций и лишних перепроверок — правки пакетом, один проход.

---

## Историческое состояние до текущего прохода (2026-11-24; закрыто, см. сводку выше)

Исходники восстановлены из `AITradingSystem.zip` в корень клона; текущие правки ведутся там.

- **Блок A (GPU/native) — закрыт полностью**: A1–A3 (ранее), A4 (диагностика состава каталога), A5 (новый конвейер загрузки QwenModel), A6/A7 (реализация найдена в коде и проверена).
- **Блок B (confidence) — закрыт, кроме интеграционных хвостов**: B8 промпт, B9 калибратор, B10 сырые индикаторы, B11 движок (pre-score → калибровка → порог по FINAL, WAITING-лог, cooldown).
- **Не начато**: D21, D23–D26, C12–C20, `appsettings.json`/`symbols.json`. D22 — частично.

### Исторические хвосты (закрыты в текущем проходе; ниже исходная диагностика)
1. **DI (C19, блокер запуска)**: `TradingEngine` теперь требует `SignalCalibrator` в конструкторе — регистрация `services.AddSingleton<SignalCalibrator>()` в `App.xaml.cs` ещё НЕ добавлена.
2. **Точность цен по символу (D24 + B11)**: в `TradingEngine` пока заглушки `GetPriceDecimals(symbol) => 8` и `GetTickSize(symbol) => null`; `BuildAiContext` берёт символ из `position?.Symbol`, поэтому для новых входов точность всегда дефолтная. Нужно: передавать `symbol` явно, кэшировать `tickSize/stepSize` из `GetSymbolInfoAsync` на старте движка и брать `PriceDecimals` из `SymbolConfig`.
3. **Лимиты символов (D22)**: `TradingConfig.Validate` и `SymbolsRegistry.Validate` всё ещё ограничивают список 10 парами (нужно 32); `WatchAssets`/`QuoteAssets` не добавлены.
4. **Проверка порогов после калибровки**: `MinConfidence=0.55` применяется к FINAL — сверить с `RiskManager.ValidateSignal` при интеграции C-блока.

## 0. Диагностика — ПОДТВЕРЖДЕНА ЛОГАМИ

Логи найдены в репозитории: `AITradingSystem.UI/bin/Debug/net8.0-windows/logs/` (ai/trading/trades/exchange/news от 2026-11-23, машина с RTX 3060, `D:\GDot`).

### Факты из логов

**F1. CUDA выбран, но инференс реально на CPU.**
- `19:33:26 AI BACKEND: Cuda12 … RTX 3060 … cuda12 -> есть каталог, llama: найден, ggml-cuda: есть`; `D:\GDot\ -> llama: найден, ggml-cpu: есть` — раскладка ровно как в диагнозе 0.1: CPU-DLL плоско рядом с exe, CUDA в подпапке.
- `модель загружена: backend=Cuda12, ctx=8192, gpuLayers=999` — но время ответа: **173 713 мс, 163 955 мс, 138 660 мс, 180 310 мс** за ~2000–2200 символов (~600–800 токенов) ≈ **3–5 ток/с** — это скорость CPU+RAM на 14B Q4_K. RTX 3060 дал бы 25–40 ток/с. Промпт-процессинг ~900 токенов занимает 40+ с (ответы «0 символов за 40 361 мс») — тоже CPU.
- Нативных строк `offloaded N/N layers to GPU` в логах нет вообще — `llama_log_set` не перехватывается (пункт A1).

**F2. Confidence 0.45 — модель буквально цитирует правило 6 промпта.** Из thinking в `ai-20261123.log` (19:36:31, BTCUSDT):
> «The confidence might be around 0.5, **but the user says to use below 0.55 if unclear** … So the action is **HOLD with confidence 0.45**, since the setup isn't clear enough to meet the confidence threshold of 0.55.»

Все 5 распарсенных сигналов за сессию: `conf=0.45`, HOLD (BTCUSDT ×3, ETHUSDT ×2). В `trades-*.log` только `DECISION … Hold — conf=45 %`. Ни одной попытки BUY.

**F3. Половина ответов модели — пустые или обрезанные.** 10 промптов → 5 `AI PARSE FAIL: в ответе нет JSON-объекта`: ответы «0 символов» (40–43 с) и обрезок на полуслове (314 символов, «The EMA align is»). Причины: (а) префилл пустого `<think>` в конце промпта — Qwen3 при `/think` иногда сразу закрывает блок/выдаёт EOS → срабатывает AntiPrompt → 0 символов; (б) обрыв генерации на CPU-сборке. Лечение в A5/B8: не префиллить пустой `<think>` (пусть модель откроет его сама), пустой ответ → немедленный repair-повтор, короткий бюджет thinking.

**F4. Биржа не опрашивается, режим PAPER.** Стартовая строка: `Старт: Binance="Demo" … режим=PAPER, symbols=BTCUSDT,ETHUSDT`. В `exchange-*.log` только klines и WS-стримы — **ни одного запроса `/api/v3/account` или openOrders**. Баланс/позиции Demo в UI неоткуда взяться — подтверждает 0.3.

**F5. Новости — шум.** В промпт ETHUSDT попадают «Jensen Huang reinstated as Nvidia CEO», «SpaceX sued … Grok» с sentiment −0.20 — маппер пропускает нерелевантное (не блокер, но учитываем в рубрике: новости — вторичный фактор).

### 0.1 Почему при выборе CUDA модель работает на CPU/RAM (i5-12400f + RTX 3060 12GB) — подтверждено F1

1. **Главная причина — раскладка DLL и динамическая регистрация бэкендов.** Сборка UI (`AITradingSystem.UI.csproj`, target `CopyLlamaNativeLibraries`) кладёт CPU-версии `llama.dll, ggml*.dll` **плоско рядом с exe**, а CUDA — в подпапку `cuda12\`. Бэкенды llama.cpp в LLamaSharp 0.30 собраны с `GGML_BACKEND_DL`: `llama.dll` при инициализации **сам сканирует каталог exe** (и `GGML_BACKEND_PATH`) **и регистрирует найденные `ggml-*.dll`. Рядом с exe лежит только `ggml-cpu.dll` → регистрируется CPU-бэкенд; `ggml-cuda.dll` в подпапке никто не регистрирует → все тензоры на CPU, RAM. Наш `DllImportResolver` тут не помогает: он решает, *какой llama.dll загрузить*, а не *какие ggml-бэкенды увидит llama*.
2. `LlamaNativeLoader.HasBackendFiles` (строки ~70–90): второй, «мягкий» проход считает бэкенд доступным, если где-то есть **только** `ggml-cuda.dll` без `llama.dll` в том же каталоге → ложный выбор CUDA, а реально грузится плоский CPU `llama.dll`.
3. **Нет VRAM-префлайта.** `QwenModel.ResolveGpuLayers()` шлёт `999` (все слои) при `ContextSize=8192`. Qwen3-14B Q4_K_M: веса ≈ 9.0 GB + KV-кэш ≈ 1.3 GB (8192 токенов) + compute-буферы ≈ 0.5–1 GB + рабочий стол Windows ≈ 0.5–1.5 GB → **больше 12 GB** → CUDA OOM → тихий откат на CPU.
4. **Нативный лог llama.cpp не перехватывается** (`llama_log_set` не вызывается) — строки `load_tensors: offloaded X/Y layers to GPU` / `using CPU instead` не попадают в наши логи, поэтому по логам приложения не видно, что оффлоада нет.

### 0.2 Почему confidence всегда 0.45 и ничего не покупается — подтверждено F2

1. **Промпт сам якорит модель на 0.45.** `PromptBuilder.cs`, правило 6: *"Use confidence below 0.55 when the setup is unclear"* — единственное число про уверенность в промпте. Модель при `Temperature=0.3` стабильно выдаёт «чуть ниже 0.55» = 0.45 и HOLD.
2. **Нет детерминированного скоринга.** В промпт уходят только нормализованные значения (`NormalizedIndicatorSnapshot.ToDictionary()`), без сырых RSI/ADX/ATR/EMA, без легенды и без рубрики «как считать confidence из фактов» → модели не из чего вывести честную уверенность, она берёт якорь из правила 6.
3. **Порог убивает все сигналы.** `TradingEngine.cs` (~437): `signal.Confidence < AI.MinConfidence (0.55)` → `TradeLog.Rejected("confidence")` → покупок нет никогда. 0.45 < 0.55 всегда.
4. Дополнительно: `appsettings.json` → `PaperTradingMode=true` — даже исполненные сигналы шли бы в симулятор, **на demo Binance ордеров не было бы вообще**.
5. `TradingEngine.cs` (~405): при `indicators == null` (прогрев < 200 свечей) — **молчаливый return**, в логах пусто, кажется что «бот ничего не делает».

### 0.3 Почему не видно позиции/балансы demo Binance — подтверждено F4

1. **UI показывает только внутреннее состояние.** `DashboardViewModel` (47 строк) берёт баланс из `RiskManager.DrawdownTracker` (paper-баланс), `TradingViewModel` — только `PositionManager.GetOpenPositions()` (позиции, открытые ботом в этом процессе). **Никакого опроса аккаунта биржи нет**: `GetAccountBalanceAsync`/`GetOpenOrdersAsync` в `BinanceRestClient` есть, но из UI не вызываются.
2. `AccountBalance.UpdateAsset` сохраняет только `free` (locked теряется для всех активов кроме USDT) → полноценную картину по активам из него не собрать.
3. Нет индикации окружения/режима/ключей: Demo + PaperTradingMode=true выглядит как «торгует на демо», хотя не отправляет ничего.

### 0.4 Xeon E5-2666v3 + RX 590 8GB: Vulkan OOM и краш на любом бэкенде

1. **Vulkan OOM ожидаем**: буфер `token_embd.weight` ≈ 1002 MiB одной аллокацией + остальные веса ≈ 9 GB не влезают в 8 GB (часть занятой десктопом). RX 590 — Polaris (GCN4), старый драйверный лимит на одну аллокацию ~1 GB. Лечится только частичным оффлоадом (VRAM-aware `GpuLayers`, маленький ctx) или CPU. Сейчас префлайта нет — грузим «всё или ничего».
2. **Краш на любом бэкенде, включая CPU** — вероятная причина в `LlamaNativeLoader.TryProbeBackend`: после неудачной пробы вызывается `FreeLoadedHandles()`, который **освобождает хэндлы, уже закэшированные P/Invoke-маршаллером .NET** (кэш `DllImport` на метод). Следующая загрузка (даже CPU) идёт по висячим указателям → AccessViolation. Плюс: если первой пробой загрузился Vulkan-`llama.dll`, `UseBackend` отказывается переключаться («потребует перезапуска»). E5-2666v3 — Haswell, AVX2 есть, так что CPU-сборка avx2 сама по себе рабочая; ломает её именно последовательность проб/освобождений.
3. Лечение: **стейджинг одного выбранного набора DLL в плоский каталог рядом с exe ДО первой загрузки**, `GGML_BACKEND_PATH` на этот каталог, никаких `NativeLibrary.Free` после первого P/Invoke, провал пробы → смена бэкенда через рестарт загрузки с чистым стейджем.

---

## A. GPU / native (Services/AI)

### A1. ✅ NEW `AITradingSystem.Services/AI/GgmlNative.cs` — ВЫПОЛНЕНО 2026-11-23
- P/Invoke обёртки поверх загруженного `llama.dll` через `LlamaNativeLoader.TryGetExport`:
  - `llama_log_set(delegate, userData)` — **свой делегат** (сохранить ссылку в статическом поле, чтобы не собрал GC) → маршрутизация строк в `AiLog.Backend`/diagnostics; парсинг ключевых строк: `offloaded (\d+)/(\d+) layers to GPU`, `using CPU instead`, `CUDA0 buffer size`, `Vulkan0 buffer size`, `out of memory`.
  - `ggml_backend_load_all_from_path(path)` / `ggml_backend_load(path)` — явная загрузка backend-DLL (если экспорт есть в сборке 0.30).
  - `llama_supports_gpu_offload()`, `ggml_backend_dev_count` / `ggml_backend_dev_get(i)` / `ggml_backend_dev_name` / `ggml_backend_dev_memory` — **перечисление реальных устройств и VRAM (free/total) изнутри llama** (точнее nvidia-smi).
- `NativeDeviceInfo { Name, Backend, VramTotalMb, VramFreeMb }`, метод `GetDevices()`.
- Все вызовы безопасны: экспорт не найден → лог + деградация к эвристикам, без краша.

### A2. ✅ NEW `AITradingSystem.Services/AI/GgufInspector.cs` — ВЫПОЛНЕНО 2026-11-23
- Чтение GGUF-заголовка/метаданных без загрузки модели (BinaryReader, magic `GGUF`, v2/v3):
  - `general.architecture`, `{arch}.block_count` (слои), `{arch}.context_length`, `{arch}.embedding_length`, `{arch}.attention.head_count_kv`, `{arch}.attention.key_length`/`value_length`, vocab.
  - Список тензоров: имя, тип квантования, размеры → **точный размер весов** и размер `token_embd.weight` (для оценки «влезет ли один буфер»).
- Расчёты: `WeightBytesTotal`, `BytesPerLayer`, `KvBytesPerToken(ctx, kvType)` (f16/q8_0), `ComputeBufferEstimate(batch, ctx)`.
- `RecommendGpuLayers(vramFreeMb, reserveMb, ctx, flashAttn, kvType)` → максимум слоёв, влезающий в VRAM; отдельно `CanFullyOffload`.
- Используется QwenModel до `LLamaWeights.LoadFromFile` (интеграция — в A5).

### A3. ✅ NEW `AITradingSystem.Services/AI/NativeStaging.cs` — ВЫПОЛНЕНО 2026-11-23
- `Stage(AiBackend backend, string? explicitPath)`: копирует **выбранный** набор (`llama.dll`, `ggml-base.dll`, `ggml.dll`, `ggml-cpu*.dll`, `ggml-cuda.dll` + `cudart/cublas*` или `ggml-vulkan.dll`) в плоский каталог рядом с exe (или `%LOCALAPPDATA%\AITradingSystem\native\<backend>`, если exe-каталог read-only).
- Гарантирует наличие **CPU-вариантов ggml-cpu** в том же каталоге (llama.cpp требует CPU-бэкенд всегда, даже при CUDA).
- Устанавливает `GGML_BACKEND_PATH` на этот каталог **до первого вызова llama API**; `SetDllDirectory`/`AddDllDirectory` для зависимостей CUDA.
- Убирает конфликт «плоский CPU ggml-cpu.dll рядом с exe + CUDA в подпапке»: при staged-загрузке резолвер смотр ТОЛЬКО в staged-каталог (строгий режим резолвера — в A4; сейчас staged-каталог приоритетный через `Initialize`).
- Вызывается из `App.xaml.cs` на старте (после выбора бэкенда) и из `QwenModel.LoadAsync` при смене бэкенда (интеграция — в A5/C19).

### A4. ✅ MODIFY `AITradingSystem.Services/AI/LlamaNativeLoader.cs` — ВЫПОЛНЕНО (проверено 2026-11-24)
- `public static bool TryGetExport(string library, string export, out IntPtr ptr)` — через `NativeLibrary.TryGetExport` на загруженном хэндле (для A1). ✅ сделано
- `public static string? GetBackendDirectory()` — каталог фактической загрузки (стейдж или найденный). ✅ сделано
- `HasBackendFiles`: **убрать мягкий fallback** (marker без llama). Требовать `llama.dll` + `ggml-base` + marker **в ОДНОМ каталоге**; для CUDA дополнительно `cudart64_12`/`cublas64_12`.
- `TryProbeBackend`: после неудачи **не вызывать `FreeLoadedHandles`, если был хоть один успешный резолв для LLamaSharp** — вместо этого пометить процесс «требуется чистый стейдж» и грузить следующий кандидат из изолированного staged-каталога; `FreeLoadedHandles` оставить только для этапа до первого P/Invoke LLamaSharp.
- Резолвер: если стейдж активен — искать только в нём (приоритет), иначе текущий порядок каталогов.
- Диагностика: печатать итоговый каталог, состав файлов, `GGML_BACKEND_PATH`.

### A5. ✅ MODIFY `AITradingSystem.Services/AI/QwenModel.cs` — ВЫПОЛНЕНО 2026-11-24
- Новый порядок `LoadAsync`:
  1. `GpuProbe.Detect` (с VRAM) → выбор бэкенда → `NativeStaging.Stage` → `LlamaNativeLoader.Initialize/TryProbeBackend`.
  2. `GgmlNative.InstallLogCapture()` → нативный лог в `AiLog`.
  3. `GgufInspector.Inspect(modelPath)` → слои/размеры/KV.
  4. `AutoFitGpu=true`: `gpuLayers = GgufInspector.RecommendGpuLayers(vramFree - VramReserveMb, ctx, …)`; при нехватке VRAM сначала урезать ctx (8192 → 4096), потом слои; `MinGpuLayers` — ниже него не оффлоадим (смысла нет → сразу CPU + предупреждение).
  5. Загрузка; **при OOM** (`ErrorOutOfDeviceMemory`, `unable to allocate … buffer`, CUDA OOM в нативном логе/исключении) — до 2 повторов с `gpuLayers *= 0.7` и ctx/2; исчерпано → чистый откат CPU (`AllowCpuFallback`), явный лог «работаем на CPU, причина X».
  6. После загрузки — **фактический оффлоад из нативного лога** (`offloaded X/Y`) + устройства из `GgmlNative.GetDevices()`; если `ActiveBackend=Cuda12`, а offload=0 → громкий WARN и попытка рестейджа/перезагрузки один раз.
- `FlashAttention` из конфига в `ModelParams` (если доступно в LLamaSharp 0.30), `KvCacheType` опционально.
- Confidence-часть (см. B): в `ParseSignalFromResponse` не терять «сырую» уверенность модели; дегенерат-детект (conf ≈ 0.45 ± 0.02 при HOLD) → помечать сигнал флагом для калибратора; логировать `model_conf` и `engine_prior` раздельно.
- **Пустой ответ (0 символов, F3)** → не считать «невалидным JSON» вслепую: немедленный повтор с repair-промптом без think-префилла; в лог — отдельная причина «пустой ответ (anti-prompt/EOS)».
- Thinking остаётся включённым; бюджет из конфига (384–512). Опция `AI:EnableThinking=false` → `/no_think` (не дефолт).

### A6. ✅ MODIFY `AITradingSystem.Services/AI/GpuProbe.cs` — ВЫПОЛНЕНО (реализация найдена в коде, проверена 2026-11-24)
- NVIDIA: `nvidia-smi --query-gpu=name,memory.total,memory.free --format=csv,noheader,nounits` → `NvidiaGpus` c VRAM.
- Vulkan: `vulkaninfo --summary` (если есть) → имена устройств; определение старых AMD (Polaris/GCN: RX 4xx/5xx, Vega) → флаг `IsLegacyAmd` + предупреждение «лимит ~1GB на аллокацию, рекомендуется частичный оффлоад/CPU».
- `GpuDetectionResult`: `VramTotalMb`, `VramFreeMb`, `GpuName`, `IsLegacyAmd`; всё в `Summary` (пишется в лог и в UI).
- Фолбэк VRAM: WMI `Win32_VideoController.AdapterRAM` (грубо, >4GB врёт — только как последний резерв), затем данные из `GgmlNative.GetDevices()` (точные).

### A7. ✅ MODIFY `AITradingSystem.Common/Configuration/AIConfig.cs` — ВЫПОЛНЕНО (реализация найдена в коде, проверена 2026-11-24)
- Новые опции (с дефолтами и Validate):
  - `AutoFitGpu = true` — авторасчёт слоёв/ctx по VRAM;
  - `VramReserveMb = 1024` — запас под десктоп/буферы;
  - `MinGpuLayers = 8` — минимум слоёв для осмысленного GPU;
  - `AllowCpuFallback = true`;
  - `FlashAttention = true`;
  - `NativeLogLevel = "Info"` (Off/Error/Warn/Info/Debug → `llama_log_set`);
  - `ThinkingBudgetTokens`: дефолт 1024 → **384** (thinking НЕ отключаем);
  - `ConfidencePolicy = "Blend"` (ModelOnly | Blend | EngineOverride) — см. B9.
- `EffectiveMaxTokens`: `MaxTokens + ThinkingBudgetTokens` как сейчас (400 + 384 = 784 — быстрее ответ).

---

## B. Confidence / «ничего не покупает» (Services/AI + Core)

### B8. ✅✅ MODIFY `AITradingSystem.Services/AI/PromptBuilder.cs` — ВЫПОЛНЕНО 2026-11-24
- **Удалить правило 6** («confidence below 0.55…») — источник якоря 0.45. Вместо него: «confidence MUST be derived from the evidence rubric below; do not default to any fixed number».
- Легенда индикаторов: что нормализовано и как, что сырое (rsi_raw 0–100, adx_raw 0–100, atr_abs, ema20/50/200 абсолютные, bb_upper/lower, volume_ratio и т.д.).
- **Рубрика уверенности** (детерминированная инструкция): старт от `engine pre-score` (приходит в промпте, см. B9), +0.05…0.15 за совпадение тренда/моментума/объёма, −0.1…0.2 за конфликты/новостной риск/низкий ADX; итог clamp [0.1; 0.9]. 2 коротких примера (сильный сетап → BUY conf 0.72; смешанный → HOLD conf 0.4).
- В промпт добавить блок `Engine pre-score: direction=LONG/FLAT, score=0.63, reasons=[...]` — модель якорится на реальных фактах, а не на выдуманном числе.
- Thinking: инструкция короче (≤ ~120 слов, бюджет 384 токена), `/think` остаётся; `/no_think` только при `EnableThinking=false`.
- **Не префиллить пустой `<think>`** в конце промпта (F3: модель иногда сразу закрывает блок → ответ 0 символов). Пусть открывает его сама; `StripThinking` уже умеет оба варианта.
- Цены: форматировать по tickSize символа (BTTC ~0.0000006 — текущий формат `0.########` обрезает/округляет до нуля → невалидные цены; передавать `price_format`/tick в контекст).

### B9. ✅✅ NEW `AITradingSystem.Services/Analysis/SignalCalibrator.cs` — ВЫПОЛНЕНО 2026-11-24
- `PreScore(Dictionary<string,decimal> indicators)` → `EngineScore { Direction (-1..1), PriorConfidence (0.1..0.9), Components[], Reasons[] }` — **чистая математика, без ИИ**:
  - тренд: ema_align, supertrend, ema20/50/200_pos (взвешенно);
  - моментум: rsi_raw (зоны 30/70), macd_hist, stoch_rsi k/d cross;
  - сила тренда: adx_raw (<20 → штраф, >25 → бонус);
  - объём: obv_trend, volume_ratio; волатильность: atr_pct (слишком низкая/высокая → штраф);
  - bb_pos (перекупленность у верхней полосы → штраф для BUY).
- `Calibrate(TradingSignal modelSignal, EngineScore score, ConfidencePolicy policy)`:
  - `Blend` (дефолт): `final = 0.5*model + 0.5*prior`; **если дегенерат** (HOLD и |conf−0.45| ≤ 0.02) → `final = prior`, а action пересматривается только если prior ≥ MinConfidence и direction согласован (BUY при score>0); иначе остаётся HOLD, но confidence честный;
  - `EngineOverride`: final = prior всегда; `ModelOnly`: как сейчас.
- Лог обоих значений: `AiLog`/`TradeLog`: `model_conf=0.45, engine_prior=0.68, final=0.68, policy=Blend`.
- SL/TP/сайзинг остаются за RiskManager (математика — алгоритмам).

### B10. ✅✅ MODIFY `AITradingSystem.Core/Indicators/NormalizedIndicatorSnapshot.cs` (+ `IndicatorCalculator.cs`) — ВЫПОЛНЕНО 2026-11-24
- В `ToDictionary()` добавить сырые значения (все геттеры уже есть в калькуляторе: `RSI.GetValue()`, `ADX.Value`, `StochasticRsi.K/D`, `ATR.GetValue()`, `EMA20/50/200.GetValue()`, `BollingerBands.Upper/Lower`, `OBV.Trend`, `MACD.Histogram`):
  `rsi_raw, adx_raw, stoch_k_raw, stoch_d_raw, atr_abs, ema20, ema50, ema200, bb_upper, bb_lower, macd_hist_abs, vwap` + `volume_ratio` (объём последней свечи / SMA20 объёма — добавить маленький расчёт в `IndicatorCalculator`, там есть история свечей).
- Нормализованные ключи не менять (обратная совместимость с тестами).

### B11. ✅🔄 MODIFY `AITradingSystem.Services/Trading/TradingEngine.cs` — ОСНОВНОЕ ВЫПОЛНЕНО 2026-11-24, хвосты ниже
- Перед вызовом ИИ: `var preScore = _calibrator.PreScore(indicators)`; залогировать (`TradeLog.Decision(symbol,"PRE-SCORE", …)`), передать в промпт через `AiRequestContext` (новые поля `EngineScore`, `EngineReasons`).
- После сигнала: `signal = _calibrator.Calibrate(signal, preScore, _config.AI.ConfidencePolicy)`; порог `MinConfidence` применять к **final** (порог 0.55 оставляем).
- `indicators == null` → **не молчать**: `TradeLog.Waiting(symbol, "прогрев индикаторов: N/200 свечей")` (не чаще раза в минуту на символ).
- Аналогично в `ReviewOpenPositionAsync` (строки ~629+).
- Опционально: per-symbol cooldown анализа (например 60 с), чтобы при 12+ символах и медленном инференсе очередь не забивалась одним символом.

---

## C. Биржевые позиции / балансы / информация в UI

### C12. ✅ NEW `AITradingSystem.Services/Trading/ExchangeAccountService.cs`
- Таймер (15–30 с, настраиваемо): `GetAccountBalanceAsync()` (полный список активов), `GetOpenOrdersAsync()`, цены — из кэша WS-тикеров `BinanceWebSocketClient` + фолбэк `GetAllPricesAsync` (новый, C13).
- Из балансов вывести «биржевые позиции»: для каждого `WatchAssets`/базовых активов настроенных пар количество > порога (minNotional) → `ExchangePosition { Asset, Free, Locked, LastPrice, ValueInQuote }`.
- Свойства: `AccountBalance`, `ExchangePositions`, `OpenOrders`, `EnvironmentName`, `HasApiKeys`, `LastRefreshUtc`, `LastError`; события `OnAccountUpdated`, `OnOrdersUpdated`.
- Paper-режим: сервис всё равно читает реальные балансы Demo (чтение не торгует), но помечает `Mode=PAPER` и рисует предупреждение «ордера не отправляются на биржу».
- Ошибки API (нет ключей / -2015 / IP ban) → `LastError` в UI, не кидать исключения в цикл.

### C13. ✅ MODIFY `AITradingSystem.Data/Binance/IBinanceRestClient.cs` + `BinanceRestClient.cs`
- Добавить: `bool HasApiKeys { get; }` (из `BinanceConfig`, без расшифровки — просто непустые Encrypted-поля);
- `Task<Dictionary<string,decimal>> GetAllPricesAsync(ct)` → `/api/v3/ticker/price` (без ключей, для оценки активов);
- `GetOpenOrdersAsync`, `GetAccountBalanceAsync`, `GetSymbolInfoAsync`, `EnvironmentName` — уже есть, не трогаем;
- `GetMyTradesAsync` — опционально, не в этот заход.

### C14. ✅ MODIFY `AITradingSystem.Core/Models/Trading/AccountBalance.cs`
- `AssetBalances` сейчас хранит только free. Добавить `Dictionary<string,(decimal Free, decimal Locked)> Assets` (или класс `AssetBalance`), `UpdateAsset` пишет оба; старые свойства USDT сохранить для совместимости.

### C15. ✅ MODIFY `AITradingSystem.UI/ViewModels/DashboardViewModel.cs`
- Новые observable-поля: `UsdtFree/Locked`, `FdusdFree/Locked`, `UsdcFree/Locked`, `EquityText` (сумма активов в USDT по ценам), `EnvironmentText` (Demo/Testnet/Live), `ModeText` (PAPER/REAL), `KeysStatusText`, `ConnectionText`, `BackendText` (Cuda12/Vulkan/Cpu + GPU name + VRAM free/total + фактически offload N/M слоёв), `LastInferenceMs`, `LastErrorText`; коллекция `AssetBalances` (все активы из C12).
- Paper-метрики (DrawdownTracker) остаются отдельной панелью, не подменяют биржевые.

### C16. ✅ MODIFY `AITradingSystem.UI/ViewModels/TradingViewModel.cs`
- Коллекции `ExchangePositions`, `ExchangeOpenOrders` + `[RelayCommand] RefreshAccountAsync` (ручное обновление через C12); внутренние позиции бота остаются как есть.

### C17. ✅ MODIFY `AITradingSystem.UI/Views/DashboardView.xaml`, `TradingView.xaml`
- Dashboard: панель «Биржа (Demo)»: окружение/режим/ключи/соединение, USDT/FDUSD/USDC free/locked, equity, таблица активов, панель «AI/GPU»: backend, GPU, VRAM, offload, время инференса, последняя ошибка.
- Trading: две новые таблицы «Позиции на бирже» и «Открытые ордера на бирже» + кнопка «Обновить».

### C18. ✅ MODIFY `AITradingSystem.UI/ViewModels/MainViewModel.cs`
- Подписка на `OnAccountUpdated/OnOrdersUpdated` (через Dispatcher, как сделано для движка), обновление Dashboard/Trading;
- предупреждение-баннер: `PaperTradingMode=true` при непустых ключах Demo → «PAPER: ордера не отправляются на биржу»; нет ключей → «ключи не заданы, балансы недоступны».

### C19. ✅ MODIFY `AITradingSystem.UI/App.xaml.cs`
- Регистрация: `ExchangeAccountService` (singleton + запуск после старта хоста), `SignalCalibrator`.
- На старте: `GpuProbe.Detect` → лог GPU/VRAM summary; ранний `NativeStaging` (до любого обращения к LLamaSharp); `GgmlNative.InstallLogCapture` сразу после первой загрузки llama.
- Лог конфигурации: backend, AutoFitGpu, ctx, thinking budget, symbols count, mode/environment.

### C20. ✅ MODIFY `AITradingSystem.UI/appsettings.json`
- `Trading:PaperTradingMode=false` (реальные ордера на Demo), `Symbols` — расширенный список (или загрузка из symbols.json, D23);
- `AI`: `ContextSize=4096`, `ThinkingBudgetTokens=384` (thinking включён!), `MaxTokens=400`, `AutoFitGpu=true`, `VramReserveMb=1024`, `MinGpuLayers=8`, `FlashAttention=true`, `NativeLogLevel="Info"`, `ConfidencePolicy="Blend"`, `MinConfidence=0.55` (оставляем — теперь работает поверх калибратора);
- `EnableThinking=true` — без изменений.

---

## D. Символы

### D21. ✅ NEW `AITradingSystem.UI/symbols.json` (CopyToOutputDirectory=PreserveNewest в csproj)
```json
{
  "Version": "1.1",
  "Symbols": [
    { "Symbol": "BTTCUSDT",  "Enabled": true, "MaxPositionPercent": 5 },
    { "Symbol": "DOGEUSDT",  "Enabled": true, "MaxPositionPercent": 10 },
    { "Symbol": "DASHUSDT",  "Enabled": true, "MaxPositionPercent": 8 },
    { "Symbol": "XRPUSDT",   "Enabled": true, "MaxPositionPercent": 12 },
    { "Symbol": "FLRUSDT",   "Enabled": true, "MaxPositionPercent": 5 },
    { "Symbol": "ENJUSDT",   "Enabled": true, "MaxPositionPercent": 5 },
    { "Symbol": "STRAXUSDT", "Enabled": true, "MaxPositionPercent": 5 },
    { "Symbol": "BNBUSDT",   "Enabled": true, "MaxPositionPercent": 12 },
    { "Symbol": "USDCUSDT",  "Enabled": true, "MaxPositionPercent": 10 },
    { "Symbol": "FDUSDUSDT", "Enabled": true, "MaxPositionPercent": 10 },
    { "Symbol": "BTCUSDT",   "Enabled": true, "MaxPositionPercent": 15 },
    { "Symbol": "ETHUSDT",   "Enabled": true, "MaxPositionPercent": 15 }
  ],
  "WatchAssets": ["BTTC","DOGE","DASH","XRP","USDT","FDUSD","FLR","ENJ","USDC","STRAX","BNB"],
  "QuoteAssets": ["USDT","FDUSD","USDC","BTC"]
}
```
- USDT — стейбл, не торгуется «сам к себе»: только как watch-asset/баланс. USDCUSDT/FDUSDUSDT — валидные пары (низкая волатильность, скоринг это учтёт — почти всегда HOLD, это нормально).
- Сумма MaxPositionPercent активных ≤ 100 (валидация есть) — подобрать значения.

### D22. ✅🔄 MODIFY `AITradingSystem.Common/Configuration/TradingConfig.cs` + `Core/Models/Config/SymbolsRegistry.cs`
- Лимит символов 10 → **32** в обоих `Validate()`; `MaxOpenPositions` лимит 10 → 32 (дефолт оставить 3–5).
- Новые поля: `List<string> WatchAssets`, `List<string> QuoteAssets`.
- Нормализация символа: Trim/Upper, проверка `BASE+QUOTE` по списку QuoteAssets (longest-match), отсев мусора с предупреждением.

### D23. ✅ MODIFY `AITradingSystem.Common/Configuration/ConfigLoader.cs`
- **Сейчас symbols.json не читается вообще** (упомянутый в комментариях SymbolRegistryService отсутствует). Добавить: чтение `symbols.json` (optional) → десериализация в `SymbolsRegistry` → merge в `config.Trading.Symbols/WatchAssets/QuoteAssets` (appsettings имеет приоритет, если Symbols там непустой — или наоборот, зафиксировать: symbols.json главный).
- Валидация против exchangeInfo — **не на загрузке** (без сети), а на старте движка: `GetValidSymbolsAsync` → неизвестные/недоступные на Demo пары отключаются с WARN в лог и UI.

### D24. ✅ MODIFY `AITradingSystem.Core/Models/Config/SymbolConfig.cs`
- `BaseAsset/QuoteAsset`: сейчас тупой `Substring(len-4)` — заменить на quote-aware парсинг по известным котировкам (USDT/FDUSD/USDC/BTC/ETH…, longest-first). Поддержка стейбл-пар.
- `PriceDecimals`/tick-aware форматирование цен для промпта и ордеров (BTTC: 8–10 знаков; брать tickSize из `GetSymbolInfoAsync`).

### D25. ✅ MODIFY `AITradingSystem.Data/News/NewsSymbolMapper.cs`
- Добавить: `bittorrent/btt/bttc → BTTC`, `dash → DASH`, `flare/flr → FLR`, `enjin/enj → ENJ`, `stratis/strax → STRAX`, `tether/usdt → USDT`, `usd coin/usdc → USDC`, `first digital usd/fdusd → FDUSD` (XRP/DOGE/BNB уже есть).

### D26. ✅ MODIFY `AITradingSystem.UI/ViewModels/SettingsViewModel.cs` (+ `SettingsView.xaml`)
- Редактирование списка символов (добавить/убрать/вкл-выкл, валидация), WatchAssets;
- переключатель PaperTradingMode с предупреждением;
- инфо-панель: backend, GPU/VRAM, offload, AutoFitGpu/VramReserveMb (read-only или редактируемо).

---

## E. Порядок выполнения и внешняя приёмка (не выполнялась в песочнице)

Порядок одним заходом: **A (1→7) → B (8→11) → D (21→26) → C (12→20) → appsettings/symbols.json**.
Без сборки/прогонов в песочнице (проект WPF/net8.0-windows, таргет — машина заказчика); правки отдаются готовым кодом.

Приёмка:
1. **GPU**: в `logs/ai-*.log` есть нативные строки `offloaded N/N layers to GPU (CUDA0)`; в Dashboard — «Cuda12, RTX 3060, VRAM 12288 MB, offload N/M»; RAM не растёт на размер модели; время инференса — секунды, не минуты.
2. **RX 590**: нет краша; при нехватке VRAM — частичный оффлоад или явный CPU-фолбэк с причиной в логе; повторный выбор бэкенда не требует перезапуска.
3. **Confidence**: значения разные (0.3–0.8), в логе `model_conf / engine_prior / final`; при сильном сетапе final ≥ 0.55 → ордер уходит на demo-api.binance.com; в логе нет «Rejected: confidence» на каждый сигнал.
4. **UI**: видны реальные балансы Demo (USDT и монеты), открытые ордера, окружение/режим/ключи; после ручной покупки на demo — позиция появляется в «Позиции на бирже» ≤ 30 с.
5. **Символы**: все 12 пар из symbols.json валидируются, недоступные на Demo — отключены с предупреждением, BTTC-цены не округляются в ноль.

## Решения заказчика
1. Логи — получены, диагноз подтверждён (F1–F5).
2. Торговать сразу на Demo (`PaperTradingMode=false`) — **да, по плану**.
3. Модель та же `qwen3-14b-q4_k_m.gguf`; AutoFit подберёт слои/ctx. Qwen3-8B — опционально, в план не закладываем.


## F. Итог текущего прохода и ограничения приёмки

Файловые задачи текущего плана завершены; изменения зафиксированы в `changes.md`. Сборка, компиляция, тестовые прогоны, запуск WPF, инференс и обращения к аккаунту Binance не выполнялись по требованию заказчика.

Перед использованием на целевой машине необходимо проверить:
1. Восстановление/сборку проекта заказчиком, NuGet-зависимости и раскладку native DLL.
2. Загрузку GGUF и фактический GPU offload по нативному логу; CPU fallback должен иметь явную причину.
3. Demo-ключи DPAPI, доступность выбранных пар и фильтров, чтение балансов/openOrders.
4. Разделение PAPER и REAL ORDERS в UI; сохранение настроек и применение после перезапуска.
5. Сигналы, калибровку и лимиты на контролируемом Demo-аккаунте до любого переключения на Live.

`README.md` обновлён: текущие исходники находятся в корне, ZIP является историческим снимком; Demo с PaperTradingMode=false отправляет ордера на Demo.
