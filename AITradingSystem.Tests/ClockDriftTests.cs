using AITradingSystem.Common.Configuration;
using AITradingSystem.Services.Monitoring;

namespace AITradingSystem.Tests;

public class ClockDriftTests
{
    [Fact]
    public void Measure_Compensates_Rtt_Using_Midpoint()
    {
        var at = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
        var sample = ClockDriftSample.Measure(1000, 1600, 1200, at);

        Assert.Equal(500, sample.DriftMs);
        Assert.Equal(200, sample.RttMs);
        Assert.Equal(at, sample.CheckedAtUtc);
    }

    [Fact]
    public void Measure_Supports_Negative_Drift_And_Threshold_Boundary()
    {
        var at = DateTime.UtcNow;
        var sample = ClockDriftSample.Measure(2000, 1500, 2200, at);

        Assert.Equal(-600, sample.DriftMs);
        Assert.Equal(200, sample.RttMs);
        Assert.True(sample.Exceeds(599));
        Assert.False(sample.Exceeds(600));
    }

    [Fact]
    public void Measure_Rejects_Reversed_Local_Window()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ClockDriftSample.Measure(2000, 1900, 1000, DateTime.UtcNow));
    }

    [Fact]
    public void Metrics_Record_Drift_Warning_Recovery_And_Failure()
    {
        var metrics = new MetricsCollector();
        var at = new DateTime(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);

        metrics.RecordClockDrift(1500, 80, warning: true, at);
        Assert.Equal(1, metrics.ClockDriftChecks);
        Assert.Equal(1500, metrics.LastClockDriftMs);
        Assert.Equal(80, metrics.LastClockRttMs);
        Assert.Equal(1500, metrics.MaxAbsClockDriftMs);
        Assert.True(metrics.IsClockDriftWarning);
        Assert.Null(metrics.ClockDriftError);

        metrics.RecordClockDrift(-200, 40, warning: false, at.AddMinutes(1));
        Assert.False(metrics.IsClockDriftWarning);
        Assert.Equal(1500, metrics.MaxAbsClockDriftMs);
        Assert.Equal(-200, metrics.LastClockDriftMs);

        metrics.RecordClockDriftFailure("timeout", at.AddMinutes(2));
        Assert.Equal(1, metrics.ClockDriftFailures);
        Assert.Equal("timeout", metrics.ClockDriftError);
        Assert.False(metrics.IsClockDriftWarning);

        metrics.RecordClockDriftAlert();
        Assert.Equal(1, metrics.ClockDriftAlerts);
        Assert.Contains("clock drift checks 2", metrics.GetSummary());
    }

    [Fact]
    public void Config_Validates_ClockDrift_Settings()
    {
        var config = new TradingConfig { ClockDriftCheckIntervalSeconds = 5 };
        Assert.Throws<InvalidOperationException>(() => config.Validate());

        config = new TradingConfig { ClockDriftWarningThresholdMs = 50 };
        Assert.Throws<InvalidOperationException>(() => config.Validate());
    }
}
