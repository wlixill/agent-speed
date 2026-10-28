using System.Globalization;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.History;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Titles;
using AgentSpeed.Presentation.Cost;
using AgentSpeed.Presentation.Localization;
using Avalonia.Threading;

namespace AgentSpeed.Linux;

internal sealed class UiContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback callback, object? state) => Dispatcher.UIThread.Post(() => callback(state));
}

internal sealed class SpeedController : IDisposable
{
    private readonly LiveMonitor _monitor;
    private readonly HistoryService _history;
    private readonly DispatcherTimer _historyTimer;
    private readonly LoginStartupService _startup;
    public LinuxCostService Cost { get; }
    public CostDisplay Money { get; }
    public LinuxUpdateService Updates { get; }
    public LinuxReportService Reporting { get; }
    public LinuxTrafficService Traffic { get; }
    public IReadOnlyList<LinuxOrbDefinition> OrbDefinitions => Settings.Orbs is { Count: > 0 } definitions
        ? definitions : [LinuxOrbDefinition.FromLegacy(Settings)];
    public event Action? OpenSourcesRequested;
    public event Action? OpenOrbsRequested;
    public event Action? OpenTrafficRequested;
    public event Action? OpenUpdatesRequested;
    public event Action? OpenReportingRequested;
    public long MoneyRevision { get; private set; }
    public bool IsDark { get; private set; } = true;
    public bool EffectiveReduceMotion { get; private set; }
    public string AppearanceStatus { get; private set; } = "正在读取系统外观设置…";
    private SpeedSnapshot? _projectedRaw;
    private SpeedSnapshot? _projected;
    private SpeedMetric _projectedMetric;
    public SpeedHub Speed { get; }
    public HistoryHub History { get; }
    public AppStorage Storage { get; }
    public UserSettings Settings { get; private set; }
    public string CodexDirectory { get; private set; }
    public double Redline => Settings.Metric == SpeedMetric.Total ? Settings.RedlineTotal : Settings.Redline;
    public bool LoginStartupEnabled => _startup.IsEnabled;
    public string LoginStartupPath => _startup.EntryPath;
    public SpeedSnapshot DisplaySnapshot
    {
        get
        {
            var raw = Speed.Snapshot;
            if (!ReferenceEquals(raw, _projectedRaw) || _projectedMetric != Settings.Metric)
            {
                _projectedRaw = raw;
                _projectedMetric = Settings.Metric;
                _projected = MetricPresentation.Project(raw, Settings.Metric);
            }
            return _projected!;
        }
    }
    public event Action? Changed;
    public string? Error { get; private set; }
    public double MaximumObservedSpeed { get; private set; }

