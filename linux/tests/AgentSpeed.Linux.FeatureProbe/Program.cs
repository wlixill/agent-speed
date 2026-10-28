using System.Globalization;
using System.Reflection;
using System.Text.Json;
using AgentSpeed.Core;
using AgentSpeed.Linux;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.X11;

if (args.Length != 1) throw new ArgumentException("Usage: FeatureProbe ISOLATED_OUTPUT_DIRECTORY");
FeatureProbeApplication.Root = Path.GetFullPath(args[0]);
return AppBuilder.Configure<FeatureProbeApplication>().UsePlatformDetect()
    .With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Software] })
    .StartWithClassicDesktopLifetime(args);

public sealed class FeatureProbeApplication : Application
{
    public static string Root { get; set; } = "";
    private static readonly BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Directory.CreateDirectory(Root);
        var sessions = Path.Combine(Root, "Codex 数据 %U", "sessions");
        WriteSyntheticSessions(sessions);
        var data = Path.Combine(Root, "data");
        var storage = new AppStorage(data);
        storage.Save(new UserSettings(OrbVisible: false));
        var options = new LaunchOptions(CodexDirectory: sessions, DataDirectory: data);
        var startup = new LoginStartupService(options, storage);
        var assembly = typeof(AppStorage).Assembly;
        var controllerType = assembly.GetType("AgentSpeed.Linux.SpeedController")!;
        var controller = Activator.CreateInstance(controllerType, options)!;
        var noop = (Action)(() => { });
        var dashboard = (Window)Activator.CreateInstance(assembly.GetType("AgentSpeed.Linux.DashboardWindow")!, controller, noop, noop, noop, noop)!;
        var history = (Window)Activator.CreateInstance(assembly.GetType("AgentSpeed.Linux.HistoryWindow")!, controller)!;
        var settings = (Window)Activator.CreateInstance(assembly.GetType("AgentSpeed.Linux.SettingsWindow")!, controller)!;
        var speed = Get<SpeedHub>(controller, "Speed");
        var historyHub = Get<HistoryHub>(controller, "History");
        desktop.MainWindow = dashboard;
        dashboard.Show(); history.Show(); settings.Show();
        controllerType.GetMethod("Start")!.Invoke(controller, null);
        var phase = 0;
        var started = DateTimeOffset.UtcNow;
        var checks = new List<string>();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            checks.Add(label);
            Console.WriteLine("PASS " + label);
        }
        void Finish(int code)
        {
            timer.Stop();
            settings.Close(); history.Close(); dashboard.Close();
            ((IDisposable)controller).Dispose();
            desktop.Shutdown(code);
        }
        timer.Tick += (_, _) =>
        {
            try
            {
                if (DateTimeOffset.UtcNow - started > TimeSpan.FromSeconds(40))
                    throw new TimeoutException("Waiting for synthetic live/history records");
                switch (phase)
                {
                    case 0:
                        if (speed.Snapshot.State.Kind != MonitorStateKind.Running
                            || speed.Snapshot.Agents.Count != 12 || historyHub.State.Kind != HistoryStateKind.Ready) return;
                        Check(Get<int>(dashboard, "DisplayedAgentCount") == 12
                            && Get<StackPanel>(dashboard, "_agents").Children.Count == 12,
                            "actual dashboard renders all 12 agents");
                        Check(speed.Snapshot.Today.Input == 78_084 && speed.Snapshot.Today.CachedInput == 7_836
                            && speed.Snapshot.Today.Output == 840 && speed.Snapshot.Today.Requests == 12,
                            "synthetic live usage matches independently known token totals");
                        Check(Math.Abs(speed.Snapshot.Live.Output - 420) < .001
                            && Math.Abs(speed.Snapshot.Live.Total - 39_462) < .001,
                            "12 sessions have known output and total reference rates");
                        Check(!startup.IsEnabled && !File.Exists(startup.EntryPath), "login startup defaults off without creating entry");
                        // Fail the settings commit deterministically without changing permissions or user files.
                        Directory.CreateDirectory(storage.SettingsPath + ".tmp");
                        Find<CheckBox>(settings, "SettingsLogin").IsChecked = true;
                        Check(!File.Exists(startup.EntryPath) && !Get<UserSettings>(controller, "Settings").StartAtLogin
                            && Find<CheckBox>(settings, "SettingsLogin").IsChecked == false,
                            "failed settings commit cannot enable login startup or leave the checkbox checked");
                        Directory.Delete(storage.SettingsPath + ".tmp");
                        Find<CheckBox>(settings, "SettingsLogin").IsChecked = true;
                        Check(startup.IsEnabled && File.ReadAllText(startup.EntryPath).Contains(" --background "),
                            "actual login checkbox creates an isolated background entry");
                        Check(startup.EntryPath.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                            "login checkbox cannot touch the real user autostart directory");
                        var originalEntry = File.ReadAllText(startup.EntryPath);
                        Directory.CreateDirectory(storage.SettingsPath + ".tmp");
                        Find<CheckBox>(settings, "SettingsBackground").IsChecked = false;
                        Check(File.ReadAllText(startup.EntryPath) == originalEntry
                            && Get<UserSettings>(controller, "Settings").StartInBackground
                            && Find<CheckBox>(settings, "SettingsBackground").IsChecked == true,
                            "failed background preference save preserves the existing login command and UI");
                        Directory.Delete(storage.SettingsPath + ".tmp");
                        Find<CheckBox>(settings, "SettingsBackground").IsChecked = false;
                        Check(File.ReadAllText(startup.EntryPath).Contains(" --dashboard ")
                            && !Get<UserSettings>(controller, "Settings").StartInBackground,
                            "background checkbox updates the actual login command and preference");
                        Find<CheckBox>(settings, "SettingsBackground").IsChecked = true;
                        Check(File.ReadAllText(startup.EntryPath).Contains(" --background "),
                            "background preference restores the background login command");
                        var originalSettings = File.ReadAllText(storage.SettingsPath);
                        originalEntry = File.ReadAllText(startup.EntryPath);
                        var invalidSource = Path.Combine(Root, "invalid\nsource", "sessions");
                        Directory.CreateDirectory(invalidSource);
                        Get<TextBox>(settings, "_directory").Text = invalidSource;
                        settings.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "应用会话目录"))
                            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Check(File.ReadAllText(storage.SettingsPath) == originalSettings
                            && File.ReadAllText(startup.EntryPath) == originalEntry
                            && Get<string>(controller, "CodexDirectory") == sessions
                            && Get<LiveMonitor>(controller, "_monitor").Configuration.CodexRoot == sessions
                            && Get<MonitorConfiguration>(Get<HistoryService>(controller, "_history"), "_configuration").CodexRoot == sessions
                            && Get<string?>(controller, "Error") is not null,
                            "failed login-entry update rolls back source settings before either monitor changes");
                        Directory.Delete(Path.GetDirectoryName(invalidSource)!, recursive: true);
                        Get<TextBox>(settings, "_directory").Text = sessions;
                        Find<ComboBox>(settings, "SettingsMetric").SelectedIndex = 1;
                        Check(Get<UserSettings>(controller, "Settings").Metric == SpeedMetric.Total
                            && Find<ComboBox>(dashboard, "DashboardMetric").SelectedIndex == 1,
                            "settings metric selector updates controller and dashboard selector");
                        Find<ComboBox>(settings, "SettingsRedline").SelectedItem = 200_000d;
                        Check(Get<double>(controller, "Redline") == 200_000
                            && Get<UserSettings>(controller, "Settings").Redline == 400,
                            "total-throughput redline saves independently from output redline");
                        Check(Math.Abs(Get<SpeedSnapshot>(controller, "DisplaySnapshot").Live.Output - 39_462) < .001,
                            "actual dashboard projects input plus output throughput");
                        phase = 1;
                        break;
                    case 1:
                        Capture(dashboard, "dashboard-total.png");
                        Check(Get<TextBlock>(dashboard, "_speed").Text?.StartsWith("≈", StringComparison.Ordinal) == true,
                            "actual live readout visibly labels its historical estimate");
                        Find<ComboBox>(dashboard, "DashboardMetric").SelectedIndex = 0;
                        Check(Get<UserSettings>(controller, "Settings").Metric == SpeedMetric.Output
                            && Find<ComboBox>(settings, "SettingsMetric").SelectedIndex == 0
                            && Get<double>(controller, "Redline") == 400,
                            "dashboard selector restores output metric and its own redline");
                        Find<ComboBox>(settings, "SettingsRedline").SelectedItem = 800d;
                        Find<ComboBox>(dashboard, "DashboardMetric").SelectedIndex = 1;
                        Check(Get<double>(controller, "Redline") == 200_000
                            && Get<UserSettings>(controller, "Settings").Redline == 800,
                            "both metric redlines survive repeated UI switching");
                        Find<Slider>(settings, "SettingsOrbSize").Value = 180;
                        Find<Slider>(settings, "SettingsOrbOpacity").Value = .65;
                        Find<CheckBox>(settings, "SettingsClickThrough").IsChecked = true;
                        Find<CheckBox>(settings, "SettingsSnap").IsChecked = false;
                        Find<Button>(settings, "ApplyOrbSettings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        var saved = storage.Load();
                        Check(saved.OrbSize == 180 && Math.Abs(saved.OrbOpacity - .65) < .001
                            && saved.OrbClickThrough && !saved.OrbSnapToEdges,
                            "actual apply button persists orb settings from its controls");
                        Find<CheckBox>(settings, "SettingsLogin").IsChecked = false;
                        Check(!File.Exists(startup.EntryPath) && !Get<UserSettings>(controller, "Settings").StartAtLogin,
                            "actual login checkbox disables and removes only its isolated entry");
                        Find<TabControl>(history, "HistoryDetailTabs").SelectedIndex = 1;
                        Find<TextBox>(history, "ModelFilter").Text = "ASTRA";
                        phase = 2;
                        break;
                    case 2:
                        Capture(settings, "settings.png");
                        Check(Get<int>(history, "VisibleModelCount") == 1, "actual model-name filter is case-insensitive");
                        var row = (Grid)Get<StackPanel>(history, "_modelRows").Children.Single();
                        var cells = row.Children.OfType<TextBlock>().Select(block => block.Text ?? "").ToArray();
                        Check(cells[0] == "gpt-6-astra" && Digits(cells[1]) == "42042"
                            && Digits(cells[2]) == "4218" && Digits(cells[3]) == "450" && Digits(cells[4]) == "6",
                            "actual model table displays exact input, cache, output and request counters");
                        Find<TextBox>(history, "ModelFilter").Text = "";
                        Find<ComboBox>(history, "ModelSort").SelectedIndex = 1;
                        Find<CheckBox>(history, "ModelSortAscending").IsChecked = true;
                        Check(Get<int>(history, "VisibleModelCount") == 2
                            && ((Grid)Get<StackPanel>(history, "_modelRows").Children[0]).Children.OfType<TextBlock>().First().Text == "gpt-6.1-sol",
                            "actual model input sort orders rows numerically");
                        var outerScroll = ((Grid)history.Content!).Children.OfType<ScrollViewer>().Single();
                        outerScroll.Offset = new Vector(0, outerScroll.Extent.Height);
                        phase = 3;
                        break;
                    case 3:
                        Capture(history, "models.png");
                        var error = Get<string?>(controller, "Error");
                        Check(error is null, "UI interactions finish without controller errors");
                        File.WriteAllText(Path.Combine(Root, "result.json"), JsonSerializer.Serialize(new
                        {
                            checks, fixtureOnly = true, sessions = 12, input = 78_084, cached = 7_836, output = 840,
                            models = Get<string>(history, "ModelStatisticsSummary"), settings = storage.Load(),
                            realAutostartUsed = false,
                        }, new JsonSerializerOptions { WriteIndented = true }));
                        Finish(0);
                        break;
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                Finish(1);
            }
        };
        timer.Start();
        base.OnFrameworkInitializationCompleted();
    }

    private static string Digits(string text) => string.Concat(text.Where(char.IsAsciiDigit));

    private static T Get<T>(object instance, string name)
    {
        var type = instance.GetType();
        var value = type.GetProperty(name, Members)?.GetValue(instance)
            ?? type.GetField(name, Members)?.GetValue(instance);
        return (T)value!;
    }

    private static T Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static void Capture(Window window, string name)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height)));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(Root, name), PngBitmapEncoderOptions.Default);
    }

    private static void WriteSyntheticSessions(string sessions)
    {
        var now = DateTimeOffset.UtcNow;
        var directory = Path.Combine(sessions, DateTime.Now.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);
        for (var i = 1; i <= 12; i++)
        {
            var records = new List<string>();
            void Add(string kind, object payload, int seconds) => records.Add(JsonSerializer.Serialize(new
            {
                timestamp = now.AddSeconds(seconds).ToString("O", CultureInfo.InvariantCulture), type = kind, payload,
            }));
            Add("session_meta", new { id = "feature-probe-" + i, cwd = "/synthetic/project-" + i, source = "cli" }, -30);
            Add("turn_context", new { model = i % 2 == 0 ? "gpt-6-astra" : "gpt-6.1-sol" }, -25);
            Add("event_msg", new { type = "task_started" }, -20);
            var input = i * 1000 + 7;
            var output = i * 10 + 5;
            Add("token_usage_record", new { thread_id = "feature-probe-" + i, response_id = "feature-response-" + i,
                usage = new { input_tokens = input, cached_input_tokens = i * 100 + 3, output_tokens = output,
                    reasoning_output_tokens = 0, total_tokens = input + output } }, -18);
            Add("event_msg", new { type = "task_complete" }, -17);
            Add("event_msg", new { type = "task_started" }, -8);
            File.WriteAllLines(Path.Combine(directory, "rollout-feature-" + i + ".jsonl"), records);
        }
    }
}
