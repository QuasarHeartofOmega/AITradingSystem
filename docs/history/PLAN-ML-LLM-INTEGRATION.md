# План внедрения ML и LLM в AITradingSystem

**Дата:** 2026-11-25  
**Статус:** план создан, задачи не выполнены  
**Предшественник:** `PLAN-10-10-LIVE-PROFIT.md`  
**Условие старта:** этот план можно начинать только после полного завершения `PLAN-10-10-LIVE-PROFIT.md`  
**Фокус:** ML для статистического преимущества, LLM для финансового контекста, новостей, veto и объяснимости  
**Ограничение:** не выполнять сборки/прогоны в рамках создания этого плана

---

## 0. Главное условие

Этот план не заменяет и не ослабляет `PLAN-10-10-LIVE-PROFIT.md`.

К моменту перехода к этому плану уже должны быть закрыты:

```text
P0 — запрет убыточного и неуправляемого Live
P1 — движок положительного матожидания после издержек
P2 — execution quality и биржевая защита
P3 — портфель, капитал и риск разорения
P4 — production-надёжность и безопасность
P5 — Live-допуск, ramp-up и авто-остановка
```

Если предыдущий план не завершён, этот план остаётся заблокированным.

ML и LLM внедряются не вместо риск-контроля, а поверх уже работающей системы gates.

---

## 1. Честная рамка

Готовой модели, которая гарантированно прибыльна именно для этого бота, нет.

Нельзя скачать “финансовую LLM” или “ML-модель для трейдинга” и сразу получить устойчивое преимущество на Binance Spot 15m для конкретных символов, комиссий, стакана, задержек и риск-лимитов.

Поэтому цель этого плана:

```text
ML = собственная обученная модель вероятности / матожидания / качества исполнения
LLM = финансовый аналитик, news-фильтр, regime/veto-слой, объяснение
Движок = детерминированный decision engine
Риск = капитал, лимиты, kill-switch
Execution = безопасное исполнение и защита
```

Гарантированная прибыль не обещается. Цель — повысить качество решений и сделать прибыль статистически проверяемой, а не декларативной.

---

## 2. Выбранный стек верхнего уровня

### 2.1. ML-стек

Основной production-вариант:

```text
Offline training:
  Python
  LightGBM / CatBoost / XGBoost
  pandas / polars
  scikit-learn calibration
  optional: Microsoft Qlib как research-платформа

Export:
  ONNX / ONNX-ML

Runtime inside bot:
  .NET
  ONNX Runtime
  IAlphaModel
  ModelRegistry
  CalibrationService
```

Почему так:

- табличные градиентные бустинги хорошо подходят для индикаторов, микроструктуры и издержек;
- inference быстрый и дешёвый;
- модель можно экспортировать в ONNX и исполнять внутри .NET;
- проще контролировать версии, признаки, калибровку и деградацию;
- меньше риск переобучения, чем у больших deep-моделей на малых данных.

---

### 2.2. LLM-стек

Основной production-вариант:

```text
Local GGUF LLM:
  7B–8B instruct-модель
  quantization Q4_K_M / Q5_K_M
  llama.cpp / LLamaSharp
  optional LoRA/QLoRA fine-tuning под задачу бота

Role:
  analyst / news / regime / veto / explanation

Not role:
  главный трейдер
  риск-менеджер
  источник вероятности прибыли
  источник размера позиции
```

Почему локальная GGUF:

- текущий проект уже использует GGUF/LLamaSharp;
- нет зависимости от внешнего API в критическом пути;
- можно контролировать задержку, формат, версию и безопасность;
- можно fine-tune’ить под конкретный JSON-контракт бота.

---

## 3. Найденные кандидаты и их роль

### 3.1. ML-кандидаты

