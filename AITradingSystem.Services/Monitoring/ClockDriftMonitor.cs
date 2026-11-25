using AITradingSystem.Common.Configuration;
using AITradingSystem.Common.Extensions;
using AITradingSystem.Data.Binance;
using Microsoft.Extensions.Logging;

namespace AITradingSystem.Services.Monitoring;

/// <summary>
/// P1.8: независимый периодический мониторинг дрейфа локальных часов относительно Binance.
/// Измерение с компенсацией RTT: берётся середина окна запроса; алерт отправляется только при смене
/// состояния (норма/предупреждение/недоступность), чтобы не спамить каждый интервал.
/// Работает и в Paper, и в Live, не зависит от наличия API-ключей и не ждёт цикла анализа.
/// </summary>
public sealed class ClockDriftMonitor : IAsyncDisposable
{
    private readonly TradingConfig _config;
    private readonly IBinanceRestClient _rest;
    private readonly MetricsCollector _metrics;
    private readonly TelegramAlertService _alerts;
    private readonly ILogger<ClockDriftMonitor> _logger;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _running;
    private State _state = State.Normal;

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    public ClockDriftMonitor(
        TradingConfig config,
        IBinanceRestClient rest,
        MetricsCollector metrics,
        TelegramAlertService alerts,
        ILogger<ClockDriftMonitor> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _rest = rest ?? throw new ArgumentNullException(nameof(rest));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _alerts = alerts ?? throw new ArgumentNullException(nameof(alerts));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Start(CancellationToken ct)
    {
        if (!_config.ClockDriftMonitorEnabled)
            return;
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return;

        _state = State.Normal;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => LoopAsync(_cts.Token), _cts.Token);
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _running, 0) == 0)
            return;

        var cts = _cts;
        var loop = _loop;
        _cts = null;
        _loop = null;

        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { }

        if (loop != null)
        {
            try { await loop; }
            catch (OperationCanceledException) { }
        }

        cts?.Dispose();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(_config.ClockDriftCheckIntervalSeconds);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await CheckOnceAsync(ct);
                await Task.Delay(interval, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task CheckOnceAsync(CancellationToken ct)
    {
        try
        {
            var localBefore = DateTime.UtcNow.ToUnixMilliseconds();
            var serverTime = await _rest.GetServerTimeAsync(ct);
            var localAfter = DateTime.UtcNow.ToUnixMilliseconds();
            var sample = ClockDriftSample.Measure(localBefore, serverTime, localAfter, DateTime.UtcNow);
            var warning = sample.Exceeds(_config.ClockDriftWarningThresholdMs);

            _metrics.RecordClockDrift(sample.DriftMs, sample.RttMs, warning, sample.CheckedAtUtc);

            if (warning)
                _logger.LogWarning(
                    "CLOCK-DRIFT: дрейф {Drift} мс, RTT {Rtt} мс, порог {Threshold} мс",
                    sample.DriftMs, sample.RttMs, _config.ClockDriftWarningThresholdMs);
            else
                _logger.LogDebug(
                    "CLOCK-DRIFT-OK: дрейф {Drift} мс, RTT {Rtt} мс",
                    sample.DriftMs, sample.RttMs);

            var previous = _state;
            _state = warning ? State.Warning : State.Normal;
            if (previous == _state)
                return;

            string message;
            string level;
            if (warning)
            {
                message = $"Дрейф часов относительно Binance: {sample.DriftMs:+0;-0;0} мс " +
                          $"(порог {_config.ClockDriftWarningThresholdMs} мс, RTT {sample.RttMs} мс). " +
                          "Подписанные запросы могут отклоняться с -1021.";
                level = "Warning";
            }
            else
            {
                message = $"Проверка часов восстановлена: дрейф {sample.DriftMs:+0;-0;0} мс, " +
                          $"RTT {sample.RttMs} мс, порог {_config.ClockDriftWarningThresholdMs} мс.";
                level = "Info";
            }

            _metrics.RecordClockDriftAlert();
            await _alerts.SendAlertAsync(message, level, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var at = DateTime.UtcNow;
            _metrics.RecordClockDriftFailure(ex.Message, at);
            _logger.LogWarning(ex, "CLOCK-DRIFT-CHECK-FAILED: не удалось получить серверное время Binance");

            if (_state == State.Unavailable)
                return;

            _state = State.Unavailable;
            _metrics.RecordClockDriftAlert();
            await _alerts.SendAlertAsync(
                $"Проверка дрейфа часов недоступна: {ex.Message}", "Warning", ct);
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private enum State
    {
        Normal,
        Warning,
        Unavailable
    }
}

/// <summary>Одно измерение дрейфа часов с компенсацией сетевой задержки.</summary>
public readonly record struct ClockDriftSample(
    long DriftMs,
    long RttMs,
    DateTime CheckedAtUtc)
{
    /// <summary>
    /// Дрейф = serverTime - середина(localBefore, localAfter). RTT = localAfter - localBefore.
    /// Так односторонняя сетевая задержка не принимается за дрейф часов.
    /// </summary>
    public static ClockDriftSample Measure(
        long localBeforeMs,
        long serverTimeMs,
        long localAfterMs,
        DateTime checkedAtUtc)
    {
        if (localAfterMs < localBeforeMs)
            throw new ArgumentOutOfRangeException(nameof(localAfterMs), "localAfter must be >= localBefore");

        var rtt = localAfterMs - localBeforeMs;
        var midpoint = localBeforeMs + rtt / 2;
        var drift = serverTimeMs - midpoint;
        return new ClockDriftSample(drift, rtt, checkedAtUtc);
    }

    public bool Exceeds(long thresholdMs) => Math.Abs(DriftMs) > thresholdMs;
}
