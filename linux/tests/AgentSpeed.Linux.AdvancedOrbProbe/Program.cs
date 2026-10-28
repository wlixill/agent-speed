using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSpeed.Core;
using AgentSpeed.Linux;
using AgentSpeed.Presentation.Settings.Orbs;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.X11;

if (args.Length != 1) throw new ArgumentException("Usage: AdvancedOrbProbe ISOLATED_DIRECTORY");
Advanced.Prepare(Path.GetFullPath(args[0]));
var exit = AppBuilder.Configure(Advanced.Create).UsePlatformDetect()
    .With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Software], WmClass = "agent-speed-advanced-orb-probe" })
    .AfterSetup(_ => Advanced.Schedule()).StartWithClassicDesktopLifetime(args);
return Advanced.Failed ? 1 : exit;

internal static class Advanced
{
    private static readonly BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type Fixture = Assembly.Load("AgentSpeed.Linux.AppFeature912Probe").GetType("Production912")!;
    private static readonly List<string> Checks = [];
    private static string _root = "", _first = "";
    private static Application _app = null!;
    private static object _controller = null!, _manager = null!;
    private static Window _management = null!;
    private static DispatcherTimer _timer = null!;
    private static int _phase;
    private static DateTimeOffset _started, _phaseAt;
    private static Dictionary<string, PixelPoint> _beforeArrange = [];
    private static bool _finished;
    public static bool Failed { get; private set; }