| Кандидат | Роль | Решение |
|---|---|---|
| LightGBM | Основная табличная ML-модель для p_win / meta-label / expected net R | Основной кандидат |
| CatBoost | Альтернатива, если много категориальных признаков | Второй кандидат |
| XGBoost | Альтернативный бустинг для champion/challenger | Резервный кандидат |
| Logistic Regression / Isotonic / Platt | Калибровка вероятностей | Обязательный слой |
| Microsoft Qlib | Research-платформа для quant/ML pipeline | Использовать offline, не как Live runtime |
| TCN / LSTM / Transformer encoder | Временные deep-модели | Только позже, если табличные модели упрутся в потолок |
| RL | Execution/адаптивное управление | Не внедрять на первом этапе |

Вывод по ML:

```text
Готовую “прибыльную модель” не искать.
Искать framework и обучать собственную модель на своих данных.
Production-модель должна быть версионирована, калибрована и экспортирована в ONNX.
```

---

### 3.2. LLM-кандидаты

| Кандидат | Что это | Подходит для роли | Ограничения |
|---|---|---|---|
| Qwen3-8B-class Instruct GGUF + собственный LoRA | Базовая локальная instruct-модель под fine-tune | Основной recommended path | Нужно проверить лицензию, формат, chat template, latency |
| Qwen-Open-Finance-R-8B GGUF | Финансово дообученная Qwen3-8B, есть GGUF-кванты | Кандидат для analyst-роли | Оригинальная модель может требовать доступа/условий; нужна проверка лицензии и качества под crypto |
| FinGPT | Open-source financial LLM project / модели / sentiment / forecaster | Кандидат для news/sentiment/аналитики | Не готовый trading-alpha engine; нужно проверять конкретный checkpoint, лицензию и формат |
| FinMA / PIXIU | Финансовые LLM и evaluation-ресурсы | Кандидат для financial NLP / benchmark | Не готовая модель для Binance spot execution; нужна адаптация |
| AdaptLLM finance-LLM GGUF | Старая financial LLM в GGUF | Исторический benchmark / fallback candidate | Llama-2-era template, возраст модели, нужно сравнивать с современными instruct-моделями |
| FinBERT | Financial sentiment classifier | Только auxiliary sentiment, если лицензия совместима | Лицензионные ограничения; не LLM и не trading decision model |

Вывод по LLM:

```text
Готовая финансовая LLM может быть стартовой точкой.
Но под задачу бота всё равно нужен task-specific fine-tuning или строгий prompt/schema contract.
LLM не должна принимать финальное торговое решение.
```

---

## 4. Что требуется от ML-модели

ML-модель должна быть не “предсказателем цены”, а поставщиком статистических оценок для decision engine.

### 4.1. Основные выходы ML

```text
p_tp_before_sl:
  вероятность того, что TP будет достигнут раньше SL в заданном горизонте

p_lower_bound / p_upper_bound:
  консервативные границы оценки вероятности

expected_net_R:
  ожидаемый результат в единицах начального риска после издержек

expected_net_return:
  ожидаемый net return после комиссий, спреда, проскальзывания

meta_label_probability:
  стоит ли исполнять первичный сигнал стратегии

uncertainty:
  насколько оценка неопределённа

calibration_status:
  можно ли доверять вероятностям

drift_status:
  не устарела ли модель / распределение признаков
```

---

### 4.2. Дополнительные ML-модели позже

```text
fill_probability_model:
  вероятность исполнения post-only / limit ордера

slippage_model:
  ожидаемое проскальзывание для заданного размера и стакана

adverse_selection_model:
  риск того, что лимитный ордер исполнится в неблагоприятный момент

stop_limit_gap_model:
  риск гэпа через stop-limit

regime_classifier:
  trend / range / panic / low_liquidity / stablecoin_stress
```

---

### 4.3. Требования к ML runtime

```text
latency:
  желательно < 50 ms на один symbol snapshot
  не блокировать PositionMonitor
  не блокировать execution/protection path

format:
  ONNX / ONNX-ML
  фиксированный feature schema
  фиксированный порядок признаков
  версионирование модели и признаков

fallback:
  если ML недоступна в Live — новые входы запрещены
  если ML некалибрована — новые входы запрещены
  если ML drift detected — риск снижается или входы блокируются

registry:
  model_id
  model_version
  feature_schema_version
  training_window
  calibration_version
  export_hash
  approval_status
```

