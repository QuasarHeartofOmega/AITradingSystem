using AITradingSystem.Common.Configuration;
using AITradingSystem.Services.Monitoring;
using AITradingSystem.Services.Trading;

namespace AITradingSystem.Tests;

/// <summary>
/// P0.4: fail-closed политика доверия к состоянию исполнения.
/// Отправленный REST-запрос не доказывает исполнение: нужен живой executionReport
/// либо свежая успешная реконсиляция в polling-режиме.
/// Тесты добавлены, но не запускались (запрет сборок/прогонов в этом проходе).
/// </summary>
public class ExecutionStateTrustTests
{
    private static readonly DateTime Now = new(2026, 11, 26, 12, 0, 0, DateTimeKind.Utc);

    private static bool Trust(
        bool streamConnected,
        bool pollingEnabled = true,
        int reconAgeSeconds = 10,
        int unresolved = 0,
        bool paper = false,
        bool hasKeys = true,
        bool streamEnabled = true,
        bool neverReconciled = false,
        int maxStaleness = 90)
    {
        var lastRecon = neverReconciled ? default : Now - TimeSpan.FromSeconds(reconAgeSeconds);
        return ReconciliationService.EvaluateExecutionTrust(
            paper, hasKeys, streamEnabled, streamConnected, pollingEnabled,
            lastRecon, Now, maxStaleness, unresolved, out _);
    }

    [Fact]
    public void Stream_Connected_With_Fresh_Reconciliation_Is_Trusted()
    {
        Assert.True(Trust(streamConnected: true));
    }

    [Fact]
    public void Stream_Down_Without_Polling_Fallback_Blocks_Entries()
    {
        Assert.False(Trust(streamConnected: false, pollingEnabled: false));
        Assert.False(ReconciliationService.EvaluateExecutionTrust(
            false, true, true, false, false, Now - TimeSpan.FromSeconds(5), Now, 90, 0, out var reason));
        Assert.Equal("user-data-stream-down-without-polling-fallback", reason);
    }

    [Fact]
    public void Polling_Fallback_Is_Trusted_Only_While_Reconciliation_Is_Fresh()
    {
        Assert.True(Trust(streamConnected: false, pollingEnabled: true, reconAgeSeconds: 20));
        Assert.False(Trust(streamConnected: false, pollingEnabled: true, reconAgeSeconds: 120));
        Assert.False(ReconciliationService.EvaluateExecutionTrust(
            false, true, true, false, true, Now - TimeSpan.FromSeconds(120), Now, 90, 0, out var reason));
        Assert.StartsWith("execution-state-stale", reason);
    }

    [Fact]
    public void Never_Reconciled_Account_Blocks_Entries_Even_With_Live_Stream()
    {
        Assert.False(Trust(streamConnected: true, neverReconciled: true));
        Assert.False(ReconciliationService.EvaluateExecutionTrust(
            false, true, true, true, true, default, Now, 90, 0, out var reason));
        Assert.Equal("no-successful-reconciliation", reason);
    }

    [Fact]
    public void Unresolved_Order_State_Blocks_Entries_Regardless_Of_Stream()
    {
        Assert.False(Trust(streamConnected: true, unresolved: 1));
        Assert.False(Trust(streamConnected: false, unresolved: 3));
        Assert.False(ReconciliationService.EvaluateExecutionTrust(
            false, true, true, true, true, Now, Now, 90, 2, out var reason));
        Assert.Equal("unknown-order-or-position-state:2", reason);
    }

    [Fact]
    public void Stale_Reconciliation_Blocks_Even_When_Socket_Is_Up()
    {
        // Стрим формально поднят, но сверка не проходила 4× окна — состояние нельзя считать известным.
        Assert.False(Trust(streamConnected: true, reconAgeSeconds: 400, maxStaleness: 90));
    }

    [Fact]
    public void Paper_And_Keyless_Modes_Have_No_Exchange_State_To_Trust()
    {
        Assert.True(Trust(streamConnected: false, pollingEnabled: false, paper: true, neverReconciled: true));
        Assert.True(Trust(streamConnected: false, pollingEnabled: false, hasKeys: false, neverReconciled: true));
        Assert.True(Trust(streamConnected: false, pollingEnabled: false, streamEnabled: false, neverReconciled: true));
    }

    [Fact]
    public void Config_Validates_UserData_Fallback_Settings()
    {
        var config = new TradingConfig { UserDataPollingIntervalSeconds = 4 };
        Assert.Throws<InvalidOperationException>(() => config.Validate());

        config = new TradingConfig { ExecutionStateMaxStalenessSeconds = 10 };
        Assert.Throws<InvalidOperationException>(() => config.Validate());

        config = new TradingConfig();
        config.Validate();
        Assert.True(config.UserDataStreamRequiredForEntries);
        Assert.True(config.UserDataPollingFallbackEnabled);
    }

    [Fact]
    public void Metrics_Record_UserData_Probe_And_Execution_State_Blocks()
    {
        var metrics = new MetricsCollector();
        metrics.RecordUserDataStreamProbe(supported: false);
        metrics.RecordUserDataStreamProbe(supported: true);
        metrics.RecordPollingFallbackReconcile();
        metrics.RecordReconciliationFailure();
        metrics.RecordExecutionStateBlock();

        Assert.Equal(2, metrics.UserDataStreamProbes);
        Assert.Equal(1, metrics.UserDataStreamProbeFailures);
        Assert.Equal(1, metrics.PollingFallbackReconciles);
        Assert.Equal(1, metrics.ReconciliationFailures);
        Assert.Equal(1, metrics.ExecutionStateBlocks);
        Assert.Contains("user-data probes 2", metrics.GetSummary());
        Assert.Contains("execution-state blocks 1", metrics.GetSummary());
    }
}
