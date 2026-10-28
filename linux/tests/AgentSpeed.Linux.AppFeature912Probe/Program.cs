using System.Globalization;
using System.Reflection;
using System.Text.Json;
using AgentSpeed.Core;
using AgentSpeed.Core.Traffic;
using AgentSpeed.Linux;
using AgentSpeed.Presentation.History;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Cost;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.X11;

if (args.Length != 2 || args[1] is not ("legacy" or "multi")) throw new ArgumentException("Usage: AppFeature912Probe ISOLATED_DIRECTORY legacy|multi");
Production912.Prepare(Path.GetFullPath(args[0]), args[1]);
var result = AppBuilder.Configure(Production912.CreateApplication).UsePlatformDetect()
    .With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Software], WmClass = "agent-speed-feature912-probe" })
    .AfterSetup(_ => Production912.Schedule()).StartWithClassicDesktopLifetime(args);
return Production912.Failed ? 1 : result;

/// <summary>Real production factory, real parsers, production menu/UI callbacks, and isolated synthetic records only.</summary>
internal static class Production912
{
    private static readonly BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly Assembly Assembly = typeof(AppStorage).Assembly;
    private static readonly Type ProgramType = Assembly.GetType("AgentSpeed.Linux.Program")!;
    private static readonly List<string> Checks = [];
    private static string _root = "", _mode = "", _codex = "", _claude = "", _gemini = "";
    private static Application _application = null!;
    private static object _controller = null!, _manager = null!;
    private static SpeedHub _speed = null!;
    private static HistoryHub _history = null!;
    private static Window _historyWindow = null!, _settings = null!, _management = null!;
    private static LinuxTrafficService _traffic = null!;
    private static DispatcherTimer _timer = null!;
    private static DateTimeOffset _started, _phaseAt;
    private static int _phase;
    private static string _firstId = "", _secondId = "", _thirdId = "";
    private static long _hiddenFrames;
    private static bool _finished;
    private static bool _recoveringRange;
    public static bool Failed { get; private set; }

    public static void Prepare(string root, string mode)
    {
        _root = root; _mode = mode; Directory.CreateDirectory(root);
        _codex = Path.Combine(root, "Codex synthetic", "sessions");
        _claude = Path.Combine(root, "Claude synthetic", "projects");
        _gemini = Path.Combine(root, "Gemini synthetic", "tmp");
        WriteFixtures();
        // Deliberately omit Orbs/EnabledSources/SourceDirectories: exercise the previous single-Codex settings shape.
        new AppStorage(Path.Combine(root, "data")).Save(new UserSettings { Appearance = AppearanceMode.Dark });
        ProgramType.GetProperty("Options", Static)!.SetValue(null, new LaunchOptions(CodexDirectory: _codex,
            DataDirectory: Path.Combine(root, "data"), Dashboard: true, SoftwareRendering: true));
    }
    public static Application CreateApplication() => (Application)Activator.CreateInstance(Assembly.GetType("AgentSpeed.Linux.SpeedApplication")!)!;

