using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AgentSpeed.Core;
using AgentSpeed.Linux;
using AgentSpeed.Presentation.History;
using AgentSpeed.Presentation.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.X11;

if (args.Length != 1) throw new ArgumentException("Usage: AppFeature58Probe ISOLATED_OUTPUT_DIRECTORY");
ProductionProbe.Prepare(Path.GetFullPath(args[0]));
return AppBuilder.Configure(() => ProductionProbe.CreateApplication()).UsePlatformDetect()
    .With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Software], WmClass = "agent-speed-feature58-probe" })
    .AfterSetup(_ => ProductionProbe.Schedule())
    .StartWithClassicDesktopLifetime(args);

/// <summary>The production application owns every service, window, tray handler and lifetime in this probe.</summary>
internal static class ProductionProbe
{
    private static readonly BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly Assembly Assembly = typeof(AppStorage).Assembly;
    private static readonly Type ProgramType = Assembly.GetType("AgentSpeed.Linux.Program")!;
    private static readonly List<string> Checks = [];
    private static readonly List<string> FixtureFiles = [];
    private static string _root = "", _sessions = "";
    private static LaunchOptions _options = new();
    private static Application _application = null!;
    private static object _controller = null!, _orbGauge = null!, _dashboardGauge = null!;
    private static Window _dashboard = null!, _settings = null!, _history = null!, _orb = null!;
    private static SpeedHub _speed = null!;
    private static HistoryHub _historyHub = null!;
    private static LinuxCostService _cost = null!;
    private static TrayReadoutRenderer _readout = null!;
    private static SolidColorBrush _background = null!;
    private static DispatcherTimer _timer = null!;
    private static DateTimeOffset _started, _phaseAt;
    private static int _phase;
    private static long _frames, _trayRevision;
    private static string _usd = "", _cny = "", _staticHash = "";
    private static bool _finished;

