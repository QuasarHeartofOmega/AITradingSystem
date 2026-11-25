using AITradingSystem.Common.Configuration;

namespace AITradingSystem.Core.RiskManagement;

/// <summary>
/// P0.4: снимок состояния трекера просадки для персиста (risk_state.json).
/// <see cref="CurrentBalance"/> = 0 означает «известен только пик» (миграция с legacy peak_balance.json):
/// дневной старт при восстановлении берётся из первого фактического баланса.
/// </summary>
public sealed record DrawdownState
{
    public string Environment { get; init; } = string.Empty;
    public string AccountId { get; init; } = string.Empty;
    public string QuoteAsset { get; init; } = "USDT";
    public decimal? FreeCashQuote { get; init; }
    public decimal CumulativeExternalCashFlow { get; init; }
    public decimal PeakBalance { get; init; }
    public decimal DayStartBalance { get; init; }
    public DateTime DayStartUtc { get; init; }
    public decimal MaxDrawdownPercent { get; init; }
    public decimal CurrentBalance { get; init; }
    public DateTime SavedAtUtc { get; init; }

    /// <summary>P0.2: kill-switch переживает рестарт в том же scoped risk-state.</summary>
    public bool KillSwitchEngaged { get; init; }
    public string KillSwitchReason { get; init; } = string.Empty;
    public DateTime KillSwitchEngagedAtUtc { get; init; }
    public decimal KillSwitchEquityAtEngage { get; init; }
    public bool KillSwitchRequiresManualReset { get; init; } = true;
    public string KillSwitchCloseStatus { get; init; } = string.Empty;
}

/// <summary>
/// Отслеживание просадки и дневных убытков (FR-5.6, FR-5.7).
/// P0.4: состояние (пик, дневной старт, максимальная просадка) снимается/восстанавливается,
/// смена дня считается по часам биржи, а не локального процесса.
/// </summary>
public sealed class DrawdownTracker
{
    private readonly TradingConfig _config;
    private decimal _peakBalance;
    private decimal _dayStartBalance;
    private DateTime _currentDay;
    private Func<DateTime> _exchangeUtcNow;

    /// <summary>P0.1: mark-to-market equity, не свободный cash.</summary>
    public decimal CurrentBalance { get; private set; }
    public decimal CurrentEquity => CurrentBalance;
    public decimal CumulativeExternalCashFlow { get; private set; }

    /// <summary>P1.7: старт текущего риск-дня для дневного лимита символа.</summary>
    public decimal DayStartBalance => _dayStartBalance;
    public decimal MaxDrawdownPercent { get; private set; }
    public decimal CurrentDrawdownPercent { get; private set; }
    public decimal DailyPnL { get; private set; }
    public decimal DailyPnLPercent { get; private set; }

    public bool IsMaxDrawdownHit => CurrentDrawdownPercent >= (decimal)_config.MaxDrawdown;
    public bool IsDailyLossLimitHit => DailyPnLPercent <= -(decimal)_config.DailyLossLimit;
    public bool IsTradingAllowed => !IsMaxDrawdownHit && !IsDailyLossLimitHit;

    public DrawdownTracker(TradingConfig config, decimal initialBalance, Func<DateTime>? exchangeUtcNow = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _exchangeUtcNow = exchangeUtcNow ?? (() => DateTime.UtcNow);
        _peakBalance = initialBalance;
        _dayStartBalance = initialBalance;
        CurrentBalance = initialBalance;
        _currentDay = _exchangeUtcNow().Date;
    }