---

## 5. Что требуется от LLM

LLM должна быть финансовым аналитиком и контролёром контекста, а не трейдером.

### 5.1. Основные выходы LLM

```json
{
  "symbol": "ETHUSDT",
  "regime": "trend|range|high_volatility|low_liquidity|panic|stablecoin_stress|unknown",
  "news_impact": "positive|negative|neutral|shock|unknown",
  "news_relevance": 0.0,
  "event_type": "none|macro|etf|hack|delisting|exchange_outage|stablecoin_depeg|regulatory|other",
  "thesis": "краткое объяснение",
  "invalidation": "что отменяет идею",
  "risk_flags": [],
  "veto": false,
  "veto_reason": null,
  "confidence_note": "не используется как вероятность прибыли"
}
```

---

### 5.2. Запрещённые выходы LLM

LLM не должна возвращать или определять:

```text
final BUY / SELL как обязательную команду
position size
leverage
risk percent
stop-loss override
take-profit override
kill-switch reset
live enable
confidence как готовую вероятность прибыли
```

Если LLM возвращает такие поля, они игнорируются или считаются ошибкой контракта.

---

### 5.3. Требования к LLM runtime

```text
format:
  строгий JSON schema
  parser/validator
  retry только в пределах бюджета
  при ошибке — conservative fallback

latency:
  не в критическом пути SL/TP/protection
  желательно отдельный analyst-cache на символ/новостной контекст
  timeout не должен блокировать execution

fallback:
  если LLM недоступна и analyst-gate обязателен — новые входы запрещены
  если LLM недоступна и разрешён ML-only режим — более строгие gates
  если LLM вернула invalid JSON — no veto override, conservative block или retry

security:
  новости считаются недоверенным текстом
  prompt injection не должен менять risk rules
  секреты/ключи/полные дампы аккаунта не передаются в prompt
```

---

## 6. Целевая архитектура решения

```text
Market Data
   |
   v
Feature Pipeline
   |
   +----------------------+
   |                      |
   v                      v
ML Alpha Model       LLM Analyst
   |                      |
   v                      v
p_win / EV /         regime / news /
uncertainty          veto / thesis
   |                      |
   +----------+-----------+
              |
              v
        Strategy Engine
              |
              v
        Edge Gate
              |
              v
        Liquidity Gate
              |
              v
        Portfolio Gate
              |
              v
        Position Sizer
              |
              v
        Execution Engine
              |
              v
        Protection / Monitor
              |
              v
        Trade Record / Feedback
```

Главное правило:

```text
LLM может запретить или снизить доверие.
ML может дать статистическое основание.
Только движок может разрешить ордер.
Только риск-модуль может определить размер.
Только execution-модуль может отправить ордер.
```

---

## 7. Требования к данным

### 7.1. Минимальный dataset для ML

```text
OHLCV:
  символы из symbols.json
  15m как основной timeframe
  дополнительные агрегации: 5m / 1h / 4h / 1d
  история желательно 1–3 года, если доступна

Order book:
  snapshots / depth
  spread
  bid/ask imbalance
  depth по уровням
  частота обновлений
  stale flags

Trades / execution:
  фактические филлы
  частичные филлы
  комиссии
  комиссия в активе
  проскальзывание
  время между сигналом и исполнением
  отменённые ордера
  неисполненные entry

News:
  timestamp публикации
  timestamp получения
  source
  asset mapping
  relevance
  sentiment / event type
  novelty

Account / portfolio:
  equity snapshots
  exposure
  correlation context
  drawdown state
  daily PnL state
```

---

### 7.2. Labels для ML

Основной label:

```text
y_tp_before_sl =
  TP достигнут раньше SL в горизонте H
  с учётом фактических или консервативно оценённых издержек
```

Дополнительные labels:

```text
y_net_return = future_net_return_after_costs
y_meta_label = primary_signal_was_profitable_after_costs
y_fill = limit_order_filled_within_timeout
y_slippage = realized_slippage
y_regime = future_or_current_market_regime
```

Запрещено:

```text
использовать будущие данные в признаках
обучать и тестировать на одном периоде
подбирать гиперпараметры по test-периоду
считать прибыль без комиссий/спреда/проскальзывания
игнорировать неисполненные ордера
калибровать на тех же данных, на которых обучали финальную модель
```

---

## 8. Требования к обучению ML

### 8.1. Validation scheme

```text
walk-forward validation
purged split для пересекающихся горизонтов
embargo между train и test
out-of-fold predictions для calibration
отдельная оценка по символам
отдельная оценка по режимам
отдельная оценка по времени
```

---

### 8.2. Calibration

```text
Platt scaling / isotonic regression
reliability diagram
Brier score
ECE
calibration по символам/режимам, если данных достаточно
запрет входа при плохой калибровке
```

---

### 8.3. Model approval

Модель может быть допущена к Live только если:

```text
feature schema зафиксирован
training window зафиксирован
validation scheme зафиксирована
calibration version зафиксирована
net expectancy после издержек положительна
нижняя граница оценки приемлема
uncertainty приемлема
drift monitoring включён
export hash зафиксирован
runtime inference проверен на совместимость признаков
```

---

## 9. Требования к fine-tuning LLM

### 9.1. База

Предпочтительный путь:

```text
Qwen3-8B-class Instruct
или совместимая 7B–8B instruct-модель
GGUF после merge/quantization
```

Альтернативный путь:

```text
финансово дообученная модель, например:
  Qwen-Open-Finance-R-8B
  FinGPT checkpoint
  FinMA checkpoint
  AdaptLLM finance-LLM

Но только после проверки:
  лицензии
  chat template
  JSON-following ability
  crypto/news relevance
  latency
  качества veto/regime/news задач
```

---

### 9.2. Метод fine-tuning

```text
LoRA / QLoRA
task-specific instruction dataset
merge adapter
convert to GGUF
quantize Q4_K_M / Q5_K_M
validate schema and latency
```

---

### 9.3. Датасет для LLM fine-tuning

Нужны примеры не “купи/продай”, а задачи аналитика:

```text
market context -> regime / risk_flags / veto
news + asset mapping -> relevance / event_type / sentiment / severity
proposed signal + context -> contradictions / veto / invalidation
indicator snapshot + order book summary -> structured analyst JSON
bad/noisy news -> ignore / low relevance
prompt injection in news -> preserve risk rules / no override
```

Минимальный стартовый объём:

```text
2 000–10 000 качественных размеченных примеров
лучше больше, но качество важнее количества
обязательно включать negative examples:
  no news
  irrelevant news
  contradictory signal
  panic regime
  stablecoin stress
  prompt injection
  invalid data
```

---

### 9.4. Запрещённый fine-tuning

Не обучать LLM как основной trading oracle:

```text
context -> BUY / SELL / HOLD
context -> position size
context -> disable stop-loss
context -> increase risk
context -> guaranteed profit
```

Это создаёт ложное ощущение торгового интеллекта и обходит статистическую проверку.

---

## 10. Интеграция в текущий проект

### 10.1. Новые интерфейсы

```text
IAlphaModel:
  PredictAlpha(features) -> AlphaPrediction

IAnalystModel:
  Analyze(context) -> AnalystReport

IModelRegistry:
  GetApprovedModel(task, symbol, regime)

ICalibrationService:
  Calibrate(raw_score, context) -> CalibratedProbability

IEdgeGate:
  Evaluate(alpha, analyst, costs, portfolio) -> GateResult
```

---

### 10.2. Новые компоненты

