using AgentSpeed.Core;

namespace AgentSpeed.Linux;

public static class LinuxSources
{
    public static SourceKind[] Enabled(UserSettings settings) => settings.EnabledSources ?? [SourceKind.Codex];
    public static string Name(SourceKind kind) => kind switch
    {
        SourceKind.Codex => "Codex", SourceKind.ClaudeCode => "Claude Code", SourceKind.ClaudeDesktop => "Claude Desktop",
        SourceKind.DeepSeek => "DSH", SourceKind.OpenCode => "OpenCode", SourceKind.Gemini => "Gemini CLI", SourceKind.Omp => "omp",
        SourceKind.Antigravity => "Antigravity", SourceKind.WorkBuddy => "WorkBuddy", SourceKind.WorkBuddyAi => "WorkBuddy AI",
        SourceKind.ZCode => "ZCode", SourceKind.Kimi => "Kimi Code CLI", SourceKind.GrokBuild => "Grok Build", _ => kind.ToString(),
    };
    public static string DefaultRoot(SourceKind kind) => kind switch
    {
        SourceKind.Codex => LiveMonitor.DefaultCodexRoot,
        SourceKind.ClaudeCode or SourceKind.ClaudeDesktop => LiveMonitor.DefaultClaudeRoot,
        SourceKind.DeepSeek => LiveMonitor.DefaultDshRoot, SourceKind.OpenCode => LiveMonitor.DefaultOpenCodeRoot,
        SourceKind.Gemini => LiveMonitor.DefaultGeminiRoot, SourceKind.Omp => LiveMonitor.DefaultOmpRoot,
        SourceKind.Antigravity => LiveMonitor.DefaultAntigravityStores.FirstOrDefault() ?? "",
        SourceKind.WorkBuddy => LiveMonitor.DefaultWorkBuddyFolder, SourceKind.WorkBuddyAi => LiveMonitor.DefaultWorkBuddyAiFolder,
        SourceKind.ZCode => LiveMonitor.DefaultZCodeRoot, SourceKind.Kimi => LiveMonitor.DefaultKimiRoot,
        SourceKind.GrokBuild => LiveMonitor.DefaultGrokBuildRoot, _ => "",
    };
    public static string? Override(UserSettings settings, SourceKind kind) => settings.SourceDirectories?.GetValueOrDefault(kind);
    public static string Root(UserSettings settings, SourceKind kind) => Override(settings, kind)
        ?? (kind == SourceKind.Codex ? settings.CodexDirectory : null) ?? DefaultRoot(kind);
    public static MonitorConfiguration Configuration(UserSettings settings, string codexDirectory) => new(
        Enabled(settings).ToHashSet(), CodexRoot: codexDirectory,
        ClaudeRoot: Enabled(settings).Contains(SourceKind.ClaudeDesktop) && !Enabled(settings).Contains(SourceKind.ClaudeCode)
            ? Override(settings, SourceKind.ClaudeDesktop) ?? Override(settings, SourceKind.ClaudeCode)
            : Override(settings, SourceKind.ClaudeCode) ?? Override(settings, SourceKind.ClaudeDesktop),
        DshRoot: Override(settings, SourceKind.DeepSeek), OpenCodeRoot: Override(settings, SourceKind.OpenCode),
        GeminiRoot: Override(settings, SourceKind.Gemini), OmpRoot: Override(settings, SourceKind.Omp),
        AntigravityRoot: Override(settings, SourceKind.Antigravity), WorkBuddyRoot: Override(settings, SourceKind.WorkBuddy),
        WorkBuddyAiRoot: Override(settings, SourceKind.WorkBuddyAi), ZCodeRoot: Override(settings, SourceKind.ZCode),
        KimiRoot: Override(settings, SourceKind.Kimi), GrokBuildRoot: Override(settings, SourceKind.GrokBuild));

    public static string Normalize(string value)
    {
        value = value.Trim();
        if (value.Any(char.IsControl)) throw new ArgumentException("目录不能包含控制字符。");
        if (value.StartsWith("~/", StringComparison.Ordinal)) value = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value[2..]);
        if (!Path.IsPathFullyQualified(value)) throw new ArgumentException("请输入完整路径或 ~/ 开头的路径。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    public static SpeedSnapshot Subset(SpeedSnapshot snapshot, SourceSet? sources)
    {
        if (sources is not { } set) return snapshot;
        var reading = OrbSubset.Reading(snapshot, set);
        var statuses = snapshot.Sources.Where(source => set.Contains(source.Kind)).ToArray();
        var cost = statuses.Where(source => !OrbSubset.IsUnavailable(source)).Aggregate(LiveCost.Zero, (sum, source) => sum + source.LiveCost);
        var todayCost = statuses.Where(source => !OrbSubset.IsUnavailable(source)).Aggregate(CostTally.Zero, (sum, source) => sum + source.TodayCost);
        return snapshot with
        {
            Live = reading.Live, LiveEstimatedShare = reading.EstimatedShare, Agents = reading.Agents,
            Sources = statuses, Today = reading.Today, LiveCost = cost, TodayCost = todayCost,
            GeneratingCount = reading.Agents.Count(agent => agent.Phase == AgentPhase.Generating),
            ToolsCount = reading.Agents.Count(agent => agent.Phase == AgentPhase.Tools),
            State = snapshot.State.Kind == MonitorStateKind.Starting ? snapshot.State
                : reading.State == SubsetState.Available ? MonitorState.Running : MonitorState.NoSources,
            PeakToday = new(reading.MeasuredPeakOutput ?? 0, 0), AverageToday = null, History = [],
            LastResponse = snapshot.LastResponse is { } last && set.Contains(last.Source) ? last : null,
        };
    }
}
