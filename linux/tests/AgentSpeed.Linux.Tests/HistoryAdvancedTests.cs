using AgentSpeed.Core;
using AgentSpeed.Core.Money;
using AgentSpeed.Core.Network;
using AgentSpeed.Core.Traffic;
using AgentSpeed.Presentation.Cost;
using AgentSpeed.Presentation.History;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Linux.Tests;

public sealed class HistoryAdvancedTests
{
    private static readonly DateOnly Today = new(2026, 10, 27);
    private static readonly DayRange Range = new(Today.AddDays(-2), Today);

    [Fact]
    public void SubscriptionExclusionChangesDisplayedMoneyWithoutDroppingUsageOrRawCsvCosts()
    {
        var period = HistoryStatistics.Build([Usage("2026-10-27", SourceKind.Codex, 100, 1_000_000),
            Usage("2026-10-27", SourceKind.ClaudeCode, 300, 2_000_000)], Today, HistoryRangeKind.Today,
            source: null, excluded: SourceSet.Of(SourceKind.Codex));
        Assert.Equal(400, period.Totals.Output);
        Assert.Equal(2, period.Totals.Requests);
        Assert.Equal(2_000_000, period.Totals.Cost.MicroUsd);
        Assert.Equal(3_000_000, Assert.Single(period.Days).TotalCost!.Value.CostMicroUsd);
        var buckets = HistoryStatistics.Buckets(period);
        Assert.Equal(2, buckets[0].Sources.Count);
        Assert.Equal(100, HistoryStatistics.SourceValue(buckets[0].Sources[SourceKind.Codex], 0));
        Assert.Equal(0, HistoryStatistics.SourceValue(buckets[0].Sources[SourceKind.Codex], 3));
        var shares = HistoryStatistics.Shares(period, 3);
        Assert.Equal(SourceKind.ClaudeCode, Assert.Single(shares).Source);
        var csv = HistoryExports.Daily(period.Days, null, Money());
        Assert.Contains("cost_cny", csv);
        Assert.Contains(",3.000000,0,", csv);
        Assert.Contains(",21.7500\r\n", csv);
    }