    public static void Schedule()
    {
        _started = _phaseAt = DateTimeOffset.UtcNow;
        _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) =>
        {
            try
            {
                if (_finished) return;
                if (DateTimeOffset.UtcNow - _started > TimeSpan.FromSeconds(110)) throw new TimeoutException($"Production912 {_mode} phase {_phase}");
                Step();
            }
            catch (Exception error)
            { File.WriteAllText(Path.Combine(_root, "failure.txt"), error.ToString()); Console.Error.WriteLine(error); Finish(1); }
        };
        _timer.Start();
    }

    private static void Step()
    {
        switch (_phase)
        {
            case 0:
                _application = Application.Current!;
                _controller = Get<object>(_application, "_controller");
                _speed = Get<SpeedHub>(_controller, "Speed"); _history = Get<HistoryHub>(_controller, "History");
                if (_speed.Snapshot.State.Kind != MonitorStateKind.Running || _history.State.Kind != HistoryStateKind.Ready
                    || _history.Days.Sum(day => day.Totals.Requests) != 2) return;
                _manager = Get<object>(_application, "_orbManager"); _settings = Get<Window>(_application, "_settingsWindow");
                _historyWindow = Get<Window>(_application, "_historyWindow"); _traffic = Get<LinuxTrafficService>(_controller, "Traffic");
                var desktop = (IClassicDesktopStyleApplicationLifetime)_application.ApplicationLifetime!;
                desktop.Exit += (_, _) => File.WriteAllText(Path.Combine(_root, "exit.json"), JsonSerializer.Serialize(new
                { visibleWindows = desktop.Windows.Count(window => window.IsVisible), managerCount = Get<int>(_manager, "Count"),
                    appearanceDisposed = Get<bool>(Get<object>(_application, "_appearance"), "_disposed") }));
                Check(LinuxSources.Enabled(Settings).SequenceEqual(new[] { SourceKind.Codex }) && Settings.EnabledSources is null,
                    "legacy source preference remains implicit Codex only");
                Check(_speed.Snapshot.Today is { Requests: 2, Input: 30000, CachedInput: 3000, Output: 300 },
                    "real Codex parser reads exact independently specified legacy totals");
                Check(Get<int>(_manager, "Count") == 1 && Definitions[0] is { Style: OrbStyle.Led, Size: 212, Opacity: 1, Sources: null, TitleTheme: "general" },
                    "legacy settings migrate to one unchanged LED orb following the global title");
                AssertOffline(); Capture(Get<Window>(_application, "_dashboard"), "legacy-dashboard.png");
                if (_mode == "legacy") { Finish(0); return; }
                NativeClick("工具来源与路径");
                var sources = Get<Window>(_application, "_sourcesWindow"); Check(sources.IsVisible, "actual tray Sources callback opens the production window");
                foreach (var kind in Enum.GetValues<SourceKind>()) Find<CheckBox>(sources, "SourceEnabled" + kind).IsChecked = kind is SourceKind.Codex or SourceKind.ClaudeCode or SourceKind.Gemini;
                Find<TextBox>(sources, "SourcePathCodex").Text = _codex;
                Find<TextBox>(sources, "SourcePathClaudeCode").Text = _claude;
                Find<TextBox>(sources, "SourcePathGemini").Text = _gemini;
                Click(Find<Button>(sources, "ApplySources")); Next(); break;
            case 1:
                if (_history.State.Kind != HistoryStateKind.Ready || _history.Days.Sum(day => day.Totals.Requests) != 5
                    || _speed.Snapshot.Sources.Count(source => source.Enabled && source.Available) < 3) return;
                Check(LinuxSources.Enabled(Settings).Order().SequenceEqual(new[] { SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.Gemini }.Order()),
                    "Sources UI persists exactly the three selected tools and their isolated directories");
                Check(_history.Days.Where(day => day.Source == SourceKind.ClaudeCode).Sum(day => day.Totals.Input) == 3300
                    && _history.Days.Where(day => day.Source == SourceKind.ClaudeCode).Sum(day => day.Totals.Output) == 210
                    && _history.Days.Where(day => day.Source == SourceKind.Gemini).Sum(day => day.Totals.Input) == 555
                    && _history.Days.Where(day => day.Source == SourceKind.Gemini).Sum(day => day.Totals.Output) == 44,
                    "real Claude and Gemini parsers retain exact independent input/output fixtures");
                Invoke(_application, "ShowHistory"); Get<ComboBox>(_historyWindow, "_source").SelectedIndex = 0;
                Get<ComboBox>(_historyWindow, "_range").SelectedIndex = 6; Next(); break;
            case 2:
                VerifyCostAndTrafficBindings();
                var all = Get<HistoryPeriod>(_historyWindow, "_period");
                Check(all.Totals is { Requests: 5, Input: 33855, Output: 554 }, "actual All Sources history view sums three parser totals without duplication");
                var allModels = Get<IReadOnlyList<ModelStatistic>>(_historyWindow, "_visibleModels");
                Check(allModels.Count == 3 && allModels.Sum(model => model.Output) == 554, "actual all-source model view has the three known model buckets");
                WriteCsv("all-source-history.csv", HistoryCsv.Build(all.Days, null));
                WriteCsv("all-source-models.csv", ModelStatistics.Csv(allModels, all.Range, null));
                SelectHistorySource(SourceKind.ClaudeCode); Next(); break;
            case 3:
                if (_recoveringRange)
                {
                    var recovered = Get<HistoryPeriod?>(_historyWindow, "_period");
                    if (recovered is null)
                    {
                        if (Age > 5) throw new InvalidOperationException("Custom-date recovery did not rebuild: "
                            + Get<CalendarDatePicker>(_historyWindow, "_from").SelectedDate + ".." + Get<CalendarDatePicker>(_historyWindow, "_to").SelectedDate);
                        return;
                    }
                    Check(recovered.Totals is { Requests: 1, Input: 555, Output: 44 }
                        && Get<IReadOnlyList<ModelStatistic>>(_historyWindow, "_visibleModels").Single().Source == SourceKind.Gemini,
                        "valid date recovery rebuilds the newly selected Gemini period and model rows");
                    WriteCsv("gemini-recovered-history.csv", HistoryCsv.Build(recovered.Days, SourceKind.Gemini));
                    NativeClick("管理悬浮窗与主题");
                    _management = ((IClassicDesktopStyleApplicationLifetime)_application.ApplicationLifetime!).Windows.Single(window => window.Title == "Agent Speed · 悬浮窗与标题主题");
                    _firstId = Definitions[0].Id; ConfigureOrb("Codex scope", SourceKind.Codex, OrbStyle.Led, "vehicles");
                    ClickText(_management, "新增"); _secondId = Definitions.Last().Id; ConfigureOrb("Claude scope", SourceKind.ClaudeCode, OrbStyle.Needle, "animals");
                    ClickText(_management, "新增"); _thirdId = Definitions.Last().Id; ConfigureOrb("Gemini scope", SourceKind.Gemini, OrbStyle.Liquid, "space");
                    Next(); break;
                }
                var claude = Get<HistoryPeriod>(_historyWindow, "_period");
                Check(claude.Totals is { Requests: 2, Input: 3300, Output: 210 }, "history source control filters exactly Claude requests and tokens");
                var models = Get<IReadOnlyList<ModelStatistic>>(_historyWindow, "_visibleModels");
                Check(models.Single() is { Source: SourceKind.ClaudeCode, Model: "claude-opus-5-5", Input: 3300, Output: 210, Requests: 2 },
                    "filtered production model rows stay attached to the selected source");
                var csv = HistoryCsv.Build(claude.Days, SourceKind.ClaudeCode); var modelCsv = ModelStatistics.Csv(models, claude.Range, SourceKind.ClaudeCode);
                WriteCsv("claude-history.csv", csv); WriteCsv("claude-models.csv", modelCsv);
                Check(csv.Contains(",claude-code,2,3300,0,0,210,", StringComparison.Ordinal)
                    && modelCsv.Contains(",claude-code,claude-opus-5-5,3300,0,210,2,", StringComparison.Ordinal)
                    && !modelCsv.Contains("gpt-", StringComparison.Ordinal), "CSV from the actual filtered view retains exact source/model counters");
                Capture(_historyWindow, "claude-history.png");
                Get<ComboBox>(_historyWindow, "_range").SelectedIndex = 7;
                Get<CalendarDatePicker>(_historyWindow, "_from").SelectedDate = DateTime.Today.AddDays(2);
                Get<CalendarDatePicker>(_historyWindow, "_to").SelectedDate = DateTime.Today;
                SelectHistorySource(SourceKind.Gemini);
                Check(Get<HistoryPeriod?>(_historyWindow, "_period") is null
                    && Get<TextBlock[]>(_historyWindow, "_values").All(value => value.Text == "—")
                    && Get<IReadOnlyList<ModelStatistic>>(_historyWindow, "_visibleModels").Count == 0
                    && !Get<Control>(_historyWindow, "_chart").IsVisible,
                    "invalid custom dates plus source change clear the period, numbers, models and chart instead of relabelling old Claude data");
                var windowCount = ((IClassicDesktopStyleApplicationLifetime)_application.ApplicationLifetime!).Windows.Count;
                foreach (var kind in new[] { (Models: false, Modes: false), (Models: true, Modes: false), (Models: false, Modes: true) })
                {
                    var export = (Task)_historyWindow.GetType().GetMethod("ExportAsync", Instance)!.Invoke(_historyWindow, [kind.Models, kind.Modes])!;
                    Check(export.IsCompletedSuccessfully, "invalid range blocks production export before opening a picker: " + kind);
                }
                Check(((IClassicDesktopStyleApplicationLifetime)_application.ApplicationLifetime!).Windows.Count == windowCount,
                    "invalid-range exports cannot leave a file picker or export data under the new source name");
                Capture(_historyWindow, "history-invalid-range.png");
                Get<CalendarDatePicker>(_historyWindow, "_from").SelectedDate = DateTime.Today.AddDays(-1);
                _recoveringRange = true; _phaseAt = DateTimeOffset.UtcNow; break;
            case 4:
                Check(Get<int>(_manager, "Count") == 3, "production management UI creates three independently configured windows");
                foreach (var (id, source) in new[] { (_firstId, SourceKind.Codex), (_secondId, SourceKind.ClaudeCode), (_thirdId, SourceKind.Gemini) })
                {
                    var raw = _speed.Snapshot.Sources.Single(item => item.Kind == source);
                    var orb = Orb(id); var expected = raw.Live.Output; var actual = Get<double>(orb, "DisplayedRate");
                    Check(expected > 0 && Math.Abs(expected - actual) < 1e-8, "production orb rate is the real parser's source-filtered live value: " + source);
                    Capture(orb, "orb-" + source + ".png");
                }
                SelectManaged(_firstId); Get<ComboBox>(_management, "_style").SelectedIndex = (int)OrbStyle.Reactor;
                Click(Find<Button>(_management, "SaveManagedOrb")); Invoke(_management, "EditTheme", false);
                var editor = ((IClassicDesktopStyleApplicationLifetime)_application.ApplicationLifetime!).Windows.Single(window => window.Title == "编辑标题主题");
                Find<TextBox>(editor, "CustomThemeName").Text = "应用实测主题";
                for (var level = 0; level <= 13; level++) Find<TextBox>(editor, "CustomTitle" + level).Text = "应用实测称号";
                Click(Find<Button>(editor, "SaveCustomTheme"));
                SelectManaged(_secondId); Get<CheckBox>(_management, "_visible").IsChecked = false;
                Click(Find<Button>(_management, "SaveManagedOrb")); _hiddenFrames = Get<long>(Orb(_secondId), "AnimationFrameCount"); Next(); break;
            case 5:
                if (Age < 1.3) return;
                Check(!Orb(_secondId).IsVisible && !Get<bool>(Orb(_secondId), "AnimationTimerEnabled")
                    && Get<long>(Orb(_secondId), "AnimationFrameCount") == _hiddenFrames,
                    "hiding one production orb stops only its continued frames");
                Check(Get<string>(Orb(_firstId), "TitleText").Contains("应用实测称号", StringComparison.Ordinal)
                    && Definitions.Single(orb => orb.Id == _firstId).Style == OrbStyle.Reactor,
                    "actual custom-theme editor and style Apply update the selected production orb");
                Get<CheckBox>(_management, "_visible").IsChecked = true; Get<CheckBox>(_management, "_through").IsChecked = true;
                Click(Find<Button>(_management, "SaveManagedOrb")); Next(); break;
            case 6:
                Check(Orb(_secondId).IsVisible && Get<bool>(Orb(_secondId), "ClickThroughApplied"), "second orb reapplies its native X11 input shape on show");
                Get<CheckBox>(_management, "_through").IsChecked = false; Click(Find<Button>(_management, "SaveManagedOrb"));
                Orb(_firstId).Position = new PixelPoint(30, 85); Orb(_secondId).Position = new PixelPoint(300, 95); Orb(_thirdId).Position = new PixelPoint(570, 105);
                NativeClick("网络流量"); NativeClick("检查应用更新"); NativeClick("上报、头像与设备"); Invoke(_application, "ShowSettings");
                Check(Get<Window>(_application, "_trafficWindow").IsVisible && Get<Window>(_application, "_updateWindow").IsVisible
                    && Get<Window>(_application, "_reportingWindow").IsVisible, "actual tray menu callbacks open all production advanced windows");
                Check(!_traffic.Snapshot.Enabled && _traffic.Snapshot.MachineLive is null, "opening Traffic window alone does not enable sampling");
                Find<CheckBox>(_settings, "SettingsTrafficEnabled").IsChecked = true; Next(); break;
            case 7:
                if (Age < 2 || _traffic.Snapshot.MachineLive is null || _traffic.Snapshot.MachineSampledMs < 900) return;
                Check(Settings.TrafficEnabled && _traffic.Snapshot.Enabled && _traffic.Snapshot.MachineLive is { Rx: >= 0, Tx: >= 0 }
                    && _traffic.Snapshot.MachineHistory.Count >= 1, "production traffic checkbox obtains a real NIC baseline and subsequent sampled interval");
                Check(_traffic.Coverage.Contains("下限", StringComparison.Ordinal) && _traffic.InterfacesUsed.Count > 0
                    && _traffic.ReportSnapshot.MachineLive is null && _traffic.ReportSnapshot.ProcessState.Kind == ProcessTrafficKind.Unavailable,
                    "local TCP coverage remains labelled partial and machine rates are unavailable to reporting");
                Capture(Get<Window>(_application, "_trafficWindow"), "traffic-enabled.png");
                Capture(Get<Window>(_application, "_updateWindow"), "updates-offline.png");
                Capture(Get<Window>(_application, "_reportingWindow"), "reporting-offline.png");
                Find<CheckBox>(_settings, "SettingsTrafficEnabled").IsChecked = false; Next(); break;
            case 8:
                Check(!_traffic.Snapshot.Enabled && _traffic.Snapshot.MachineLive is null && _traffic.Days.Any(day => day.IsMachine && day.SampledMs > 0),
                    "traffic checkbox stops live sampling while preserving only actually sampled local history");
                Invoke(_manager, "SavePositions");
                Check(Definitions.All(orb => orb.Placements is { Count: > 0 }) && !Get<bool>(Orb(_secondId), "ClickThroughApplied"),
                    "production orbs persist separate normalized positions and restore mouse interaction");
                Capture(_management, "management.png"); AssertOffline();
                Check(Get<string?>(_controller, "Error") is null, "production controller finishes feature 9-12 interactions without errors");
                Finish(0); break;
        }
    }

    private static void AssertOffline()
    {
        var cost = Get<LinuxCostService>(_controller, "Cost"); var updates = Get<LinuxUpdateService>(_controller, "Updates"); var reporting = Get<LinuxReportService>(_controller, "Reporting");
        Check(!Settings.EnablePriceUpdates && !cost.Status.NetworkEnabled && cost.Status.LastPriceCheck is null && cost.Status.LastRatesCheck is null
            && !Settings.AutoCheckUpdates && Settings.UpdateFeedUrl.Length == 0 && updates.LastCheck is null
            && !reporting.Enabled && !reporting.HasIdentity && !File.Exists(reporting.IdentityPath),
            "production price/update/report services remain unconfigured and offline without a stored reporting identity");
        Check(!Settings.StartAtLogin && !File.Exists(Get<string>(_controller, "LoginStartupPath")), "production exercise never registers login startup");
    }
    private static void ConfigureOrb(string name, SourceKind source, OrbStyle style, string theme)
    {
        Get<TextBox>(_management, "_name").Text = name; Get<ComboBox>(_management, "_style").SelectedIndex = (int)style;
        var titles = Get<ComboBox>(_management, "_titles"); titles.SelectedItem = titles.Items.Cast<object>().Single(item => Get<string>(item, "Id") == theme);
        Get<CheckBox>(_management, "_all").IsChecked = false;
        foreach (var (kind, check) in Get<Dictionary<SourceKind, CheckBox>>(_management, "_sources")) check.IsChecked = kind == source;
        Get<Slider>(_management, "_size").Value = 160; Get<Slider>(_management, "_opacity").Value = .9;
        Click(Find<Button>(_management, "SaveManagedOrb"));
    }
    private static void SelectManaged(string id)
    { var box = Get<ComboBox>(_management, "_orbs"); box.SelectedItem = box.Items.Cast<object>().Single(item => Get<string>(item, "Id") == id); }
    private static void VerifyCostAndTrafficBindings()
    {
        Invoke(_application, "ShowSettings");
        var raw = _speed.Snapshot;
        var getMoney = _controller.GetType().GetMethod("GetLiveMoney", Instance)!;
        LiveMoney Money(SourceSet? source = null) => (LiveMoney)getMoney.Invoke(_controller, [source])!;
        var original = Money();
        Check(original.Value > 0 && !original.Unknown, "production fixture has a known positive live money rate before subscription edits");
        Find<ComboBox>(_settings, "SettingsLiveUnit").SelectedIndex = 1;
        Find<CheckBox>(_settings, "SubscriptionCodex").IsChecked = true;
        Find<CheckBox>(_settings, "SettingsIncludeSubscriptions").IsChecked = false;
        Click(Find<Button>(_settings, "ApplyCostDisplay"));
        Check(Settings.CostSettings is { LiveUnit: LiveUnit.Cost, IncludeSubscriptions: false }
            && Settings.CostSettings.Subscriptions.Contains(SourceKind.Codex), "actual settings cost controls save the main unit and Codex subscription mark");
        Check(Money(SourceSet.Of(SourceKind.Codex)) is { Number: "0", Unknown: false }, "a subscription-only orb source has an explicitly excluded zero rather than an unknown price");
        Check(Money().Value < original.Value && _speed.Snapshot.Live == raw.Live && _speed.Snapshot.Today == raw.Today,
            "subscription exclusion changes money without changing measured token rates or request totals");
        var dashboard = Get<Window>(_application, "_dashboard");
        Check(Get<TextBlock>(dashboard, "_speed").Text == Money().NumberText
            && Get<TextBlock>(dashboard, "_unit").Text == Money().Unit, "production dashboard actually displays the selected cost number and currency-per-second unit");
        Find<ComboBox>(_settings, "SettingsCurrency").SelectedItem = "CNY";
        var loaded = new AppStorage(Path.Combine(_root, "data")).Load().Validated();
        Check(loaded.Currency == "CNY" && loaded.CostSettings.Currency == "CNY" && loaded.CostSettings.Subscriptions.Contains(SourceKind.Codex),
            "currency edits and JSON reload retain subscription SourceSet and currency consistently");
        _application.GetType().GetField("_lastTrayUpdateTick", Instance)!.SetValue(_application, Environment.TickCount64 - 1100);
        Invoke(_application, "UpdateTrayReadout");
        Check(Get<TrayReadoutRenderer>(_application, "_trayReadout").LastText.EndsWith(Money().Unit, StringComparison.Ordinal),
            "production cost tray renderer uses the selected currency unit rather than tok/s");
        Capture(dashboard, "dashboard-cost-cny.png");
        Find<CheckBox>(_settings, "SettingsIncludeSubscriptions").IsChecked = true;
        Click(Find<Button>(_settings, "ApplyCostDisplay"));
        Check(Money().Value == original.Value, "include-subscriptions control restores the original raw-USD live cost before currency formatting");
        Find<CheckBox>(_settings, "SettingsTrafficEnabled").IsChecked = true;
        Find<ComboBox>(_settings, "SettingsTrafficUnit").SelectedIndex = 1;
        Find<ComboBox>(_settings, "SettingsTrafficBar").SelectedIndex = 1;
        _application.GetType().GetField("_lastTrayUpdateTick", Instance)!.SetValue(_application, Environment.TickCount64 - 1100);
        Invoke(_application, "UpdateTrayReadout");
        Check(Settings.TrafficEnabled && Settings.TrafficUnit == TrafficUnit.Bits && Settings.TrafficBar == BarTraffic.Machine,
            "actual traffic settings persist sampler enable, bit units and machine top-bar scope");
        Check(Get<TrayIcon>(_application, "_trafficDownTray").IsVisible && Get<TrayIcon>(_application, "_trafficUpTray").IsVisible
            && Get<TrayReadoutRenderer>(_application, "_trafficDownReadout").LastText.Contains("b/s", StringComparison.Ordinal),
            "production top bar exposes separate upload/download numeric indicators with explicit bit units");
        Check(!Get<LinuxTrafficService>(_controller, "Traffic").EnhancedActive, "ordinary traffic settings never launch the privileged event helper");
        Find<CheckBox>(_settings, "SettingsTrafficEnabled").IsChecked = false;
        Find<ComboBox>(_settings, "SettingsTrafficUnit").SelectedIndex = 0;
        Find<ComboBox>(_settings, "SettingsTrafficBar").SelectedIndex = 0;
        _application.GetType().GetField("_lastTrayUpdateTick", Instance)!.SetValue(_application, Environment.TickCount64 - 1100);
        Invoke(_application, "UpdateTrayReadout");
        Check(!Get<TrayIcon>(_application, "_trafficDownTray").IsVisible && !Get<TrayIcon>(_application, "_trafficUpTray").IsVisible,
            "disabling traffic removes both extra production indicators");
        Find<CheckBox>(_settings, "SubscriptionCodex").IsChecked = false;
        Find<CheckBox>(_settings, "SettingsIncludeSubscriptions").IsChecked = false;
        Find<ComboBox>(_settings, "SettingsLiveUnit").SelectedIndex = 0;
        Click(Find<Button>(_settings, "ApplyCostDisplay"));
        Find<ComboBox>(_settings, "SettingsCurrency").SelectedItem = "USD";
        Check(Settings.CostSettings.LiveUnit == LiveUnit.Tokens && Settings.CostSettings.Subscriptions.IsEmpty
            && !Settings.TrafficEnabled && !Settings.EnablePriceUpdates && !Get<LinuxReportService>(_controller, "Reporting").Enabled,
            "cost/traffic UI probe restores legacy display defaults without enabling any public network service");
        _settings.Hide();
    }
    private static void SelectHistorySource(SourceKind source)
    { var box = Get<ComboBox>(_historyWindow, "_source"); box.SelectedItem = box.Items.Cast<object>().Single(item => Get<SourceKind?>(item, "Kind") == source); }
    private static IReadOnlyList<LinuxOrbDefinition> Definitions => Get<IReadOnlyList<LinuxOrbDefinition>>(_controller, "OrbDefinitions");
    private static UserSettings Settings => Get<UserSettings>(_controller, "Settings");
    private static Window Orb(string id) => Get<System.Collections.IEnumerable>(_manager, "Windows").Cast<Window>().Single(window => Get<LinuxOrbDefinition>(window, "Definition").Id == id);
    private static double Age => (DateTimeOffset.UtcNow - _phaseAt).TotalSeconds;
    private static void Next() { _phase++; _phaseAt = DateTimeOffset.UtcNow; }
    private static void Check(bool condition, string text) { if (!condition) throw new InvalidOperationException(text); Checks.Add(text); Console.WriteLine("PASS " + text); }
    private static T Get<T>(object instance, string name) => (T)(instance.GetType().GetProperty(name, Instance)?.GetValue(instance) ?? instance.GetType().GetField(name, Instance)?.GetValue(instance))!;
    private static void Invoke(object instance, string name, params object[] values) => instance.GetType().GetMethod(name, Instance)!.Invoke(instance, values);
    private static T Find<T>(Window window, string name) where T : Control => window.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void ClickText(Window window, string text) => Click(window.GetLogicalDescendants().OfType<Button>().Single(button => Equals(button.Content, text)));
    private static void NativeClick(string text)
    {
        var item = Get<TrayIcon>(_application, "_tray").Menu!.Items.OfType<NativeMenuItem>().Single(item => item.Header == text);
        var raise = item.GetType().GetMethods(Instance).FirstOrDefault(method => method.Name.Contains("Raise", StringComparison.Ordinal) && method.Name.Contains("Click", StringComparison.Ordinal) && method.GetParameters().Length == 0);
        if (raise is not null) raise.Invoke(item, null);
        else ((Delegate)item.GetType().GetField("Click", Instance)!.GetValue(item)!).DynamicInvoke(item, EventArgs.Empty);
    }
    private static void Capture(Window window, string name)
    { using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height))); bitmap.Render(window); bitmap.Save(Path.Combine(_root, name), PngBitmapEncoderOptions.Default); }
    private static void WriteCsv(string name, string content) => File.WriteAllBytes(Path.Combine(_root, name), HistoryCsv.Encode(content));
    private static void Finish(int code)
    {
        if (_finished) return; _finished = true; Failed = code != 0; _timer.Stop();
        File.WriteAllText(Path.Combine(_root, "result.json"), JsonSerializer.Serialize(new { mode = _mode, productionApplication = _application.GetType().FullName,
            isolated = true, fixtureOnly = true, checks = Checks, settings = Settings, traffic = _traffic.Snapshot,
            sourceTotals = _history.Days.Select(day => new { source = day.Source.ToString(), day.Totals }) }, new JsonSerializerOptions { WriteIndented = true }));
        Invoke(_application, "Quit");
    }

    private static void WriteFixtures()
    {
        var now = DateTimeOffset.UtcNow;
        string Stamp(int offset) => now.AddSeconds(offset).ToString("O", CultureInfo.InvariantCulture);
        string Record(string type, object payload, int offset) => JsonSerializer.Serialize(new { timestamp = Stamp(offset), type, payload });
        var codexLines = new List<string> { Record("session_meta", new { id = "feature912-codex", cwd = "/synthetic/feature912-codex", source = "cli" }, -40), Record("turn_context", new { model = "gpt-6.1-sol" }, -35) };
        for (var i = 1; i <= 2; i++)
        {
            var start = i == 1 ? -28 : -20;
            codexLines.Add(Record("event_msg", new { type = "task_started" }, start));
            codexLines.Add(Record("token_usage_record", new { thread_id = "feature912-codex", response_id = "feature912-codex-" + i,
                usage = new { input_tokens = i * 10000, cached_input_tokens = i * 1000, output_tokens = i * 100, reasoning_output_tokens = 0 } }, start + 3));
            codexLines.Add(Record("event_msg", new { type = "task_complete" }, start + 4));
        }
        codexLines.Add(Record("event_msg", new { type = "task_started" }, -1)); Directory.CreateDirectory(_codex);
        File.WriteAllLines(Path.Combine(_codex, "rollout-feature912.jsonl"), codexLines);
        var claudeDirectory = Path.Combine(_claude, "-synthetic-feature912"); Directory.CreateDirectory(claudeDirectory);
        var claudeLines = new List<string>();
        for (var i = 1; i <= 2; i++)
        {
            var start = i == 1 ? -28 : -20;
            claudeLines.Add(JsonSerializer.Serialize(new { type = "user", timestamp = Stamp(start), cwd = "/synthetic/feature912-claude", message = new { role = "user", content = "Synthetic validation input" } }));
            claudeLines.Add(JsonSerializer.Serialize(new { type = "assistant", timestamp = Stamp(start + 3), requestId = "req-feature912-" + i,
                uuid = "uuid-feature912-" + i, message = new { id = "message-feature912-" + i, type = "message", role = "assistant", model = "claude-opus-5-5",
                    content = new[] { new { type = "text", text = "Synthetic response" } }, stop_reason = "end_turn",
                    usage = new { input_tokens = i * 1100, cache_creation_input_tokens = 0, cache_read_input_tokens = 0, output_tokens = i == 1 ? 77 : 133 } } }));
        }
        claudeLines.Add(JsonSerializer.Serialize(new { type = "user", timestamp = Stamp(-1), cwd = "/synthetic/feature912-claude", message = new { role = "user", content = "Synthetic in-flight request" } }));
        File.WriteAllLines(Path.Combine(claudeDirectory, "feature912-claude.jsonl"), claudeLines);
        var geminiChats = Path.Combine(_gemini, "synthetic-project", "chats"); Directory.CreateDirectory(geminiChats);
        File.WriteAllText(Path.Combine(_gemini, "synthetic-project", ".project_root"), "/synthetic/feature912-gemini\n");
        object[] messages = [new { id = "user-feature912", timestamp = Stamp(-26), type = "user", content = new[] { new { text = "Synthetic Gemini input" } } },
            new { id = "gemini-feature912", timestamp = Stamp(-22), type = "gemini", content = "Synthetic Gemini response", model = "gemini-2.5-pro", tokens = new { input = 555, output = 44, cached = 0, thoughts = 0, tool = 0, total = 599 } },
            new { id = "user-feature912-active", timestamp = Stamp(-1), type = "user", content = new[] { new { text = "Synthetic Gemini in-flight" } } }];
        File.WriteAllText(Path.Combine(geminiChats, "session-2026-10-27T10-00-feature912.json"), JsonSerializer.Serialize(new
        { sessionId = "feature912-gemini", projectHash = "synthetic-hash", startTime = Stamp(-30), lastUpdated = Stamp(-1), messages }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(_root, "fixtures.json"), JsonSerializer.Serialize(new { synthetic = true,
            codex = new { requests = 2, input = 30000, output = 300, cache = 3000 }, claude = new { requests = 2, input = 3300, output = 210 }, gemini = new { requests = 1, input = 555, output = 44 } }));
    }
}
