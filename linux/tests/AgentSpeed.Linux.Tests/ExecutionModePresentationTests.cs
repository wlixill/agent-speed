using AgentSpeed.Core;
using AgentSpeed.Presentation.History;

namespace AgentSpeed.Linux.Tests;

public sealed class ExecutionModePresentationTests
{
    private static readonly DayRange Range = new(new(2026, 10, 20), new(2026, 10, 26));

    [Fact]
    public void ConfiguredFastNeverBecomesAnActualAgentModeOrHistoryBucket()
    {
        var configured = ExecutionModeNative.OpenAiTier("priority", ExecutionModeEvidence.Configured, ExecutionModeScope.Configuration);
        var agent = Agent(configured);
        var snapshot = Snapshot(agent) with
        {
            Sources = [new(SourceKind.Codex, true, "/synthetic/sessions", true, 1, Rate.Zero, TokenTotals.Zero, 1)
            { ConfiguredModes = configured }],
        };
        Assert.Equal(ExecutionModePresentation.Unknown, ExecutionModePresentation.Agent(agent));
        Assert.DoesNotContain("Fast", ExecutionModePresentation.Current(snapshot));
        Assert.Contains("Fast", ExecutionModePresentation.ConfiguredSource(snapshot));
        Assert.Contains("配置默认", ExecutionModePresentation.ConfiguredSource(snapshot));
        Assert.Contains("不代表", ExecutionModePresentation.ConfiguredDetails(snapshot));
        var day = Day("2026-10-24", "m", 2, 50, 40, 2000) with
        {
            Modes = [new("m", SourceKind.Codex, ExecutionModeKind.ServiceTier, "fast", "configured", "openai", 2, 50, 40, 2000)],
        };
        var history = ExecutionModePresentation.HistoryDays([day], Range);
        var row = Assert.Single(ExecutionModePresentation.Rows(history, ExecutionModeKind.ServiceTier));
        Assert.Equal(("unknown", "unknown", 2L, 50L), (row.Mode, row.Evidence, row.Requests, row.Output));
        Assert.DoesNotContain(",fast,", ExecutionModePresentation.Csv(history));
        Assert.DoesNotContain(",configured,", ExecutionModePresentation.Csv(history));
    }

    [Fact]
    public void SelectedAndServedEvidenceRemainDistinctAndServedControlsSummary()
    {
        var modes = ExecutionModeNative.OpenAiTier("priority", ExecutionModeEvidence.Selected, ExecutionModeScope.Request)
            .Merge(ExecutionModeNative.OpenAiTier("default"));
        var agent = Agent(modes);
        Assert.Contains("标准", ExecutionModePresentation.Agent(agent));
        Assert.Contains("实际响应", ExecutionModePresentation.Agent(agent));
        Assert.DoesNotContain("Fast", ExecutionModePresentation.Agent(agent));
        var detail = ExecutionModePresentation.AgentDetails(agent);
        Assert.Contains("任务选择", detail);
        Assert.Contains("Fast", detail);
        Assert.Contains("实际响应", detail);
        Assert.Contains("priority", detail);
        Assert.Equal("Agent 记录", ExecutionModePresentation.Evidence("recorded"));
    }

    [Fact]
    public void CurrentUsesOnlyActiveCodexAgentsAndDoesNotPickAnArbitraryMixedMode()
    {
        var standard = Agent(ExecutionModeNative.OpenAiTier("default"));
        var fast = Agent(ExecutionModeNative.OpenAiTier("priority"));
        var snapshot = Snapshot(standard, fast with { Source = SourceKind.ClaudeCode }, fast with { Phase = AgentPhase.Idle });
        Assert.Contains("标准", ExecutionModePresentation.Current(snapshot));
        Assert.DoesNotContain("Fast", ExecutionModePresentation.Current(snapshot));
        Assert.Contains("多种模式", ExecutionModePresentation.Current(Snapshot(standard, fast)));
        Assert.Contains("未知", ExecutionModePresentation.Current(Snapshot(fast with { Phase = AgentPhase.Idle })));
        Assert.DoesNotContain("PRIVATE_PROJECT", ExecutionModePresentation.CurrentDetails(snapshot));
        Assert.DoesNotContain("PRIVATE_SESSION", ExecutionModePresentation.CurrentDetails(snapshot));
    }

    [Fact]
    public void LegacyHistoryKeepsActualCountsUnderUnknownInsteadOfHidingThem()
    {
        var day = Day("2026-10-24", "m", 3, 90, 40, 2000);
        var history = ExecutionModePresentation.HistoryDays([day], Range);
        foreach (var kind in ExecutionModePresentation.Kinds)
        {
            var row = Assert.Single(ExecutionModePresentation.Rows(history, kind));
            Assert.Equal(("unknown", "unknown", 3L, 90L), (row.Mode, row.Evidence, row.Requests, row.Output));
            Assert.Equal(20, row.AverageSpeed);
        }
        Assert.Contains("未知", ExecutionModePresentation.Evidence("unknown"));
    }

