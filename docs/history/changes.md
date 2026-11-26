

## 2026-11-25 — Пакет Qwen3/Vulkan: повторения/артефакты, жёсткий think-бюджет, KV-reuse, сторож деградации

Основание: `trdupd.md` + llama.cpp issue #24812 (кластерный мусор на Vulkan/RADV RX590; фикс в llama.cpp ≈ b9892).
Без компиляций/сборок/прогонов — правки пакетом, один проход. Приложенный заказчиком txt оказался служебным баннером GitHub,
не логом аномалий; диагностика восстановлена по первоисточнику issue.

### Внесено
- NEW `Services/AI/DegenerateOutputDetector.cs` — потоковый сторож мусора/циклов: ≥32 одинаковых символов подряд,
  ≥6 повторов блока 2…64 симв., ≥3 спецтокенов `<unusedN>`/`<|…|>`, ≥4 U+FFFD. Срабатывание залипает, `Reason` для лога.
- NEW `Services/AI/KvManagedExecutor.cs` — постоянный KV-контекст вместо `StatelessExecutor`: переиспользование общего
  префикса промпта, удаление старого хвоста (`MemorySequenceRemove`), полная чистка (`MemoryClear`) при деградации/таймауте/ошибке,
  `HardReset` пересозданием контекста. Жёсткий think-бюджет: принудительное декодирование `\n</think>\n\n` в KV.
  Сэмплеры Qwen3 через `DefaultSamplingPipeline` (TopK/MinP/Repeat/Presence/Frequency/PenaltyCount, PenalizeNewline=false).
  `DecodeSpecialTokens=true`, `PromptTooLongException`, частичный ответ при таймауте.
- MODIFY `Common/Configuration/AIConfig.cs` — Qwen3-сэмплеры и штрафы, раздельные think/no_think temperature/top-p,
  ThinkingBudgetTokens 128→96 (жёсткий стоп), InferenceTimeoutMs 600000→180000, KvCacheType F16→Q8_0,
  ReuseKvContext/KvMinReusePrefix/DetectDegenerateOutput/DegenerateResetThreshold/DegenerateReloadThreshold/MaxNewsChars,
  Validate() для новых полей.