    public static void Prepare(string root)
    {
        _root = root;
        Directory.CreateDirectory(root);
        _sessions = Path.Combine(root, "Codex synthetic", "sessions");
        WriteSyntheticSessions();
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_sessions)!, "config.toml"), "service_tier = 'fast'\n");
        var data = Path.Combine(root, "data");
        new AppStorage(data).Save(new UserSettings(OrbVisible: true)
        {
            Currency = "USD", EnablePriceUpdates = false, Appearance = AppearanceMode.Dark,
            Effects = EffectsIntensity.Standard, TrayShowSpeed = true,
        });
        _options = new(CodexDirectory: _sessions, DataDirectory: data, Dashboard: true, SoftwareRendering: true);
        ProgramType.GetProperty("Options", Static)!.SetValue(null, _options);
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
                if (DateTimeOffset.UtcNow - _started > TimeSpan.FromSeconds(65)) throw new TimeoutException($"Production feature probe phase {_phase}");
                Step();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                File.WriteAllText(Path.Combine(_root, "failure.txt"), exception.ToString());
                Finish(1);
            }
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
                _speed = Get<SpeedHub>(_controller, "Speed");
                _historyHub = Get<HistoryHub>(_controller, "History");
                if (_speed.Snapshot.State.Kind != MonitorStateKind.Running || _speed.Snapshot.Agents.Count != 12
                    || _historyHub.State.Kind != HistoryStateKind.Ready) return;
                _dashboard = Get<Window>(_application, "_dashboard");
                _history = Get<Window>(_application, "_historyWindow");
                _settings = Get<Window>(_application, "_settingsWindow");
                _orb = Get<Window>(_application, "_orb");
                _orbGauge = Get<object>(_orb, "_gauge");
                _dashboardGauge = Get<object>(_dashboard, "_gauge");
                _cost = Get<LinuxCostService>(_controller, "Cost");
                _readout = Get<TrayReadoutRenderer>(_application, "_trayReadout");
                _background = (SolidColorBrush)Assembly.GetType("AgentSpeed.Linux.Ui")!.GetField("Background", Static)!.GetValue(null)!;
                var monitor = Get<LiveMonitor>(_controller, "_monitor");
                var historyService = Get<HistoryService>(_controller, "_history");
                Check(ReferenceEquals(_cost.PriceBook, monitor.Pricing) && ReferenceEquals(_cost.PriceBook, historyService.Pricing),
                    "actual production live/history services share the one cost price book");
                Check(_speed.Snapshot.Today is { Requests: 12, Input: 78084, CachedInput: 7836, Output: 840 }, "12 synthetic sessions retain independently known totals");
                Check(_dashboard.IsVisible && _orb.IsVisible && Get<TrayIcon>(_application, "_tray").IsVisible,
                    "production application created visible dashboard/orb and actual tray");
                Invoke(_application, "ShowSettings"); Invoke(_application, "ShowHistory");
                Get<ComboBox>(_history, "_metric").SelectedIndex = 3;
                Find<CheckBox>(_settings, "SettingsPriceUpdates").IsChecked = true;
                Check(Settings.EnablePriceUpdates && _cost.Status.NetworkEnabled, "actual network checkbox enables the opt-in setting");
                Find<CheckBox>(_settings, "SettingsPriceUpdates").IsChecked = false;
                Check(!Settings.EnablePriceUpdates && !_cost.Status.NetworkEnabled && !_cost.Status.IsBusy,
                    "network opt-in is disabled again before any scheduled or currency-triggered request");
                Next();
                break;
            case 1:
                var values = Get<TextBlock[]>(_history, "_values");
                _usd = values[4].Text ?? "";
                Check(_usd != "—" && _cost.PriceBook.CurrentVersion > 0, "known-model production history has a priced USD amount");
                Check(Get<string>(Get<object>(_history, "_chart"), "MoneyCurrency") == "USD", "production cost chart initially uses USD");
                Check(ExecutionModePresentation.ConfiguredSource(_speed.Snapshot).Contains("Fast", StringComparison.Ordinal)
                    && _speed.Snapshot.Sources.Single(source => source.Kind == SourceKind.Codex).ConfiguredModes.Facts.All(fact => fact.Evidence == ExecutionModeEvidence.Configured),
                    "real Codex config.toml Fast is read only as configured evidence");
                Check(ExecutionModePresentation.Current(_speed.Snapshot).Contains("未知", StringComparison.Ordinal)
                    && !ExecutionModePresentation.Current(_speed.Snapshot).Contains("Fast", StringComparison.Ordinal),
                    "actual active session mode stays unknown despite configured Fast");
                Find<TabControl>(_history, "HistoryDetailTabs").SelectedIndex = 2;
                Next();
                break;
            case 2:
                var modeRows = Get<StackPanel>(_history, "_modeRows").Children.OfType<Grid>().ToArray();
                Check(modeRows.Length == 2 && modeRows.All(row => row.Children.OfType<TextBlock>().ElementAt(2).Text!.Contains("未知", StringComparison.Ordinal)),
                    "actual history execution-mode tab visibly retains unknown rows for both models");
                var period = Get<HistoryPeriod>(_history, "_period");
                var modeDays = ExecutionModePresentation.HistoryDays(_historyHub.Days, period.Range);
                var rows = ExecutionModePresentation.Rows(modeDays, ExecutionModeKind.ServiceTier);
                Check(rows.Sum(row => row.Requests) == 12 && rows.All(row => row is { Mode: "unknown", Evidence: "unknown" }),
                    "mode history preserves exact unknown request counts without confirming Fast");
                var csv = ExecutionModePresentation.Csv(modeDays, ExecutionModeKind.ServiceTier);
                File.WriteAllBytes(Path.Combine(_root, "mode-service-tier.csv"), HistoryCsv.Encode(csv));
                Check(csv.Contains(",unknown,unknown,,", StringComparison.Ordinal) && !csv.Contains(",fast,", StringComparison.Ordinal),
                    "production mode CSV retains unknown evidence and contains no configured Fast bucket");
                Get<ComboBox>(_history, "_modeKind").SelectedIndex = 1;
                Check(Get<string>(_history, "ExecutionModesSummary").Contains("accelerator/unknown/unknown", StringComparison.Ordinal),
                    "actual mode dimension control selects independent unknown accelerator usage");
                Get<ComboBox>(_history, "_modeKind").SelectedIndex = 0;
                Find<ComboBox>(_settings, "SettingsCurrency").SelectedItem = "CNY";
                Find<ComboBox>(_settings, "SettingsAppearance").SelectedIndex = 1;
                Next();
                break;
            case 3:
                if (_application.ActualThemeVariant != ThemeVariant.Light || Get<bool>(_controller, "IsDark")) return;
                var chart = Get<object>(_history, "_chart");
                var conversion = MoneyPresentation.ConversionRate(_cost.Currency, _cost.Rates, _cost.CurrencyTable)!.Value;
                _cny = Get<TextBlock[]>(_history, "_values")[4].Text ?? "";
                Check(Settings.Currency == "CNY" && _cost.CurrencyCode == "CNY" && _cny != "—" && _cny != _usd,
                    "actual currency selector updates shared service and already-open history money");
                Check(Get<string>(chart, "MoneyCurrency") == "CNY" && Math.Abs(Get<double>(chart, "MoneyFactor") - (double)conversion) < 1e-9,
                    "actual CNY chart axis and numeric conversion factor match the selected rate");
                Check(_background.Color == Color.Parse("#F4F5F8") && _dashboard.ActualThemeVariant == ThemeVariant.Light
                    && _settings.ActualThemeVariant == ThemeVariant.Light && _history.ActualThemeVariant == ThemeVariant.Light,
                    "production Light setting changes actual app/window theme variants and mutable palette");
                Capture(_dashboard, "dashboard-light.png");
                Capture(_settings, "settings-light.png");
                ScrollToBottom(_history);
                Next();
                break;
            case 4:
                Capture(_history, "history-modes-light-cny.png");
                Find<TabControl>(_history, "HistoryDetailTabs").SelectedIndex = 1;
                Get<TextBox>(_history, "_modelFilter").Text = "ASTRA";
                Next();
                break;
            case 5:
                Check(Get<int>(_history, "VisibleModelCount") == 1, "production history model filter remains functional with CNY");
                var cells = ((Grid)Get<StackPanel>(_history, "_modelRows").Children.Single()).Children.OfType<TextBlock>().Select(text => text.Text ?? "").ToArray();
                Check(cells[0] == "gpt-6-astra" && Digits(cells[1]) == "42042" && Digits(cells[2]) == "4218"
                    && Digits(cells[3]) == "450" && Digits(cells[4]) == "6" && cells[6] != "—",
                    "production CNY model table retains exact token counters and available model cost");
                Capture(_history, "history-models-light-cny.png");
                Find<ComboBox>(_settings, "SettingsAppearance").SelectedIndex = 2;
                Next();
                break;
            case 6:
                if (_application.ActualThemeVariant != ThemeVariant.Dark || !Get<bool>(_controller, "IsDark")) return;
                Check(_background.Color == Color.Parse("#191A20") && ReferenceEquals(_background,
                    Assembly.GetType("AgentSpeed.Linux.Ui")!.GetField("Background", Static)!.GetValue(null)),
                    "production Dark setting repaints the same palette brush objects");
                Capture(_dashboard, "dashboard-dark.png");
                if (_readout.LastPng.Length == 0) return;
                using (var numeric = new Bitmap(new MemoryStream(_readout.LastPng)))
                    Check(numeric.PixelSize == new PixelSize(64, 64), "production tray renderer supplies a 64x64 numeric PNG");
                File.WriteAllBytes(Path.Combine(_root, "tray-numeric-estimate.png"), _readout.LastPng);
                _trayRevision = _readout.Revision;
                Check(!ReferenceEquals(Get<TrayIcon>(_application, "_tray").Icon, Get<WindowIcon>(_application, "_defaultIcon")),
                    "actual production tray has its numeric icon rather than the default artwork");
                Find<CheckBox>(_settings, "SettingsTraySpeed").IsChecked = false;
                Stage("tray-default-requested");
                Next();
                break;
            case 7:
                if (Age < 1.5) return;
                Check(!Settings.TrayShowSpeed && ReferenceEquals(Get<TrayIcon>(_application, "_tray").Icon,
                    Get<WindowIcon>(_application, "_defaultIcon")), "actual tray checkbox restores default icon through the production one-second update path");
                Stage("tray-default");
                Find<CheckBox>(_settings, "SettingsTraySpeed").IsChecked = true;
                Find<ComboBox>(_settings, "SettingsEffects").SelectedIndex = 0;
                CompleteSyntheticRequests();
                Next();
                break;
            case 8:
                if (Age < 4 || _speed.Snapshot.GeneratingCount != 0 || _speed.Snapshot.Live.Output != 0) return;
                Check(_readout.Revision > _trayRevision && _readout.LastText == "0"
                    && !ReferenceEquals(Get<TrayIcon>(_application, "_tray").Icon, Get<WindowIcon>(_application, "_defaultIcon")),
                    "actual tray updates numeric PNG revision when live data becomes idle");
                File.WriteAllBytes(Path.Combine(_root, "tray-numeric-idle.png"), _readout.LastPng);
                Stage("tray-numeric-idle");
                Check(Settings.Effects == EffectsIntensity.Off && Get<EffectsIntensity>(_orbGauge, "Effects") == EffectsIntensity.Off
                    && !Get<bool>(_orbGauge, "AnimationTimerEnabled") && !Get<bool>(_dashboardGauge, "AnimationTimerEnabled"),
                    "actual Effects Off UI setting stops production orb and dashboard animation timers");
                Capture(_orb, "orb-effects-off-a.png");
                _staticHash = Hash("orb-effects-off-a.png");
                _frames = Get<long>(_orbGauge, "AnimationFrameCount");
                Next();
                break;
            case 9:
                if (Age < 1.5) return;
                Check(Get<long>(_orbGauge, "AnimationFrameCount") == _frames, "actual idle orb does not render continuous frames with effects off");
                Capture(_orb, "orb-effects-off-b.png");
                Check(Hash("orb-effects-off-b.png") == _staticHash, "actual Effects Off orb pixels remain stable across elapsed time");
                Find<ComboBox>(_settings, "SettingsEffects").SelectedIndex = 2;
                Find<CheckBox>(_settings, "SettingsReduceMotion").IsChecked = true;
                Next();
                break;
            case 10:
                if (Age < .8 || !Get<bool>(_controller, "EffectiveReduceMotion")) return;
                Check(Settings.ReduceMotion && Get<bool>(_orbGauge, "ReduceMotion") && !Get<bool>(_orbGauge, "AnimationTimerEnabled"),
                    "actual Reduce Motion UI resolves through the production appearance service into the orb");
                Capture(_orb, "orb-reduced-a.png");
                _staticHash = Hash("orb-reduced-a.png");
                _frames = Get<long>(_orbGauge, "AnimationFrameCount");
                Next();
                break;
            case 11:
                if (Age < 1.5) return;
                Check(Get<long>(_orbGauge, "AnimationFrameCount") == _frames, "actual Reduce Motion orb stops continuous frames");
                Capture(_orb, "orb-reduced-b.png");
                Check(Hash("orb-reduced-b.png") == _staticHash, "actual Reduce Motion orb pixels remain stable");
                Find<CheckBox>(_settings, "SettingsReduceMotion").IsChecked = false;
                Find<ComboBox>(_settings, "SettingsAppearance").SelectedIndex = 0;
                Next();
                break;
            case 12:
                if (Age < .8) return;
                var appearance = Get<LinuxAppearanceService>(_application, "_appearance");
                Check(Settings.Appearance == AppearanceMode.Auto && _application.ActualThemeVariant == appearance.ResolvedTheme
                    && Get<bool>(_controller, "IsDark") == appearance.IsDark,
                    "actual Auto selection follows the production system/platform theme resolver");
                Capture(_dashboard, "dashboard-auto.png");
                Check(!_cost.Status.NetworkEnabled && !_cost.Status.IsBusy && _cost.Status.LastPriceCheck is null && _cost.Status.LastRatesCheck is null,
                    "feature UI exercise finishes offline without initiating a public-data request");
                Check(!File.Exists(Get<string>(_controller, "LoginStartupPath")) && !Settings.StartAtLogin,
                    "actual production feature exercise never registers any login startup entry");
                Check(Get<string?>(_controller, "Error") is null, "production controller finishes all feature interactions without errors");
                ProgramType.GetProperty("Options", Static)!.SetValue(null, _options with { CaptureDirectory = Path.Combine(_root, "production-capture") });
                Invoke(_application, "Capture");
                ProgramType.GetProperty("Options", Static)!.SetValue(null, _options);
                File.WriteAllText(Path.Combine(_root, "result.json"), JsonSerializer.Serialize(new
                {
                    checks = Checks, productionApplication = _application.GetType().FullName, isolated = true,
                    pid = Environment.ProcessId, usd = _usd, cny = _cny, priceVersion = _cost.PriceBook.CurrentVersion,
                    currency = Settings.Currency, network = _cost.Status, trayRevision = _readout.Revision,
                    actualTheme = _application.ActualThemeVariant.ToString(), autoResolvedDark = appearance.IsDark,
                    fixtureOnly = true, realAutostartUsed = false,
                }, new JsonSerializerOptions { WriteIndented = true }));
                Finish(0);
                break;
        }
    }

    private static UserSettings Settings => Get<UserSettings>(_controller, "Settings");
    private static double Age => (DateTimeOffset.UtcNow - _phaseAt).TotalSeconds;
    private static void Next() { _phase++; _phaseAt = DateTimeOffset.UtcNow; }
    private static void Check(bool condition, string text)
    {
        if (!condition) throw new InvalidOperationException(text);
        Checks.Add(text); Console.WriteLine("PASS " + text);
    }
    private static void Stage(string stage) => File.WriteAllText(Path.Combine(_root, "stage.json"), JsonSerializer.Serialize(new { stage, pid = Environment.ProcessId }));
    private static T Get<T>(object instance, string name) => (T)(instance.GetType().GetProperty(name, Instance)?.GetValue(instance)
        ?? instance.GetType().GetField(name, Instance)?.GetValue(instance))!;
    private static void Invoke(object instance, string name) => instance.GetType().GetMethod(name, Instance)!.Invoke(instance, null);
    private static T Find<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static string Digits(string value) => string.Concat(value.Where(char.IsAsciiDigit));
    private static string Hash(string name) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_root, name))));
    private static void ScrollToBottom(Window window)
    {
        var scroll = ((Grid)window.Content!).Children.OfType<ScrollViewer>().Single();
        scroll.Offset = new Vector(0, scroll.Extent.Height);
    }
    private static void Capture(Window window, string name)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height)));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(_root, name), PngBitmapEncoderOptions.Default);
    }
    private static void Finish(int code)
    {
        if (_finished) return;
        _finished = true; _timer.Stop();
        if (code == 0) Invoke(_application, "Quit");
        else ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).Shutdown(code);
    }
    private static void CompleteSyntheticRequests()
    {
        var line = JsonSerializer.Serialize(new { timestamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            type = "event_msg", payload = new { type = "task_complete" } });
        foreach (var file in FixtureFiles) File.AppendAllText(file, line + "\n");
    }
    private static void WriteSyntheticSessions()
    {
        var now = DateTimeOffset.UtcNow;
        var directory = Path.Combine(_sessions, DateTime.Now.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        for (var i = 1; i <= 12; i++)
        {
            var records = new List<string>();
            void Add(string kind, object payload, int seconds) => records.Add(JsonSerializer.Serialize(new
            { timestamp = now.AddSeconds(seconds).ToString("O", CultureInfo.InvariantCulture), type = kind, payload }));
            Add("session_meta", new { id = "feature58-" + i, cwd = "/synthetic/feature58-project-" + i, source = "cli" }, -30);
            Add("turn_context", new { model = i % 2 == 0 ? "gpt-6-astra" : "gpt-6.1-sol" }, -25);
            Add("event_msg", new { type = "task_started" }, -20);
            var input = i * 1000 + 7; var output = i * 10 + 5;
            Add("token_usage_record", new { thread_id = "feature58-" + i, response_id = "feature58-response-" + i,
                usage = new { input_tokens = input, cached_input_tokens = i * 100 + 3, output_tokens = output,
                    reasoning_output_tokens = 0, total_tokens = input + output } }, -18);
            Add("event_msg", new { type = "task_complete" }, -17);
            Add("event_msg", new { type = "task_started" }, -8);
            var file = Path.Combine(directory, "rollout-feature58-" + i + ".jsonl");
            File.WriteAllLines(file, records); FixtureFiles.Add(file);
        }
    }
}