    public SpeedController(LaunchOptions options)
    {
        Storage = new(options.DataDirectory);
        Directory.CreateDirectory(Storage.DataDirectory);
        Settings = Storage.Load().Validated();
        _startup = new(options, Storage);
        Settings = Settings with { StartAtLogin = _startup.IsEnabled };
        CodexDirectory = options.CodexDirectory ?? Settings.CodexDirectory ?? LinuxSources.Root(Settings, SourceKind.Codex);
        Speed = new(new UiContext());
        History = new(new UiContext());
        Cost = new(Storage, Settings.EnablePriceUpdates, Settings.Currency, version: BuildInformation.Version);
        Money = new(new Localizer(Localizer.LoadEmbeddedTable(), "zh-Hans"), Cost.CurrencyTable, Cost.Rates)
        { Settings = Settings.CostSettings };
        _monitor = new(Speed, Configuration(), pricing: Cost.PriceBook);
        _history = new(History, Configuration(), Storage.HistoryPath, pricing: Cost.PriceBook);
        Updates = new(Storage);
        Updates.SetOptions(Settings.AutoCheckUpdates, Settings.UpdateFeedUrl);
        Reporting = new(Storage);
        Traffic = new(Storage, () => Speed.Snapshot);
        Updates.Changed += () => Dispatcher.UIThread.Post(() => Changed?.Invoke());
        Reporting.Changed += () => Dispatcher.UIThread.Post(() => Changed?.Invoke());
        Traffic.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            ObserveReporting(Speed.Snapshot);
            Changed?.Invoke();
        });
        IsDark = Settings.Appearance != AppearanceMode.Light;
        EffectiveReduceMotion = Settings.ReduceMotion;
        Cost.Changed += () => Dispatcher.UIThread.Post(() => { RefreshMoney(); Changed?.Invoke(); });
        Cost.PricesChanged += () => Dispatcher.UIThread.Post(() => History.Refresh());
        Speed.SnapshotChanged += (_, snapshot) =>
        {
            MaximumObservedSpeed = Math.Max(MaximumObservedSpeed, snapshot.Live.Output);
            _history.ObserveLive(snapshot);
            ObserveReporting(snapshot);
            Changed?.Invoke();
        };
        History.Changed += (_, _) => Changed?.Invoke();
        _historyTimer = new() { Interval = TimeSpan.FromSeconds(30) };
        _historyTimer.Tick += (_, _) =>
        {
            if (History.State.Kind != HistoryStateKind.Analyzing) History.Refresh();
        };
        Error = Storage.SettingsWarning ?? _startup.StatusWarning;
    }

    private MonitorConfiguration Configuration() => LinuxSources.Configuration(Settings, CodexDirectory);
    private void ObserveReporting(SpeedSnapshot snapshot)
    {
        Reporting.Metric = Settings.Metric;
        Reporting.Redline = Redline;
        Reporting.PriceVersion = snapshot.Timestamp > DateTimeOffset.UnixEpoch
            ? Cost.PriceBook.ReportPriceVersion(snapshot.Timestamp.ToUnixTimeMilliseconds()) : Cost.PriceBook.CurrentVersion;
        Reporting.Observe(snapshot, Traffic.ReportSnapshot);
    }
    public SpeedSnapshot GetSnapshotForSources(SourceSet? sources) => LinuxSources.Subset(Speed.Snapshot, sources);
    private void UpdateSubsets() => _monitor.SetSubsets(OrbDefinitions.Where(orb => orb.SourceSet is not null)
        .Select(orb => orb.SourceSet!.Value).Distinct().ToArray());

    public void Start()
    {
        UpdateSubsets();
        _monitor.Start();
        _history.Start();
        _historyTimer.Start();
        Cost.Start();
        Traffic.SetEnabled(Settings.TrafficEnabled);
        Traffic.Start();
        Reporting.Start();
        Updates.Start();
    }

    public void ChangeDirectory(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("请输入 Codex 的 sessions 目录。");
            if (path.StartsWith("~/", StringComparison.Ordinal))
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
            path = Path.GetFullPath(path.Trim());
            if (!Directory.Exists(path)) throw new ArgumentException("目录不存在，请选择 Codex 的 sessions 目录。");
            var paths = Settings.SourceDirectories is null ? null : new Dictionary<SourceKind, string>(Settings.SourceDirectories)
            { [SourceKind.Codex] = path };
            var settings = Settings with { CodexDirectory = path, SourceDirectories = paths };
            var updateStartup = _startup.IsEnabled;
            if (!TryPersistSettings(settings, updateStartup ? () => _startup.SetEnabled(true, path, settings.StartInBackground) : null))
                return;
            CodexDirectory = path;
            _monitor.Update(Configuration());
            _history.Update(Configuration());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Error = "无法应用目录：" + exception.Message;
        }
        finally { Changed?.Invoke(); }
    }

    public void SaveOrb(int x, int y, bool visible, string? layoutKey = null, OrbPlacement? placement = null)
    {
        try
        {
            var positions = Settings.OrbPlacements;
            if (layoutKey is not null && placement is not null)
            {
                positions = positions is not null ? new(positions) : [];
                positions.Remove(layoutKey);
                while (positions.Count >= 16) positions.Remove(positions.Keys.First());
                positions[layoutKey] = placement;
            }
            var definitions = Settings.Orbs;
            if (definitions is { Count: > 0 }) definitions = [definitions[0] with
            { OrbX = x, OrbY = y, Visible = visible, Placements = positions }, .. definitions.Skip(1)];
            var next = Settings with { OrbX = x, OrbY = y, OrbVisible = visible, OrbPlacements = positions, Orbs = definitions };
            Storage.Save(next);
            Settings = next;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Error = "无法保存悬浮窗位置：" + exception.Message;
        }
    }

    public void SetRedline(double value)
    {
        try
        {
            var settings = Settings.Metric == SpeedMetric.Total
                ? Settings with { RedlineTotal = HeatMath.ClampRedlineTotal(value) }
                : Settings with { Redline = HeatMath.ClampRedline(value) };
            Storage.Save(settings);
            Settings = settings;
            Error = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Error = "无法保存表盘刻度：" + exception.Message;
        }
        Changed?.Invoke();
    }

    private bool TryPersistSettings(UserSettings next, Action? afterSave = null)
    {
        var previous = Settings;
        var saved = false;
        try
        {
            next = next.Validated();
            Storage.Save(next);
            saved = true;
            afterSave?.Invoke();
            Settings = next;
            Error = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Error = "无法保存设置：" + exception.Message;
            if (saved)
            {
                try { Storage.Save(previous); }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
                {
                    Settings = next;
                    Error += "；回滚设置也失败，请重新打开设置核对：" + rollback.Message;
                }
            }
            return false;
        }
    }

    private void UpdateSettings(UserSettings next)
    {
        TryPersistSettings(next);
        Changed?.Invoke();
    }

    public void SetMetric(SpeedMetric metric) => UpdateSettings(Settings with { Metric = metric });
    public string FormatHistoryMoney(HistoryCost cost, bool hasData = true) => Cost.FormatHistoryMoney(cost, hasData);
    public string FormatLiveMoney(LiveCost cost, double output) => Cost.FormatLiveMoney(cost, output);
    public LiveMoney GetLiveMoney(SourceSet? sources = null)
    {
        return MoneyPresentation.Readout(Money, Speed.Snapshot, sources);
    }
    public LiveMoney GetSourceMoney(SourceKind source) => MoneyPresentation.Readout(Money, Speed.Snapshot, SourceSet.Of(source), apiEquivalent: true);
    private void RefreshMoney()
    {
        Money.Settings = Settings.CostSettings;
        Money.Rates = Cost.Rates;
        MoneyRevision++;
    }
    public void SetCostDisplay(LiveUnit unit, SourceSet subscriptions, bool includeSubscriptions)
    {
        if (TryPersistSettings(Settings with { CostSettings = Settings.CostSettings with
            { LiveUnit = unit, Subscriptions = subscriptions, IncludeSubscriptions = includeSubscriptions } })) RefreshMoney();
        Changed?.Invoke();
    }
    public void SetHistoryIncludeSubscriptions(bool include)
    {
        if (TryPersistSettings(Settings with { CostSettings = Settings.CostSettings with { HistoryIncludeSubscriptions = include } })) RefreshMoney();
        Changed?.Invoke();
    }
    public void SetTrafficDisplay(bool includeTools, TrafficUnit unit, BarTraffic bar) =>
        UpdateSettings(Settings with { TrafficIncludeTools = includeTools, TrafficUnit = unit, TrafficBar = bar });
    public Task RefreshPricesAsync() => Cost.RefreshAsync();

    public void SetCostOptions(bool enabled, string currency)
    {
        if (TryPersistSettings(Settings with { EnablePriceUpdates = enabled, Currency = currency }))
        {
            Cost.SetOptions(Settings.EnablePriceUpdates, Settings.Currency);
            RefreshMoney();
        }
        Changed?.Invoke();
    }

    public void SetAppearance(AppearanceMode appearance, EffectsIntensity effects, bool reduceMotion) =>
        UpdateSettings(Settings with { Appearance = appearance, Effects = effects, ReduceMotion = reduceMotion });
    public void SetTrayShowSpeed(bool show) => UpdateSettings(Settings with { TrayShowSpeed = show });

    public void UpdateAppearanceState(bool dark, bool reduceMotion, string status)
    {
        if (IsDark == dark && EffectiveReduceMotion == reduceMotion && AppearanceStatus == status) return;
        IsDark = dark;
        EffectiveReduceMotion = reduceMotion;
        AppearanceStatus = status;
        Changed?.Invoke();
    }
    public void SetStartInBackground(bool background)
    {
        var enabled = _startup.IsEnabled;
        TryPersistSettings(Settings with { StartInBackground = background },
            enabled ? () => _startup.SetEnabled(true, CodexDirectory, background) : null);
        Changed?.Invoke();
    }
    public void SetOrbOptions(double size, double opacity, bool clickThrough, bool snap) =>
        UpdateSettings(Settings with
        {
            OrbSize = size, OrbOpacity = opacity, OrbClickThrough = clickThrough, OrbSnapToEdges = snap,
            Orbs = Settings.Orbs is { Count: > 0 } definitions ? [definitions[0] with
            { Size = size, Opacity = opacity, ClickThrough = clickThrough, SnapToEdges = snap }, .. definitions.Skip(1)] : null,
        });

    public void SetOrbs(IEnumerable<LinuxOrbDefinition> definitions)
    {
        var values = LinuxOrbDefinition.ValidateList(definitions);
        if (values.Count == 0) values.Add(LinuxOrbDefinition.FromLegacy(Settings) with { Visible = false });
        if (TryPersistSettings(WithPrimary(Settings with { Orbs = values }, values[0]))) UpdateSubsets();
        Changed?.Invoke();
    }

    public void SaveOrbDefinition(LinuxOrbDefinition definition)
    {
        var values = OrbDefinitions.ToList();
        var index = values.FindIndex(orb => orb.Id == definition.Id);
        if (index < 0) return;
        values[index] = definition.Validated();
        if (TryPersistSettings(WithPrimary(Settings with { Orbs = values }, values[0]))) UpdateSubsets();
        Changed?.Invoke();
    }

    private static UserSettings WithPrimary(UserSettings settings, LinuxOrbDefinition primary) => settings with
    {
        OrbSize = primary.Size, OrbOpacity = primary.Opacity, OrbClickThrough = primary.ClickThrough,
        OrbSnapToEdges = primary.SnapToEdges, OrbVisible = primary.Visible, OrbX = primary.OrbX, OrbY = primary.OrbY,
        OrbPlacements = primary.Placements,
    };
    public void SetTitleThemes(TitleThemesDocument themes) => UpdateSettings(Settings with { TitleThemes = themes.Validated() });
    public void SetTrafficEnabled(bool enabled)
    {
        if (TryPersistSettings(Settings with { TrafficEnabled = enabled })) Traffic.SetEnabled(enabled);
        Changed?.Invoke();
    }
    public void SetUpdateOptions(bool enabled, string url)
    {
        if (!string.IsNullOrWhiteSpace(url) && (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)
            || parsed.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Fragment)))
        { ReportError("更新源需要不含账号和片段的 HTTPS 地址。"); return; }
        TryPersistSettings(Settings with { AutoCheckUpdates = enabled, UpdateFeedUrl = url.Trim() }, () => Updates.SetOptions(enabled, url.Trim()));
        Changed?.Invoke();
    }
    public void ShowSources() => OpenSourcesRequested?.Invoke();
    public void ShowOrbManagement() => OpenOrbsRequested?.Invoke();
    public void ShowTraffic() => OpenTrafficRequested?.Invoke();
    public void ShowUpdates() => OpenUpdatesRequested?.Invoke();
    public void ShowReporting() => OpenReportingRequested?.Invoke();

    public void SetSources(IEnumerable<SourceKind> enabled, IReadOnlyDictionary<SourceKind, string> directories)
    {
        try
        {
            var kinds = enabled.Where(Enum.IsDefined).Distinct().ToArray();
            var paths = directories.ToDictionary(pair => pair.Key, pair => LinuxSources.Normalize(pair.Value));
            if (kinds.Contains(SourceKind.ClaudeCode) && kinds.Contains(SourceKind.ClaudeDesktop)
                && paths.TryGetValue(SourceKind.ClaudeCode, out var code) && paths.TryGetValue(SourceKind.ClaudeDesktop, out var desktop)
                && code != desktop) throw new ArgumentException("Claude Code 与 Claude Desktop 共用记录目录，请设置相同路径或将其中一项留空。");
            var root = paths.GetValueOrDefault(SourceKind.Codex) ?? LiveMonitor.DefaultCodexRoot;
            var next = Settings with { EnabledSources = kinds, SourceDirectories = paths, CodexDirectory = root };
            var login = _startup.IsEnabled;
            if (!TryPersistSettings(next, login ? () => _startup.SetEnabled(true, root, Settings.StartInBackground) : null)) return;
            CodexDirectory = root;
            _monitor.Update(Configuration());
            _history.Update(Configuration());
            UpdateSubsets();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        { Error = "无法应用来源：" + exception.Message; }
        finally { Changed?.Invoke(); }
    }

    public void SetLoginStartup(bool enabled)
    {
        TryPersistSettings(Settings with { StartAtLogin = enabled },
            () => _startup.SetEnabled(enabled, CodexDirectory, Settings.StartInBackground));
        Changed?.Invoke();
    }

    public void ReportError(string message)
    {
        if (Error == message) return;
        Error = message;
        Changed?.Invoke();
    }

    public async Task FlushHistoryAsync()
    {
        // Finish an incremental analysis at normal quit, so today's last completed responses and observed peaks
        // survive even if Codex removes its original transcript before the next launch. Keep exit bounded.
        var requested = DateTimeOffset.UtcNow;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, EventArgs args)
        {
            if (History.State.Kind == HistoryStateKind.Failed || History.State.UpdatedAt >= requested)
                ready.TrySetResult();
        }
        History.Changed += Changed;
        try
        {
            History.Refresh();
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException) { }
        finally { History.Changed -= Changed; }
    }

    public void Dispose()
    {
        _historyTimer.Stop();
        Updates.Dispose();
        Reporting.Dispose();
        Traffic.Dispose();
        Cost.Dispose();
        _monitor.Dispose();
        _history.Dispose();
    }
}