    /// <summary>
    /// P0.4: источник времени для границы торгового дня (часы биржи).
    /// До установки используется локальный UTC.
    /// </summary>
    public void SetExchangeClock(Func<DateTime> utcNow)
    {
        _exchangeUtcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    /// <summary>P0.1: обновить risk-метрики от mark-to-market equity.</summary>
    public void UpdateEquity(decimal equity) => UpdateBalance(equity);

    /// <summary>
    /// Обновить баланс. P0.1: аргумент обязан быть equity, а не free cash.
    /// </summary>
    public void UpdateBalance(decimal newBalance)
    {
        CurrentBalance = newBalance;

        // Обновляем пик
        if (newBalance > _peakBalance)
            _peakBalance = newBalance;

        // Расчет просадки от пика
        CurrentDrawdownPercent = _peakBalance > 0
            ? (_peakBalance - newBalance) / _peakBalance
            : 0;

        if (CurrentDrawdownPercent > MaxDrawdownPercent)
            MaxDrawdownPercent = CurrentDrawdownPercent;

        // Проверка смены дня — по времени биржи (P0.4), не локального процесса
        var today = _exchangeUtcNow().Date;
        if (today != _currentDay)
        {
            _dayStartBalance = newBalance;
            _currentDay = today;
        }

        // Расчет дневного PnL
        DailyPnL = newBalance - _dayStartBalance;
        DailyPnLPercent = _dayStartBalance > 0
            ? DailyPnL / _dayStartBalance
            : 0;
    }

    /// <summary>
    /// P0.1: депозит/вывод/трансфер корректирует baseline, а не торговый PnL.
    /// Положительный amount = ввод капитала, отрицательный = вывод.
    /// </summary>
    public void RegisterExternalCashFlow(decimal amount)
    {
        if (amount == 0m) return;
        _peakBalance = Math.Max(0m, _peakBalance + amount);
        _dayStartBalance = Math.Max(0m, _dayStartBalance + amount);
        CumulativeExternalCashFlow += amount;
        UpdateBalance(Math.Max(0m, CurrentBalance + amount));
    }

    /// <summary>
    /// P0.4: снимок состояния для персиста.
    /// </summary>
    public DrawdownState CaptureState() => new()
    {
        CumulativeExternalCashFlow = CumulativeExternalCashFlow,
        PeakBalance = _peakBalance,
        DayStartBalance = _dayStartBalance,
        DayStartUtc = _currentDay,
        MaxDrawdownPercent = MaxDrawdownPercent,
        CurrentBalance = CurrentBalance,
        SavedAtUtc = _exchangeUtcNow()
    };

    /// <summary>
    /// P0.4: восстановление после рестарта. Пик и максимальная просадка переживают рестарт;
    /// дневной старт восстанавливается вместе с датой дня — если день по часам биржи уже сменился,
    /// дневной PnL обнуляется, но пик остаётся. Снимок «только пик» (CurrentBalance = 0)
    /// не трогает баланс/день — их задаст первый фактический UpdateBalance.
    /// </summary>
    public void RestoreState(DrawdownState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        CumulativeExternalCashFlow = state.CumulativeExternalCashFlow;
        if (state.PeakBalance > 0)
            _peakBalance = state.PeakBalance;

        if (state.MaxDrawdownPercent > MaxDrawdownPercent)
            MaxDrawdownPercent = state.MaxDrawdownPercent;

        if (state.CurrentBalance <= 0)
            return; // миграция с legacy peak_balance.json: известен только пик

        if (state.DayStartBalance > 0)
        {
            _dayStartBalance = state.DayStartBalance;
            _currentDay = state.DayStartUtc.Date;
        }
        else
        {
            _dayStartBalance = state.CurrentBalance;
            _currentDay = _exchangeUtcNow().Date;
        }

        // Пересчёт просадки/дневного PnL; заодно корректный ролловер, если сохранённый день уже прошёл.
        UpdateBalance(state.CurrentBalance);
    }

    /// <summary>
    /// Сбросить дневную статистику (при ручном сбросе).
    /// </summary>
    public void ResetDaily()
    {
        _dayStartBalance = CurrentBalance;
        DailyPnL = 0;
        DailyPnLPercent = 0;
    }

    /// <summary>
    /// Полная статистика для логирования.
    /// </summary>
    public string GetStatus()
    {
        return $"Balance: {CurrentBalance:F2}, " +
               $"Drawdown: {CurrentDrawdownPercent:P2} (max: {MaxDrawdownPercent:P2}), " +
               $"Daily PnL: {DailyPnL:F2} ({DailyPnLPercent:P2})";
    }
}
