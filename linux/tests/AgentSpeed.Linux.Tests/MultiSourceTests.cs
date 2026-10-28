using AgentSpeed.Core;
using AgentSpeed.Presentation.History;

namespace AgentSpeed.Linux.Tests;

public sealed class MultiSourceTests
{
    private static readonly DateOnly Day = new(2026, 10, 27);

    [Fact]
    public void EquivalentClaudeRootsNormalizeBeforeSharedRootValidation()
    {
        Assert.Equal(LinuxSources.Normalize("/tmp/shared-claude"), LinuxSources.Normalize("/tmp/shared-claude/"));
        Assert.Equal("/", LinuxSources.Normalize("/"));
    }

    [Fact]
    public void SameModelNameAcrossSourcesKeepsIndependentCountersAndCsvAttribution()
    {
        DailyUsage[] usage = [Usage(SourceKind.Codex, 100, 1000), Usage(SourceKind.ClaudeCode, 300, 9000)];
        var range = new DayRange(Day, Day);
        var models = ModelStatistics.Build(usage, range, null);
        Assert.Equal(2, models.Count);
        Assert.Equal(100, Assert.Single(models, row => row.Source == SourceKind.Codex).Output);
        Assert.Equal(300, Assert.Single(models, row => row.Source == SourceKind.ClaudeCode).Output);
        var csv = ModelStatistics.Csv(models, range, null);
        Assert.Contains(",codex,same-model,", csv);
        Assert.Contains(",claude-code,same-model,", csv);
        var all = HistoryStatistics.Build(usage, Day, HistoryRangeKind.Today, source: null);
        Assert.Equal(400, all.Totals.Output);
        Assert.Equal(40, all.Totals.AverageSpeed);
        var claude = HistoryStatistics.Build(usage, Day, HistoryRangeKind.Today, source: SourceKind.ClaudeCode);
        Assert.Equal(300, claude.Totals.Output);
        Assert.Equal(300 / 9d, claude.Totals.AverageSpeed);
    }

    [Fact]
    public void ClaudeOnlyConfigurationAndPresentationNeverRequireCodexRecords()
    {
        var settings = new UserSettings { EnabledSources = [SourceKind.ClaudeDesktop], SourceDirectories = new()
            { [SourceKind.ClaudeCode] = "/inactive", [SourceKind.ClaudeDesktop] = "/active" } };
        var config = LinuxSources.Configuration(settings, "/codex");
        Assert.Equal("/active", config.ClaudeRoot);
        Assert.DoesNotContain(SourceKind.Codex, config.EnabledSources);
        var now = DateTimeOffset.UtcNow;
        var snapshot = SpeedSnapshot.Empty with
        {
            State = MonitorState.Running, Timestamp = now,
            Sources = [new(SourceKind.ClaudeDesktop, true, "/active", true, 3, new(20, 100), TokenTotals.Zero, 0)],
            LastResponse = new(SourceKind.ClaudeDesktop, "private", "model", 100, 1000, TimeSpan.FromSeconds(5), 20, now),
        };
        var reading = SpeedReadingPresentation.From(snapshot);
        Assert.Contains("100 token", reading.LastMeasurement);
        var status = DataStatusPresentation.Describe(snapshot, "/codex");
        Assert.Contains("3 个会话", status);
        Assert.Contains("/active", status);
        Assert.DoesNotContain("/codex", status);
    }

    [Fact]
    public void SourceSubsetDropsOtherLiveValuesAndGlobalHistoricalCurve()
    {
        var now = DateTimeOffset.UtcNow;
        var codex = new SourceStatus(SourceKind.Codex, true, "/codex", true, 1, new(100, 1000), new(Requests: 2, Output: 200), 1);
        var claude = new SourceStatus(SourceKind.ClaudeCode, true, "/claude", true, 1, new(30, 500), new(Requests: 1, Output: 300), 1, new(10, 100));
        var snapshot = SpeedSnapshot.Empty with
        {
            State = MonitorState.Running, Timestamp = now, Live = new(130, 1500), Sources = [codex, claude],
            Today = new(Requests: 3, Output: 500), History = [new(now, new(120, 1400), new(10, 100))],
            LastResponse = new(SourceKind.Codex, "private", "model", 100, 1000, TimeSpan.FromSeconds(1), 100, now),
        };
        var subset = LinuxSources.Subset(snapshot, SourceSet.Of(SourceKind.ClaudeCode));
        Assert.Equal(30, subset.Live.Output);
        Assert.Equal(300, subset.Today.Output);
        Assert.Equal(1 / 3d, subset.LiveEstimatedShare);
        Assert.Single(subset.Sources);
        Assert.Empty(subset.History);
        Assert.Null(subset.LastResponse);
        Assert.Same(snapshot, LinuxSources.Subset(snapshot, null));
    }

    private static DailyUsage Usage(SourceKind source, long output, long ms) => new("2026-10-27", source,
        new(Requests: 1, Input: output * 10, Output: output), output, ms, 0, 1, null,
        [new("same-model", 1, output) { Input = output * 10, CachedInput = 0, MeasuredOutput = output, MeasuredMs = ms }]);
}
