namespace AITradingSystem.Common.Configuration;

using AITradingSystem.Core.Models.Config;
using AITradingSystem.Core.Models.Enums;

/// <summary>
/// Параметры торговой стратегии и риск-менеджмента.
/// Соответствуют требованиям FR-5.1 - FR-5.7.
/// </summary>
public sealed class TradingConfig
{
    /// <summary>
    /// Список торговых пар. Загружается из symbols.json через SymbolRegistryService.
    /// Здесь задаются только значения по умолчанию, если symbols.json отсутствует.
    /// </summary>
    public List<string> Symbols { get; set; } = new();

    /// <summary>P0.6: единственная вселенная трендовых входов. Пустой список — legacy fallback.</summary>
    public List<string> VolatileSymbols { get; set; } = new();
    /// <summary>P0.6: наблюдаются отдельно; обычные трендовые входы запрещены.</summary>
    public List<string> StablecoinPairs { get; set; } = new(SymbolUniversePolicy.DefaultStablecoinPairs);
    /// <summary>P0.6: рыночный контекст/корреляция, не торговые входы.</summary>
    public List<string> BenchmarkSymbols { get; set; } = new(SymbolUniversePolicy.DefaultBenchmarkSymbols);
    /// <summary>P0.6: исключение → reason_code; включается в эффективный config hash.</summary>
    public Dictionary<string, string> SymbolExclusions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> WatchAssets { get; set; } = new();
    public List<string> QuoteAssets { get; set; } = new(SymbolNaming.DefaultQuotes);
    public Dictionary<string, int> SymbolPriceDecimals { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, decimal> SymbolPositionLimits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int AccountRefreshIntervalSeconds { get; set; } = 20;
    public decimal MinExchangePositionNotional { get; set; } = 1m;

    /// <summary>
    /// Буфер к минимальной сумме ордера биржи (NOTIONAL/minNotional), %.
    /// Binance считает нотариал по средней цене исполнения, а не по mid, поэтому
    /// ордер ровно «в минимум» на MARKET может отклониться. Диапазон: [0; 10].
    /// </summary>
    public decimal MinNotionalBufferPercent { get; set; } = 1.5m;

    /// <summary>
    /// На сколько процентов разрешено превысить риск-сайзинг ради минимума биржи.
    /// 0 — никогда не увеличивать объём против риска: пара просто пропускается.
    /// Подъём всё равно ограничен свободными деньгами, лимитом пары и портфельными кэпами.
    /// Диапазон: [0; 100].
    /// </summary>
    public decimal MinNotionalBumpMaxPercent { get; set; } = 25m;

    /// <summary>
    /// Процент риска от депозита на одну сделку (FR-5.1).
    /// Диапазон: 0.001 (0.1%) — 0.05 (5%).
    /// </summary>
    public double RiskPerTrade { get; set; } = 0.02;

    /// <summary>
    /// Максимальная просадка, при которой торговля останавливается (FR-5.6).
    /// </summary>
    public double MaxDrawdown { get; set; } = 0.10;

    /// <summary>
    /// Дневной лимит убытков (FR-5.7).
    /// </summary>
    public double DailyLossLimit { get; set; } = 0.05;

    /// <summary>
    /// Множитель ATR для расчета Stop-Loss (FR-5.3).
    /// SL = Entry - N * ATR.
    /// </summary>
    public double StopLossAtrMultiplier { get; set; } = 2.0;

    /// <summary>
    /// Минимальное соотношение Risk/Reward (FR-5.4).
    /// </summary>
    public double MinRiskRewardRatio { get; set; } = 2.0;

    /// <summary>
    /// P2.7: детерминированная починка TP сигнала до риск-валидации. Модель регулярно
    /// ставит симметричные SL/TP (gross R/R = 1, net после комиссий ≈ 0.7) и завышает
    /// R/R в reasoning — такие входы отклонялись бы всегда. При true движок пересчитывает
    /// TP до порога MinRiskRewardRatio той же формулой, что и после исполнения
    /// (TradeCostModel.TakeProfitForNetRiskReward). SL модели сохраняется; если риск
    /// после комиссий неположителен (SL внутри комиссий), берётся ATR-стоп движка.
    /// </summary>
    public bool AutoFixSignalTakeProfit { get; set; } = true;

    /// <summary>
    /// Включен ли Trailing Stop (FR-5.5).
    /// </summary>
    public bool EnableTrailingStop { get; set; } = true;

    /// <summary>
    /// Множитель ATR для подтягивания Trailing Stop.
    /// </summary>
    public double TrailingStopAtrMultiplier { get; set; } = 1.5;

    /// <summary>
    /// Биржевая защита позиции OCO (P0.1): STOP_LOSS_LIMIT + TAKE_PROFIT_LIMIT сразу после филла входа.
    /// true — позиция переживает падение процесса: биржа закрывает её сама.
    /// false — остаётся только локальный SL (небезопасно, для отладки).
    /// </summary>
    public bool ExchangeProtectionEnabled { get; set; } = true;

    /// <summary>
    /// Отступ лимитной цены защитной ноги от её триггера в долях (0.003 = 0.3%).
    /// Гарантирует, что сработавший стоп/тейк нальётся, а не останется висеть лимиткой.
    /// </summary>
    public decimal ProtectionLimitSlippage { get; set; } = 0.003m;

    /// <summary>
    /// Не удалось поставить биржевую защиту в live — закрыть позицию market'ом.
    /// Правило «нет защиты на бирже → нет позиции»: иначе капитал снова зависит от живого процесса.
    /// </summary>
    public bool ClosePositionIfProtectionFails { get; set; } = true;

    /// <summary>Phase 1.7 (Blocker 7): подхват ручных/external спотовых остатков по символам реестра.
    /// Off — только детект/алерт; Confirm — кандидат ждёт согласия оператора (дефолт); Auto — подхватывать сразу.</summary>
    public ExternalAdoptionMode AdoptExternalPositions { get; set; } = ExternalAdoptionMode.Confirm;

    /// <summary>Phase 1.7: окно реконструкции цены входа external-остатка по myTrades, часы.</summary>
    public int ExternalAdoptionLookbackHours { get; set; } = 72;

    /// <summary>Phase 1.7: отказ биржевой защиты у external-позиции. Дефолт — алерт без автоматической продажи чужого актива.</summary>
    public ExternalProtectionFailurePolicy ExternalProtectionFailurePolicy { get; set; } = ExternalProtectionFailurePolicy.AlertNotSell;

    /// <summary>
    /// Минимальная пауза между cancel/replace защитной связки при трейлинге, секунд.
    /// Ограничивает частоту запросов к бирже (бюджет весов, P1.6).
    /// </summary>
    public int TrailingReplaceMinIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Минимальное улучшение стопа для cancel/replace, в процентах от текущего стопа.
    /// 0.15 = подтягиваем биржевый стоп только если он ушёл минимум на 0.15%.
    /// </summary>
    public decimal TrailingReplaceMinStepPercent { get; set; } = 0.15m;

    /// <summary>
    /// P0.2: период независимого контура сопровождения позиций, мс.
    /// Контур живёт отдельно от цикла анализа и не ждёт инференса LLM.
    /// </summary>
    public int PositionMonitorIntervalMs { get; set; } = 500;

    /// <summary>
    /// Принудительный обход позиций, даже если новых тиков нет (WS молчит), секунд.
    /// Гарантирует, что позиция без биржевой защиты будет замечена.
    /// </summary>
    public int PositionMonitorSweepSeconds { get; set; } = 5;

    /// <summary>
    /// Порог задержки «тик пришёл → проверка сопровождения», мс.
    /// Превышение пишется в лог как MONITOR-LAG (критерий P0.2: ≤1–2 с).
    /// </summary>
    public int PositionMonitorMaxLatencyMs { get; set; } = 2000;

    /// <summary>
    /// Минимальная пауза между попытками поставить биржевую защиту позиции, секунд.
    /// Без неё тик-контур долбил бы биржу OCO-запросами при устойчивом отказе.
    /// </summary>
    public int ProtectionRetryIntervalSeconds { get; set; } = 15;

    /// <summary>
    /// P0.3: сколько операций исполнения одновременно отправлять на биржу.
    /// По символу всегда не более одной; приоритет защиты/закрытия выше входа.
    /// </summary>
    public int ExecutionQueueMaxParallel { get; set; } = 2;

    /// <summary>
    /// Сколько помнить результат отправленного clientOrderId, секунд.
    /// Повтор с тем же ключом в этом окне не создаёт второй ордер.
    /// </summary>
    public int ExecutionIdempotencyTtlSeconds { get; set; } = 600;

    /// <summary>
    /// Порог ожидания в очереди исполнения, мс — превышение пишется как QUEUE-WAIT.
    /// </summary>
    public int ExecutionQueueWarnWaitMs { get; set; } = 3000;

    /// <summary>
    /// P0.3: сколько всего попыток делает одна операция очереди при транзиентном сбое
    /// (1 = без повторов). Повтор идёт с тем же clientOrderId — дубль не создаётся.
    /// </summary>
    public int ExecutionRetryMaxAttempts { get; set; } = 3;

    /// <summary>
    /// Базовая задержка backoff между попытками операции, мс. Растёт как base·2^(n−1),
    /// с полным jitter — иначе все 12 пар ретраились бы синхронно.
    /// </summary>
    public int ExecutionRetryBaseDelayMs { get; set; } = 300;

    /// <summary>
    /// Потолок задержки backoff, мс. На время ожидания слот символа освобождается,
    /// поэтому закрытие другой операции по этой паре не стоит за ретраем входа.
    /// </summary>
    public int ExecutionRetryMaxDelayMs { get; set; } = 5000;

    /// <summary>
    /// P0.5: user-data stream (executionReport) + стартовая реконсиляция с биржей.
    /// В paper-режиме и без API-ключей контур пропускается независимо от ключа.
    /// </summary>
    public bool UserDataStreamEnabled { get; set; } = true;

    /// <summary>
    /// P0.5: интервал keepalive listenKey, минуты. Binance аннулирует ключ через 60 минут
    /// без продления, поэтому значение должно быть меньше 60.
    /// </summary>
    public int UserDataKeepAliveMinutes { get; set; } = 30;

    /// <summary>P0.5: проверка живости user-data socket и пересоздание listenKey при обрыве, секунды.</summary>
    public int UserDataStreamHealthCheckSeconds { get; set; } = 15;

    /// <summary>
    /// P0.4: user-data stream обязателен для новых входов. Если стрим недоступен и polling-fallback выключен —
    /// входы запрещены (fail-closed), сопровождение открытых позиций продолжается.
    /// </summary>
    public bool UserDataStreamRequiredForEntries { get; set; } = true;

    /// <summary>P0.4: разрешить polling-режим (периодическая REST-реконсиляция) вместо user-data stream.</summary>
    public bool UserDataPollingFallbackEnabled { get; set; } = true;

    /// <summary>P0.4: интервал polling-реконсиляции в режиме EXECUTION-MODE=POLLING-FALLBACK, секунды.</summary>
    public int UserDataPollingIntervalSeconds { get; set; } = 20;

    /// <summary>
    /// P0.4: предельный возраст последней успешной реконсиляции. Старше — состояние исполнения считается
    /// неизвестным и новые входы запрещаются, даже если socket формально поднят.
    /// </summary>
    public int ExecutionStateMaxStalenessSeconds { get; set; } = 90;

    /// <summary>P0.5: подхватывать сиротские позиции бота из журнала/ордеров при реконсиляции.</summary>
    /// <summary>P0.4a: grace after the entry fill timeout before reconciliation may cancel it.</summary>
    public int ReconciliationEntryGraceSeconds { get; set; } = 30;

    /// <summary>P0.5: подхватывать сиротские позиции бота из журнала/ордеров при реконсиляции.</summary>
    public bool ReconciliationAdoptOrphanPositions { get; set; } = true;

    /// <summary>P0.5: снимать сиротские ордера/OCO бота, не принадлежащие открытым позициям.</summary>
    public bool ReconciliationCancelOrphanBotOrders { get; set; } = true;

    /// <summary>
    /// P0.6: kill-switch — при пробитии лимита просадки/дневного убытка автоматически
    /// закрыть все позиции и заблокировать новые входы (раньше лимиты только запрещали входы).
    /// </summary>
    public bool KillSwitchAutoCloseOnRiskBreach { get; set; } = true;

    /// <summary>P1.8: периодический мониторинг дрейфа локальных часов относительно Binance.</summary>
    public bool ClockDriftMonitorEnabled { get; set; } = true;

    /// <summary>P1.8: интервал проверки серверного времени, секунд.</summary>
    public int ClockDriftCheckIntervalSeconds { get; set; } = 60;

    /// <summary>P1.8: порог абсолютного дрейфа для предупреждения, мс.</summary>
    public int ClockDriftWarningThresholdMs { get; set; } = 1000;

    /// <summary>
    /// P1.5: фильтр ликвидности перед входом (спред/глубина/объём).
    /// Раньше широкий спред только логировался — вход на тонкой паре не блокировался.
    /// </summary>
    public bool LiquidityFilterEnabled { get; set; } = true;

    /// <summary>P1.5: максимальный спред (ask−bid)/mid для входа, в процентах. 0.35 = 0.35%.</summary>
    public decimal MaxEntrySpreadPercent { get; set; } = 0.35m;

    /// <summary>P1.5: абсолютный минимум суммарной глубины стороны входа в котировке (USDT).</summary>
    public decimal MinEntryDepthQuote { get; set; } = 1000m;

    /// <summary>P1.5: глубина стороны входа должна быть ≥ этого множителя от нотариала ордера.</summary>
    public decimal MinDepthNotionalRatio { get; set; } = 5m;

    /// <summary>P1.5: сколько уровней стакана суммировать для оценки глубины.</summary>
    public int LiquidityDepthLevels { get; set; } = 10;

    /// <summary>
    /// P1.5: минимальный средний объём закрытой свечи в котировке (SMA20 объёма × цена).
    /// 0 — проверка объёма выключена глобально.
    /// </summary>
    public decimal MinCandleQuoteVolume { get; set; } = 15000m;

    /// <summary>
    /// P1.5: точечные пороги ликвидности из symbols.json (BTTC/STRAX/FLR и прочие тонкие пары).
    /// null-поле порога — использовать глобальное значение.
    /// </summary>
    public Dictionary<string, SymbolLiquidityOverride> SymbolLiquidityOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Интервал свечей для анализа (1m, 5m, 15m, 1h, 4h, 1d).
    /// </summary>
    public string CandleInterval { get; set; } = "15m";

    /// <summary>
    /// Глубина истории свечей для расчета индикаторов.
    /// Должна быть достаточной для EMA(200) + буфер.
    /// </summary>
    public int HistoryCandlesCount { get; set; } = 300;

    /// <summary>
    /// P1.4: максимальный возраст kline/стакана для разрешения входа, секунд.
    /// После реконнекта WS или тихого обрыва данные старше порога блокируют вход до REST-восстановления.
    /// </summary>
    public int MarketDataMaxAgeSeconds { get; set; } = 30;

    /// <summary>P1.4: пауза перед повторной попыткой REST-дозагрузки истории при неудаче, секунд.</summary>
    public int MarketDataRecoveryRetrySeconds { get; set; } = 15;

    /// <summary>
    /// P0.3: сколько секунд после REST-прогрева ждать живой WS-тик по символу.
    /// REST-история сама по себе не подтверждает, что стрим снова delivering.
    /// </summary>
    public int MarketDataLiveConfirmSeconds { get; set; } = 20;

    /// <summary>P0.3: сколько попыток resubscribe symbol/stream до полного реконнекта и карантина.</summary>
    public int MarketDataMaxResubscribeAttempts { get; set; } = 2;

    /// <summary>P0.3: длительность карантина символа, у которого WS так и не ожил, секунд.</summary>
    public int MarketDataQuarantineSeconds { get; set; } = 300;

    /// <summary>
    /// P0.3a шаг 3: потолок экспоненциального backoff карантина для persistently silent символа, секунд.
    /// Каждый повторный карантин без live-подтверждения удваивает длительность до этого предела.
    /// </summary>
    public int MarketDataQuarantineMaxSeconds { get; set; } = 3600;

    /// <summary>P0.3: минимальная пауза между принудительными реконнектами рыночного WS, секунд.</summary>
    public int MarketDataReconnectCooldownSeconds { get; set; } = 60;

    /// <summary>
    /// Период обновления стакана ордеров в секундах (FR-1.3).
    /// </summary>
    public int OrderBookUpdateIntervalSeconds { get; set; } = 3;

    /// <summary>
    /// Включен ли режим paper trading (NFR-5.2).
    /// </summary>
    public bool PaperTradingMode { get; set; } = false;

    /// <summary>
    /// Начальный баланс для paper trading (USDT).
    /// </summary>
    public decimal PaperTradingInitialBalance { get; set; } = 10000m;

    /// <summary>P0.1: идентификатор виртуального paper-счёта для изоляции risk-state.</summary>
    public string PaperTradingAccountId { get; set; } = "default";

    /// <summary>
    /// Комиссия симулируемых сделок в paper-режиме (0.001 = 0.1% как на споте Binance).
    /// </summary>
    public decimal PaperCommissionRate { get; set; } = 0.001m;

    /// <summary>P1.1: симулировать paper taker-филл проходом по стакану, а не по цене сигнала.</summary>
    public bool PaperSlippageEnabled { get; set; } = true;

    /// <summary>P1.1: максимум уровней стакана для paper taker-филла (0 — все доступные уровни).</summary>
    public int PaperSlippageMaxLevels { get; set; } = 0;

    /// <summary>P1.2: комиссия maker в live/demo (доля нотариала). Используется для post-only входа и net R/R.</summary>
    public decimal MakerCommissionRate { get; set; } = 0.001m;

    /// <summary>P1.2: комиссия taker в live/demo (доля нотариала). Используется для market-входа/выхода и net R/R.</summary>
    public decimal TakerCommissionRate { get; set; } = 0.001m;

    /// <summary>P1.2: ограничивать проскальзывание обычного выхода marketable LIMIT IOC вместо MARKET.</summary>
    public bool MarketExitSlippageGuardEnabled { get; set; } = true;

    /// <summary>P1.2: максимальное проскальзывание обычного выхода от лучшей цены стакана (0.003 = 0.3%).</summary>
    public decimal MarketExitMaxSlippage { get; set; } = 0.003m;

    /// <summary>
    /// P1.2: аварийные выходы (Stop-Loss / NoExchangeProtection / KillSwitch) обходят cap и идут MARKET.
    /// Приоритет — закрытие риска; остаток после частичного IOC снова защищается.
    /// </summary>
    public bool EmergencyMarketExitBypassSlippageCap { get; set; } = true;

    /// <summary>
    /// Legacy-совместимость. Ordinary MARKET SELL не обходит фильтры биржи; остаток ниже
    /// LOT_SIZE/MARKET_LOT_SIZE/NOTIONAL помечается для ручного аудита/конвертации пыли.
    /// Значение ключа больше не включает заведомо невалидную отправку.
    /// </summary>
    public bool DustMarketCloseEnabled { get; set; } = true;

    /// <summary>
    /// Phase 1.5 (Blocker 5): максимум принудительных закрытий одной позиции
    /// (NoExchangeProtection / trailing-fail). Дальше — алерт оператору и freeze входов,
    /// а не новый MARKET каждые 20 секунд по уже проданному объёму.
    /// </summary>
    public int ForcedCloseMaxAttempts { get; set; } = 3;

    /// <summary>Phase 1.5: минимальная пауза между принудительными закрытиями одной позиции, секунды.</summary>
    public int ForcedCloseCooldownSeconds { get; set; } = 60;

    /// <summary>
    /// Phase 1.5: через сколько секунд зависший close_in_flight считается протухшим
    /// и снимается (ордер мог умереть вместе с сетевым вызовом).
    /// </summary>
    public int CloseInFlightStaleSeconds { get; set; } = 120;

    /// <summary>
    /// Phase 1.5: отложенная/незаписанная evidence E1 блокирует новые входы до догона журнала.
    /// Закрытие и защита при этом продолжают работать: биржевой факт важнее записи.
    /// </summary>
    public bool EvidenceFailureFreezesEntries { get; set; } = true;

    /// <summary>P1.2: перенос стопа в безубыток после достижения net прибыли, кратной начальному net риску.</summary>
    public bool BreakEvenEnabled { get; set; } = true;

    /// <summary>P1.2: порог активации безубытка в долях начального net риска (1.0 = 1R).</summary>
    public decimal BreakEvenActivationRR { get; set; } = 1.0m;

    /// <summary>P1.2: буфер безубытка в процентах от цены входа (0.05 = 0.05%), чтобы не остаться в нуле из-за tick.</summary>
    public decimal BreakEvenBufferPercent { get; set; } = 0.05m;

    /// <summary>P1.7: портфельный риск поверх поштучных лимитов (экспозиция/корреляция/день/серия).</summary>
    public bool PortfolioRiskEnabled { get; set; } = true;

    /// <summary>P1.7: кап суммарной экспозиции открытых позиций + нового входа, % от equity.</summary>
    public decimal MaxPortfolioExposurePercent { get; set; } = 60m;

    /// <summary>P1.7: кап экспозиции коррелированного кластера (BTC-кластер альтов), % от equity.</summary>
    public decimal MaxCorrelatedExposurePercent { get; set; } = 35m;

    /// <summary>P1.7: учитывать корреляцию закрытых свечей при входе.</summary>
    public bool CorrelationRiskEnabled { get; set; } = true;

    /// <summary>P1.7: бенчмарк корреляционного кластера.</summary>
    public string CorrelationBenchmarkSymbol { get; set; } = "BTCUSDT";

    /// <summary>P1.7: окно закрытых свечей для корреляции.</summary>
    public int CorrelationLookbackCandles { get; set; } = 96;

    /// <summary>P1.7: минимум общих доходностей для Pearson; меньше — fail-closed.</summary>
    public int MinCorrelationSamples { get; set; } = 48;

    /// <summary>P1.7: порог сильной корреляции Pearson [0; 1].</summary>
    public decimal MaxPairwiseCorrelation { get; set; } = 0.80m;

    /// <summary>P1.7: дневной лимит убытка по символу, % от дневного старта equity; 0 — выключено.</summary>
    public decimal SymbolDailyLossLimitPercent { get; set; } = 1m;

    /// <summary>P1.7: серия подряд убыточных закрытий до паузы входов; 0 — выключено.</summary>
    public int LossStreakPauseThreshold { get; set; } = 3;

    /// <summary>P1.7: длительность паузы после серии убытков, минуты.</summary>
    public int LossStreakPauseMinutes { get; set; } = 60;

    /// <summary>
    /// Разрешить шорт. На споте Binance шортить нельзя, поэтому по умолчанию false:
    /// сигнал SELL без открытой позиции игнорируется, с открытой — закрывает её.
    /// </summary>
    public bool AllowShort { get; set; } = false;

    /// <summary>
    /// Сколько секунд ждать исполнения лимитного (Post-Only) ордера на вход.
    /// По истечении неисполненный остаток отменяется, позиция открывается только на исполненный объём.
    /// </summary>
    public int OrderFillTimeoutSeconds { get; set; } = 60;

    /// <summary>Интервал опроса статуса ордера, мс.</summary>
    public int OrderFillPollIntervalMs { get; set; } = 1500;

    /// <summary>Входить MARKET-ордером вместо Post-Only лимитного (быстрее, но комиссия taker).</summary>
    public bool UseMarketEntry { get; set; } = false;

    /// <summary>
    /// Спрашивать AI об уже открытой позиции (закрыть/держать) с этим интервалом, секунд.
    /// </summary>
    public int PositionReviewIntervalSeconds { get; set; } = 120;

    /// <summary>Разрешить AI закрывать позицию сигналом CLOSE/SELL.</summary>
    public bool AiCanClosePositions { get; set; } = true;

    /// <summary>Интервал основного цикла анализа, секунд.</summary>
    public int AnalysisIntervalSeconds { get; set; } = 5;

    /// <summary>Максимум одновременно открытых позиций.</summary>
    public int MaxOpenPositions { get; set; } = 3;

    /// <summary>
    /// Минимальная пауза между запросами к ИИ по одному символу, секунд (B11).
    /// При 12+ символах и медленном инференсе очередь не забивается одним символом.
    /// 0 — без паузы (анализировать каждый цикл).
    /// </summary>
    public int SymbolAnalysisCooldownSeconds { get; set; } = 60;

    /// <summary>
    /// Валидация конфигурации. Вызывается при загрузке.
    /// </summary>
    public void Validate()
    {
        if (RiskPerTrade <= 0 || RiskPerTrade > 0.05)
            throw new InvalidOperationException(
                $"RiskPerTrade должен быть в диапазоне (0; 0.05]. Текущее: {RiskPerTrade}");

        if (MaxDrawdown <= 0 || MaxDrawdown >= 1)
            throw new InvalidOperationException(
                $"MaxDrawdown должен быть в диапазоне (0; 1). Текущее: {MaxDrawdown}");

        if (DailyLossLimit <= 0 || DailyLossLimit >= 1)
            throw new InvalidOperationException(
                $"DailyLossLimit должен быть в диапазоне (0; 1). Текущее: {DailyLossLimit}");

        if (StopLossAtrMultiplier <= 0)
            throw new InvalidOperationException(
                $"StopLossAtrMultiplier должен быть > 0. Текущее: {StopLossAtrMultiplier}");

        if (MinRiskRewardRatio < 1)
            throw new InvalidOperationException(
                $"MinRiskRewardRatio должен быть >= 1. Текущее: {MinRiskRewardRatio}");

        QuoteAssets = SymbolNaming.NormalizeAssets(QuoteAssets);
        if (QuoteAssets.Count == 0) throw new InvalidOperationException("QuoteAssets не может быть пустым.");
        WatchAssets = SymbolNaming.NormalizeAssets(WatchAssets);
        var normalized = new List<string>();
        foreach (var raw in Symbols ?? new List<string>())
        {
            var symbol = SymbolNaming.NormalizeSymbol(raw, QuoteAssets);
            if (symbol is null)
            {
                Serilog.Log.Warning("Пропущен некорректный символ {Symbol}: неизвестная котировка или формат", raw);
                continue;
            }
            if (!normalized.Contains(symbol)) normalized.Add(symbol);
        }
        Symbols = normalized;
        if (Symbols.Count == 0)
            throw new InvalidOperationException("После нормализации список торговых пар пуст.");
        if (Symbols.Count > 32)
            throw new InvalidOperationException($"Максимум 32 торговые пары. Указано: {Symbols.Count}");
        VolatileSymbols = SymbolUniversePolicy.Normalize(VolatileSymbols, QuoteAssets);
        StablecoinPairs = SymbolUniversePolicy.Normalize(StablecoinPairs, QuoteAssets);
        BenchmarkSymbols = SymbolUniversePolicy.Normalize(BenchmarkSymbols, QuoteAssets);
        SymbolUniversePolicy.ValidateLists(VolatileSymbols, StablecoinPairs, BenchmarkSymbols);
        foreach (var symbol in Symbols)
        {
            var reason = SymbolUniversePolicy.EntryExclusionReason(symbol, VolatileSymbols, StablecoinPairs, BenchmarkSymbols);
            if (reason != null) throw new InvalidOperationException($"Symbols содержит не-торговый класс {symbol}: {reason}");
        }
        SymbolExclusions = SymbolExclusions
            .Where(x => !string.IsNullOrWhiteSpace(x.Key) && !string.IsNullOrWhiteSpace(x.Value))
            .ToDictionary(x => x.Key.Trim().ToUpperInvariant(), x => x.Value.Trim(), StringComparer.OrdinalIgnoreCase);
        if (AccountRefreshIntervalSeconds < 5 || AccountRefreshIntervalSeconds > 300)
            throw new InvalidOperationException("AccountRefreshIntervalSeconds должен быть в диапазоне [5; 300].");
        if (MinExchangePositionNotional < 0)
            throw new InvalidOperationException("MinExchangePositionNotional должен быть >= 0.");
        if (!Enum.IsDefined(typeof(ExternalAdoptionMode), AdoptExternalPositions))
            throw new InvalidOperationException($"AdoptExternalPositions имеет недопустимое значение: {AdoptExternalPositions}");
        if (ExternalAdoptionLookbackHours < 1 || ExternalAdoptionLookbackHours > 24 * 30)
            throw new InvalidOperationException("ExternalAdoptionLookbackHours должен быть в диапазоне [1; 720].");
        if (!Enum.IsDefined(typeof(ExternalProtectionFailurePolicy), ExternalProtectionFailurePolicy))
            throw new InvalidOperationException($"ExternalProtectionFailurePolicy имеет недопустимое значение: {ExternalProtectionFailurePolicy}");
        if (MinNotionalBufferPercent < 0m || MinNotionalBufferPercent > 10m)
            throw new InvalidOperationException("MinNotionalBufferPercent должен быть в диапазоне [0; 10].");
        if (MinNotionalBumpMaxPercent < 0m || MinNotionalBumpMaxPercent > 100m)
            throw new InvalidOperationException("MinNotionalBumpMaxPercent должен быть в диапазоне [0; 100].");

        var validIntervals = new HashSet<string>
        {
            "1m", "3m", "5m", "15m", "30m", "1h", "2h", "4h", "6h", "12h", "1d"
        };
        if (!validIntervals.Contains(CandleInterval))
            throw new InvalidOperationException(
                $"Недопустимый интервал свечей: {CandleInterval}");

        if (HistoryCandlesCount < 200)
            throw new InvalidOperationException(
                $"HistoryCandlesCount должен быть >= 200 для корректного расчета EMA(200). " +
                $"Текущее: {HistoryCandlesCount}");

        if (HistoryCandlesCount > 1000)
            throw new InvalidOperationException(
                "HistoryCandlesCount должен быть <= 1000 (лимит Binance /api/v3/klines).");

        if (MarketDataMaxAgeSeconds < 5 || MarketDataMaxAgeSeconds > 300)
            throw new InvalidOperationException(
                $"MarketDataMaxAgeSeconds должен быть в диапазоне [5; 300]. Текущее: {MarketDataMaxAgeSeconds}");

        if (MarketDataRecoveryRetrySeconds < 5 || MarketDataRecoveryRetrySeconds > 600)
            throw new InvalidOperationException(
                $"MarketDataRecoveryRetrySeconds должен быть в диапазоне [5; 600]. Текущее: {MarketDataRecoveryRetrySeconds}");

        if (MarketDataLiveConfirmSeconds < 5 || MarketDataLiveConfirmSeconds > 300)
            throw new InvalidOperationException(
                $"MarketDataLiveConfirmSeconds должен быть в диапазоне [5; 300]. Текущее: {MarketDataLiveConfirmSeconds}");

        if (MarketDataMaxResubscribeAttempts < 0 || MarketDataMaxResubscribeAttempts > 5)
            throw new InvalidOperationException(
                $"MarketDataMaxResubscribeAttempts должен быть в диапазоне [0; 5]. Текущее: {MarketDataMaxResubscribeAttempts}");

        if (MarketDataQuarantineSeconds < 30 || MarketDataQuarantineSeconds > 3600)
            throw new InvalidOperationException(
                $"MarketDataQuarantineSeconds должен быть в диапазоне [30; 3600]. Текущее: {MarketDataQuarantineSeconds}");

        if (MarketDataQuarantineMaxSeconds < 60 || MarketDataQuarantineMaxSeconds > 86400)
            throw new InvalidOperationException(
                $"MarketDataQuarantineMaxSeconds должен быть в диапазоне [60; 86400]. Текущее: {MarketDataQuarantineMaxSeconds}");

        if (MarketDataQuarantineMaxSeconds < MarketDataQuarantineSeconds)
            throw new InvalidOperationException(
                $"MarketDataQuarantineMaxSeconds ({MarketDataQuarantineMaxSeconds}) не может быть меньше MarketDataQuarantineSeconds ({MarketDataQuarantineSeconds})");

        if (MarketDataReconnectCooldownSeconds < 10 || MarketDataReconnectCooldownSeconds > 600)
            throw new InvalidOperationException(
                $"MarketDataReconnectCooldownSeconds должен быть в диапазоне [10; 600]. Текущее: {MarketDataReconnectCooldownSeconds}");

        if (PaperCommissionRate < 0 || PaperCommissionRate > 0.01m)
            throw new InvalidOperationException("PaperCommissionRate должен быть в диапазоне [0; 0.01].");

        if (PaperSlippageMaxLevels < 0 || PaperSlippageMaxLevels > 1000)
            throw new InvalidOperationException("PaperSlippageMaxLevels должен быть в диапазоне [0; 1000].");

        if (MakerCommissionRate < 0 || MakerCommissionRate > 0.01m)
            throw new InvalidOperationException("MakerCommissionRate должен быть в диапазоне [0; 0.01].");

        if (TakerCommissionRate < 0 || TakerCommissionRate > 0.01m)
            throw new InvalidOperationException("TakerCommissionRate должен быть в диапазоне [0; 0.01].");

        if (MarketExitMaxSlippage < 0 || MarketExitMaxSlippage > 0.05m)
            throw new InvalidOperationException("MarketExitMaxSlippage должен быть в диапазоне [0; 0.05].");

        if (BreakEvenActivationRR < 0)
            throw new InvalidOperationException("BreakEvenActivationRR должен быть >= 0.");

        if (BreakEvenBufferPercent < 0 || BreakEvenBufferPercent > 5m)
            throw new InvalidOperationException("BreakEvenBufferPercent должен быть в диапазоне [0; 5].");

        if (OrderFillTimeoutSeconds < 1 || OrderFillTimeoutSeconds > 3600)
            throw new InvalidOperationException("OrderFillTimeoutSeconds должен быть в диапазоне [1; 3600].");

        if (OrderFillPollIntervalMs < 200 || OrderFillPollIntervalMs > 30_000)
            throw new InvalidOperationException("OrderFillPollIntervalMs должен быть в диапазоне [200; 30000].");

        if (PositionReviewIntervalSeconds < 5 || PositionReviewIntervalSeconds > 86_400)
            throw new InvalidOperationException("PositionReviewIntervalSeconds должен быть в диапазоне [5; 86400].");

        if (AnalysisIntervalSeconds < 1 || AnalysisIntervalSeconds > 3600)
            throw new InvalidOperationException("AnalysisIntervalSeconds должен быть в диапазоне [1; 3600].");

        if (MaxOpenPositions < 1 || MaxOpenPositions > 32)
            throw new InvalidOperationException("MaxOpenPositions должен быть в диапазоне [1; 32].");

        if (SymbolAnalysisCooldownSeconds < 0 || SymbolAnalysisCooldownSeconds > 3600)
            throw new InvalidOperationException(
                $"SymbolAnalysisCooldownSeconds должен быть в диапазоне [0; 3600]. Текущее: {SymbolAnalysisCooldownSeconds}");

        if (ProtectionLimitSlippage < 0m || ProtectionLimitSlippage > 0.05m)
            throw new InvalidOperationException(
                $"ProtectionLimitSlippage должен быть в диапазоне [0; 0.05]. Текущее: {ProtectionLimitSlippage}");

        if (TrailingReplaceMinIntervalSeconds < 0 || TrailingReplaceMinIntervalSeconds > 3600)
            throw new InvalidOperationException(
                "TrailingReplaceMinIntervalSeconds должен быть в диапазоне [0; 3600].");

        if (TrailingReplaceMinStepPercent < 0m || TrailingReplaceMinStepPercent > 10m)
            throw new InvalidOperationException(
                $"TrailingReplaceMinStepPercent должен быть в диапазоне [0; 10]. Текущее: {TrailingReplaceMinStepPercent}");

        if (PositionMonitorIntervalMs < 100 || PositionMonitorIntervalMs > 10_000)
            throw new InvalidOperationException(
                $"PositionMonitorIntervalMs должен быть в диапазоне [100; 10000]. Текущее: {PositionMonitorIntervalMs}");

        if (PositionMonitorSweepSeconds < 1 || PositionMonitorSweepSeconds > 300)
            throw new InvalidOperationException(
                $"PositionMonitorSweepSeconds должен быть в диапазоне [1; 300]. Текущее: {PositionMonitorSweepSeconds}");

        if (PositionMonitorMaxLatencyMs < 200 || PositionMonitorMaxLatencyMs > 60_000)
            throw new InvalidOperationException(
                $"PositionMonitorMaxLatencyMs должен быть в диапазоне [200; 60000]. Текущее: {PositionMonitorMaxLatencyMs}");

        if (ProtectionRetryIntervalSeconds < 1 || ProtectionRetryIntervalSeconds > 600)
            throw new InvalidOperationException(
                $"ProtectionRetryIntervalSeconds должен быть в диапазоне [1; 600]. Текущее: {ProtectionRetryIntervalSeconds}");

        if (ExecutionQueueMaxParallel < 1 || ExecutionQueueMaxParallel > 8)
            throw new InvalidOperationException(
                $"ExecutionQueueMaxParallel должен быть в диапазоне [1; 8]. Текущее: {ExecutionQueueMaxParallel}");

        if (ExecutionIdempotencyTtlSeconds < 10 || ExecutionIdempotencyTtlSeconds > 86_400)
            throw new InvalidOperationException(
                $"ExecutionIdempotencyTtlSeconds должен быть в диапазоне [10; 86400]. Текущее: {ExecutionIdempotencyTtlSeconds}");

        if (ExecutionQueueWarnWaitMs < 100 || ExecutionQueueWarnWaitMs > 120_000)
            throw new InvalidOperationException(
                $"ExecutionQueueWarnWaitMs должен быть в диапазоне [100; 120000]. Текущее: {ExecutionQueueWarnWaitMs}");

        if (ExecutionRetryMaxAttempts < 1 || ExecutionRetryMaxAttempts > 10)
            throw new InvalidOperationException(
                $"ExecutionRetryMaxAttempts должен быть в диапазоне [1; 10]. Текущее: {ExecutionRetryMaxAttempts}");

        if (ExecutionRetryBaseDelayMs < 50 || ExecutionRetryBaseDelayMs > 10_000)
            throw new InvalidOperationException(
                $"ExecutionRetryBaseDelayMs должен быть в диапазоне [50; 10000]. Текущее: {ExecutionRetryBaseDelayMs}");

        if (ExecutionRetryMaxDelayMs < 100 || ExecutionRetryMaxDelayMs > 60_000)
            throw new InvalidOperationException(
                $"ExecutionRetryMaxDelayMs должен быть в диапазоне [100; 60000]. Текущее: {ExecutionRetryMaxDelayMs}");

        if (ExecutionRetryMaxDelayMs < ExecutionRetryBaseDelayMs)
            throw new InvalidOperationException(
                $"ExecutionRetryMaxDelayMs ({ExecutionRetryMaxDelayMs}) не может быть меньше ExecutionRetryBaseDelayMs ({ExecutionRetryBaseDelayMs})");

        if (UserDataKeepAliveMinutes < 1 || UserDataKeepAliveMinutes > 59)
            throw new InvalidOperationException(
                $"UserDataKeepAliveMinutes должен быть в диапазоне [1; 59] (Binance аннулирует listenKey через 60 мин). Текущее: {UserDataKeepAliveMinutes}");

        if (ReconciliationEntryGraceSeconds < 5 || ReconciliationEntryGraceSeconds > 300)
            throw new InvalidOperationException($"ReconciliationEntryGraceSeconds должен быть в диапазоне [5; 300]. Текущее: {ReconciliationEntryGraceSeconds}");

        if (UserDataPollingIntervalSeconds < 5 || UserDataPollingIntervalSeconds > 300)
            throw new InvalidOperationException(
                $"UserDataPollingIntervalSeconds должен быть в диапазоне [5; 300]. Текущее: {UserDataPollingIntervalSeconds}");

        if (ExecutionStateMaxStalenessSeconds < 15 || ExecutionStateMaxStalenessSeconds > 900)
            throw new InvalidOperationException(
                $"ExecutionStateMaxStalenessSeconds должен быть в диапазоне [15; 900]. Текущее: {ExecutionStateMaxStalenessSeconds}");

        if (UserDataStreamHealthCheckSeconds < 5 || UserDataStreamHealthCheckSeconds > 300)
            throw new InvalidOperationException(
                $"UserDataStreamHealthCheckSeconds должен быть в диапазоне [5; 300]. Текущее: {UserDataStreamHealthCheckSeconds}");

        if (ClockDriftCheckIntervalSeconds < 10 || ClockDriftCheckIntervalSeconds > 3600)
            throw new InvalidOperationException(
                $"ClockDriftCheckIntervalSeconds должен быть в диапазоне [10; 3600]. Текущее: {ClockDriftCheckIntervalSeconds}");

        if (ClockDriftWarningThresholdMs < 100 || ClockDriftWarningThresholdMs > 60_000)
            throw new InvalidOperationException(
                $"ClockDriftWarningThresholdMs должен быть в диапазоне [100; 60000]. Текущее: {ClockDriftWarningThresholdMs}");

        if (MaxEntrySpreadPercent <= 0m || MaxEntrySpreadPercent > 10m)
            throw new InvalidOperationException(
                $"MaxEntrySpreadPercent должен быть в диапазоне (0; 10]. Текущее: {MaxEntrySpreadPercent}");

        if (MinEntryDepthQuote < 0m)
            throw new InvalidOperationException(
                $"MinEntryDepthQuote должен быть >= 0. Текущее: {MinEntryDepthQuote}");

        if (MinDepthNotionalRatio < 0m || MinDepthNotionalRatio > 1000m)
            throw new InvalidOperationException(
                $"MinDepthNotionalRatio должен быть в диапазоне [0; 1000]. Текущее: {MinDepthNotionalRatio}");

        if (LiquidityDepthLevels < 1 || LiquidityDepthLevels > 20)
            throw new InvalidOperationException(
                $"LiquidityDepthLevels должен быть в диапазоне [1; 20] (partial-depth stream отдаёт 20 уровней). Текущее: {LiquidityDepthLevels}");

        if (MinCandleQuoteVolume < 0m)
            throw new InvalidOperationException(
                $"MinCandleQuoteVolume должен быть >= 0 (0 — проверка выключена). Текущее: {MinCandleQuoteVolume}");

        if (MaxPortfolioExposurePercent <= 0m || MaxPortfolioExposurePercent > 100m)
            throw new InvalidOperationException(
                $"MaxPortfolioExposurePercent должен быть в диапазоне (0; 100]. Текущее: {MaxPortfolioExposurePercent}");

        if (MaxCorrelatedExposurePercent <= 0m || MaxCorrelatedExposurePercent > 100m)
            throw new InvalidOperationException(
                $"MaxCorrelatedExposurePercent должен быть в диапазоне (0; 100]. Текущее: {MaxCorrelatedExposurePercent}");

        if (MaxCorrelatedExposurePercent > MaxPortfolioExposurePercent)
            throw new InvalidOperationException(
                "MaxCorrelatedExposurePercent не может быть больше MaxPortfolioExposurePercent.");

        if (string.IsNullOrWhiteSpace(CorrelationBenchmarkSymbol))
            throw new InvalidOperationException("CorrelationBenchmarkSymbol не может быть пустым.");

        if (CorrelationLookbackCandles < 2 || CorrelationLookbackCandles > 1000)
            throw new InvalidOperationException(
                $"CorrelationLookbackCandles должен быть в диапазоне [2; 1000]. Текущее: {CorrelationLookbackCandles}");

        if (MinCorrelationSamples < 2 || MinCorrelationSamples > CorrelationLookbackCandles)
            throw new InvalidOperationException(
                $"MinCorrelationSamples должен быть в диапазоне [2; CorrelationLookbackCandles]. Текущее: {MinCorrelationSamples}");

        if (MaxPairwiseCorrelation < 0m || MaxPairwiseCorrelation > 1m)
            throw new InvalidOperationException(
                $"MaxPairwiseCorrelation должен быть в диапазоне [0; 1]. Текущее: {MaxPairwiseCorrelation}");

        if (SymbolDailyLossLimitPercent < 0m || SymbolDailyLossLimitPercent > 100m)
            throw new InvalidOperationException(
                $"SymbolDailyLossLimitPercent должен быть в диапазоне [0; 100]. Текущее: {SymbolDailyLossLimitPercent}");

        if (LossStreakPauseThreshold < 0 || LossStreakPauseThreshold > 100)
            throw new InvalidOperationException(
                $"LossStreakPauseThreshold должен быть в диапазоне [0; 100]. Текущее: {LossStreakPauseThreshold}");

        if (LossStreakPauseMinutes < 1 || LossStreakPauseMinutes > 10080)
            throw new InvalidOperationException(
                $"LossStreakPauseMinutes должен быть в диапазоне [1; 10080]. Текущее: {LossStreakPauseMinutes}");
    }
}