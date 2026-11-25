namespace AITradingSystem.Common.Extensions;

/// <summary>
/// Расширения для async/await с retry-логикой (FR-6.4).
/// </summary>
public static class TaskExtensions
{
    /// <summary>
    /// Выполняет операцию с экспоненциальным retry.
    /// </summary>
    public static async Task<T> RetryAsync<T>(
        Func<Task<T>> action,
        int maxRetries,
        int baseDelayMs,
        Func<Exception, bool>? shouldRetry = null,
        Action<int, Exception>? onRetry = null,
        CancellationToken ct = default)
    {
        if (maxRetries < 0)
            throw new ArgumentOutOfRangeException(nameof(maxRetries));

        var attempt = 0;
        while (true)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxRetries &&
                                       (shouldRetry?.Invoke(ex) ?? true))
            {
                attempt++;
                var delay = baseDelayMs * (int)Math.Pow(2, attempt - 1);
                // Добавляем jitter для избежания thundering herd
                delay += Random.Shared.Next(0, baseDelayMs / 2);

                onRetry?.Invoke(attempt, ex);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Выполняет void-операцию с retry.
    /// </summary>
    public static async Task RetryAsync(
        Func<Task> action,
        int maxRetries,
        int baseDelayMs,
        Func<Exception, bool>? shouldRetry = null,
        Action<int, Exception>? onRetry = null,
        CancellationToken ct = default)
    {
        await RetryAsync(
            async () =>
            {
                await action().ConfigureAwait(false);
                return true;
            },
            maxRetries, baseDelayMs, shouldRetry, onRetry, ct).ConfigureAwait(false);
    }
}