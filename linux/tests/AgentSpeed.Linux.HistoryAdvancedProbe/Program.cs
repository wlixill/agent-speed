using System.Globalization;
using System.Reflection;
using System.Text.Json;
using AgentSpeed.Core;
using AgentSpeed.Core.Traffic;
using AgentSpeed.Linux;
using AgentSpeed.Presentation.History;
using AgentSpeed.Presentation.Settings;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.X11;

if (args.Length != 1 || Environment.GetEnvironmentVariable("AGENT_SPEED_HISTORY_PROBE_ISOLATED") != "1")
    throw new ArgumentException("Use history-advanced-smoke.py, which isolates the clipboard on its own X11 display.");
HistoryProbe.Root = Path.GetFullPath(args[0]);
return AppBuilder.Configure<HistoryProbe>().UsePlatformDetect()
    .With(new X11PlatformOptions { RenderingMode = [X11RenderingMode.Software] }).StartWithClassicDesktopLifetime(args);

public sealed class HistoryProbe : Application
{
    public static string Root = "";
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly List<string> _checks = [];
    private Window _history = null!;
    private object _controller = null!;
    private int _phase;
    private bool _busy;
    private DateTimeOffset _started;
    private readonly DateOnly _today = DateOnly.FromDateTime(DateTime.Now);
    private DispatcherTimer _timer = null!;
    private IClassicDesktopStyleApplicationLifetime _desktop = null!;

    public override void Initialize() { Styles.Add(new FluentTheme()); RequestedThemeVariant = ThemeVariant.Dark; }
    public override void OnFrameworkInitializationCompleted()
    {
        _desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        _desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Directory.CreateDirectory(Root);
        var sessions = Path.Combine(Root, "empty-sessions"); Directory.CreateDirectory(sessions);
        new AppStorage(Path.Combine(Root, "data")).Save(new UserSettings
        {
            EnabledSources = [SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.Gemini], Currency = "CNY", EnablePriceUpdates = false,
            CostSettings = new CostSettings { Currency = "CNY", Subscriptions = SourceSet.Of(SourceKind.Codex) }, OrbVisible = false,
        });
        var assembly = typeof(AppStorage).Assembly;
        _controller = Activator.CreateInstance(assembly.GetType("AgentSpeed.Linux.SpeedController")!,
            new LaunchOptions(DataDirectory: Path.Combine(Root, "data"), CodexDirectory: sessions))!;
        var historyHub = Get<HistoryHub>(_controller, "History"); historyHub.RefreshHandler = null;
        _history = (Window)Activator.CreateInstance(assembly.GetType("AgentSpeed.Linux.HistoryWindow")!, _controller)!;
        _desktop.MainWindow = _history; _history.Show();
        historyHub.Publish(HistoryState.Ready(DateTimeOffset.Now), [Usage(_today, SourceKind.Codex, 100, 1_000_000),
            Usage(_today, SourceKind.ClaudeCode, 300, 4_000_000), Usage(_today.AddDays(-1), SourceKind.Gemini, 50, 2_000_000)]);
        Get<LinuxTrafficService>(_controller, "Traffic").Hub.PublishDays([
            new(_today.ToString("yyyy-MM-dd"), "codex", 100, 200, 10, 20, 60_000),
            new(_today.ToString("yyyy-MM-dd"), "claudeCode", 300, 400, 30, 40, 60_000),
            new(_today.ToString("yyyy-MM-dd"), "machine", 1000, 2000, 0, 0, 60_000)], _today.ToString("yyyy-MM-dd"));
        _started = DateTimeOffset.Now;
        _timer = new() { Interval = TimeSpan.FromMilliseconds(350) };
        _timer.Tick += async (_, _) =>
        {
            if (_busy) return; _busy = true;
            try { if (DateTimeOffset.Now - _started > TimeSpan.FromSeconds(30)) throw new TimeoutException("History UI probe timed out"); await Run(); }
            catch (Exception error) { Console.Error.WriteLine(error); Finish(1); }
            finally { _busy = false; }
        };
        _timer.Start(); base.OnFrameworkInitializationCompleted();
    }

