using System.Globalization;
using AgentSpeed.Core;
using AgentSpeed.Core.Traffic;
using AgentSpeed.Presentation.History;

namespace AgentSpeed.Linux;

public sealed record HistoryPeriod(DayRange Range, IReadOnlyList<DaySummary> Days, HistoryTotals Totals, HistoryTotals Previous)
{
    public IReadOnlyDictionary<SourceKind, IReadOnlyList<DaySummary>> SourceDays { get; init; } = new Dictionary<SourceKind, IReadOnlyList<DaySummary>>();
    public bool TrafficMeasured { get; init; }
}

public enum HistoryDaySort { Date, Output, Input, Requests, AverageSpeed, Peak, Cost, Traffic }
public sealed record HistoryShare(SourceKind Source, string? Model, double Value, double Fraction);

/// <summary>The Linux history view uses the shared weighted speeds, subscription rules, traffic and calendar ranges.</summary>
public static class HistoryStatistics
{
    public static HistoryPeriod Build(IReadOnlyList<DailyUsage> usage, DateOnly today, HistoryRangeKind kind, DayRange? custom = null,
        SourceKind? source = SourceKind.Codex, SourceSet excluded = default, IReadOnlyList<TrafficDay>? trafficDays = null,
        string? trafficSince = null, bool includeTools = true, DateTimeOffset? now = null)
    {
        var bounds = HistoryAggregator.Bounds(usage, source);
        var trafficRows = (trafficDays ?? []).Where(row => row.SampledMs > 0 && (source is null || row.Source == source)
            && HistoryRanges.TryParseDay(row.Day, out _)).ToArray();
        var firstTraffic = trafficRows.Select(row => DateOnly.ParseExact(row.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture)).DefaultIfEmpty(DateOnly.MaxValue).Min();
        var first = bounds?.Start;
        if (firstTraffic != DateOnly.MaxValue && (first is null || firstTraffic < first)) first = firstTraffic;
        var range = HistoryRanges.Resolve(kind, today, first, custom);
        var traffic = new HistoryTraffic(trafficRows, trafficSince, includeTools);
        var all = WithTraffic(HistoryAggregator.Daily(usage, source, excluded: excluded), trafficRows, traffic, source, today, now ?? DateTimeOffset.Now);
        var days = all.Where(day => range.Contains(day.Day)).ToArray();
        var previous = range.Start.DayNumber >= range.DayCount
            ? HistoryAggregator.Totals(all.Where(day => HistoryRanges.Previous(range).Contains(day.Day))) : HistoryTotals.Empty;
        var sources = usage.Where(day => source is null || day.Source == source).Select(day => day.Source)
            .Concat(trafficRows.Where(row => row.Source is not null).Select(row => row.Source!.Value)).Distinct().Order().ToArray();
        return new(range, days, HistoryAggregator.Totals(days), previous)
        {
            TrafficMeasured = traffic.IsMeasured,
            SourceDays = sources.ToDictionary(key => key, key => (IReadOnlyList<DaySummary>)WithTraffic(
                HistoryAggregator.Daily(usage, key, range, excluded), trafficRows.Where(row => row.Source == key).ToArray(),
                traffic, key, today, now ?? DateTimeOffset.Now).Where(day => range.Contains(day.Day)).ToArray()),
        };
    }

    private static IReadOnlyList<DaySummary> WithTraffic(IReadOnlyList<DaySummary> days, IReadOnlyList<TrafficDay> rows,
        HistoryTraffic traffic, SourceKind? source, DateOnly today, DateTimeOffset now)
    {
        var result = days.ToDictionary(day => day.Day);
        foreach (var group in rows.GroupBy(row => DateOnly.ParseExact(row.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture)))
        {
            if (!result.TryGetValue(group.Key, out var day)) day = DaySummary.TrafficOnly(group.Key);
            var observed = traffic.Day(group.Key, source, today, now);
            // A stored observation is required. Missing sample days and absent machine rows remain unknown.
            if (observed is not null)
                result[group.Key] = day with { Traffic = observed with { Machine = source is null && group.Any(row => row.IsMachine) ? observed.Machine : null } };
        }
        return result.Values.OrderBy(day => day.Day).ToArray();
    }

    public static string Money(HistoryCost cost, bool hasData = true)
    {
        if (!hasData || cost.Kind is HistoryCostKind.Unknown or HistoryCostKind.Unavailable) return "—";
        if (cost.Kind == HistoryCostKind.Subscription) return "订阅（已排除）";
        var value = cost.MicroUsd / 1_000_000m;
        return (cost.IsPartial ? "≥US$" : "≈US$") + value.ToString(value > 0m && value < .01m ? "0.######" : value < 1m ? "0.0000" : "N2", CultureInfo.InvariantCulture);
    }

    public static string Trend(double? current, double? previous)
    {
        var change = HistoryAggregator.Change(current, previous);
        return change is { } value ? $"较前期 {(value >= 0 ? "+" : "")}{value:P0}" : "暂无可比记录";
    }