```text
AITradingSystem.Services/ML/FeaturePipeline.cs
AITradingSystem.Services/ML/OnnxAlphaModel.cs
AITradingSystem.Services/ML/ModelRegistry.cs
AITradingSystem.Services/ML/CalibrationService.cs
AITradingSystem.Services/ML/DriftMonitor.cs

AITradingSystem.Services/Analysis/LlmAnalystModel.cs
AITradingSystem.Services/Analysis/AnalystSchemaValidator.cs
AITradingSystem.Services/Analysis/NewsRiskExtractor.cs

AITradingSystem.Services/Analysis/EdgeEngine.cs
AITradingSystem.Services/Analysis/ProfitabilityGate.cs
```

Если предыдущий план уже создал `EdgeEngine` / `ProfitabilityGate`, этот план расширяет их, а не дублирует.

---

### 10.3. Изменяемые компоненты

```text
TradingEngine:
  получает AlphaPrediction и AnalystReport
  передаёт их в StrategyEngine / EdgeGate
  не использует LLM confidence как вероятность прибыли

SignalCalibrator:
  перестаёт быть главным источником вероятности
  становится compatibility/calibration layer или заменяется CalibrationService

QwenModel:
  может остаться как legacy LLM
  но новая роль — analyst, не direct trading signal

RiskManager / PositionSizer:
  получают expected_net_R / uncertainty / edge_score
  сайзинг остаётся детерминированным

OrderExecutor / PositionMonitor:
  не зависят от LLM
  могут позже использовать execution ML models
```

---

### 10.4. Конфигурация

Пример целевой структуры:

```json
{
  "ML": {
    "Enabled": true,
    "Runtime": "Onnx",
    "ModelRegistryPath": "models/registry.json",
    "FeatureSchemaVersion": "1.0",
    "RequireCalibration": true,
    "RequireDriftCheck": true,
    "FailClosed": true,
    "MaxInferenceMs": 50
  },
  "Analyst": {
    "Enabled": true,
    "ModelPath": "models/analyst-8b-q4_k_m.gguf",
    "SchemaVersion": "1.0",
    "RequireVetoCheck": true,
    "FailClosed": true,
    "TimeoutMs": 15000,
    "CacheSeconds": 60,
    "AllowMlOnlyWhenUnavailable": false
  },
  "Edge": {
    "MinExpectedNetR": 0.15,
    "MinLowerBoundNetR": 0.05,
    "MaxUncertainty": 0.25,
    "RequirePositiveLowerBound": true
  }
}
```

---

## 11. Этапы внедрения

## M0. Проверка предшественника

**Задачи:**

- `[ ]` Убедиться, что `PLAN-10-10-LIVE-PROFIT.md` завершён.
- `[ ]` Убедиться, что equity-учёт работает как источник риска.
- `[ ]` Убедиться, что Live-профиль изолирован.
- `[ ]` Убедиться, что kill-switch, reconciliation, protection и fail-closed gates активны.
- `[ ]` Убедиться, что EdgeEngine/ProfitabilityGate существуют или могут быть расширены.

**Критерий:**

```text
Без завершённого предыдущего плана ML/LLM не допускается к Live.
```

---

## M1. Контракты и интерфейсы

**Задачи:**

- `[ ]` Добавить `IAlphaModel`.
- `[ ]` Добавить `IAnalystModel`.
- `[ ]` Добавить `AlphaPrediction`.
- `[ ]` Добавить `AnalystReport`.
- `[ ]` Добавить `GateResult`.
- `[ ]` Добавить `ModelRegistry`.
- `[ ]` Добавить `FeatureSchema`.
- `[ ]` Добавить strict JSON schema для analyst.

**Критерий:**

```text
ML и LLM имеют разные контракты.
LLM не может вернуть торговую команду, размер позиции или риск-параметр.
```

---

## M2. Feature Pipeline и dataset

**Задачи:**

- `[ ]` Сформировать единый feature schema.
- `[ ]` Подключить индикаторы.
- `[ ]` Подключить микроструктуру.
- `[ ]` Подключить кросс-рыночные признаки.
- `[ ]` Подключить news features.
- `[ ]` Подключить execution/cost features.
- `[ ]` Сохранять point-in-time snapshots.
- `[ ]` Запретить future leakage.
- `[ ]` Подготовить offline dataset для обучения.

