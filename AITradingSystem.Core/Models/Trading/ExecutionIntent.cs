namespace AITradingSystem.Core.Models.Trading;

/// <summary>
/// P0.3: состояние намерения отправить ордер.
/// </summary>
public enum ExecutionIntentStatus
{
    /// <summary>Намерение записано ДО отправки, ответ биржи неизвестен (в т.ч. процесс умер).</summary>
    Pending = 0,

    /// <summary>Биржа приняла ордер/связку — повторная отправка с этим ключом недопустима.</summary>
    Confirmed = 1,

    /// <summary>Отправка окончательно отклонена — ключ можно переиспользовать.</summary>
    Failed = 2
}

/// <summary>
/// P0.3: журнал намерений исполнения. Запись создаётся до сетевого вызова и переживает
/// рестарт процесса, поэтому повтор с тем же <c>clientOrderId</c> не создаёт второй ордер
/// даже после падения (в памяти кэш идемпотентности этого не умеет).
///
/// <see cref="ExecutionIntentStatus.Pending"/> после рестарта означает «отправили, но не знаем,
/// долетело ли»: такой ключ сначала сверяется с биржей по <c>origClientOrderId</c>,
/// и только потом решается, отправлять ли ордер.
/// </summary>
public sealed class ExecutionIntent
{
    /// <summary>clientOrderId ордера или listClientOrderId связки — он же ключ идемпотентности.</summary>
    public string ClientOrderId { get; set; } = string.Empty;

    public string Symbol { get; set; } = string.Empty;

    /// <summary>Тип операции = приоритет очереди: Entry / Close / Trailing / Protection.</summary>
    public string Operation { get; set; } = string.Empty;

    /// <summary>E1: stable decision/intent/position linkage persisted before submission.</summary>
    public string? DecisionId { get; set; }
    public string? EntryIntentId { get; set; }
    public string? PositionId { get; set; }
    public string? ProtectionIdentity { get; set; }
    public Enums.OrderSide? Side { get; set; }

    public ExecutionIntentStatus Status { get; set; } = ExecutionIntentStatus.Pending;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Сколько сетевых попыток сделано по этому намерению.</summary>
    public int Attempts { get; set; }

    /// <summary>Последняя ошибка (для разбора и для реконсиляции P0.5).</summary>
    public string? Error { get; set; }

    /// <summary>Ответ биржи не подтверждён — требуется сверка с биржей.</summary>
    public bool IsUnresolved => Status == ExecutionIntentStatus.Pending;
}