    public static IReadOnlyList<DaySummary> SortDays(IEnumerable<DaySummary> days, HistoryDaySort sort, bool ascending)
    {
        if (sort == HistoryDaySort.Date) return (ascending ? days.OrderBy(row => row.Day) : days.OrderByDescending(row => row.Day)).ToArray();
        double? Value(DaySummary day) => sort switch
        {
            HistoryDaySort.Input => day.Input, HistoryDaySort.Requests => day.Requests,
            HistoryDaySort.AverageSpeed => day.AverageSpeed, HistoryDaySort.Peak => day.Peak,
            HistoryDaySort.Cost => day.Cost.SortValue, HistoryDaySort.Traffic => day.Traffic?.Shown.Sum, _ => day.Output,
        };
        var known = days.OrderBy(day => Value(day) is null);
        return (ascending ? known.ThenBy(Value) : known.ThenByDescending(Value)).ThenByDescending(row => row.Day).ToArray();
    }

    public static IReadOnlyList<HistoryShare> Shares(HistoryPeriod period, int metric)
    {
        var rows = period.SourceDays.Select(pair => new HistoryShare(pair.Key, null, SourceValue(pair.Value, metric), 0)).Where(row => row.Value > 0).ToArray();
        var total = rows.Sum(row => row.Value);
        return rows.OrderByDescending(row => row.Value).Select(row => row with { Fraction = row.Value / total }).ToArray();
    }

    public static IReadOnlyList<HistoryShare> ModelShares(IEnumerable<ModelStatistic> models, int metric)
    {
        var rows = models.Select(row => new HistoryShare(row.Source ?? SourceKind.Codex, row.Model, metric switch
        { 1 => row.Input ?? 0, 2 => row.Requests, 3 => row.Cost.Kind == HistoryCostKind.Priced ? row.Cost.MicroUsd / 1_000_000d : 0, _ => row.Output }, 0)).Where(row => row.Value > 0).ToArray();
        var total = rows.Sum(row => row.Value);
        return rows.OrderByDescending(row => row.Value).Select(row => row with { Fraction = row.Value / total }).ToArray();
    }

    public static double SourceValue(IEnumerable<DaySummary> days, int metric) => metric switch
    {
        1 => days.Sum(day => (double)day.Input), 2 => days.Sum(day => (double)day.Requests),
        3 => days.Sum(day => day.Cost.Kind == HistoryCostKind.Priced ? day.Cost.MicroUsd / 1_000_000d : 0),
        4 => days.Sum(day => (double)(day.Traffic?.Shown.Sum ?? 0)), _ => days.Sum(day => (double)day.Output),
    };

    /// <summary>Bound chart work while retaining each source's contribution and unknown sample gaps.</summary>
    public static IReadOnlyList<HistoryChartBucket> Buckets(HistoryPeriod period, int maximum = 180)
    {
        var count = Math.Clamp(period.Range.DayCount, 1, Math.Max(1, maximum));
        var result = new List<HistoryChartBucket>(count);
        for (var i = 0; i < count; i++)
        {
            var start = period.Range.Start.AddDays(i * period.Range.DayCount / count);
            var end = period.Range.Start.AddDays((i + 1) * period.Range.DayCount / count - 1);
            var days = period.Days.Where(day => day.Day >= start && day.Day <= end).ToArray();
            result.Add(new(start, end, HistoryAggregator.Totals(days))
            {
                Sources = period.SourceDays.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<DaySummary>)pair.Value.Where(day => day.Day >= start && day.Day <= end).ToArray()),
                Traffic = CombineTraffic(days.Select(day => day.Traffic).ToArray()),
            });
        }
        return result;
    }

    private static DayTraffic? CombineTraffic(IReadOnlyList<DayTraffic?> rows)
    {
        var observed = rows.OfType<DayTraffic>().ToArray();
        if (observed.Length == 0) return null;
        var bySource = observed.SelectMany(row => row.BySource).GroupBy(row => row.Key).ToDictionary(group => group.Key,
            group => group.Aggregate(TrafficBytes.Zero, (sum, row) => sum + row.Value));
        return new(observed.Aggregate(TrafficBytes.Zero, (sum, row) => sum + row.Own),
            observed.Aggregate(TrafficBytes.Zero, (sum, row) => sum + row.Tools), bySource,
            observed.Any(row => row.Machine is not null) ? observed.Aggregate(TrafficBytes.Zero, (sum, row) => sum + (row.Machine ?? TrafficBytes.Zero)) : null,
            observed.Sum(row => row.RecordedMs), rows.Count != observed.Length || observed.Any(row => row.Partial), observed[0].IncludeTools);
    }
}

public sealed record HistoryChartBucket(DateOnly Start, DateOnly End, HistoryTotals Totals)
{
    public IReadOnlyDictionary<SourceKind, IReadOnlyList<DaySummary>> Sources { get; init; } = new Dictionary<SourceKind, IReadOnlyList<DaySummary>>();
    public DayTraffic? Traffic { get; init; }
    public double? Value(int metric) => metric == 4 ? Traffic?.Shown.Sum : !Totals.HasData ? null : metric switch
    {
        1 => Totals.Input, 2 => Totals.Requests,
        3 => Totals.Cost.Kind == HistoryCostKind.Priced ? Totals.Cost.MicroUsd / 1_000_000d : null, _ => Totals.Output,
    };
}