**Критерий:**

```text
Один и тот же feature pipeline используется для training и runtime.
Признаки не заглядывают в будущее.
```

---

## M3. Baseline ML

**Задачи:**

- `[ ]` Обучить LightGBM baseline для `p_tp_before_sl`.
- `[ ]` Обучить CatBoost/XGBoost challenger.
- `[ ]` Обучить meta-label model.
- `[ ]` Обучить expected_net_R model или аналитический расчёт EV.
- `[ ]` Провести walk-forward validation.
- `[ ]` Сохранить out-of-fold predictions.
- `[ ]` Экспортировать лучшую модель в ONNX.
- `[ ]` Проверить совпадение признаков training/runtime.

**Критерий:**

```text
Есть версионированная ML-модель с калибруемыми вероятностями и net-оценкой после издержек.
```

---

## M4. Calibration и Edge Gate

**Задачи:**

- `[ ]` Добавить CalibrationService.
- `[ ]` Добавить reliability/Brier/ECE оценку.
- `[ ]` Добавить lower-bound оценку.
- `[ ]` Интегрировать ML output в EdgeEngine.
- `[ ]` Интегрировать EdgeEngine в ProfitabilityGate.
- `[ ]` Запретить Live-вход при плохой калибровке.
- `[ ]` Запретить Live-вход при отрицательном lower-bound EV.
- `[ ]` Запретить Live-вход при высоком uncertainty.

**Критерий:**

```text
BUY разрешается только если статистическое матожидание после издержек положительно и достоверно.
```

---

## M5. LLM Analyst selection

**Задачи:**

- `[ ]` Выбрать базовую локальную GGUF-модель.
- `[ ]` Проверить лицензию.
- `[ ]` Проверить chat template.
- `[ ]` Проверить latency на целевом железе.
- `[ ]` Проверить JSON-following.
- `[ ]` Сравнить несколько кандидатов:
  - базовая Qwen3-8B-class instruct;
  - Qwen-Open-Finance-R-8B;
  - FinGPT/FinMA checkpoint, если доступен и совместим;
  - AdaptLLM finance-LLM как исторический benchmark.
- `[ ]` Выбрать модель для analyst-роли.

**Критерий:**

```text
LLM выбрана по задаче аналитика, а не по маркетинговому названию “finance”.
```

---

## M6. LLM fine-tuning под задачу бота

**Задачи:**

- `[ ]` Собрать instruction dataset для analyst-роли.
- `[ ]` Добавить negative examples.
- `[ ]` Добавить prompt-injection examples.
- `[ ]` Добавить news relevance examples.
- `[ ]` Добавить regime/veto examples.
- `[ ]` Выполнить LoRA/QLoRA fine-tuning.
- `[ ]` Merge adapter.
- `[ ]` Convert to GGUF.
- `[ ]` Quantize.
- `[ ]` Проверить schema validity.
- `[ ]` Проверить latency.
- `[ ]` Проверить veto precision.
- `[ ]` Проверить news relevance quality.

**Критерий:**

```text
LLM стабильно возвращает валидный AnalystReport и не пытается управлять риском.
```

---

## M7. Интеграция Analyst Gate

**Задачи:**

- `[ ]` Добавить `LlmAnalystModel`.
- `[ ]` Добавить `AnalystSchemaValidator`.
- `[ ]` Добавить `NewsRiskExtractor`.
- `[ ]` Добавить analyst cache.
- `[ ]` Добавить timeout/fallback.
- `[ ]` Интегрировать veto в StrategyEngine/EdgeGate.
- `[ ]` Запретить LLM увеличивать риск.
- `[ ]` Запретить LLM обходить gates.
- `[ ]` Логировать только безопасный AnalystReport.

**Критерий:**

```text
LLM может заблокировать сделку, но не может разрешить сделку против ML/EV/risk gates.
```

---

## M8. Shadow mode

**Задачи:**