    [Fact]
    public void TrafficOnlyDaysJoinHistoryWhileMissingSampleDaysStayUnrecorded()
    {
        TrafficDay[] traffic = [new("2026-10-25", "machine", 400, 500, 0, 0, 60_000),
            new("2026-10-27", "codex", 100, 20, 10, 2, 60_000), new("2026-10-27", "machine", 1000, 2000, 0, 0, 60_000)];
        var period = HistoryStatistics.Build([Usage("2026-10-26", SourceKind.Codex, 50, 1)], Today, HistoryRangeKind.Days7,
            source: null, trafficDays: traffic, trafficSince: "2026-10-25", now: new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero));
        Assert.True(period.TrafficMeasured);
        Assert.Equal(3, period.Days.Count);
        Assert.False(period.Days[0].HasUsage);
        Assert.NotNull(period.Days[0].Traffic?.Machine);
        Assert.Null(period.Days.Single(day => day.Day == Today.AddDays(-1)).Traffic);
        Assert.Equal(new TrafficBytes(110, 22), period.Days[^1].Traffic!.Shown);
        Assert.Equal(new TrafficBytes(1000, 2000), period.Days[^1].Traffic!.Machine);
        var csv = HistoryExports.Daily(period.Days, null, traffic: true);
        Assert.Contains("traffic_rx_bytes,traffic_tx_bytes,traffic_rx_tools_bytes,traffic_tx_tools_bytes,traffic_coverage", csv);
        Assert.Contains(",100,20,10,2,observed_intervals\r\n", csv);
        Assert.Contains("2026-10-27,machine,", csv);
        Assert.Contains(",not_recorded\r\n", csv);
        Assert.Equal(50, period.Totals.Output);
    }

    [Fact]
    public void SingleSourceTrafficDoesNotIncludeMachineOrOtherSources()
    {
        TrafficDay[] traffic = [new("2026-10-27", "codex", 100, 20, 10, 2, 60_000),
            new("2026-10-27", "claudeCode", 900, 180, 90, 18, 60_000), new("2026-10-27", "machine", 1000, 2000, 0, 0, 60_000)];
        var period = HistoryStatistics.Build([], Today, HistoryRangeKind.Today, source: SourceKind.Codex, trafficDays: traffic, includeTools: false);
        var day = Assert.Single(period.Days);
        Assert.Equal(new TrafficBytes(100, 20), day.Traffic!.Shown);
        Assert.Null(day.Traffic.Machine);
        Assert.Equal(SourceKind.Codex, Assert.Single(day.Traffic.BySource).Key);
        Assert.DoesNotContain(",machine,", HistoryExports.Daily(period.Days, SourceKind.Codex, traffic: true));
    }

    [Fact]
    public void LongRangeSourceStacksRetainExactOutputInputRequestsAndTrafficTotals()
    {
        var usage = Enumerable.Range(0, 400).SelectMany(i => new[]
        { Usage(Today.AddDays(-i).ToString("yyyy-MM-dd"), SourceKind.Codex, i + 1, i),
          Usage(Today.AddDays(-i).ToString("yyyy-MM-dd"), SourceKind.Gemini, 2 * i + 1, i) }).ToArray();
        var period = HistoryStatistics.Build(usage, Today, HistoryRangeKind.All, source: null);
        var buckets = HistoryStatistics.Buckets(period);
        Assert.Equal(180, buckets.Count);
        for (var metric = 0; metric < 3; metric++)
            Assert.Equal(HistoryStatistics.SourceValue(period.Days, metric), buckets.Sum(bucket => bucket.Sources.Values.Sum(days => HistoryStatistics.SourceValue(days, metric))));
        Assert.Equal(1, HistoryStatistics.Shares(period, 0).Sum(row => row.Fraction), 12);
    }

    [Fact]
    public void UnknownDailySpeedsAndCostsSortAfterKnownValuesInBothDirections()
    {
        var period = HistoryStatistics.Build([Usage("2026-10-25", SourceKind.Codex, 1, null) with { MeasuredMs = 0 },
            Usage("2026-10-26", SourceKind.Codex, 10, 20), Usage("2026-10-27", SourceKind.Codex, 100, 30)], Today, HistoryRangeKind.Days7);
        foreach (var ascending in new[] { true, false })
        {
            Assert.Null(HistoryStatistics.SortDays(period.Days, HistoryDaySort.AverageSpeed, ascending)[^1].AverageSpeed);
            Assert.Null(HistoryStatistics.SortDays(period.Days, HistoryDaySort.Cost, ascending)[^1].Cost.SortValue);
        }
        Assert.Equal(Today, HistoryStatistics.SortDays(period.Days, HistoryDaySort.Output, false)[0].Day);
    }

    [Fact]
    public void ModelCurrencyCsvAndDailyTrafficCsvPreserveQuotedNewlinesAndRawUsd()
    {
        var usage = Usage("2026-10-27", SourceKind.Codex, 10, 1_000_000) with
        { Models = [new("model,\"quoted\"\nnext", 1, 10) { Input = 100, Cost = new CostTally(1_000_000) }] };
        var models = ModelStatistics.Build([usage], Range);
        var modelCsv = ModelStatistics.Csv(models, Range, money: Money());
        Assert.Contains("cost_cny", modelCsv);
        Assert.Contains("\"model,\"\"quoted\"\"\nnext\"", modelCsv);
        Assert.Contains(",1.000000,0,7.2500\r\n", modelCsv);
        var day = HistoryAggregator.Daily([usage], SourceKind.Codex);
        var csv = HistoryExports.Daily(day, SourceKind.Codex, Money(), true);
        Assert.Contains("\"model,\"\"quoted\"\"\nnext\"", csv);
        Assert.Equal(2, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.EndsWith(",not_recorded\r\n", csv);
    }

    private static HistoryMoney Money()
    {
        var currencies = new CurrencyTable([new("USD", 2, "US$", new Dictionary<string, string>()), new("CNY", 2, "¥", new Dictionary<string, string>())],
            new FallbackRates("2026-10-27", "test", new Dictionary<string, decimal> { ["USD"] = 1, ["CNY"] = 7.25m }));
        return new(new CostDisplay(new Localizer(Localizer.LoadEmbeddedTable(), "zh-Hans"), currencies) { Settings = new CostSettings { Currency = "CNY" } });
    }

    private static DailyUsage Usage(string date, SourceKind source, long output, long? microUsd) => new(date, source,
        new(Requests: 1, Input: output * 10, Output: output), output, 1000, 0, 1, null,
        [new("model", 1, output) { Input = output * 10, Cost = microUsd is { } value ? new CostTally(value) : null }])
        { Cost = microUsd is { } usd ? new CostTally(usd) : null };
}
