using System.Globalization;
using System.Text;
using AgentSpeed.Core;
using AgentSpeed.Presentation.History;

namespace AgentSpeed.Linux;

public enum ModelStatisticSort { Output, Input, CachedInput, Requests, AverageSpeed, Cost, Name }

/// <summary>Model counters retain their source; missing pre-upgrade input stays unknown across a mixed period.</summary>
public sealed record ModelStatistic(string Model, long Requests, long? Input, long? CachedInput, long Output,
    long MeasuredOutput, long MeasuredMs, HistoryCost Cost)
{
    public SourceKind? Source { get; init; }
    public double? AverageSpeed => MeasuredMs > 0 ? MeasuredOutput / (MeasuredMs / 1000d) : null;
}

public static class ModelStatistics
{
    public static IReadOnlyList<ModelStatistic> Build(IEnumerable<DailyUsage> usage, DayRange range, SourceKind? source = SourceKind.Codex)
    {
        var models = new Dictionary<(SourceKind Source, string Model), ModelStatistic>();
        foreach (var day in usage)
        {
            if (source is { } only && day.Source != only || !HistoryRanges.TryParseDay(day.Day, out var date) || !range.Contains(date)) continue;
            foreach (var model in day.Models)
            {
                var name = model.Model ?? string.Empty;
                var part = new ModelStatistic(name, Math.Max(0, model.Requests), Nonnegative(model.Input), Nonnegative(model.CachedInput),
                    Math.Max(0, model.Output), Math.Max(0, model.MeasuredOutput), Math.Max(0, model.MeasuredMs),
                    new HistoryCost(model.Cost, Math.Max(0, model.Requests))) { Source = day.Source };
                var key = (day.Source, name);
                if (!models.TryGetValue(key, out var sum)) models[key] = part;
                else models[key] = sum with
                {
                    Requests = sum.Requests + part.Requests,
                    Input = sum.Input + part.Input,
                    CachedInput = sum.CachedInput + part.CachedInput,
                    Output = sum.Output + part.Output,
                    MeasuredOutput = sum.MeasuredOutput + part.MeasuredOutput,
                    MeasuredMs = sum.MeasuredMs + part.MeasuredMs,
                    Cost = sum.Cost.Add(part.Cost),
                };
            }
        }
        return FilterAndSort(models.Values);
    }

    /// <summary>Unknown numbers sort last in either direction. Name ties keep a stable ordinal order.</summary>
    public static IReadOnlyList<ModelStatistic> FilterAndSort(IEnumerable<ModelStatistic> models, string? filter = null,
        ModelStatisticSort sort = ModelStatisticSort.Output, bool ascending = false)
    {
        var query = (filter ?? string.Empty).Trim();
        var rows = models.Where(model => query.Length == 0 || model.Model.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (sort == ModelStatisticSort.Name)
            return ascending ? rows.OrderBy(row => row.Model, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Model, StringComparer.Ordinal).ToArray()
                : rows.OrderByDescending(row => row.Model, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Model, StringComparer.Ordinal).ToArray();
        double? Value(ModelStatistic row) => sort switch
        {
            ModelStatisticSort.Input => row.Input,
            ModelStatisticSort.CachedInput => row.CachedInput,
            ModelStatisticSort.Requests => row.Requests,
            ModelStatisticSort.AverageSpeed => row.AverageSpeed,
            ModelStatisticSort.Cost => row.Cost.SortValue,
            _ => row.Output,
        };
        var knownFirst = rows.OrderBy(row => Value(row) is null);
        return (ascending ? knownFirst.ThenBy(Value) : knownFirst.ThenByDescending(Value))
            .ThenBy(row => row.Model, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Exports only model names and usage aggregates. Missing input/cost/speed stays empty; partial costs carry unpriced request counts.</summary>
    public static string Csv(IEnumerable<ModelStatistic> models, DayRange range, SourceKind? source = SourceKind.Codex, HistoryMoney? money = null)
    {
        var builder = new StringBuilder("range_start,range_end,source,model,input_tokens,cached_input_tokens,output_tokens,requests,measured_output_tokens,measured_ms,output_avg_speed_tok_s,cost_usd,unpriced_requests");
        if (money?.CsvColumn is { } column) builder.Append(',').Append(column);
        builder.Append("\r\n");
        foreach (var row in models)
        {
            string[] fields = [HistoryRanges.FormatDay(range.Start), HistoryRanges.FormatDay(range.End), HistoryCsv.SourceId(row.Source ?? source), row.Model,
                Integer(row.Input), Integer(row.CachedInput), Integer(row.Output), Integer(row.Requests), Integer(row.MeasuredOutput), Integer(row.MeasuredMs),
                row.AverageSpeed?.ToString("0.######", CultureInfo.InvariantCulture) ?? string.Empty,
                row.Cost.Tally is { } cost ? (cost.CostMicroUsd / 1_000_000m).ToString("0.000000", CultureInfo.InvariantCulture) : string.Empty,
                row.Cost.Tally is { } tally ? Integer(tally.UnpricedRequests) : string.Empty];
            builder.AppendJoin(',', fields.Select(Field));
            if (money?.CsvColumn is not null) builder.Append(',').Append(row.Cost.Tally is { } local ? money.CsvAmount(local.CostMicroUsd) : string.Empty);
            builder.Append("\r\n");
        }
        return builder.ToString();
    }

    public static string FileName(DayRange range, SourceKind? source = SourceKind.Codex) =>
        $"agent-speed-models-{HistoryCsv.SourceId(source)}-{HistoryRanges.FormatDay(range.Start)}-{HistoryRanges.FormatDay(range.End)}.csv";

    private static long? Nonnegative(long? value) => value is { } known ? Math.Max(0, known) : null;
    private static string Integer(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    private static string Field(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"' : value;
    }
}