- `[ ]` Включить ML/LLM параллельно с текущей логикой.
- `[ ]` Не менять реальные ордера на этом этапе.
- `[ ]` Логировать AlphaPrediction.
- `[ ]` Логировать AnalystReport.
- `[ ]` Логировать hypothetical gate decisions.
- `[ ]` Сравнивать old logic vs new logic.
- `[ ]` Сравнивать predicted p_win vs actual outcomes.
- `[ ]` Сравнивать predicted costs vs realized costs.
- `[ ]` Выявлять drift и calibration decay.

**Критерий:**

```text
Новая логика наблюдается в тени и не влияет на Live-ордера до отдельного допуска.
```

---

## M9. Champion / Challenger

**Задачи:**

- `[ ]` Ввести champion-модель.
- `[ ]` Ввести challenger-модели.
- `[ ]` Разрешить challenger только в shadow или ограниченном risk sleeve.
- `[ ]` Сравнивать net expectancy после издержек.
- `[ ]` Сравнивать calibration.
- `[ ]` Сравнивать drawdown contribution.
- `[ ]` Сравнивать execution quality.
- `[ ]` Автоматически понижать challenger при деградации.
- `[ ]` Повышать challenger только через approval gate.

**Критерий:**

```text
Модель меняется не вручную “потому что нравится”, а по статистическому превосходству и gates.
```

---

## M10. Ограниченный Live-допуск

**Задачи:**

- `[ ]` Разрешить ML/LLM только на минимальной capital stage.
- `[ ]` Сохранить все gates из предыдущего плана.
- `[ ]` Включить строгий edge gate.
- `[ ]` Включить строгий analyst gate.
- `[ ]` Включить drift monitoring.
- `[ ]` Включить автоматическое снижение риска.
- `[ ]` Включить автоматическую остановку при потере преимущества.
- `[ ]` Увеличивать капитал только ступенями.

**Критерий:**

```text
ML/LLM получают доступ к реальному капиталу только через уже существующую систему Live-допуска.
```

---

## M11. Execution ML позже

**Задачи:**

- `[ ]` Обучить fill probability model.
- `[ ]` Обучить slippage model.
- `[ ]` Обучить adverse selection model.
- `[ ]` Обучить stop-limit gap risk model.
- `[ ]` Интегрировать execution ML в OrderExecutor/PositionMonitor.
- `[ ]` Не разрешать execution ML отключать защиту.
- `[ ]` Использовать execution ML только для улучшения качества исполнения и оценки издержек.

**Критерий:**

```text
Execution ML уменьшает плохие исполнения, но не управляет капиталом и не отключает safety.
```

---

## M12. Monitoring, retrain и деградация

**Задачи:**

- `[ ]` Мониторить feature drift.
- `[ ]` Мониторить label drift.
- `[ ]` Мониторить calibration decay.
- `[ ]` Мониторить net expectancy decay.
- `[ ]` Мониторить execution cost drift.
- `[ ]` Мониторить LLM schema validity.
- `[ ]` Мониторить LLM veto quality.
- `[ ]` Автоматически снижать риск при деградации.
- `[ ]` Автоматически блокировать новые входы при потере преимущества.
- `[ ]` Запускать retrain только через controlled pipeline.
- `[ ]` Новую модель допускать только через approval gate.

**Критерий:**

```text
Система не продолжает торговать по устаревшей модели только потому, что она когда-то работала.
```

---

## 12. Runtime-правила после внедрения

```text
Если ML недоступна:
  Live new entries blocked

Если ML некалибрована:
  Live new entries blocked

Если ML drift detected:
  risk reduced или entries blocked

Если LLM недоступна и analyst gate обязателен:
  Live new entries blocked

Если LLM вернула invalid JSON:
  conservative fallback / retry / block

Если LLM veto = true:
  entry rejected

Если EV_low <= 0:
  entry rejected

Если calibration bad:
  entry rejected

Если regime incompatible:
  entry rejected

Если liquidity bad:
  entry rejected

Если portfolio limits violated:
  entry rejected

Если protection not confirmed:
  emergency close / kill-switch
```