    public static void Prepare(string root)
    {
        _root = root; Directory.CreateDirectory(root);
        Fixture.GetMethod("Prepare")!.Invoke(null, [root, "multi"]);
        var codex = Path.Combine(root, "Codex synthetic", "sessions");
        var claude = Path.Combine(root, "Claude synthetic", "projects");
        var gemini = Path.Combine(root, "Gemini synthetic", "tmp");
        // Complete the fixture's deliberately in-flight final turns, allowing an actual elapsed idle-fade check.
        foreach (var path in new[] { Path.Combine(codex, "rollout-feature912.jsonl"), Path.Combine(claude, "-synthetic-feature912", "feature912-claude.jsonl") })
            File.WriteAllLines(path, File.ReadAllLines(path).SkipLast(1));
        var geminiPath = Directory.GetFiles(gemini, "session-*.json", SearchOption.AllDirectories).Single();
        var node = JsonNode.Parse(File.ReadAllText(geminiPath))!;
        node["messages"]!.AsArray().RemoveAt(2);
        File.WriteAllText(geminiPath, node.ToJsonString());
        new AppStorage(Path.Combine(root, "data")).Save(new UserSettings
        {
            EnabledSources = [SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.Gemini],
            SourceDirectories = new() { [SourceKind.Codex] = codex, [SourceKind.ClaudeCode] = claude, [SourceKind.Gemini] = gemini },
        });
    }
    public static Application Create() => (Application)Fixture.GetMethod("CreateApplication")!.Invoke(null, null)!;
    public static void Schedule()
    {
        _started = _phaseAt = DateTimeOffset.UtcNow;
        _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) =>
        {
            try { if (_finished) return; if (DateTimeOffset.UtcNow - _started > TimeSpan.FromSeconds(70)) throw new TimeoutException("Advanced orb phase " + _phase); Step(); }
            catch (Exception error) { File.WriteAllText(Path.Combine(_root, "failure.txt"), error.ToString()); Console.Error.WriteLine(error); Finish(true); }
        };
        _timer.Start();
    }
    private static void Step()
    {
        switch (_phase)
        {
            case 0:
                _app = Application.Current!; _controller = Get<object>(_app, "_controller"); _manager = Get<object>(_app, "_orbManager");
                var speed = Get<SpeedHub>(_controller, "Speed").Snapshot;
                if (speed.State.Kind != MonitorStateKind.Running || speed.Sources.Count(source => source.Enabled && source.Available) != 3) return;
                Check(Definitions.Count == 1 && Definitions[0].AlwaysOnTop && !Definitions[0].FadeWhenIdle && Definitions[0].ShowName, "legacy orb keeps visible/topmost defaults and receives safe advanced options");
                _first = Definitions[0].Id;
                Call(_manager, "OpenManagementWindow"); _management = Get<Window>(_manager, "_management");
                Find<ComboBox>(_management, "OrbUnit").SelectedIndex = (int)OrbUnit.Cost;
                Find<ComboBox>(_management, "OrbTraffic").SelectedIndex = (int)OrbTraffic.Machine;
                Find<TextBox>(_management, "OrbRedlineOutput").Text = "900";
                Find<TextBox>(_management, "OrbRedlineTotal").Text = "150000";
                Find<CheckBox>(_management, "OrbAlwaysOnTop").IsChecked = false;
                Find<CheckBox>(_management, "OrbShowName").IsChecked = false;
                Click("SaveManagedOrb");
                Check(Definitions[0].Unit == OrbUnit.Cost && Definitions[0].RedlineOutput == 900 && Definitions[0].RedlineTotal == 150000, "real management controls persist per-window cost unit and both independent scales");
                Check(!Get<bool>(Orb(_first), "Topmost") && !Get<bool>(Orb(_first), "NameVisible"), "real window updates topmost and name visibility immediately");
                Check(Get<double>(Orb(_first), "EffectiveRedline") == 900 && !Get<string>(Orb(_first), "ReadingText").Contains("tok/s"), "orb displays money as primary value while heat still uses token scale");
                Call(_controller, "SetMetric", SpeedMetric.Total);
                Check(Get<double>(Orb(_first), "EffectiveRedline") == 150000, "total metric resolves this orb's own total scale");
                Call(_controller, "SetMetric", SpeedMetric.Output);
                Click("SplitManagedOrb");
                Check(Definitions.Count == 3 && Definitions.All(orb => orb.Sources?.Length == 1), "actual split button partitions three available source parsers");
                Check(Definitions.All(orb => orb.Unit == OrbUnit.Cost && orb.RedlineOutput == 900 && !orb.AlwaysOnTop), "split inherits per-window display options");
                Check(Clear(), "newly split windows avoid overlap including readout/name rectangles");
                Next(); break;
            case 1:
                Check(ReadoutFits(Orb(_first), "_costText"), "money and token secondary readings fit completely inside the actual orb window");
                Capture("cost-orb", Orb(_first));
                Click("MergeAllManagedOrbs");
                Check(Definitions.Count == 1 && Definitions[0].Sources is null && Definitions[0].Unit == OrbUnit.Cost, "actual merge-all preserves target options and follows every enabled source");
                Click("UndoManagedOrb");
                Check(Definitions.Count == 3, "actual undo restores split windows after merge");
                var target = Find<ComboBox>(_management, "OrbMergeTarget"); target.SelectedIndex = 0;
                Click("MergeSelectedOrb");
                Check(Definitions.Count == 2 && Definitions.Any(orb => orb.Sources?.Length == 2), "selected-target merge leaves the third source window independent");
                Click("UndoManagedOrb");
                Check(Definitions.Count == 3, "undo restores a selected-target merge");
                Click("DuplicateManagedOrb");
                Check(Definitions.Count == 4 && Definitions.Select(orb => orb.Id).Distinct().Count() == 4, "actual duplicate creates fresh window identity");
                var common = new PixelPoint(100, 100);
                foreach (var orb in Windows) Call(orb, "MoveToPosition", common);
                Check(!Clear(), "test deliberately places windows on the same coordinates before arranging");
                _beforeArrange = Windows.ToDictionary(orb => Get<LinuxOrbDefinition>(orb, "Definition").Id, orb => Get<PixelPoint>(orb, "ClientPosition"));
                Click("ArrangeManagedOrbs");
                Check(Clear(), "arrange button finds non-overlapping on-screen positions for all four readouts");
                Click("UndoManagedOrb");
                Check(Windows.All(orb => _beforeArrange[Get<LinuxOrbDefinition>(orb, "Definition").Id] == Get<PixelPoint>(orb, "ClientPosition")), "undo restores exact pre-arrangement positions on already open windows");
                Click("ArrangeManagedOrbs");
                Capture("management", _management);
                var settings = Get<Window>(_app, "_settingsWindow");
                Find<CheckBox>(settings, "SettingsTrafficEnabled").IsChecked = true;
                Next(); break;
            case 2:
                if (Get<LinuxTrafficService>(_controller, "Traffic").Snapshot.MachineLive is null) return;
                Check(Windows.All(orb => Get<string>(orb, "TrafficText").Contains("网卡") && Get<string>(orb, "TrafficText").Contains('↑')), "enabled machine traffic is actually rendered on every copied orb");
                Check(Windows.All(orb => ReadoutFits(orb, "_trafficText")), "both traffic lines fit completely beneath every actual orb readout");
                Call(_manager, "OpenManagementWindow");
                // Select the first orb through the real list, rather than assigning a model directly.
                var list = Find<ComboBox>(_management, "ManagedOrbs"); list.SelectedIndex = 0;
                Find<TextBox>(_management, "OrbRedlineOutput").Text = "NaN"; Click("SaveManagedOrb");
                Check(Definitions[0].RedlineOutput == 900, "invalid redline input cannot overwrite a valid stored per-window scale");
                Find<TextBox>(_management, "OrbRedlineOutput").Text = "";
                Find<TextBox>(_management, "OrbRedlineTotal").Text = "";
                Find<CheckBox>(_management, "OrbFadeWhenIdle").IsChecked = true;
                Find<CheckBox>(_management, "OrbShowName").IsChecked = true;
                Click("SaveManagedOrb");
                Check(Definitions[0].RedlineOutput is null && Get<double>(Orb(_first), "EffectiveRedline") == Settings.Redline, "blank scale restores the global fallback without changing global settings");
                Call(Orb(_first), "MoveToPosition", new PixelPoint(0, 40));
                _management.Hide(); Next(); break;
            case 3:
                if (DateTimeOffset.UtcNow - _phaseAt < TimeSpan.FromSeconds(11)) return;
                Check(Get<bool>(Orb(_first), "IdleFadeApplied") && Get<double>(Orb(_first), "WindowOpacity") == Definitions[0].Opacity * .4, "real elapsed idle interval applies the configured fade without stopping live monitoring");
                Check(Get<bool>(Orb(_first), "NameVisible"), "name display can be restored independently from the main reading");
                Capture("idle-faded-orb", Orb(_first));
                Call(_manager, "OpenManagementWindow");
                Find<CheckBox>(_management, "OrbFadeWhenIdle").IsChecked = false; Click("SaveManagedOrb");
                Check(!Get<bool>(Orb(_first), "IdleFadeApplied") && Get<double>(Orb(_first), "WindowOpacity") == Definitions[0].Opacity, "disabling idle fade restores configured opacity immediately");
                var actual = (Window)Orb(_first);
                var labels = actual.ContextMenu!.Items.OfType<MenuItem>().Select(item => item.Header?.ToString()).ToArray();
                Check(labels.Contains("按来源拆分") && labels.Contains("合并所有悬浮窗到这里") && labels.Contains("撤销上一步悬浮窗操作"), "each real orb exposes advanced management actions in its context menu");
                Check(!Settings.EnablePriceUpdates && !Settings.AutoCheckUpdates && !Get<LinuxReportService>(_controller, "Reporting").Enabled, "advanced orb exercise does not enable external network services");
                File.WriteAllText(Path.Combine(_root, "result.json"), JsonSerializer.Serialize(new { checks = Checks, orbs = Definitions, settings = Settings, synthetic = true }, new JsonSerializerOptions { WriteIndented = true }));
                Finish(false); break;
        }
    }
    private static UserSettings Settings => Get<UserSettings>(_controller, "Settings");
    private static IReadOnlyList<LinuxOrbDefinition> Definitions => Get<IReadOnlyList<LinuxOrbDefinition>>(_controller, "OrbDefinitions");
    private static IReadOnlyList<object> Windows => Get<System.Collections.IEnumerable>(_manager, "Windows").Cast<object>().ToArray();
    private static object Orb(string id) => Windows.Single(orb => Get<LinuxOrbDefinition>(orb, "Definition").Id == id);
    private static bool Clear()
    {
        var occupied = new List<OrbObstacle>();
        foreach (var window in Windows)
        {
            var point = Get<PixelPoint>(window, "ClientPosition"); var size = Get<PixelSize>(window, "ClientPixelSize");
            if (OrbWindowArrangement.Overlaps(point, size, occupied)) return false;
            occupied.Add(new(Get<LinuxOrbDefinition>(window, "Definition").Id, new(point, size)));
        }
        return true;
    }
    private static bool ReadoutFits(object value, string name)
    {
        var window = (Window)value; var block = Get<TextBlock>(value, name);
        var bottom = block.TranslatePoint(new Point(0, block.Bounds.Height), window);
        return bottom is { } point && point.Y <= window.Bounds.Height + 1;
    }
    private static void Next() { _phase++; _phaseAt = DateTimeOffset.UtcNow; }
    private static T Get<T>(object target, string name)
    {
        var type = target.GetType(); return (T)(type.GetProperty(name, Flags)?.GetValue(target) ?? type.GetField(name, Flags)!.GetValue(target))!;
    }
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethods(Flags).Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(target, args);
    private static T Find<T>(Window window, string name) where T : Control => window.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
    private static void Click(string name) => Find<Button>(_management, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); Checks.Add(message); Console.WriteLine("PASS " + message); }
    private static void Capture(string name, object value)
    {
        var window = (Window)value; if (!window.IsVisible) window.Show();
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height)));
        bitmap.Render(window); bitmap.Save(Path.Combine(_root, name + ".png"), PngBitmapEncoderOptions.Default);
    }
    private static void Finish(bool failed)
    {
        _finished = true; Failed = failed; _timer.Stop();
        if (_app is not null) _app.GetType().GetMethod("Quit", Flags)!.Invoke(_app, null);
        else ((IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!).Shutdown(failed ? 1 : 0);
    }
}
