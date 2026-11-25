using AITradingSystem.Core.Models.Trading;

namespace AITradingSystem.Data.Storage;

/// <summary>
/// P0.3: durable-журнал намерений исполнения (write-ahead перед отправкой ордера).
/// Нужен, чтобы идемпотентность по clientOrderId переживала рестарт процесса.
/// </summary>
public interface IExecutionJournal
{
    /// <summary>Создать/обновить намерение. Вызывается ДО отправки и после каждого исхода.</summary>
    Task RecordAsync(ExecutionIntent intent, CancellationToken ct = default);

    /// <summary>Прочитать журнал целиком (при старте движка).</summary>
    Task<List<ExecutionIntent>> LoadAsync(CancellationToken ct = default);

    /// <summary>Удалить подтверждённые/отклонённые записи старше TTL. Незавершённые не удаляются.</summary>
    Task PruneAsync(TimeSpan ttl, CancellationToken ct = default);
}
