using System.Text.Json;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Core.Money;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Titles;
using System.Text.Json.Serialization;
using CostConfiguration = AgentSpeed.Presentation.Settings.CostSettings;

namespace AgentSpeed.Linux;

public sealed record UserSettings(string? CodexDirectory = null, int? OrbX = null, int? OrbY = null, bool OrbVisible = true,
    double Redline = 400)
{
    public bool StartAtLogin { get; init; }
    public bool StartInBackground { get; init; } = true;
    public SpeedMetric Metric { get; init; } = SpeedMetric.Output;
    public double RedlineTotal { get; init; } = HeatMath.ReferenceRedlineTotal;
    public double OrbSize { get; init; } = 212;
    public double OrbOpacity { get; init; } = 1;
    public bool OrbClickThrough { get; init; }
    public bool OrbSnapToEdges { get; init; } = true;
    public Dictionary<string, OrbPlacement>? OrbPlacements { get; init; }
    public bool EnablePriceUpdates { get; init; }
    public string Currency { get; init; } = "USD";
    [JsonConverter(typeof(LinuxCostSettingsConverter))]
    public CostConfiguration CostSettings { get; init; } = CostConfiguration.Default;
    public bool TrayShowSpeed { get; init; } = true;
    public AppearanceMode Appearance { get; init; } = AppearanceMode.Dark;
    public EffectsIntensity Effects { get; init; } = EffectsIntensity.Standard;
    public bool ReduceMotion { get; init; }
    public SourceKind[]? EnabledSources { get; init; }
    public Dictionary<SourceKind, string>? SourceDirectories { get; init; }
    public bool TrafficEnabled { get; init; }
    public bool TrafficIncludeTools { get; init; } = true;
    public TrafficUnit TrafficUnit { get; init; } = TrafficUnit.Bytes;
    public BarTraffic TrafficBar { get; init; } = BarTraffic.Off;
    public bool AutoCheckUpdates { get; init; }
    public string UpdateFeedUrl { get; init; } = "";
    public List<LinuxOrbDefinition>? Orbs { get; init; }
    [JsonConverter(typeof(LinuxTitleThemesConverter))]
    public TitleThemesDocument TitleThemes { get; init; } = TitleThemesDocument.Default;

    public UserSettings Validated() => this with
    {
        Redline = HeatMath.ClampRedline(Redline),
        RedlineTotal = HeatMath.ClampRedlineTotal(RedlineTotal),
        Metric = Enum.IsDefined(Metric) ? Metric : SpeedMetric.Output,
        OrbSize = double.IsFinite(OrbSize) ? Math.Clamp(OrbSize, 96, 300) : 212,
        OrbOpacity = double.IsFinite(OrbOpacity) ? Math.Clamp(OrbOpacity, .4, 1) : 1,
        Currency = CurrencyTable.Bundled.CurrencyOrUsd(Currency?.Trim().ToUpperInvariant() ?? "USD").Code,
        CostSettings = ((CostSettings ?? CostConfiguration.Default) with { Currency = Currency ?? "USD", SyncPrices = EnablePriceUpdates }).Validated(),
        TrafficUnit = Enum.IsDefined(TrafficUnit) ? TrafficUnit : TrafficUnit.Bytes,
        TrafficBar = Enum.IsDefined(TrafficBar) ? TrafficBar : BarTraffic.Off,
        Appearance = Enum.IsDefined(Appearance) ? Appearance : AppearanceMode.Dark,
        Effects = Enum.IsDefined(Effects) ? Effects : EffectsIntensity.Standard,
        EnabledSources = EnabledSources?.Where(kind => Enum.IsDefined(kind)).Distinct().ToArray(),
        SourceDirectories = SourceDirectories?.Where(pair => Enum.IsDefined(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value)
            && Path.IsPathFullyQualified(pair.Value)).ToDictionary(pair => pair.Key, pair => pair.Value),
        TitleThemes = (TitleThemes ?? TitleThemesDocument.Default).Validated(),
        Orbs = Orbs is null ? null : LinuxOrbDefinition.ValidateList(Orbs),
        UpdateFeedUrl = Uri.TryCreate(UpdateFeedUrl?.Trim(), UriKind.Absolute, out var feed) && feed.Scheme == Uri.UriSchemeHttps
            && feed.UserInfo.Length == 0 && feed.Fragment.Length == 0 && feed.AbsoluteUri.Length <= 2048 ? feed.AbsoluteUri : "",
    };
}

public sealed class AppStorage
{
    public AppStorage(string? dataDirectory = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        static string Xdg(string key, string fallback) =>
            Environment.GetEnvironmentVariable(key) is { Length: > 0 } value && Path.IsPathFullyQualified(value) ? value : fallback;
        DataDirectory = dataDirectory ?? Path.Combine(Xdg("XDG_DATA_HOME", Path.Combine(home, ".local", "share")), "agent-speed");
        SettingsPath = dataDirectory is null
            ? Path.Combine(Xdg("XDG_CONFIG_HOME", Path.Combine(home, ".config")), "agent-speed", "settings.json")
            : Path.Combine(dataDirectory, "settings.json");
    }

    public string DataDirectory { get; }
    public string SettingsPath { get; }
    public string HistoryPath => Path.Combine(DataDirectory, "history.sqlite");
    public string? SettingsWarning { get; private set; }

    public UserSettings Load()
    {
        if (!File.Exists(SettingsPath)) return new();
        try
        {
            if (new FileInfo(SettingsPath).Length > 512 * 1024) throw new JsonException("设置文件过大。");
            return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(SettingsPath)) ?? new();
        }
        catch (JsonException)
        {
            var backup = SettingsPath + ".corrupt-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            File.Copy(SettingsPath, backup);
            SettingsWarning = "设置文件损坏，已保留备份并使用默认值。";
            return new();
        }
    }

    public void Save(UserSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, SettingsPath, overwrite: true);
    }
}