---

## 13. Что не делать

```text
Не заменять риск-менеджмент ML/LLM.
Не давать LLM управлять размером позиции.
Не давать LLM отключать SL/TP/protection.
Не использовать LLM confidence как вероятность прибыли.
Не обучать LLM как оракул BUY/SELL.
Не обучать ML на всём периоде и тестировать на нём же.
Не считать прибыль без издержек.
Не игнорировать неисполненные ордера.
Не включать новую модель сразу на весь капитал.
Не обещать гарантированную прибыль.
Не начинать этот план до завершения PLAN-10-10-LIVE-PROFIT.md.
```

---

## 14. Критерии завершения этого плана

План считается завершённым, если:

```text
ML-модель обучена на собственном dataset.
ML-модель экспортирована в ONNX и работает через IAlphaModel.
CalibrationService выдаёт калиброванные вероятности.
EdgeGate использует net expectancy после издержек.
LLM работает через IAnalystModel и strict JSON schema.
LLM не может обходить risk gates.
Analyst veto интегрирован.
Shadow mode пройден.
Champion/challenger механизм работает.
Live-допуск идёт только через capital stages.
Drift/calibration/expectancy monitoring включён.
При деградации система автоматически снижает риск или блокирует новые входы.
```

---

## 15. Итоговая целевая формула

```text
ML даёт:
  p_win
  expected_net_R
  uncertainty
  calibration_status

LLM даёт:
  regime
  news_impact
  risk_flags
  veto
  thesis

Движок проверяет:
  EV_after_costs > threshold
  lower_bound_EV > 0
  calibration OK
  regime compatible
  no veto
  liquidity OK
  portfolio OK
  protection possible

Риск определяет:
  position size
  exposure caps
  drawdown throttle
  kill-switch

Execution обеспечивает:
  journal
  order lifecycle
  fill confirmation
  exchange protection
  reconciliation
```

Это и есть целевая архитектура: не “финансовая LLM торгует за нас”, а “статистическая модель оценивает преимущество, финансовая LLM оценивает контекст, а детерминированный движок допускает сделку к реальным деньгам только через систему gates”.

---

## 16. Ссылки для первичной проверки кандидатов

Эти ссылки не являются рекомендацией “сразу использовать в Live”. Они нужны для проверки лицензий, форматов, checkpoint-ов, требований доступа и совместимости с задачей бота.

### ML / quant research

- Microsoft Qlib: https://github.com/microsoft/qlib
- ONNX Runtime traditional ML deployment: https://onnxruntime.ai/docs/tutorials/traditional-ml.html
- LightGBM ONNX conversion example: https://onnx.ai/sklearn-onnx/auto_examples/plot_pipeline_lightgbm.html
- CatBoost ONNX export: https://catboost.ai/docs/en/concepts/apply-onnx-ml

### Financial LLM / NLP candidates

- FinGPT project: https://github.com/ai4finance-foundation/fingpt
- FinGPT models org: https://huggingface.co/FinGPT
- PIXIU / FinMA project: https://github.com/the-finai/pixiu
- FinMA-7B-full: https://huggingface.co/TheFinAI/finma-7b-full
- FinMA-7B-NLP: https://huggingface.co/TheFinAI/finma-7b-nlp
- Qwen-Open-Finance-R-8B original: https://huggingface.co/DragonLLM/Qwen-Open-Finance-R-8B
- Qwen-Open-Finance-R-8B GGUF example: https://huggingface.co/vividdream/Qwen-Open-Finance-R-8B-IQ4_NL-GGUF
- AdaptLLM finance-LLM GGUF: https://huggingface.co/TheBloke/finance-LLM-GGUF
- FinBERT: https://huggingface.co/ProsusAI/finbert

Перед использованием любого кандидата обязательно проверить:

```text
лицензию
условия доступа
коммерческое использование
chat template
требования к VRAM/RAM
качество JSON/schema following
релевантность crypto/news задачам
скорость inference
возможность fine-tuning/конвертации в GGUF
```
