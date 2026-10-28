using AgentSpeed.Core;
using AgentSpeed.Presentation.History;

namespace AgentSpeed.Linux.Tests;

public sealed class HistoryStatisticsTests
{
    [Fact]
    public void CodexRangeTotalsUseMeasuredTimeInsteadOfAveragingDailySpeeds()
    {
        var period = HistoryStatistics.Build([Day("2026-10-25", 100, 1000), Day("2026-10-26", 300, 9000),
            Day("2026-10-26", 9999, 1) with { Source = SourceKind.ClaudeCode }], new(2026, 10, 26), HistoryRangeKind.Days7);
        Assert.Equal(2, period.Days.Count);
        Assert.Equal(400, period.Totals.Output);
        Assert.Equal(40, period.Totals.AverageSpeed);
    }

    [Fact]
    public void UnknownCostsRemainUnknownAndMixedKnownCostsBecomeALowerBound()
    {
        var period = HistoryStatistics.Build([Day("2026-10-25", 100, 1000),
            Day("2026-10-26", 100, 1000) with { Cost = new CostTally(2_500_000) }], new(2026, 10, 26), HistoryRangeKind.Days7);
        Assert.StartsWith("≥US$", HistoryStatistics.Money(period.Totals.Cost));
        Assert.Equal(2_500_000, period.Totals.Cost.MicroUsd);
        Assert.Equal("—", HistoryStatistics.Money(new HistoryCost(new CostTally(0, 2), 2)));
        Assert.Equal("—", HistoryStatistics.Money(new HistoryCost(null, 2)));
    }

    [Fact]
    public void CalendarGapsAreNotTurnedIntoMeasuredZeroUsage()
    {
        var period = HistoryStatistics.Build([Day("2026-10-26", 100, 1000)], new(2026, 10, 26), HistoryRangeKind.Days7);
        var buckets = HistoryStatistics.Buckets(period);
        Assert.Equal(7, buckets.Count);
        Assert.Null(buckets[0].Value(0));
        Assert.Null(buckets[0].Totals.AverageSpeed);
        Assert.Equal(100, buckets[^1].Value(0));
    }

    [Fact]
    public void LongRangeChartBinningPreservesAllOutputAndRequests()
    {
        var end = new DateOnly(2026, 10, 26);
        var source = Enumerable.Range(0, 400).Select(i => Day(end.AddDays(-i).ToString("yyyy-MM-dd"), i + 1, 1000)).ToArray();
        var period = HistoryStatistics.Build(source, end, HistoryRangeKind.All);
        var buckets = HistoryStatistics.Buckets(period);
        Assert.Equal(180, buckets.Count);
        Assert.Equal(period.Totals.Output, buckets.Sum(bucket => bucket.Totals.Output));
        Assert.Equal(400, buckets.Sum(bucket => bucket.Totals.Requests));
        Assert.All(buckets, bucket => Assert.True(bucket.Start <= bucket.End));
    }

    [Fact]
    public void PreviousPeriodAndCustomDatesHaveExactInclusiveBoundaries()
    {
        var period = HistoryStatistics.Build([Day("2026-10-19", 100, 1000), Day("2026-10-20", 200, 1000),
            Day("2026-10-26", 300, 1000)], new(2026, 10, 26), HistoryRangeKind.Days7);
        Assert.Equal(500, period.Totals.Output);
        Assert.Equal(100, period.Previous.Output);
        var custom = HistoryStatistics.Build([Day("2026-10-19", 100, 1000), Day("2026-10-20", 200, 1000)],
            new(2026, 10, 26), HistoryRangeKind.Custom, new DayRange(new(2026, 10, 20), new(2026, 10, 20)));
        Assert.Equal(200, custom.Totals.Output);
    }

    private static DailyUsage Day(string day, long output, long measuredMs) => new(day, SourceKind.Codex,
        new TokenTotals(Requests: 1, Input: output * 10, Output: output), output, measuredMs, 0, 1, null, []);
}
