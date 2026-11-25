# PLAN — Qwen3-14B: повторения/артефакты, короткие раздумья, ускорение инференса, чистка KV

Дата: 25.11.2026. Основание: запрос заказчика + llama.cpp issue #24812. Без компиляций/сборок — правки пакетом, один проход.

## 0. Что добыто (факты, на которых основан план)

1. **Приложенный файл заказчика** (`23f472c4-…txt`, 237 байт) — это НЕ лог аномалий, а служебный баннер GitHub
   («You signed in with another tab…»). Страница issue сохранилась некорректно. Сами аномалии восстановлены из
   первоисточника: **ggml-org/llama.cpp#24812** «Qwen on Vulkan/RADV RX590 produces clustered garbage / question-mark output».
2. **Суть #24812**: на Vulkan (legacy AMD Polaris/RX590) после многих успешных «тёплых» запросов кластерами идёт мусор:
   - `??????????…` (десятки одинаковых символов подряд);
   - циклы спецтокенов (`<unused49>` × 1024 у Gemma — тот же класс сбоя);
   - несвязный текст. CPU-контроль чистый → это баг Vulkan-бэкенда llama.cpp, а не модели/промпта.
   **Исправлено в llama.cpp ≈ b9892 (commit ee445f93d, июль 2026)** — автор issue подтвердил: с b9892 мусор не воспроизводится.
   Вывод: главное «лечение» — обновить нативные DLL; на уровне приложения нужна защита-сторож (детект + чистка KV + сброс).
3. **Текущий код** (`Services/AI/QwenModel.cs`, LLamaSharp 0.30.0):
   - сэмплинг: только `Temperature=0.3, TopP=0.9`; `DefaultSamplingPipeline` молча применяет свои дефолты `TopK=40, MinP=0.1`,
     штрафы за повторы ВЫКЛ (`RepeatPenalty=1, PresencePenalty=0, PenaltyCount=64`). Низкая температура + отсутствие
     штрафов = типичные зацикливания Qwen3 («тупит», повторяется). Официальные рекомендации Qwen3: thinking —
     `temp 0.6, top_p 0.95, top_k 20, min_p 0`; non-thinking — `temp 0.7, top_p 0.8, top_k 20`; presence_penalty 0…1.5 против повторов.
   - `ThinkingBudgetTokens=128` — НЕ жёсткий стоп: модель может думать сколько угодно в пределах MaxTokens+128.
   - `StatelessExecutor` создаёт **новый контекст на каждый запрос** (KV не накапливается между запросами, но буферы KV
     каждый раз пересоздаются — медленно, особенно на Vulkan; префикс системного промпта ~1000 токенов пересчитывается каждый раз).
   - нет детекта деградации вывода; таймаут 600000 мс (10 минут) на запрос.
   - `KvCacheType=F16`, `FlashAttention=true` (проставляется рефлекцией), `UBatchSize` не задаётся явно.
4. **API LLamaSharp 0.30.0** (проверено по исходникам v0.30.0 и nupkg):
   - `DefaultSamplingPipeline`: TopK, MinP, RepeatPenalty, FrequencyPenalty, PresencePenalty, PenaltyCount, PenalizeNewline, Seed — всё есть;
   - `ModelParams`: `FlashAttention (bool?)`, `TypeK/TypeV (GGMLType?)`, `UBatchSize`, `NoKqvOffload` — можно ставить напрямую, без рефлекции;
   - `LLamaContext.NativeHandle`: `MemoryClear(data)`, `MemorySequenceRemove(seq,p0,p1)`, `MemoryCanShift` — публичные;
   - `StreamingTokenDecoder`, `AntipromptProcessor`, `LLamaToken.IsEndOfGeneration`, `LLamaBatch`, `DecodeResult` — публичные,
     из них собирается собственный цикл генерации (как в StatelessExecutor, но с постоянным контекстом).

## 1. План работ (реализовано в этом проходе)

