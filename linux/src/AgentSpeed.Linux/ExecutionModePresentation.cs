using AgentSpeed.Core;
using AgentSpeed.Presentation.Formatting;
using AgentSpeed.Presentation.History;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Linux;

/// <summary>Execution-mode evidence is independent of model names, speed and configuration defaults.</summary>
public static class ExecutionModePresentation
{
    private static readonly Localizer Chinese = new(Localizer.LoadEmbeddedTable(), "zh-Hans");
    public static IReadOnlyList<ExecutionModeKind> Kinds { get; } = Enum.GetValues<ExecutionModeKind>();
    public const string Unknown = "未知（日志未提供）";

    public static string Kind(ExecutionModeKind kind) => Chinese["mode.kind." + ExecutionModes.Name(kind)];
    public static string Value(string value) => value == "unknown" ? "未知" : ExecutionModeText.Value(value, Chinese);
    public static string Evidence(string evidence) => evidence == "unknown" ? "未知（未提供）" : Chinese["mode.evidence." + evidence];

    public static string Agent(AgentStatus agent, bool includeAllSources = false) => !includeAllSources && agent.Source != SourceKind.Codex ? "—" : Summary(Actual(agent.Modes));

    public static string AgentDetails(AgentStatus agent, bool includeAllSources = false)
    {
        if (!includeAllSources && agent.Source != SourceKind.Codex) return "—";
        var text = ExecutionModeText.Detail(agent.Modes, Chinese);
        if (Actual(agent.Modes).IsEmpty) text = Unknown + Environment.NewLine + text;
        if (agent.Modes.Facts.Any(fact => fact.Evidence == ExecutionModeEvidence.Configured)
            && agent.Modes.Facts.Any(fact => fact.Evidence != ExecutionModeEvidence.Configured))
            text += Environment.NewLine + Chinese["mode.defaultNote"];
        return text;
    }

    public static string Current(SpeedSnapshot snapshot, SourceKind? source = SourceKind.Codex)
    {
        var modes = ActiveAgents(snapshot, source).Select(agent => Actual(agent.Modes)).ToArray();
        return "执行模式：" + (modes.Length == 0 ? Unknown
            : modes.All(mode => mode.IsEmpty) ? Unknown : ExecutionModeText.Aggregate(modes, Chinese));
    }

    public static string CurrentDetails(SpeedSnapshot snapshot)
    {
        var details = ActiveAgents(snapshot, SourceKind.Codex).Select(agent => AgentDetails(agent)).Distinct(StringComparer.Ordinal).ToArray();
        return (details.Length == 0 ? Unknown : string.Join(Environment.NewLine + Environment.NewLine, details))
            + Environment.NewLine + "任务选择、Agent 记录和实际响应分别标明证据；速度快慢不能证明 Fast。";
    }

    public static string ConfiguredSource(SpeedSnapshot snapshot, SourceKind? source = SourceKind.Codex)
    {
        var configured = Configured(snapshot, source);
        return "配置默认：" + (configured.IsEmpty ? "未提供" : ExecutionModeText.Summary(configured, Chinese));
    }

    public static string ConfiguredDetails(SpeedSnapshot snapshot, SourceKind? source = SourceKind.Codex)
    {
        var configured = Configured(snapshot, source);
        return configured.IsEmpty ? "未读取到配置默认值；不据此推断实际执行模式。"
            : ExecutionModeText.Detail(configured, Chinese);
    }

    /// <summary>
    /// Builds Codex-only days in the chosen range. Older aggregates retain their real counters under unknown modes;
    /// configured defaults never enter response attribution or history buckets.
    /// </summary>
    public static IReadOnlyList<DaySummary> HistoryDays(IEnumerable<DailyUsage> usage, DayRange range, SourceKind? source = SourceKind.Codex) =>
        HistoryAggregator.Daily(usage.Where(day => (source is null || day.Source == source)
                && HistoryRanges.TryParseDay(day.Day, out var date) && range.Contains(date))
            .Select(day => day with { Modes = CompleteUnknown(day) }), source, range);

    /// <summary>One selected dimension at a time: its requests/output are not summed with other dimensions.</summary>
    public static IReadOnlyList<ExecutionModeUsage> Rows(IEnumerable<DaySummary> days, ExecutionModeKind kind, SourceKind? source = SourceKind.Codex) =>
        ExecutionModeUsage.Combine(days.SelectMany(day => day.Modes)
            .Where(row => (source is null || row.Source == source) && row.Kind == kind));

