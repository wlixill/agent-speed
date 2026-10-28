using System.Text.Json;
using System.Text.Json.Serialization;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Settings.Orbs;
using AgentSpeed.Presentation.Titles;
using AgentSpeed.Presentation.Heat;

namespace AgentSpeed.Linux;

public sealed record LinuxOrbDefinition(string Id)
{
    public const int MaximumCount = 8;
    public string? Name { get; init; }
    public SourceKind[]? Sources { get; init; }
    public OrbStyle Style { get; init; } = OrbStyle.Led;
    public string TitleTheme { get; init; } = "general";
    public double Size { get; init; } = 212;
    public double Opacity { get; init; } = 1;
    public bool Visible { get; init; } = true;
    public bool ClickThrough { get; init; }
    public bool SnapToEdges { get; init; } = true;
    public bool SnapToOrbs { get; init; } = true;
    public bool AlwaysOnTop { get; init; } = true;
    public bool FadeWhenIdle { get; init; }
    public bool ShowName { get; init; } = true;
    public double? RedlineOutput { get; init; }
    public double? RedlineTotal { get; init; }
    public OrbUnit Unit { get; init; } = OrbUnit.Tokens;
    public OrbTraffic Traffic { get; init; } = OrbTraffic.Sources;
    public SpeedMetric? Metric { get; init; }
    public int? OrbX { get; init; }
    public int? OrbY { get; init; }
    public Dictionary<string, OrbPlacement>? Placements { get; init; }
    [JsonIgnore] public SourceSet? SourceSet => Sources is null ? null : Core.SourceSet.From(Sources);
    [JsonIgnore] public string DisplayName => Name ?? (Sources is null ? "全部来源" : Sources.Length == 0 ? "未选择来源" : string.Join(" + ", Sources.Select(LinuxSources.Name)));

    public LinuxOrbDefinition Validated() => this with
    {
        Id = OrbDefinition.CleanId(Id) ?? Guid.NewGuid().ToString("N"), Name = OrbDefinition.CleanName(Name),
        Sources = Sources?.Where(Enum.IsDefined).Distinct().Order().ToArray(),
        Style = Enum.IsDefined(Style) ? Style : OrbStyle.Led,
        TitleTheme = OrbDefinition.CleanId(TitleTheme) ?? "general",
        Size = double.IsFinite(Size) ? Math.Clamp(Size, 96, 300) : 212,
        Opacity = double.IsFinite(Opacity) ? Math.Clamp(Opacity, .4, 1) : 1,
        Metric = Metric is { } metric && Enum.IsDefined(metric) ? metric : null,
        RedlineOutput = RedlineOutput is { } output && double.IsFinite(output) ? HeatMath.ClampRedline(output) : null,
        RedlineTotal = RedlineTotal is { } total && double.IsFinite(total) ? HeatMath.ClampRedlineTotal(total) : null,
        Unit = Enum.IsDefined(Unit) ? Unit : OrbUnit.Tokens,
        Traffic = Enum.IsDefined(Traffic) ? Traffic : OrbTraffic.Off,
        Placements = Placements?.Where(pair => pair.Key.Length <= 1024 && pair.Value is { } value
            && double.IsFinite(value.X) && double.IsFinite(value.Y)).Take(16).ToDictionary(pair => pair.Key, pair => pair.Value),
    };

    public double EffectiveRedline(SpeedMetric metric, UserSettings settings) => metric == SpeedMetric.Total
        ? RedlineTotal ?? settings.RedlineTotal : RedlineOutput ?? settings.Redline;

    public static List<LinuxOrbDefinition> ValidateList(IEnumerable<LinuxOrbDefinition>? definitions)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<LinuxOrbDefinition>();
        foreach (var definition in definitions?.Take(MaximumCount) ?? [])
        {
            if (definition is null) continue;
            var clean = definition.Validated();
            if (!ids.Add(clean.Id)) { clean = clean with { Id = Guid.NewGuid().ToString("N") }; ids.Add(clean.Id); }
            result.Add(clean);
        }
        return result;
    }

    public static LinuxOrbDefinition FromLegacy(UserSettings settings) => new("primary")
    {
        Size = settings.OrbSize, Opacity = settings.OrbOpacity, Visible = settings.OrbVisible,
        ClickThrough = settings.OrbClickThrough, SnapToEdges = settings.OrbSnapToEdges,
        OrbX = settings.OrbX, OrbY = settings.OrbY, Placements = settings.OrbPlacements,
    };
}

public sealed class LinuxTitleThemesConverter : JsonConverter<TitleThemesDocument>
{
    public override TitleThemesDocument Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    { using var value = JsonDocument.ParseValue(ref reader); return TitleThemesJson.Read(value.RootElement) ?? TitleThemesDocument.Default; }
    public override void Write(Utf8JsonWriter writer, TitleThemesDocument value, JsonSerializerOptions options) => TitleThemesJson.Write(writer, value);
}
