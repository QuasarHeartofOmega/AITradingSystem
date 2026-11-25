using AITradingSystem.Core.Models.MarketData;

namespace AITradingSystem.Core.Indicators;

/// <summary>
/// Supertrend indicator (FR-3.1).
/// Комбинация ATR и ценового действия для определения тренда.
/// </summary>
public sealed class Supertrend : BaseIndicator
{
    private readonly ATR _atr;
    private readonly decimal _multiplier;
    private decimal _upperBand;
    private decimal _lowerBand;
    private decimal _supertrend;
    private bool _isUptrend = true;
    private decimal _prevClose;
    private decimal _prevUpperBand;
    private decimal _prevLowerBand;
    private bool _prevIsUptrend = true;

    public override string Name => $"Supertrend_{_atr.WarmupPeriod}_{_multiplier}";
    public override int WarmupPeriod => _atr.WarmupPeriod + 1;

    public decimal Value => _supertrend;
    public bool IsUptrend => _isUptrend;

    public Supertrend(int atrPeriod = 10, decimal multiplier = 3.0m)
    {
        _atr = new ATR(atrPeriod);
        _multiplier = multiplier;
    }

    public override void Update(OHLCV candle)
    {
        _atr.Update(candle);
        ProcessedCount++;

        if (!_atr.IsReady)
            return;

        var atrValue = _atr.GetValue();
        var hl2 = (candle.High + candle.Low) / 2m;

        var currentUpperBand = hl2 + _multiplier * atrValue;
        var currentLowerBand = hl2 - _multiplier * atrValue;

        if (ProcessedCount == WarmupPeriod)
        {
            // Первая итерация после прогрева
            _upperBand = currentUpperBand;
            _lowerBand = currentLowerBand;
            _supertrend = _isUptrend ? _lowerBand : _upperBand;
            _prevClose = candle.Close;
            _prevUpperBand = _upperBand;
            _prevLowerBand = _lowerBand;
            _prevIsUptrend = _isUptrend;
            return;
        }

        // Корректировка bands: не даем им двигаться против тренда
        if (currentLowerBand > _prevLowerBand || _prevClose < _prevLowerBand)
            _lowerBand = currentLowerBand;
        else
            _lowerBand = _prevLowerBand;

        if (currentUpperBand < _prevUpperBand || _prevClose > _prevUpperBand)
            _upperBand = currentUpperBand;
        else
            _upperBand = _prevUpperBand;

        // Определение тренда
        var prevSupertrend = _prevIsUptrend ? _prevLowerBand : _prevUpperBand;

        if (_prevIsUptrend)
        {
            _isUptrend = candle.Close >= _lowerBand;
        }
        else
        {
            _isUptrend = candle.Close > _upperBand;
        }

        _supertrend = _isUptrend ? _lowerBand : _upperBand;

        _prevClose = candle.Close;
        _prevUpperBand = _upperBand;
        _prevLowerBand = _lowerBand;
        _prevIsUptrend = _isUptrend;
    }

    public override decimal UpdateUnfinished(OHLCV candle)
    {
        return _supertrend;
    }

    public override decimal GetValue() => _supertrend;

    public override void Reset()
    {
        _atr.Reset();
        _upperBand = 0;
        _lowerBand = 0;
        _supertrend = 0;
        _isUptrend = true;
        _prevClose = 0;
        _prevUpperBand = 0;
        _prevLowerBand = 0;
        _prevIsUptrend = true;
        ProcessedCount = 0;
    }
}