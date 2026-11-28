// Phase 1.7: физически лежит в Common (TradingConfig использует эти enum'ы),
// namespace сохранён Core.Models.Enums — Core ссылается на Common, обратная ссылка создала бы цикл.
namespace AITradingSystem.Core.Models.Enums;

/// <summary>Phase 1.7: как бот относится к ручным/external спотовым остаткам.</summary>
public enum ExternalAdoptionMode
{
    /// <summary>Только детект и алерт; ничего не подхватывается автоматически.</summary>
    Off,
    /// <summary>Кандидаты показываются оператору; подхват только после явного согласия (дефолт).</summary>
    Confirm,
    /// <summary>Подхватывать найденные external-остатки автоматически.</summary>
    Auto
}

/// <summary>Phase 1.7: поведение при отказе биржевой защиты у external-позиции.</summary>
public enum ExternalProtectionFailurePolicy
{
    /// <summary>Алерт оператору, но НЕ продавать чужой актив автоматически (дефолт).</summary>
    AlertNotSell,
    /// <summary>Применить общее fail-closed правило бота (закрыть MARKET). Только явный выбор оператора.</summary>
    ClosePosition
}