    private async Task Run()
    {
        var period = Get<HistoryPeriod?>(_history, "_period");
        if (period?.Totals.Requests != 3 && _phase == 0) return;
        switch (_phase++)
        {
            case 0:
                Check(period!.Totals.Output == 450 && period.Totals.Cost.MicroUsd == 6_000_000, "actual history excludes Codex subscription only from displayed cost");
                Check(period.SourceDays.Count == 3 && Get<int>(Get<object>(_history, "_chart"), "VisibleSegmentCount") >= 3, "actual history renders three colored source stacks");
                Check(Get<WrapPanel>(_history, "_legend").Children.Count == 3, "actual source legend exposes each source");
                Capture("stacked-cny");
                Find<CheckBox>("HistoryIncludeSubscriptions").IsChecked = true;
                break;
            case 1:
                Check(Get<HistoryPeriod>(_history, "_period").Totals.Cost.MicroUsd == 7_000_000, "actual include-subscriptions control restores all API-equivalent costs");
                Check(Get<UserSettings>(_controller, "Settings").CostSettings.HistoryIncludeSubscriptions == true, "history subscription preference persists through controller");
                Find<ComboBox>("HistoryDailySort").SelectedIndex = 1;
                Find<CheckBox>("HistoryDailyAscending").IsChecked = true;
                Check(Get<IReadOnlyList<DaySummary>>(_history, "_visibleDays")[0].Output == 50, "actual daily ascending sort orders exact output totals");
                Find<CheckBox>("HistoryDailyAscending").IsChecked = false;
                Check(Get<IReadOnlyList<DaySummary>>(_history, "_visibleDays")[0].Output == 400, "actual daily descending sort reverses known output values");
                Click(Find<Button>("HistoryCopyDaily"));
                break;
            case 2:
                var copied = await _history.Clipboard!.TryGetTextAsync();
                Check(copied is not null && copied.Contains("cost_cny") && copied.Contains("traffic_coverage") && copied.Contains(",machine,"), "actual copy button writes converted currency and machine/source traffic CSV to isolated clipboard");
                Check(copied!.Contains("observed_intervals") && copied.Contains("not_recorded"), "actual copied history distinguishes sampled traffic from unknown previous dates");
                File.WriteAllText(Path.Combine(Root, "copied-history.csv"), copied);
                Find<ComboBox>("HistoryMetric").SelectedIndex = 4;
                break;
            case 3:
                Check(Get<int>(Get<object>(_history, "_chart"), "VisibleSegmentCount") >= 4, "actual mirrored traffic chart draws download and upload source stacks");
                Capture("traffic-history");
                var dayButton = Get<StackPanel>(_history, "_dailyRows").GetVisualDescendants().OfType<Button>().First(button => Equals(button.Content, _today.ToString("yyyy-MM-dd")));
                Click(dayButton);
                break;
            case 4:
                Check(Get<HistoryPeriod>(_history, "_period").Range.DayCount == 1 && Find<TabControl>("HistoryDetailTabs").SelectedIndex == 3,
                    "actual day button drills into that date's source breakdown");
                Find<ComboBox>("HistoryMetric").SelectedIndex = 0;
                _history.GetVisualDescendants().OfType<ScrollViewer>().First(view => view.Content is StackPanel).ScrollToEnd();
                break;
            case 5:
                Capture("source-shares");
                var sourceButton = Get<StackPanel>(_history, "_sourceShares").Children.OfType<Button>().First(button => button.Content?.ToString()?.Contains("Claude Code", StringComparison.Ordinal) == true);
                Click(sourceButton);
                break;
            case 6:
                Check(Get<IReadOnlyList<ModelStatistic>>(_history, "_visibleModels") is { Count: 1 } models && models[0].Source == SourceKind.ClaudeCode && models[0].Output == 300,
                    "actual source breakdown button drills into exact Claude model usage");
                Check(Find<TabControl>("HistoryDetailTabs").SelectedIndex == 1, "source drilldown activates model statistics tab");
                Capture("claude-model-drilldown"); Finish(0);
                break;
        }
    }

    private void Check(bool ok, string text) { if (!ok) throw new InvalidOperationException(text); _checks.Add(text); Console.WriteLine("PASS " + text); }
    private void Finish(int code)
    {
        _timer.Stop();
        File.WriteAllText(Path.Combine(Root, "result.json"), JsonSerializer.Serialize(new { success = code == 0, checks = _checks, fixtureOnly = true, ownX11Clipboard = true }));
        _history.Close(); ((IDisposable)_controller).Dispose(); _desktop.Shutdown(code);
    }
    private void Capture(string name)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(_history.Bounds.Width), (int)Math.Ceiling(_history.Bounds.Height)));
        bitmap.Render(_history); bitmap.Save(Path.Combine(Root, name + ".png"), PngBitmapEncoderOptions.Default);
    }
    private T Find<T>(string name) where T : Control => _history.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
    private static T Get<T>(object value, string member) => (T)(value.GetType().GetField(member, Members)?.GetValue(value) ?? value.GetType().GetProperty(member, Members)!.GetValue(value))!;
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static DailyUsage Usage(DateOnly day, SourceKind source, long output, long cost) => new(day.ToString("yyyy-MM-dd"), source,
        new(Requests: 1, Input: output * 10, Output: output), output, 1000, 0, 1, null,
        [new(source + "-model", 1, output) { Input = output * 10, CachedInput = 0, Cost = new(cost) }]) { Cost = new(cost) };
}