    public static string Csv(IEnumerable<DaySummary> days, ExecutionModeKind? kind = null, SourceKind? source = SourceKind.Codex) =>
        ExecutionModeCsv.Build(days.Select(day => day with
        {
            Modes = day.Modes.Where(row => (source is null || row.Source == source) && (kind is null || row.Kind == kind)).ToArray(),
        }));

    private static IEnumerable<AgentStatus> ActiveAgents(SpeedSnapshot snapshot, SourceKind? source) =>
        snapshot.Agents.Where(agent => (source is null || agent.Source == source) && agent.Phase != AgentPhase.Idle);
    private static ExecutionModes Actual(ExecutionModes modes) => modes.Without(ExecutionModeEvidence.Configured);
    private static string Summary(ExecutionModes modes) => modes.IsEmpty ? Unknown : ExecutionModeText.Summary(modes, Chinese);
    private static ExecutionModes Configured(SpeedSnapshot snapshot, SourceKind? kind) => new(snapshot.Sources
        .Where(source => (kind is null || source.Kind == kind) && source.Enabled)
        .SelectMany(source => source.ConfiguredModes.Facts).Where(fact => fact.Evidence == ExecutionModeEvidence.Configured));

    private static IReadOnlyList<ExecutionModeUsage> CompleteUnknown(DailyUsage day)
    {
        var rows = day.Modes.Where(row => row.Source == day.Source
            && ((row.Evidence is "selected" or "recorded" or "served") && row.Mode is not ("unknown" or "none" or "auto" or "inherit" or "inherited")
                || row.Evidence == "unknown" && row.Mode == "unknown")).ToList();
        var models = day.Models.GroupBy(model => model.Model, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => new ModelUsage(group.Key, group.Sum(model => model.Requests), group.Sum(model => model.Output),
                group.Sum(model => model.MeasuredOutput), group.Sum(model => model.MeasuredMs)), StringComparer.Ordinal);
        var rest = new ModelUsage(string.Empty, Math.Max(0, day.Totals.Requests - models.Values.Sum(model => model.Requests)),
            Math.Max(0, day.Totals.Output - models.Values.Sum(model => model.Output)),
            Math.Max(0, day.MeasuredOutput - models.Values.Sum(model => model.MeasuredOutput)),
            Math.Max(0, day.MeasuredMs - models.Values.Sum(model => model.MeasuredMs)));
        if (HasUsage(rest))
        {
            var previous = models.GetValueOrDefault(string.Empty);
            models[string.Empty] = previous is null ? rest : rest with
            {
                Requests = rest.Requests + previous.Requests, Output = rest.Output + previous.Output,
                MeasuredOutput = rest.MeasuredOutput + previous.MeasuredOutput, MeasuredMs = rest.MeasuredMs + previous.MeasuredMs,
            };
        }
        rows.RemoveAll(row => !models.ContainsKey(row.Model));
        foreach (var model in models.Values)
        foreach (var kind in Kinds)
        {
            var known = rows.Where(row => row.Model == model.Model && row.Kind == kind).ToArray();
            var missing = new ModelUsage(model.Model, model.Requests - known.Sum(row => row.Requests), model.Output - known.Sum(row => row.Output),
                model.MeasuredOutput - known.Sum(row => row.MeasuredOutput), model.MeasuredMs - known.Sum(row => row.MeasuredMs));
            if (missing.Requests < 0 || missing.Output < 0 || missing.MeasuredOutput < 0 || missing.MeasuredMs < 0)
            {
                rows.RemoveAll(row => row.Model == model.Model && row.Kind == kind);
                missing = model;
            }
            if (HasUsage(missing)) rows.Add(new(model.Model, day.Source, kind, "unknown", "unknown", null,
                missing.Requests, missing.Output, missing.MeasuredOutput, missing.MeasuredMs));
        }
        return ExecutionModeUsage.Combine(rows);
    }

    private static bool HasUsage(ModelUsage model) => model.Requests > 0 || model.Output > 0 || model.MeasuredOutput > 0 || model.MeasuredMs > 0;
}