- MODIFY `Services/AI/QwenModel.cs` — переход на `KvManagedExecutor`; `ModelParams` напрямую (FlashAttention/TypeK/TypeV/UBatchSize),
  удалены мёртвые рефлекс-хелперы; корректное освобождение executor/weights в `LoadOnce`, `VerifyOffload`, `UnloadAsync`;
  `GenerateResponseAsync` возвращает `KvGenerationResult` с linked-CTS таймаутом и частичным ответом;
  `GetTradingSignalAsync` логирует диагностику генерации и эскалацию деградации: KV-чистка → HardReset → перезагрузка весов → CPU-фолбэк;
  успешный сигнал сбрасывает счётчики. Добавлено влезание промпта: новости режутся до `MaxNewsChars`, затем половинное урезание,
  repair-промпт при нехватке места откатывается к базовому. При активации Vulkan — WARN обновить native DLL до llama.cpp ≥ b9892 (#24812).
- MODIFY `UI/appsettings.json` — AI-секция приведена к плану: Temperature 0.6, TopP 0.95, NoThinkTemperature 0.7, NoThinkTopP 0.8,
  TopK 20, MinP 0, RepeatPenalty 1.05, PresencePenalty 0.5, FrequencyPenalty 0, PenaltyLastN 256, ThinkingBudgetTokens 96,
  MaxTokens 320, InferenceTimeoutMs 180000, KvCacheType Q8_0, ReuseKvContext true, KvMinReusePrefix 64,
  DetectDegenerateOutput true, DegenerateResetThreshold 2, DegenerateReloadThreshold 3, MaxNewsChars 2500.
- NEW `Tests/DegenerateOutputDetectorTests.cs` — xUnit: `?`×40 (в т.ч. по кускам), цикл `<unused49>`, цикл «0.45, »,
  U+FFFD×4, чистый JSON/think+JSON не флагуются, disabled-режим, залипание сторожа.
- MODIFY `trdupd.md` — план/статусы обновлены по ходу выполнения; все файловые шаги пакета закрыты.

### Отклонения / заметки
- Урезание новостей до влезания выполняется в `QwenModel` (`BuildPromptWithinBudget`), не в `PromptBuilder.cs`.
  `PromptBuilder.cs` не менялся; его существующие тесты не затрагиваются.
- Главное лечение #24812 — обновление нативных DLL оператором до llama.cpp ≥ b9892 через `AI:NativeLibraryPath`/стейджинг.
  Сторож + KV-чистка + сброс + CPU-фолбэк — защитный контур приложения, не замена обновлению DLL.
- На legacy AMD Vulkan квантованный KV принудительно остаётся F16 (консервативно).
- Компиляции/сборки/тесты/инференс не запускались (требование заказчика). Runtime-приёмка — на машине заказчика.

## 2026-11-24 — Пакет «остатки аудита»: B8 (компиляция), C15, D23, D25 + перенос реестра в Common

Основание: аудит заказчика (A1–D26) и лог сборки `AITradingSystem.Data` (CS1023/CS0103 в NewsSymbolMapper).
Без компиляций/сборок/прогонов — правки пакетом, один проход.

### Важно о состоянии дерева
Лог сборки заказчика (`NewsSymbolMapper.cs(87,13) CS1023`, `(88,49)/(88,84) CS0103`) относится к состоянию **до** предыдущего пакета.
В текущем дереве цикл `foreach (Match m in Regex.Matches(haystack, @"\$([A-Za-z]{2,10})\b"))` уже имеет фигурные скобки,
`var ticker` и `result.Add(...)` находятся внутри тела цикла — этих трёх ошибок в исходниках больше нет.
Также уже присутствуют правки прошлого пакета: A2 (однократный учёт KV), A3 (строгий CUDA-рантайм + отклонение GPU-бэкенда),
A4 (освобождение пробных хэндлов при смене стейджа + `ResetLogCapture`), A5 (`ActivateCpuFallback`), B9 (`IsInferenceFailure`),
C12 (`PeriodicTimer.WaitForNextTickAsync`). Этот пакет закрывает то, что аудит оставил ⚠️/❌.

### Внесено сейчас
- MODIFY `Services/AI/PromptBuilder.cs` (**B8, блокер компиляции Services**): строка «Price precision … (tick size …)»
  собиралась как `$"..." + (условие ? $"..." : string.Empty)` и передавалась в `sb.AppendLine(CultureInfo.InvariantCulture, …)`.
  У `StringBuilder` есть только `AppendLine(IFormatProvider, ref AppendInterpolatedStringHandler)` — перегрузки
  `(IFormatProvider, string)` НЕ существует, а конкатенация даёт обычный `string` → CS1503 (аргумент не приводится к
  handler-типу). Теперь строка целиком форматируется через `string.Create(CultureInfo.InvariantCulture, $"…")`,
  а в `AppendLine(string)` отдаётся готовый результат. Инвариантная культура и формат tick `0.##########` сохранены.
- MODIFY `Services/Trading/ExchangeAccountService.cs` (**C15, equity — сумма ВСЕХ активов**): оценка депозита больше
  не ограничена watch-list'ом и базовыми активами настроенных пар. Цикл идёт по `AccountBalance.Assets` целиком:
  любой положительный актив с маршрутом конвертации добавляется в `Equity`, любой положительный актив БЕЗ маршрута
  выставляет `EquityIsPartial=true` — включая активы вне watch-list (раньше они молча выпадали из суммы и не помечали
  оценку частичной). Таблица «позиции на бирже» по-прежнему фильтруется watch-list'ом и `MinExchangePositionNotional`.
  `EstimateUsdt`: сравнение с USDT переведено на `StringComparison.OrdinalIgnoreCase`.
- NEW `Common/Configuration/SymbolsRegistryLoader.cs` (**D23**): единая точка чтения `symbols.json` для конфигурации
  и экрана настроек. Разбор в модель `SymbolsRegistry` (`System.Text.Json`), поддержка legacy-формата
  `"Symbols": ["BTCUSDT", …]` (строка превращается в объект реестра), комментарии/хвостовые запятые, явные `null`.
  Котировки/активы: файл главный, appsettings — fallback (в т.ч. когда ключа в файле нет, ведь `QuoteAssets`
  по умолчанию заполнен `DefaultQuotes`). Повреждённый JSON → `InvalidOperationException` (осознанный отказ старта).
- MODIFY `Common/Configuration/ConfigLoader.cs` (**D23**): ручной разбор `JsonDocument` и **вторая** сумма активных
  лимитов (`enabledLimitSum`) удалены. Теперь: `SymbolsRegistryLoader.TryLoad` → `SymbolsRegistry.Validate()`
  (нормализация BASE+QUOTE, дубликаты, диапазон лимитов, сумма активных ≤ 100% — ровно один раз) → merge в `TradingConfig`.
  В `Symbols`/`SymbolPositionLimits`/`SymbolPriceDecimals` попадают только `Enabled=true`; отключённые пары остаются
  в реестре и не теряются. Пустой `Symbols` в файле не затирает список из appsettings; все пары отключены → явная ошибка.
- MOVE `Core/Models/Config/SymbolConfig.cs` → `Common/Models/Config/SymbolConfig.cs` и
  `Core/Models/Config/SymbolsRegistry.cs` → `Common/Models/Config/SymbolsRegistry.cs` (**D23**): Common не ссылается
  на Core, а реестр нужен загрузчику конфигурации. **Namespace `AITradingSystem.Core.Models.Config` сохранён** —
  `using`'и в Core/Data/Services/UI не меняются, публичный API тот же. Файлы физически перенесены (не скопированы),
  дублей типов нет; SDK-проекты собирают `.cs` по маске, правки `.csproj` не требуются.
- MODIFY `UI/ViewModels/SettingsViewModel.cs` (**D23**): `LoadRegistry()` вызывает тот же `SymbolsRegistryLoader`.
  Побочный эффект — исправлен падёж конструктора VM (и всего DI-резолва `MainWindow`) на legacy/повреждённом
  `symbols.json`: раньше `JsonSerializer.Deserialize<SymbolsRegistry>` бросал исключение прямо в конструкторе.
  Теперь ошибка показывается в `StatusText`, а список берётся из appsettings.
- MODIFY `Data/News/NewsSymbolMapper.cs` (**D25, было ❌**): словарь дополнен
  `bittorrent/btt/bttc → BTTC`, `dash → DASH`, `flare/flr/flare networks → FLR`, `enjin/enj → ENJ`,
  `stratis/strax → STRAX`, `tether/usdt → USDT`, `usd coin/usdc → USDC`, `first digital usd/fdusd → FDUSD`.
  `$BTT` теперь действительно даёт `BTTC` (механизм `$TICKER → NameToTicker` был, самого ключа не хватало).
  `DASH` и `FLR` добавлены в `AmbiguousTickers`: «dash»/«flare» — обычные английские слова, поэтому ищутся только
  капсом (`\bDASH\b`, `\bFLR\b`), а `$DASH`/`$FLR` работают через словарь. Остальные новые имена не омонимы.

### Статусы аудита после пакета
| Пункт | Было | Стало | Комментарий |
|---|---|---|---|
| A1 | ✅ static | ✅ static | Runtime-приёмка (нативный лог `offloaded N/M`) остаётся внешней |
| A2 | ⚠️ KV double count | ✅ | KV учитывается один раз — `kvPerLayer` в делителе |
| A3 | ⚠️ CUDA runtime | ✅ | `CUDA_PATH` ≠ рантайм; GPU-бэкенд отклоняется при `llama_supports_gpu_offload()=false` |
| A4 | ⚠️ cached handles | ✅ | Пробные хэндлы освобождаются при смене стейджа; после P/Invoke — `RequiresCleanStage` |
| A5 | ⚠️ fallback | ✅ | `ActivateCpuFallback` во всех трёх точках; явный `Cpu` не подменяется GPU |
| A6/A7 | ✅ | ✅ | Без изменений |
| B8 | ⚠️ compile | ✅ | CS1503 `AppendLine(IFormatProvider, string)` устранён |
| B9 | ⚠️ failure→BUY | ✅ | `IsInferenceFailure` → HOLD/conf=0 при любой политике |
| B10/B11 | ✅ | ✅ | Интеграция разблокирована снятием ошибок Data/Services |
| C12 | ⚠️ compile timer | ✅ | `WaitForNextTickAsync` |
| C13/C14 | ✅ | ✅ | Без изменений |
| C15 | ⚠️ equity subset | ✅ | Equity по всем активам, `EquityIsPartial` для любого актива без маршрута |
| C16–C18/C20 | ✅ | ✅ | Без изменений |
| C19 | ✅ с отклонением | ✅ с отклонением | Стейджинг выполняется в `QwenModel.ActivateBackend` **до загрузки весов**, а не в `App` вслепую: в `App` нельзя выбрать набор DLL, не зная бэкенда. DI + GPU/config preflight в `App` на месте |
| D21 | ✅ | ✅ | FLR может быть недоступен на Demo — это ожидаемо, пара отключается движком с WARN |
| D22 | ✅ | ✅ | Без изменений |
| D23 | ⚠️ manual parse + duplicate sum | ✅ | Реестр — единая модель, валидация один раз, перенос в Common |
| D24/D26 | ✅ | ✅ | Без изменений |
| D25 | ❌ | ✅ | Словарь пополнен, `$BTT → BTTC`, DASH/FLR — только капсом |

Итог по подсчёту: **Implemented — 26 из 26 файловых задач**, из них A1/A6/B11/C19/D21/D26 — со статической оговоркой
(runtime-приёмка на Windows/GPU/Binance Demo не выполнялась). Отдельных ⚠️/❌ по коду не осталось.

### Ограничения
- Сборка/компиляция/тесты/запуск WPF/инференс/запросы к Binance **не выполнялись** (требование заказчика).
- Приёмка на машине заказчика: `dotnet build` (Common → Core → Data → Services → UI), нативный лог оффлоада,
  балансы/ордера Demo, equity по всем активам, новости по BTTC/DASH/FLR/ENJ/STRAX.
- `TreatWarningsAsErrors=true` во всех проектах: новые правки не вводят неиспользуемых полей/переменных
  и не оставляют nullable-предупреждений.

## 2026-11-24 — Пакет исправлений по аудиту (компиляция + A2–A5, B9, C12; далее C15, D23, D25)

Основание: аудит заказчика (A1–D26) и лог сборки `AITradingSystem.Data` (CS1023/CS0103 в NewsSymbolMapper).
Без компиляций/сборок/прогонов — правки пакетом, один проход.

### Внесено сейчас
- MODIFY `Data/News/NewsSymbolMapper.cs` (**компиляция, блокер Data**): цикл `foreach` по `$TICKER` получил фигурные скобки — `var ticker = …` и `result.Add(…)` были вне тела цикла (CS1023 «внедрённый оператор не может быть объявлением» + CS0103 «ticker не существует»). Это устраняет все 3 ошибки из лога сборки заказчика и разблокирует Data → Services → UI.
- MODIFY `Services/AI/GgufInspector.cs` (**A2, двойной учёт KV**): `RecommendGpuLayers` больше не вычитает полный KV-кэш из бюджета заранее. KV учитывается ровно один раз — по числу оффлоаженных слоёв (`kvPerLayer` в делителе `вес слоя + KV слоя`). Прежняя формула вычитала полный KV И добавляла kvPerLayer в делитель → число слоёв занижалось. Doc-комментарий приведён в соответствие.
- MODIFY `Services/AI/GpuProbe.cs` (**A3, строгий CUDA-рантайм**): `CUDA_PATH` больше не считается признаком рантайма (тулкит ≠ драйвер); рантайм определяется по nvcuda.dll / libcuda.so.1 / /proc/driver/nvidia/version или успешному nvidia-smi. `SelectBackend` для CUDA-кандидатов строго требует `HasCudaRuntime` (и GPU), иначе пропускает с явным логом.
- MODIFY `Services/AI/QwenModel.cs` (**A3/A5, строгий отбор бэкенда**): `ActivateBackend` ОТКЛОНЯЕТ GPU-бэкенд при `llama_supports_gpu_offload()=false` (раньше был только WARN → модель молча считалась на CPU под видом Cuda12, диагноз F1, а цепочка Vulkan→CPU не срабатывала). `ActiveBackend` присваивается только после успешной проверки.
- MODIFY `Services/AI/LlamaNativeLoader.cs` (**A4, закэшированные хэндлы**): `ResolveCore` при смене staged-каталога освобождает пробные хэндлы прежнего набора, пока P/Invoke-маршаллер не закэшировал указатели — переключение бэкенда между пробами стало возможным. После первого P/Invoke Free по-прежнему запрещён (AccessViolation), помечается `RequiresCleanStage`.
- NEW метод `Services/AI/GgmlNative.cs` `ResetLogCapture()` + вызов из `LlamaNativeLoader.FreeLoadedHandlesUnsafe()`: после освобождения библиотеки перехват `llama_log_set` переустанавливается на новой DLL (иначе нативный лог нового бэкенда не перехватывался).
- MODIFY `Services/AI/QwenModel.cs` (**A5, корректный CPU-фолбэк**): новый `ActivateCpuFallback(out note)` — сначала чистый CPU-стейдж; если он невозможен (нет CPU-набора на диске / хэндлы закэшированы маршаллером), CPU-режим `GpuLayers=0` на УЖЕ загруженной llama (ggml-cpu присутствует в любой сборке llama.cpp). Подключён во всех трёх точках фолбэка: `ResolveFit` (MinGpuLayers), `LoadWithRetries` (после OOM/ошибок), `VerifyOffload` (после неудачного рестейджа). Явный `Backend=Cpu` больше не подменяется GPU-бэкендом в цепочке фолбэков `LoadAsync`.
- MODIFY `Core/Models/Trading/TradingSignal.cs` + `Services/AI/QwenModel.cs` + `Services/Analysis/SignalCalibrator.cs` (**B9, отказ→BUY**): новый флаг `IsInferenceFailure`; фолбэк-HOLD после отказа инференса помечается им; `Calibrate` при флаге оставляет HOLD с `Confidence=0` (`ConfidenceSource=Failure→Hold`) при ЛЮБОЙ политике — `EngineOverride` больше не превращает отказ ИИ в BUY. Доп. защита: пересмотр HOLD→вход запрещён при нулевой уверенности модели (`modelConf > 0`).
- MODIFY `Services/Trading/ExchangeAccountService.cs` (**C12, компиляция таймера**): `PeriodicTimer.WaitAsync(ct)` → `WaitForNextTickAsync(ct)` (CS1061: у PeriodicTimer нет метода WaitAsync).

### Запланировано следующим пакетом (ещё не внесено в файлы)
- **C15** `Services/Trading/ExchangeAccountService.cs`: equity — сумма ВСЕХ активов аккаунта (сейчас только watch-list + базовые активы пар); `EquityIsPartial=true` при любом положительном активе без маршрута конвертации в USDT, включая активы вне watch-list. Таблица «позиций на бирже» остаётся отфильтрованной по watch-list.
- **D23** `Common/Configuration/ConfigLoader.cs`: разбор symbols.json через `SymbolsRegistry` (единая модель с UI настроек) вместо ручного разбора JSON; валидация (нормализация, дубликаты, сумма активных лимитов) один раз в `SymbolsRegistry.Validate()` — дублирующая ручная сумма убирается. `SymbolsRegistry`/`SymbolConfig` переносятся из Core в Common (namespace `AITradingSystem.Core.Models.Config` сохраняется), т.к. Common не ссылается на Core. Legacy-формат со строками сохраняется.
- **D25** `Data/News/NewsSymbolMapper.cs`: словарь дополняется bittorrent/btt/bttc→BTTC, dash→DASH, flare/flr→FLR, enjin/enj→ENJ, stratis/strax→STRAX, tether/usdt→USDT, usd coin/usdc→USDC, first digital usd/fdusd→FDUSD; $BTT→BTTC работает через словарь; DASH/FLR добавляются в AmbiguousTickers (слова-омонимы ищутся только капсом).

---

## 2026-11-24 — Завершение документации и передача результата
- MODIFY `README.md`: актуальное расположение исходников, назначение конфигурации, различие Paper/Demo/Live, требование перезапуска и явное отсутствие сборки/runtime-проверок.
- MODIFY `plan.md`: сводка B11/C12–C20/D21–D26 обновлена до внесённых файловых правок; старые диагнозы сохранены как история; добавлены внешние критерии приёмки и ограничения.
- Исторический `AITradingSystem.zip` не изменялся. Сборки/компиляции/прогоны не выполнялись.

## 2026-11-24 — Статическая интеграционная проверка D/C
- MODIFY `Common/Configuration/{TradingConfig,ConfigLoader}.cs`: SymbolPositionLimits, проверка процентов и суммы активных лимитов при загрузке реестра.
- MODIFY `Services/Trading/TradingEngine.cs`: SYMBOL-CAP ограничивает количество по MaxPositionPercent пары; активный список отделён от конфигурации, недоступные пары не удаляются навсегда из настроек.
- MODIFY `UI/ViewModels/SettingsViewModel.cs`: сохранение только персистит настройки для следующего запуска, не меняет активный режим/риск; лимиты также записываются в appsettings fallback.
- MODIFY `Core/Models/Config/SymbolConfig.cs`: KnownQuotes помечен JsonIgnore.
- MODIFY `Services/Trading/ExchangeAccountService.cs`: первый опрос внутри защищённого цикла; убран дублирующий проход цен, WS приоритетнее REST.
- MODIFY `UI/App.xaml.cs`: MainViewModel IDisposable освобождается контейнером, без двойного Dispose.
- MODIFY `UI/Views/DashboardView.xaml`: формат заголовка окружения заключён в кавычки.
- Это просмотр исходников, не компиляция и не тестовый прогон.

## 2026-11-24 — C15–C20: UI, DI и конфигурация
- MODIFY `UI/ViewModels/DashboardViewModel.cs`, `UI/Views/DashboardView.xaml`: биржа, режим, ключи, соединение, USDT/FDUSD/USDC free/locked, equity/partial, все активы, фактический GPU/backend/offload, последний инференс и ошибки. Внутренний баланс/PnL бота оставлены отдельными.
- MODIFY `UI/ViewModels/TradingViewModel.cs`, `UI/Views/TradingView.xaml`: позиции бота, биржевые остатки, открытые ордера, история, ручное обновление. Таблицы прокручиваются, цены не форматируются принудительно до двух знаков.
- MODIFY `UI/ViewModels/MainViewModel.cs`, `UI/MainWindow.xaml`: Dispatcher-подписки на аккаунт, баннер PAPER/REAL/LIVE/ключи/недоступные пары, отписка и остановка UI-таймера при закрытии.
- MODIFY `UI/App.xaml.cs`: регистрация SignalCalibrator/ExchangeAccountService; запуск read-only опроса после StartAsync; GPU preflight и конфигурационный лог. Early staging выполняется существующим QwenModel.ActivateBackend после выбора бэкенда, не в App вслепую.
- MODIFY `UI/appsettings.json`: Demo + PaperTradingMode=false (реальные Demo-ордера согласно исходному плану), symbols.json основной список, ctx=4096, thinking=384, MaxTokens=400, AutoFit/VRAM reserve/FlashAttention/Blend, интервал аккаунта 20 с.
- Конструкторы Dashboard/Trading/Main VM расширены сервисом аккаунта; Settings VM принимает IAIModel. Все разрешения обеспечиваются существующей/добавленной DI-регистрацией.
- Без сборки/компиляции/прогонов. Требуется внешняя приёмка на Windows и Binance Demo.

## 2026-11-24 — C12–C14: биржевой аккаунт
- NEW `Services/Trading/ExchangeAccountService.cs`: PeriodicTimer + SemaphoreSlim, последовательные account/openOrders/prices, атомарные снимки, события, ручной RefreshAsync, остановка через IAsyncDisposable. Paper читает аккаунт, но не торгует через этот сервис.
- NEW `Core/Models/Trading/ExchangePosition.cs`: спотовые остатки и оценка USDT с маршрутом конвертации; неизвестная оценка остаётся null.
- MODIFY `Core/Models/Trading/AccountBalance.cs`: AssetBalance Free/Locked, словарь Assets; старый AssetBalances сохранён.
- MODIFY `Data/Binance/{IBinanceRestClient,BinanceRestClient,BinanceModels}.cs`: HasApiKeys по непустым encrypted-полям; GetAllPricesAsync через публичный ticker/price выбранного окружения.
- MODIFY `Data/Binance/{IBinanceWebSocketClient,BinanceWebSocketClient}.cs`: потокобезопасный кэш последних цен, TryGetLastPrice.
- Сервис ещё не зарегистрирован в DI и не подключён к UI — следующий пакет C15–C20. Без сборки/прогонов.

## 2026-11-24 — Завершение D24/D26
- MODIFY `Services/Trading/TradingEngine.cs`: ValidateSymbolsAsync до загрузки модели; список недоступных пар в UnavailableSymbols; кэш SymbolPrecision; tick-aware PriceDecimals в промпте и калибровке для входов и сопровождения. StepSize кэшируется, исполнение использует собственный актуальный кэш OrderExecutor.
- MODIFY `UI/ViewModels/SettingsViewModel.cs`, `UI/Views/SettingsView.xaml`: фактически записаны таблица символов и новая форма настроек. Сохранение изменяет существующий singleton TradingConfig, а не заменяет ссылку; список/режим/окружение требуют перезапуска. Конструктор теперь принимает IAIModel для GPU-сводки.
- Исправлена запись предыдущего прогресса: эти две правки действительно присутствуют в файлах.

## 2026-11-24 — D: завершение инфраструктуры реестра
- MODIFY `UI/AITradingSystem.UI.csproj`: symbols.json → PreserveNewest.
- MODIFY `Data/News/NewsSymbolMapper.cs`: явный $BTT преобразуется в BTTC.
- MODIFY `Common/Configuration/{TradingConfig,ConfigLoader}.cs`: словарь SymbolPriceDecimals из реестра.
- MODIFY `Data/Binance/BinanceRestClient.cs`: exchangeInfo для доступности/фильтров берётся из активного Demo/Testnet/Live, а не из mainnet override.
- TradingEngine и исправление сохранения SettingsViewModel ещё не записаны; пакет продолжится следующими файловыми правками.

## 2026-11-24 — D21–D26: реестр символов и настройки (промежуточный пакет)
- NEW `Common/Configuration/SymbolNaming.cs`: нормализация активов и longest-match BASE+QUOTE.
- MODIFY `Common/Configuration/TradingConfig.cs`, `Core/Models/Config/{SymbolsRegistry,SymbolConfig}.cs`: до 32 пар, WatchAssets/QuoteAssets, проверка лимитов, PriceDecimals. Подготовлены AccountRefreshIntervalSeconds=20 и MinExchangePositionNotional=1.
- MODIFY `Common/Configuration/ConfigLoader.cs`: symbols.json — основной непустой реестр; fallback appsettings; отсутствующий файл допустим, повреждённый JSON останавливает загрузку.
- NEW `UI/symbols.json`: 12 пар; исходные проценты заменены на 4,9,7,11,4,4,4,11,9,9,14,14 (сумма 100).
- MODIFY `Data/News/NewsSymbolMapper.cs`: словарь BTTC/DASH/FLR/ENJ/STRAX/USDT/USDC/FDUSD дополнен; изменение поиска $BTT пока не завершено.
- MODIFY `UI/ViewModels/SettingsViewModel.cs`, `UI/Views/SettingsView.xaml`: таблица пар, добавление/удаление, лимиты, активы/котировки, Paper/REAL, сводка GPU, сохранение symbols.json + appsettings с требованием перезапуска.
- НЕ ЗАВЕРШЕНО: csproj CopyToOutputDirectory для symbols.json (правка остановлена на несовпадении шаблона), поиск коротких новостных тикеров, точность exchangeInfo в TradingEngine, DI новой сигнатуры SettingsViewModel. Это исправляется до закрытия D-блока.
- Сборки/прогоны не выполнялись.

## 2026-11-24 — Восстановление исходников и продолжение плана
- Исходники и прежние документы восстановлены из `AITradingSystem.zip` в корень репозитория; каталоги `bin/obj`, логи и бинарные артефакты не переносились. Архив пока сохранён без изменений.
- `PLAN.md` → `plan.md`, `CHANGES.md` → `changes.md`: приведены к именам, запрошенным заказчиком.
- Продолжаем незавершённые задачи в порядке плана: D22–D26 → C12–C20 → финальная конфигурация; регистрация `SignalCalibrator` (C19) обязательна для работоспособности уже внесённых изменений.
- Сборки, компиляции и запуски не выполняются. Статус оставшихся задач будет уточняться по исходникам; прежние записи изменений сохранены.

# Введённые изменения — AITradingSystem

Формат: дата · задача · файлы · что сделано.
Исходники: `repo/src/AITradingSystem/`. Сборка/прогоны не выполнялись (по требованию: без компиляций).

---

## 2026-11-24 — Пакет B8–B11 (confidence / детерминированный pre-score)

### B8. MODIFY `AITradingSystem.Services/AI/PromptBuilder.cs` (переписан)
- **Удалено правило 6** («confidence below 0.55») — источник якоря 0.45. Вместо него: «confidence MUST be derived from the evidence rubric … do NOT default to any fixed number».
- Добавлена **легенда индикаторов**: что нормализовано и как (ema*_pos, ema_align, supertrend, adx, rsi, macd_hist, stoch_rsi, atr_pct, bb_*, obv_trend, vwap_dev) и что сырое (rsi_raw/adx_raw 0–100, stoch_k_raw/d_raw, atr_abs, ema20/50/200, bb_upper/lower, vwap, macd_hist_abs, volume_ratio).
- Добавлена **рубрика уверенности**: старт от `engine_prior_confidence`, +0.05…0.15 за согласие тренда/моментума/объёма, −0.10…0.20 за конфликты/ADX<20/объём<0.6/край BB/новостной риск, clamp [0.10; 0.90]; два примера (BUY 0.72 / HOLD 0.30).
- В промпт уходит блок **Engine pre-score**: direction, score, engine_prior_confidence, reasons.
- Thinking сохранён (`/think`), инструкция сокращена до ~110 слов; **пустой think-блок больше не префиллится** (F3).
- Цены форматируются по **tickSize/PriceDecimals символа** (BTTC не округляется в ноль); в контекст печатается «Price precision: N decimals (tick size …)».
- `BuildRepairPrompt(..., bool enableThinking = true)` — ремонт без think-префилла, с явной просьбой не отвечать пустым сообщением.

### B8 (поддержка). MODIFY `AITradingSystem.Services/AI/AiRequestContext.cs`
- Новые поля: `EngineScore`, `EngineDirection`, `EnginePriorConfidence`, `EngineReasons`, `PriceDecimals` (дефолт 8), `TickSize`.

### B9. NEW `AITradingSystem.Services/Analysis/SignalCalibrator.cs`
- `EngineScore { Direction [-1;1], PriorConfidence [0.1;0.9], Components[], Reasons[] }`, `DirectionName` (LONG/SHORT/FLAT), `Summary`, `ReasonsText`; `ScoreComponent { Name, Weight, Value, Contribution }`.
- `PreScore(indicators)` — чистая математика: веса ema_align 0.30, supertrend 0.20, ema_position 0.15, rsi 0.15, macd_hist 0.10, stoch_rsi 0.05, obv_trend 0.05; модификаторы ADX (<20 ×0.6, ≥25 ×1.0), объём (volume_ratio <0.6 ×0.8, ≥1.5 ×1.1), волатильность (ATR <0.1% ×0.8, >6% ×0.75), край Bollinger ×0.85; `prior = 0.2 + 0.7·|score|`. Работает и без сырых ключей (берёт нормализованные аналоги).
- `Calibrate(signal, score, policy, CalibrationContext)`: `ModelOnly` — как раньше; `Blend` — 0.5·model + 0.5·prior, **дегенерат (HOLD, |conf−0.45| ≤ 0.02) → prior**; `EngineOverride` — всегда prior. Заполняет `ModelConfidence/EngineConfidence/IsDegenerateConfidence/ConfidenceSource`, пишет `TradeLog.Decision CALIBRATE` со всеми тремя значениями и политикой.
- Пересмотр HOLD → BUY/SELL только при `prior ≥ MinConfidence`, согласованном направлении и отсутствии позиции; цены входа считает алгоритм (`entry = цена`, `SL = entry − k·ATR`, `TP = entry + R/R·risk`, округление по decimals) — лог `ENGINE-ENTRY`. Сайзинг/SL/TP сопровождения остаются за RiskManager.
- `CalibrationContext { MinConfidence, HasPosition, AllowShort, Price, AtrAbsolute, StopLossAtrMultiplier, MinRiskReward, PriceDecimals }`.

### B10. MODIFY `AITradingSystem.Core/Indicators/NormalizedIndicatorSnapshot.cs`
- Новые свойства и ключи словаря: `rsi_raw, adx_raw, stoch_k_raw, stoch_d_raw, atr_abs, ema20, ema50, ema200, bb_upper, bb_lower, macd_hist_abs, vwap, volume_ratio`. Нормализованные ключи не тронуты (совместимость с тестами).

### B10/B11. MODIFY `AITradingSystem.Core/Indicators/IndicatorCalculator.cs`
- Окно объёмов SMA20 (`VolumeSma20`, `GetVolumeRatio(volume)`) — обновляется в `Update`, чистится в `Reset`.
- Счётчик `ClosedCandles` — для лога прогрева «N/M свечей».

### B11. MODIFY `AITradingSystem.Services/Analysis/MarketAnalyzer.cs`
- `GetWarmupInfo(symbol) → (Ready, Candles, Required)`; убран дублирующийся XML-комментарий.

### B11. MODIFY `AITradingSystem.Common/Logging/LogChannels.cs`
- `TradeLog.Waiting(symbol, reason)` — строка `WAITING` в `logs/trades-*.log`.

### B11. MODIFY `AITradingSystem.Services/Trading/TradingEngine.cs`
- Конструктор принимает `SignalCalibrator` (новое поле `_calibrator`).
- `ProcessSymbolAsync`: `PreScore` до вызова ИИ + лог `PRE-SCORE`, pre-score уходит в промпт через `AiRequestContext`; после ответа — `Calibrate(...)` с политикой `AI:ConfidencePolicy`; порог `MinConfidence` применяется к **FINAL** (в отказе печатаются model/engine/source).
- `indicators == null` / нет свечи → `TradeLog.Waiting` с прогрессом прогрева, не чаще раза в минуту на символ (вместо молчаливого return).
- Cooldown анализа символа `TryEnterAnalysisCooldown` (`Trading:SymbolAnalysisCooldownSeconds`, дефолт 60 с).
- `ReviewOpenPositionAsync` — тот же pre-score + калибровка + WAITING.
- Новые хелперы: `BuildCalibrationContext`, `LogWaiting`, `TryEnterAnalysisCooldown`; `BuildAiContext` расширен (pre-score, indicators, цена).

### D22 (частично). MODIFY `AITradingSystem.Common/Configuration/TradingConfig.cs`
- Новое поле `SymbolAnalysisCooldownSeconds = 60` + валидация [0; 3600].
- Лимит `MaxOpenPositions` поднят с 10 до **32** (дефолт 3 сохранён).

### Открытые хвосты этого пакета (зафиксированы в PLAN.md)
1. `App.xaml.cs`: регистрация `SignalCalibrator` в DI ещё не добавлена (C19) — без неё `TradingEngine` не резолвится.
2. `TradingEngine.GetPriceDecimals/GetTickSize` — заглушки (8/ null); символ в `BuildAiContext` берётся из `position?.Symbol`. Будет закрыто в D24 (tickSize из `GetSymbolInfoAsync` + `SymbolConfig.PriceDecimals`).
3. Лимит списка символов 10 → 32 в `TradingConfig.Validate`/`SymbolsRegistry.Validate` и `WatchAssets`/`QuoteAssets` — ещё не сделаны (D22).

---

## 2026-11-24 — Пакет A4–A7 (GPU/native, завершение блока A)

### A4. MODIFY `AITradingSystem.Services/AI/LlamaNativeLoader.cs` — завершено
- Проверено в коде (было сделано ранее): строгий `HasBackendFiles` (llama + ggml-base + marker в ОДНОМ каталоге, для CUDA ещё cudart/cublas), строгий staged-режим резолвера (`UseStagedDirectory` → поиск только в стейдже), `FreeLoadedHandles` запрещён после первого P/Invoke LLamaSharp (`_marshalerResolved` → `RequiresCleanStage`).
- Добавлено сейчас: `GetDiagnostics()` печатает **состав каталога** (все `llama*`, `ggml*`, `cudart*`, `cublas*` файлы) — видно, какие бэкенды реально зарегистрирует llama; чтение каталога защищено от исключений.

### A5. MODIFY `AITradingSystem.Services/AI/QwenModel.cs` — новый конвейер загрузки
- `LoadAsync`: `GpuProbe.Detect` (VRAM) → `SelectBackend` → `ActivateBackend` (стейдж DLL → резолвер → проба → `GgmlNative.InstallLogCapture` → `TryLoadAllBackends` → `GpuProbe.EnrichFromNative`) → `EnsureAvailable` → `GgufInspector.TryInspect` → `ResolveFit` → `LoadWithRetries`.
- `ActivateBackend(backend)` — единая точка активации: `NativeStaging.Stage`, `LlamaNativeLoader.Initialize/TryProbeBackend`, перехват нативного лога, явная регистрация ggml-бэкендов, предупреждение при `llama_supports_gpu_offload()=false`.
- `ResolveFit()` (AutoFitGpu): `RecommendGpuLayers(free − VramReserveMb, ctx, FlashAttention, KvCacheType, batch, maxSingleAlloc)`; при нехватке VRAM сначала ctx 8192 → 4096, затем при `layers < MinGpuLayers` — чистый CPU с причиной; legacy AMD → лимит одной аллокации 1024 MiB; в лог — KV/compute-оценка и «полный/частичный оффлоад N/M».
- `LoadWithRetries`: до 2 OOM-ретраев (`слои ×0.7, ctx/2`, затем `×0.49, ctx=2048`), распознавание OOM по исключению и нативному логу (`IsOutOfMemory`), затем явный CPU-фолбэк (`AllowCpuFallback`) с причиной в `AiLog`; «тихих» откатов нет.
- `VerifyOffload`: факт оффлоада из нативного лога (`offloaded N/M`), при GPU-бэкенде и offload=0 — громкий WARN + один рестейдж/перезагрузка, при потере весов — CPU-фолбэк.
- `ApplyOptionalParams`: `FlashAttention`, `TypeK/TypeV` (Q8_0/Q4_0) выставляются через рефлексию — отсутствие свойства в конкретной версии LLamaSharp не ломает загрузку (лог «свойство недоступно»).
- Новые свойства для UI (C15): `EffectiveContextSize`, `EffectiveGpuLayers`, `OffloadedLayers`, `TotalLayers`, `GpuName`, `VramTotalMb/VramFreeMb`, `LoadWarning`, `BackendSummary`.
- **F3 (пустой ответ)**: 0 символов больше не считается «невалидным JSON» — отдельная причина «пустой ответ (anti-prompt/EOS)» в лог и немедленный repair-повтор без think-префилла.
- **Confidence**: убран даунгрейд BUY→HOLD по `MinConfidence` внутри модели (порог теперь применяется движком к FINAL после калибратора); в лог пишется `model_conf` и признак дегенерата; в сигнал сохраняются `ModelConfidence` и `IsDegenerateConfidence` (HOLD, |conf−0.45| ≤ 0.02).

### A5 (поддержка). MODIFY `AITradingSystem.Core/Models/Trading/TradingSignal.cs`
- Добавлены `ModelConfidence`, `EngineConfidence`, `IsDegenerateConfidence`, `ConfidenceSource`; `Confidence` документирован как FINAL (после калибратора). Старые поля/`IsValid` не менялись.

### A6/A7 — верификация
- `GpuProbe.cs`: VRAM через nvidia-smi, `vulkaninfo --summary`, `IsLegacyAmd` (Polaris/Vega), WMI-фолбэк, `EnrichFromNative` — реализовано, правок не потребовалось.
- `AIConfig.cs`: `AutoFitGpu`, `VramReserveMb`, `MinGpuLayers`, `AllowCpuFallback`, `FlashAttention`, `KvCacheType`, `NativeLogLevel`, `ConfidencePolicy`, `ThinkingBudgetTokens=384` + Validate — реализовано, правок не потребовалось.

---

## 2026-11-23 — Пакет A1–A3 (GPU/native)

### A1. NEW `AITradingSystem.Services/AI/GgmlNative.cs`
- Безопасные обёртки над экспортами загруженных `llama.dll` / `ggml.dll` / `ggml-base.dll` через `LlamaNativeLoader.TryGetExport` (экспорт не найден → однократный лог + деградация, без краша).
- `InstallLogCapture(level)`: `llama_log_set` со своим делегатом (статическая ссылка от GC), маршрутизация нативных строк в `AiLog.Backend` с фильтром уровня (Off/Error/Warn/Info/Debug).
- Парсинг нативного лога: `offloaded N/M layers to GPU`, `using CPU instead`, `CUDA0/Vulkan0 buffer size`, `out of memory` → свойства `OffloadedLayers`, `TotalLayers`, `CpuFallbackDetected`, `OutOfMemoryDetected`, `GpuBufferSizesMb`; `ResetLoadStats()` перед каждой загрузкой модели.
- `TryLoadAllBackends(path)` / `TryLoadBackend(dllPath)` — явная регистрация backend-DLL (`ggml_backend_load_all_from_path` / `ggml_backend_load`), если экспорты есть.
- `SupportsGpuOffload()` (`llama_supports_gpu_offload`), `GetDevices()` → `List<NativeDeviceInfo>` (имя, бэкенд, VRAM free/total через `ggml_backend_dev_*`).

### A1 (предпосылка из A4). MODIFY `AITradingSystem.Services/AI/LlamaNativeLoader.cs`
- Добавлены `public static bool TryGetExport(string library, string export, out IntPtr ptr)` (резолв хэндла + `NativeLibrary.TryGetExport`) и `public static string? GetBackendDirectory()` (каталог фактической загрузки). Остальные правки A4 — впереди.

### A2. NEW `AITradingSystem.Services/AI/GgufInspector.cs`
- Парсер GGUF v2/v3 без загрузки модели: magic/версия, metadata KV (все скалярные типы + пропуск массивов), список тензоров (имя, тип, dims, offset).
- `GgufModelInfo`: Architecture, BlockCount, ContextLength, EmbeddingLength, HeadCount/HeadCountKv/KeyLength/ValueLength, VocabSize; `WeightBytesTotal` (точно по типам квантования ggml, таблица size/block для F32…Q6_K/IQ*), `TokenEmbeddingBytes`, `OutputWeightBytes`, `MaxTensorBytes`, `BytesPerLayer` (среднее по `blk.N.*`), `DataStartOffset`.
- `KvBytesPerToken(kvType)` — f16/q8_0/q4_0, по реальным head_count_kv и key/value length.
- `ComputeBufferEstimate(batch, ctx, flashAttn)` — оценка compute-буферов (attention scores без flash = batch·ctx·heads·4).
- `RecommendGpuLayers(vramFreeMb, reserveMb, ctx, flashAttn, kvType, batch, maxSingleAllocMb)` → максимум слоёв; `CanFullyOffload(...)` отдельно. `maxSingleAllocMb` — лимит одной аллокации для legacy AMD (Polaris ~1 GB): если `token_embd` больше лимита → 0 (CPU).

### A3. NEW `AITradingSystem.Services/AI/NativeStaging.cs`
- `Stage(backend, explicitPath, logger)` → плоский каталог `<exe>\native-staged\<backend>` (fallback `%LOCALAPPDATA%\AITradingSystem\native\<backend>` при read-only exe).
- Копирует выбранный набор: `llama`, `ggml`, `ggml-base`, все найденные `ggml-cpu*` (для GPU — добирает CPU-варианты из CPU-каталогов, llama.cpp требует CPU-бэкенд всегда), `ggml-cuda` + `cudart*/cublas*` для CUDA, `ggml-vulkan` для Vulkan.
- До первого вызова llama API: `GGML_BACKEND_PATH` = staged-каталог; на Windows `SetDllDirectory` + `AddDllDirectory` (зависимости CUDA ищутся там).
- Копирование идемпотентно (пропуск по длине+времени записи), занятые файлы не валят стейдж.
- `LlamaNativeLoader.Initialize(stagedDir)` — staged-каталог становится приоритетным для резолвера (строгий «только staged» режим — в A4).
- Диагностика: итоговый каталог, список файлов, `GGML_BACKEND_PATH`; предупреждение, если llama уже загружена из другого каталога (смена бэкенда потребует чистого стейджа).

### Служебные файлы
- `PLAN.md` — копия плана со статусами задач (A1–A3 ✅).
- `CHANGES.md` — этот журнал изменений.

---

## P0.8. Залипший REST-запрос останавливал торговлю навсегда (лог 2026-11-26, после 11:47)

### Что видно в логах
- `11:47:24` `DATA-STALE DASHUSDT` → REST-дозагрузка `klines DASHUSDT` в основном цикле.
- `11:47:34 / 11:47:44 / 11:47:55` `HTTP RETRY 1..3/4` (таймаут `HttpClient.Timeout` 10 с).
- `11:47:57.713` `HTTP → GET .../klines?symbol=DASHUSDT...` — **4-я попытка не вернулась никогда**:
  ни `HTTP ←`, ни `RETRY 4/4`, ни ошибки. Единственный незакрытый запрос из 1724 за остаток сессии.
- После этого из `trades`/`trading` исчезают `RISK-SNAPSHOT`, `DECISION`, `ИТОГ`, `DATA-*` —
  до конца лога (**1 ч 50 мин**, 11:47 → 13:38). Живыми остаются только `RECON-*` (polling-fallback,
  отдельный таймер) и часовой `CONFIDENCE-TUNE`. Процесс жив, REST работает, торговля мертва.
- Ни `Ошибка в основном цикле`, ни `Основной цикл завершен` в логе нет → цикл не упал, а висит
  внутри `await` навсегда.

### Причина
`HttpClient.Timeout` не гарантирует возврата управления: залипание транспорта
(connect/DNS/чтение тела) может не отреагировать на отмену. Один такой `await`
в `RecoverMarketDataAsync → ReloadHistoryAsync → GetKlinesAsync` держал весь
`MainLoopAsync`: анализ, риск-снимок и восстановление данных по всем символам.
Второй дефект: `catch (OperationCanceledException) { break; }` в цикле принимал
таймаут за остановку бота — тихий выход без ошибки.

### Исправлено (один шаг)
- `BinanceRestClient.SendRequestAsync`: жёсткий дедлайн каждой попытки
  (`HttpTimeoutMs` + 2 с) поверх linked-CTS — `WithHardDeadlineAsync` races the task,
  поэтому вызывающий код возвращается **даже если транспорт игнорирует отмену**.
  Брошенная задача наблюдается (не роняет процесс) и освобождает ответ.
  `TimeoutException`/`OperationCanceledException` без отмены бота → транзиентный сбой:
  ретрай с backoff, на исчерпании попыток — явная ошибка.
- `TradingEngine.MainLoopAsync`: `catch (OperationCanceledException) when (ct.IsCancellationRequested)` —
  выход только по реальной остановке; таймаут больше не убивает цикл.
- `TradingEngine.RunGuardedPhaseAsync`: `account-refresh`, `market-data-recovery`,
  `correlation-benchmark` под бюджетом 60 с → `LOOP-PHASE-TIMEOUT` и продолжение цикла.
  Обход символов (побочные эффекты — ордера) не бросается: `LOOP-STALL` через 30 с.
- `ReloadHistoryAsync`: ожидание замка восстановления ограничено 15 с
  (`DATA-RECOVERY-LOCK-BUSY`) — залипшая фаза не держит замок для остальных.
