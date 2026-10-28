using AgentSpeed.Core;
using AgentSpeed.Core.Traffic;

namespace AgentSpeed.Linux.Tests;

public sealed class TrafficWindowProjectionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly TrafficSplit CodexBytes = new(new(100, 200), new(10, 20));
    private static readonly TrafficSplit ClaudeBytes = new(new(300, 400), new(30, 40));

    [Fact]
    public void DisabledSourcesDisappearFromRatesAgentsAndCurvesWhileMachineCountersStayUnchanged()
    {
        var original = Snapshot();
        var projected = TrafficWindowProjection.Snapshot(original, new HashSet<SourceKind> { SourceKind.Codex },
            new Dictionary<string, SourceKind> { ["codex-agent"] = SourceKind.Codex, ["claude-agent"] = SourceKind.ClaudeCode });
        Assert.Equal([SourceKind.Codex], projected.Sources.Keys);
        Assert.Equal([SourceKind.Codex], projected.SourceHistory.Keys);
        Assert.Equal(["codex-agent"], projected.Agents.Keys);
        Assert.Equal([SourceKind.Codex], projected.ReportTotals.Sources.Keys);
        Assert.Equal(original.MachineLive, projected.MachineLive);
        Assert.Equal(original.MachineToday, projected.MachineToday);
        Assert.Equal(original.MachineSampledMs, projected.MachineSampledMs);
        Assert.Same(original.MachineHistory, projected.MachineHistory);
        Assert.Equal(original.ReportTotals.Epoch, projected.ReportTotals.Epoch);
        Assert.Equal(2, original.Sources.Count);
        Assert.Equal(3, original.Agents.Count);
    }

    [Fact]
    public void AgentWithoutSourceProofIsHiddenWhenVisibilityFilteringIsRequested()
    {
        var filtered = TrafficWindowProjection.Snapshot(Snapshot(), new HashSet<SourceKind> { SourceKind.Codex });
        Assert.Empty(filtered.Agents);
        Assert.Single(filtered.Sources);
        var mapped = TrafficWindowProjection.Snapshot(Snapshot(), new HashSet<SourceKind> { SourceKind.Codex },
            new Dictionary<string, SourceKind> { ["codex-agent"] = SourceKind.Codex });
        Assert.Equal(["codex-agent"], mapped.Agents.Keys);
    }

    [Fact]
    public void HistoricalRowsAndCsvRespectCurrentSourcesWithoutDeletingInternalHistory()
    {
        TrafficDay[] original = [new("2026-10-28", "machine", 1000, 2000, 0, 0, 1000),
            new("2026-10-28", "codex", 100, 200, 10, 20, 1000),
            new("2026-10-28", "claudeCode", 300, 400, 30, 40, 1000)];
        var visible = TrafficWindowProjection.Days(original, new HashSet<SourceKind> { SourceKind.Codex });
        Assert.Equal(["machine", "codex"], visible.Select(row => row.Key));
        var csv = LinuxTrafficStore.Csv(visible);
        Assert.Contains(",machine,1000,2000,", csv);
        Assert.Contains(",codex,100,200,", csv);
        Assert.DoesNotContain(",claudeCode,", csv);
        Assert.Equal(3, original.Length);
        var changed = TrafficWindowProjection.Days(original, new HashSet<SourceKind> { SourceKind.ClaudeCode });
        Assert.Equal(["machine", "claudeCode"], changed.Select(row => row.Key));
    }

    [Fact]
    public void EmptySourceSelectionKeepsMachineVisibleAndLegacyConstructorSemanticsRemainUnfiltered()
    {
        var original = Snapshot();
        Assert.Same(original, TrafficWindowProjection.Snapshot(original, null));
        var hidden = TrafficWindowProjection.Snapshot(original, new HashSet<SourceKind>());
        Assert.Empty(hidden.Sources); Assert.Empty(hidden.Agents); Assert.Empty(hidden.SourceHistory);
        Assert.Equal(original.MachineToday, hidden.MachineToday);
        TrafficDay[] days = [new("2026-10-28", "machine", 1, 2, 0, 0, 1000), new("2026-10-28", "codex", 3, 4, 0, 0, 1000)];
        Assert.Same(days, TrafficWindowProjection.Days(days, null));
        Assert.True(Assert.Single(TrafficWindowProjection.Days(days, new HashSet<SourceKind>())).IsMachine);
    }

    private static TrafficSnapshot Snapshot() => new()
    {
        Enabled = true, Timestamp = Now, ProcessState = ProcessTrafficState.Running(1),
        MachineLive = new(1234, 5678), MachineToday = new(10000, 20000), MachineSampledMs = 12000,
        MachineHistory = [new(Now, new(1234, 5678))],
        Sources = new Dictionary<SourceKind, SourceTraffic>
        {
            [SourceKind.Codex] = new(SourceKind.Codex, CodexBytes, default, new(110, 220), new(100, 200)),
            [SourceKind.ClaudeCode] = new(SourceKind.ClaudeCode, ClaudeBytes, default, new(330, 440), new(300, 400)),
        },
        Agents = new Dictionary<string, AgentTraffic>
        {
            ["codex-agent"] = new("codex-agent", CodexBytes, new(110, 220), new(100, 200)),
            ["claude-agent"] = new("claude-agent", ClaudeBytes, new(330, 440), new(300, 400)),
            ["unknown-stale-agent"] = new("unknown-stale-agent", CodexBytes, null, null),
        },
        SourceHistory = new Dictionary<SourceKind, IReadOnlyList<TrafficPoint>>
        { [SourceKind.Codex] = [new(Now, new(110, 220))], [SourceKind.ClaudeCode] = [new(Now, new(330, 440))] },
        ReportTotals = new(99, new Dictionary<SourceKind, TrafficSplit> { [SourceKind.Codex] = CodexBytes, [SourceKind.ClaudeCode] = ClaudeBytes }),
    };
}
