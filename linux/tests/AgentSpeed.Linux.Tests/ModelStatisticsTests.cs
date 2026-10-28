using AgentSpeed.Core;
using AgentSpeed.Presentation.History;

namespace AgentSpeed.Linux.Tests;

public sealed class ModelStatisticsTests
{
    private static readonly DayRange Range = new(new(2026, 10, 20), new(2026, 10, 26));

    [Fact]
    public void ModelInputComesFromExactCodexModelCountersWithinTheRange()
    {
        var rows = ModelStatistics.Build([
            Day("2026-10-20", Model("a", input: 9000, cached: 8000, output: 10), Model("b", input: 100, cached: 20, output: 90)),
            Day("2026-10-26", Model("a", input: 30, cached: 0, output: 1)),
            Day("2026-10-19", Model("a", input: 99999, cached: 99999, output: 99999)),
            Day("invalid", Model("a", input: 99999, cached: 99999, output: 99999)),
            Day("2026-10-21", Model("a", input: 99999, cached: 99999, output: 99999)) with { Source = SourceKind.ClaudeCode },
        ], Range);
        var a = Assert.Single(rows, row => row.Model == "a");
        Assert.Equal((9030L, 8000L, 11L, 2L), (a.Input, a.CachedInput, a.Output, a.Requests));
        var b = Assert.Single(rows, row => row.Model == "b");
        Assert.Equal((100L, 20L, 90L), (b.Input, b.CachedInput, b.Output));
    }

    [Fact]
    public void MixedOldAndNewInputStaysUnknownWithoutHidingOutputOrCost()
    {
        var rows = ModelStatistics.Build([
            Day("2026-10-20", Model("a", 100, 80, 10) with { Cost = new CostTally(123456) }),
            Day("2026-10-21", Model("a", null, null, 20)),
        ], Range);
        var row = Assert.Single(rows);
        Assert.Null(row.Input);
        Assert.Null(row.CachedInput);
        Assert.Equal(30, row.Output);
        Assert.Equal(2, row.Requests);
        Assert.Equal(123456, row.Cost.MicroUsd);
        Assert.True(row.Cost.IsPartial);
    }

    [Fact]
    public void OutputAverageIsWeightedByMeasuredDurationAndUnknownWithoutMeasurements()
    {
        var rows = ModelStatistics.Build([
            Day("2026-10-20", Model("a", 1, 0, 100) with { MeasuredOutput = 100, MeasuredMs = 1000 }),
            Day("2026-10-21", Model("a", 2, 0, 300) with { MeasuredOutput = 300, MeasuredMs = 9000 }, Model("b", 0, 0, 0)),
        ], Range);
        Assert.Equal(40, Assert.Single(rows, row => row.Model == "a").AverageSpeed);
        Assert.Null(Assert.Single(rows, row => row.Model == "b").AverageSpeed);
    }

    [Fact]
    public void FilteringAndNumericSortAreDeterministicAndUnknownValuesStayLast()
    {
        var rows = ModelStatistics.Build([Day("2026-10-20", Model("GPT-B", 20, 0, 10),
            Model("gpt-a", 10, 0, 10), Model("unknown", null, null, 1))], Range);
        Assert.Equal(["GPT-B", "gpt-a", "unknown"], ModelStatistics.FilterAndSort(rows, sort: ModelStatisticSort.Input).Select(row => row.Model));
        Assert.Equal(["gpt-a", "GPT-B", "unknown"], ModelStatistics.FilterAndSort(rows, sort: ModelStatisticSort.Input, ascending: true).Select(row => row.Model));
        Assert.Equal(["gpt-a", "GPT-B"], ModelStatistics.FilterAndSort(rows, " GPT- ", ModelStatisticSort.Name, true).Select(row => row.Model));
        Assert.Empty(ModelStatistics.FilterAndSort(rows, "missing"));
    }

    [Fact]
    public void CsvRetainsExactTokensWeightedSpeedAndMicroUsdWithoutConversationContent()
    {
        var row = Assert.Single(ModelStatistics.Build([Day("2026-10-20", Model("gpt-test", 101, 70, 20) with
        { MeasuredOutput = 20, MeasuredMs = 2000, Cost = new CostTally(7) })], Range));
        var csv = ModelStatistics.Csv([row], Range);
        Assert.Contains("input_tokens,cached_input_tokens,output_tokens,requests", csv);
        Assert.Contains("2026-10-20,2026-10-26,codex,gpt-test,101,70,20,1,20,2000,10,0.000007,0\r\n", csv);
        Assert.Equal("agent-speed-models-codex-2026-10-20-2026-10-26.csv", ModelStatistics.FileName(Range));
    }

    [Fact]
    public void CsvQuotesModelNamesBlocksFormulasAndLeavesUnknownCountersEmpty()
    {
        var rows = ModelStatistics.Build([Day("2026-10-20", Model("=formula,\"name\"", null, null, 4))], Range);
        var csv = ModelStatistics.Csv(rows, Range);
        Assert.Contains("\"'=formula,\"\"name\"\"\",,,4,1,0,0,,,\r\n", csv);
        Assert.DoesNotContain("NaN", csv);
        Assert.DoesNotContain("Infinity", csv);
    }

    private static ModelUsage Model(string name, long? input, long? cached, long output) => new(name, 1, output)
    { Input = input, CachedInput = cached };

    // Deliberately unrelated daily totals ensure the model view never allocates inputs from a day's totals.
    private static DailyUsage Day(string day, params ModelUsage[] models) => new(day, SourceKind.Codex,
        new TokenTotals(Requests: 100, Input: 999_999, CachedInput: 555_555, Output: 100_000), 0, 0, 0, 1, null, models);
}
