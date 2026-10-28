using AgentSpeed.Core;

namespace AgentSpeed.Linux.Tests;

public sealed class SpeedReadingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 2, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SlowHistoricalReferenceIsExplicitAndCompletedRequestIsSeparate()
    {
        var snapshot = Snapshot(1.1, 12.1, 1) with { LastResponse = Response(330, 3630, 300, Now.AddSeconds(-5)) };
        var reading = SpeedReadingPresentation.From(snapshot);
        Assert.StartsWith("≈", reading.Value);
        Assert.Equal("历史参考估算", reading.KindLabel);
        Assert.Contains("参考", reading.VehiclePrefix);
        Assert.Contains("330 token", reading.LastMeasurement);
        Assert.Contains("300.0 秒", reading.LastMeasurement);
        Assert.Contains("不代表当前屏幕", reading.Explanation);

        var completed = SpeedReadingPresentation.From(snapshot with
        {
            Live = new(100, 1100), LiveEstimatedShare = 0, GeneratingCount = 0,
            LastResponse = Response(500, 5500, 5, Now),
        });
        Assert.Equal("100", completed.Value);
        Assert.False(completed.IsEstimated);
        Assert.Contains("500 token", completed.LastMeasurement);
    }

    [Fact]
    public void TotalMetricUsesItsOwnEstimatedFractionAndNeverEditsSourceSnapshot()
    {
        var source = new SourceStatus(SourceKind.Codex, true, "/synthetic/sessions", true, 1,
            new(100, 10_000), TokenTotals.Zero, 1, new(0, 5000));
        var original = Snapshot(100, 10_000, 0) with
        {
            Sources = [source],
            History = [new(Now, new(10, 1000), new(5, 500))],
            PeakToday = new(120, 12_000),
        };
        var reading = SpeedReadingPresentation.From(original, SpeedMetric.Total);
        Assert.StartsWith("≈", reading.Value);
        Assert.Equal("含历史估算", reading.KindLabel);
        Assert.Contains("输入含缓存", reading.Explanation);
        var projected = MetricPresentation.Project(original, SpeedMetric.Total);
        Assert.Equal(10_000, projected.Live.Output);
        Assert.Equal(1000, projected.History[0].Measured.Output);
        Assert.Equal(500, projected.History[0].Estimated.Output);
        Assert.Equal(12_000, projected.PeakToday.Output);
        Assert.Equal(100, original.Live.Output);
        Assert.Equal(10, original.History[0].Measured.Output);
        Assert.Same(original, MetricPresentation.Project(original, SpeedMetric.Output));
    }

    [Fact]
    public void GeneratingWithoutReferenceIsUnknownAndOldDataNeverBecomesLive()
    {
        var reading = SpeedReadingPresentation.From(Snapshot(0, 0, 0) with
        {
            LastResponse = Response(500, 5500, 5, Now.AddHours(-1)),
        });
        Assert.Equal("—", reading.Value);
        Assert.False(reading.IsAvailable);
        Assert.Contains("等待本次用量", reading.KindLabel);
        Assert.StartsWith("旧记录", reading.LastMeasurement);
        Assert.DoesNotContain("100", reading.Value);
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(-1, 0)]
    [InlineData(1, double.NaN)]
    [InlineData(1, -0.1)]
    [InlineData(1, 1.1)]
    public void InvalidMetricsStayUnknown(double value, double share)
    {
        var reading = SpeedReadingPresentation.From(Snapshot(value, value, share));
        Assert.Equal("—", reading.Value);
        Assert.False(reading.IsAvailable);
    }

    [Fact]
    public void DiagnosticExportOmitsProjectsSessionPathsAndMessageFields()
    {
        var snapshot = Snapshot(1, 10, 1) with
        {
            LastResponse = Response(20, 200, 2, Now) with { Project = "PRIVATE_PROJECT_SENTINEL" },
            Agents = [new("/private/PRIVATE_SESSION_SENTINEL.jsonl", SourceKind.Codex, "PRIVATE_PROJECT_SENTINEL", "model", AgentPhase.Generating,
                false, new(1, 10), 1, Now)],
        };
        var diagnostics = DataStatusPresentation.Diagnostics(snapshot, HistoryState.Ready(Now), "/synthetic/sessions", new());
        Assert.DoesNotContain("PRIVATE_PROJECT_SENTINEL", diagnostics);
        Assert.DoesNotContain("PRIVATE_SESSION_SENTINEL", diagnostics);
        Assert.Contains("/synthetic/sessions", diagnostics);
        Assert.Contains("\"OutputTokens\": 20", diagnostics);
        Assert.Contains("model", diagnostics);
    }

    [Fact]
    public void LegacySettingsRetainOutputScaleWhileTotalAndOrbHaveSafeDefaults()
    {
        var legacy = System.Text.Json.JsonSerializer.Deserialize<UserSettings>("{\"Redline\":400,\"OrbX\":-1200}")!.Validated();
        Assert.Equal(SpeedMetric.Output, legacy.Metric);
        Assert.Equal(400, legacy.Redline);
        Assert.Equal(200_000, legacy.RedlineTotal);
        Assert.True(legacy.StartInBackground);
        Assert.False(legacy.StartAtLogin);
        Assert.False(legacy.OrbClickThrough);
        var invalid = (legacy with { OrbSize = double.NaN, OrbOpacity = 0, RedlineTotal = double.PositiveInfinity }).Validated();
        Assert.Equal(212, invalid.OrbSize);
        Assert.Equal(.4, invalid.OrbOpacity);
        Assert.Equal(400, invalid.Redline);
        Assert.Equal(2_000_000, invalid.RedlineTotal);
    }

    private static SpeedSnapshot Snapshot(double output, double total, double share) => SpeedSnapshot.Empty with
    {
        Timestamp = Now, State = MonitorState.Running, Live = new(output, total), LiveEstimatedShare = share, GeneratingCount = 1,
    };

    private static ResponseSummary Response(long output, long total, double seconds, DateTimeOffset end) =>
        new(SourceKind.Codex, "project", "model", output, total, TimeSpan.FromSeconds(seconds), output / seconds, end);
}