| # | Задача | Решение | Файлы |
|---|---|---|---|
| 1 | Повторения/артефакты Qwen3 | Сэмплеры по рекомендациям Qwen3 + штрафы: TopK=20, MinP=0, RepeatPenalty=1.05, PresencePenalty=0.5, PenaltyCount=256, PenalizeNewline=false (JSON нужны переносы); temp 0.6/top_p 0.95 (think), 0.7/0.8 (no_think). Всё в конфиг | `AIConfig.cs`, `QwenModel.cs`, `appsettings.json` |
| 2 | Короткие раздумья — ЖЁСТКО | Собственный цикл генерации: счётчик think-токенов; при превышении `ThinkingBudgetTokens` принудительно декодируется `\n</think>\n\n` в KV (приём reasoning-budget из llama-server) и модель продолжает сразу JSON. Бюджет 128→96 | `KvManagedExecutor.cs` (new), `AIConfig.cs`, `appsettings.json` |
| 3 | Мусор Vulkan (#24812): `????`, циклы спецтокенов, повторы | Потоковый сторож `DegenerateOutputDetector`: ≥32 одинаковых символа подряд; ≥6 повторов блока (2…64 симв.); ≥3 спецтокенов `<unusedN>`/`<\|…\|>`; ≥4 U+FFFD. Срабатывание → немедленный стоп генерации (экономия минут) | `DegenerateOutputDetector.cs` (new), тесты |
| 4 | «Буфер памяти перегружается» — чистить старое (приоритет), иначе сброс | Постоянный KV-контекст вместо пересоздания на каждый запрос. Перед запросом: общий префикс с прошлым промптом сохраняется, старый хвост удаляется `MemorySequenceRemove` (чистка старого); после генерации хвост ответа тоже удаляется. При деградации/ошибке — полный `MemoryClear` (приоритетная чистка). Эскалация: 2 деградации подряд → пересоздание контекста (сброс); 3 → перезагрузка весов; далее → CPU-фолбэк | `KvManagedExecutor.cs` (new), `QwenModel.cs` |
| 5 | Ускорение инференса | (а) KV-переиспользование префикса системного промпта (~1000 токенов префилла не пересчитываются); (б) контекст не пересоздаётся каждый запрос; (в) KV Q8_0 по умолчанию (вдвое меньше KV, быстрее attention; на legacy AMD Vulkan остаётся F16 — консервативно); (г) жёсткий think-бюджет и ранний стоп мусора; (д) таймаут 600с→180с; (е) FlashAttention/TypeK/TypeV/UBatchSize напрямую | `KvManagedExecutor.cs`, `QwenModel.cs`, `AIConfig.cs`, `appsettings.json` |
| 6 | Переполнение контекста большим промптом (новости) | Проверка токенов до генерации; новости урезаются до влезания (сначала лимит символов, затем половинное урезание, затем отказ от новостей) — вместо ContextOverflowException | `QwenModel.cs`, `PromptBuilder.cs` |
| 7 | Нативные DLL (главное лечение #24812) | Документировано: обновить llama.dll/ggml*.dll до llama.cpp ≥ b9892 через `AI:NativeLibraryPath`/стейджинг (механизм уже есть). При активации Vulkan — предупреждение в лог со ссылкой на issue | `QwenModel.cs`, этот файл |
| 8 | Совместимость | Интерфейс `IAIModel`, формат сигнала, логи AiLog, промпт-рубрика и тесты PromptBuilder не ломаются; `DecodeSpecialTokens=true`, чтобы мусор спецтокенов был видим сторожу (раньше он был невидим и съедал бюджет пустыми токенами) | — |

## 2. Порядок эскалации при «тупизне» (как просил заказчик)

1. Сторож поймал мусор/цикл → генерация немедленно остановлена → **чистка KV** (`MemoryClear`) → повтор запроса (repair-промпт).
2. 2 деградации подряд → **сброс контекста** (пересоздание LLamaContext) → повтор.
3. 3 деградации подряд → **перезагрузка весов модели** (тот же бэкенд) → повтор.
4. Снова деградация → **CPU-фолбэк** (если `AllowCpuFallback`) → повтор. Успешный сигнал сбрасывает счётчик.

## 3. Обновление нативных библиотек (вне кода, делает оператор)

LLamaSharp 0.30.0 несёт llama.cpp старее b9892. Для полного устранения Vulkan-мусора:
1. Скачать релиз llama.cpp ≥ b9892 (win-x64, нужный бэкенд: vulkan/cuda/cpu).
2. Положить DLL в каталог и указать `AI:NativeLibraryPath` (или заменить DLL в стейдже бэкенда).
3. Проверить лог `AI BACKEND`: оффлоад `offloaded N/M`, отсутствие `????` в ai-логе.
Если обновиться нельзя — сторож+чистка KV+сброс+CPU-фолбэк удерживают систему в рабочем состоянии ценой повторов.

## 4. Критерии приёмки (runtime-проверка на машине заказчика, здесь не выполняется)

- В `logs/ai-*.log` нет ответов с ≥32 одинаковыми символами подряд и циклами `<unusedN>`; при появлении мусора строка
  `AI KV/DEGENERATE` и успешный повтор после чистки.
- Think-блок ≤ бюджета: строка `think budget: принудительно закрыт` при превышении; среднее время инференса падает.
- Повторные запросы переиспользуют префикс: в логе `KV reuse N токенов` (N ≈ размер системного промпта).
- `BackendSummary`/дашборд работают как раньше.

## 5. Риски / отклонения

- PresencePenalty 0.5 может слегка снижать уверенность формулировок — значение в конфиге, ставится 0 при желании.
- Q8_0 KV требует FlashAttention для V-кэша в llama.cpp; FA включён. На legacy AMD (RX 4xx/5xx) KV принудительно F16.
- Собственный цикл генерации повторяет логику StatelessExecutor 0.30.0 (проверено по исходникам), но с постоянным контекстом;
  при обновлении LLamaSharp сверить API `MemorySequenceRemove/MemoryClear/DecodeAsync`.
- Приложенный файл аномалий был битым — если у заказчика есть РЕАЛЬНЫЙ лог с другими аномалиями, прислать повторно:
  сторож расширяется новыми правилами точечно.

## 6. СТАТУС РЕАЛИЗАЦИИ (обновляется по ходу работ)

### Уже внесено (файлы изменены/созданы)

| Файл | Статус | Что именно |
|---|---|---|
| `PLAN.md` | ✅ NEW | Этот план: диагностика (#24812 → Vulkan-баг, фикс ≈ b9892), факты по API LLamaSharp 0.30.0, план, эскалация, критерии приёмки |
| `Services/AI/DegenerateOutputDetector.cs` | ✅ NEW | Потоковый сторож: ≥32 одинаковых символов подряд (`????…`); ≥6 повторов блока 2…64 симв. (циклы); ≥3 спецтокенов `<unusedN>`/`<\|…\|>`; ≥4 U+FFFD. Залипает в true, Reason для лога. Чистый класс, без нативных зависимостей |
| `Services/AI/KvManagedExecutor.cs` | ✅ NEW | Постоянный KV-контекст вместо StatelessExecutor. `PrepareKv`: общий префикс с прошлым промптом сохраняется, старый хвост `MemorySequenceRemove` («чистка старого»); после генерации хвост ответа удаляется. Деградация/таймаут/ошибка → полный `MemoryClear`. `HardReset` — пересоздание контекста. Жёсткий think-бюджет: при превышении декодирует `\n</think>\n\n` прямо в KV и продолжает сразу JSON. Сэмплеры Qwen3 через `DefaultSamplingPipeline` (TopK/MinP/Repeat/Presence/Frequency/PenaltyCount, PenalizeNewline=false). `DecodeSpecialTokens=true` — мусор спецтокенов видим сторожу. `PromptTooLongException` при нехватке контекста. Частичный ответ при таймауте сохраняется |
| `Common/Configuration/AIConfig.cs` | ✅ MODIFY | Temperature 0.3→0.6, TopP 0.9→0.95; NEW: NoThinkTemperature 0.7, NoThinkTopP 0.8, TopK 20, MinP 0, RepeatPenalty 1.05, PresencePenalty 0.5, FrequencyPenalty 0, PenaltyLastN 256; ThinkingBudgetTokens 128→96 и теперь ЖЁСТКИЙ стоп; InferenceTimeoutMs 600000→180000; KvCacheType F16→Q8_0; NEW: ReuseKvContext=true, KvMinReusePrefix=64, DetectDegenerateOutput=true, DegenerateResetThreshold=2, DegenerateReloadThreshold=3, MaxNewsChars=2500; Validate() для всех новых полей |
| `Services/AI/QwenModel.cs` | ✅ MODIFY | Дочистка завершена: удалены мёртвые рефлекс-хелперы `ApplyOptionalParams`/`TrySetParam`/`TrySetEnumParam`; `VerifyOffload` и `UnloadAsync` корректно освобождают `_executor` перед обнулением; `LoadOnce` освобождает старые веса/контекст до загрузки новых и логирует сэмплеры; `GenerateResponseAsync` возвращает `KvGenerationResult` с опциями think/no_think, linked CTS таймаута и сохранением частичного ответа; `GetTradingSignalAsync` держит `_inferenceLock` на весь цикл попыток, логирует диагностику генерации, обрабатывает `DegenerateReason` и эскалацию KV→HardReset→reload weights→CPU fallback, сбрасывает счётчики при успешном сигнале; добавлены `BuildPromptWithinBudget`/`BuildRepairPromptWithinBudget` (урезание новостей до `MaxNewsChars`, затем половинное урезание, откат repair→base); при активации Vulkan — WARN про обновление native DLL до llama.cpp ≥ b9892 (#24812) |
| `UI/appsettings.json` | ✅ MODIFY | AI-секция приведена к плану: Temperature 0.6, TopP 0.95, NoThinkTemperature 0.7, NoThinkTopP 0.8, TopK 20, MinP 0, RepeatPenalty 1.05, PresencePenalty 0.5, FrequencyPenalty 0, PenaltyLastN 256, ThinkingBudgetTokens 96, MaxTokens 320, InferenceTimeoutMs 180000, KvCacheType Q8_0, ReuseKvContext true, KvMinReusePrefix 64, DetectDegenerateOutput true, DegenerateResetThreshold 2, DegenerateReloadThreshold 3, MaxNewsChars 2500 |
| `Tests/DegenerateOutputDetectorTests.cs` | ✅ NEW | xUnit-тесты сторожа: `?`×40 (в т.ч. по кускам), цикл `<unused49>`, цикл блока «0.45, », U+FFFD×4, чистый JSON и чистый think+JSON не флагуются, disabled-режим не флагует, сработавший сторож залипает |
| `changes.md` | ✅ MODIFY | Запись пакета 25.11.2026: все файлы, что/зачем, отклонения, требование обновить native DLL ≥ b9892 |

### Осталось сделать

Файловые шаги плана закрыты. Runtime-приёмка (сборка/тесты/инференс на машине заказчика) не выполнялась по требованию заказчика.
Отклонение от §1 п.6 зафиксировано: урезание новостей делается в `QwenModel` (не в `PromptBuilder`) — `PromptBuilder.cs` НЕ меняется.

### Журнал выполнения

- 25.11.2026: исходники/конфигурация/документация распакованы из `AITradingSystem.zip` в рабочее дерево (без `bin/obj/.vs`), чтобы править файлы напрямую.
- 25.11.2026: выполнен шаг «`QwenModel.cs` — дочистка» (см. строку статуса выше). Компиляции/сборки не запускались.
- 25.11.2026: выполнен шаг «`UI/appsettings.json`» — AI-ключи обновлены/добавлены по плану.
- 25.11.2026: выполнен шаг «`Tests/DegenerateOutputDetectorTests.cs`» — добавлены xUnit-тесты сторожа деградации.
- 25.11.2026: выполнен шаг «`changes.md`» — добавлена запись пакета со всеми файлами/отклонениями.
- 25.11.2026: зафиксировано отклонение §1 п.6: урезание новостей в `QwenModel`, `PromptBuilder.cs` не меняется. Все файловые шаги закрыты.

### Известные мелочи для проверки при дочистке
- `ParseKvType`/`KvCacheType` в `ResolveFit` — enum для VRAM-расчёта из `GgufInspector.cs`, НЕ трогать (это не тип KV llama).
- `EffectiveMaxTokens` (= MaxTokens + ThinkingBudgetTokens) остаётся общим лимитом; жёсткий think-стоп работает внутри него.