    [Fact]
    public void KnownAndUnknownHistoryAreBothVisibleAndModeAveragesAreTimeWeighted()
    {
        var a = Day("2026-10-24", "m", 2, 150, 100, 1000) with
        {
            Modes = [new("m", SourceKind.Codex, ExecutionModeKind.ServiceTier, "fast", "served", "openai", 1, 100, 100, 1000)],
        };
        var b = Day("2026-10-25", "m", 1, 300, 300, 9000) with
        {
            Modes = [new("m", SourceKind.Codex, ExecutionModeKind.ServiceTier, "fast", "served", "openai", 1, 300, 300, 9000)],
        };
        var rows = ExecutionModePresentation.Rows(ExecutionModePresentation.HistoryDays([a, b], Range), ExecutionModeKind.ServiceTier);
        Assert.Equal(2, rows.Count);
        var known = Assert.Single(rows, row => row.Mode == "fast");
        Assert.Equal((2L, 400L), (known.Requests, known.Output));
        Assert.Equal(40, known.AverageSpeed);
        var unknown = Assert.Single(rows, row => row.Mode == "unknown");
        Assert.Equal((1L, 50L), (unknown.Requests, unknown.Output));
        Assert.Null(unknown.AverageSpeed);
        Assert.Equal(3, rows.Sum(row => row.Requests));
    }

    [Fact]
    public void UnnamedLegacyRequestsAreRetainedWithoutAllocatingThemToNamedModels()
    {
        var day = Day("2026-10-24", "m", 3, 90, 40, 2000) with { Models = [] };
        var row = Assert.Single(ExecutionModePresentation.Rows(ExecutionModePresentation.HistoryDays([day], Range), ExecutionModeKind.ServiceTier));
        Assert.Equal(string.Empty, row.Model);
        Assert.Equal((3L, 90L, 40L, 2000L), (row.Requests, row.Output, row.MeasuredOutput, row.MeasuredMs));
    }

    [Fact]
    public void CsvKeepsUnknownDatesAndEvidenceAndFiltersCodexAndSelectedDimension()
    {
        var day = Day("2026-10-24", "=unsafe,model", 1, 20, 0, 0);
        var history = ExecutionModePresentation.HistoryDays([day,
            day with { Source = SourceKind.ClaudeCode, Day = "2026-10-25" }, day with { Day = "2026-10-19" }], Range);
        var csv = ExecutionModePresentation.Csv(history, ExecutionModeKind.ServiceTier);
        Assert.Contains("2026-10-24,codex,\"'=unsafe,model\",serviceTier,unknown,unknown,,1,20,0,0,\r\n", csv);
        Assert.DoesNotContain("2026-10-25", csv);
        Assert.DoesNotContain("2026-10-19", csv);
        Assert.DoesNotContain(",accelerator,", csv);
        Assert.Equal(2, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void NoUsageDoesNotProduceInventedZeroModeRows()
    {
        Assert.Empty(ExecutionModePresentation.HistoryDays([], Range));
        Assert.Empty(ExecutionModePresentation.Rows([], ExecutionModeKind.ServiceTier));
        Assert.Single(ExecutionModePresentation.Csv([], ExecutionModeKind.ServiceTier).Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public void ModelNamesAndHighSpeedDoNotSupplyEvidenceAndRecordedIsNotUpgradedToServed()
    {
        var agent = Agent(ExecutionModes.Empty) with { Model = "fast-model-highspeed", Live = new(99_999, 999_999) };
        Assert.Equal(ExecutionModePresentation.Unknown, ExecutionModePresentation.Agent(agent));
        var day = Day("2026-10-24", "fast-model-highspeed", 3, 60, 0, 0) with
        {
            Modes = [
                new("fast-model-highspeed", SourceKind.Codex, ExecutionModeKind.ServiceTier, "fast", "selected", "openai", 1, 10, 0, 0),
                new("fast-model-highspeed", SourceKind.Codex, ExecutionModeKind.ServiceTier, "fast", "recorded", "openai", 1, 20, 0, 0),
                new("fast-model-highspeed", SourceKind.Codex, ExecutionModeKind.ServiceTier, "standard", "served", "openai", 1, 30, 0, 0),
            ],
        };
        var days = ExecutionModePresentation.HistoryDays([day], Range);
        var rows = ExecutionModePresentation.Rows(days, ExecutionModeKind.ServiceTier);
        Assert.Equal(3, rows.Count);
        Assert.Equal(1, Assert.Single(rows, row => row.Evidence == "selected").Requests);
        Assert.Equal(1, Assert.Single(rows, row => row.Evidence == "recorded").Requests);
        Assert.Equal("standard", Assert.Single(rows, row => row.Evidence == "served").Mode);
        var csv = ExecutionModePresentation.Csv(days, ExecutionModeKind.ServiceTier);
        Assert.Contains(",fast,recorded,openai,1,20,", csv);
        Assert.DoesNotContain(",fast,served,", csv);
    }

    private static AgentStatus Agent(ExecutionModes modes) => new("PRIVATE_SESSION", SourceKind.Codex, "PRIVATE_PROJECT", "m",
        AgentPhase.Generating, false, Rate.Zero, null, DateTimeOffset.UtcNow) { Modes = modes };
    private static SpeedSnapshot Snapshot(params AgentStatus[] agents) => SpeedSnapshot.Empty with { State = MonitorState.Running, Agents = agents };
    private static DailyUsage Day(string date, string model, long requests, long output, long measuredOutput, long measuredMs) =>
        new(date, SourceKind.Codex, new TokenTotals(Requests: requests, Output: output), measuredOutput, measuredMs, 0, 1, null,
            [new(model, requests, output, measuredOutput, measuredMs)]);
}