public sealed record UsageRow(string Day, long Requests, long Input, long Output, double? AverageSpeed, double? Peak);

public static class UsageProjection
{
    public static IReadOnlyList<UsageRow> Rows(IReadOnlyList<DailyUsage> stored, SpeedSnapshot live, DateTime today, int? lastDays, SourceKind? source = SourceKind.Codex)
    {
        var rows = HistoryAggregator.Daily(stored, source)
            .ToDictionary(day => day.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), day => new UsageRow(day.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), day.Requests, day.Input,
                day.Output, day.AverageSpeed, day.Peak), StringComparer.Ordinal);
        var label = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (live.State.Kind == MonitorStateKind.Running && live.DayStart != DateTimeOffset.MinValue
            && live.DayStart.LocalDateTime.Date == today.Date && live.Today.Requests > 0)
        {
            rows.TryGetValue(label, out var old);
            rows[label] = new(label, Math.Max(old?.Requests ?? 0, live.Today.Requests),
                Math.Max(old?.Input ?? 0, live.Today.Input), Math.Max(old?.Output ?? 0, live.Today.Output),
                live.AverageToday ?? old?.AverageSpeed, Math.Max(old?.Peak ?? 0, live.PeakToday.Output) is > 0 and var peak ? peak : null);
        }
        var start = lastDays is { } days ? today.Date.AddDays(1 - days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
        return rows.Values.Where(row => start is null || string.CompareOrdinal(row.Day, start) >= 0)
            .OrderByDescending(row => row.Day, StringComparer.Ordinal).ToArray();
    }

    public static string Number(long number) => number.ToString("N0", CultureInfo.CurrentCulture);
    public static string Compact(long number) => number switch
    {
        >= 1_000_000_000 => (number / 1_000_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "B",
        >= 1_000_000 => (number / 1_000_000d).ToString("0.##", CultureInfo.InvariantCulture) + "M",
        >= 10_000 => (number / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "K",
        _ => Number(number),
    };
    public static string Rate(double? speed) => speed is { } value && double.IsFinite(value)
        ? value.ToString(value >= 100 ? "N0" : "N1", CultureInfo.CurrentCulture) : "—";

    public static string State(SpeedSnapshot snapshot) => snapshot.State.Kind switch
    {
        MonitorStateKind.Starting => "正在读取记录…",
        MonitorStateKind.NoSources => "未找到已启用来源的记录",
        _ when snapshot.GeneratingCount > 0 => snapshot.LiveEstimatedShare > 0 ? "生成中 · 含估算" : "正在生成",
        _ when snapshot.ToolsCount > 0 => "正在调用工具",
        _ => "等待工具工作",
    };
}
