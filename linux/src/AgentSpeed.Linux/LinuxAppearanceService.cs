using System.ComponentModel;
using System.Diagnostics;
using AgentSpeed.Presentation.Settings;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;

namespace AgentSpeed.Linux;

/// <summary>Resolves explicit preferences and follows GNOME changes without blocking the UI thread.</summary>
public sealed class LinuxAppearanceService : IDisposable, IAsyncDisposable
{
    private const string Schema = "org.gnome.desktop.interface";
    private readonly IPlatformSettings? _platform;
    private readonly string _executable;
    private readonly Action<Action> _dispatch;
    private readonly Action<bool> _applyPalette;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _processGate = new();
    private readonly Task _observer;
    private Process? _monitor;
    private bool _disposed;
    private bool _platformDark;
    private bool? _gnomeDark;
    private bool _systemReduceMotion;
    private AppearanceMode _appearance = AppearanceMode.Dark;
    private EffectsIntensity _effects = EffectsIntensity.Standard;
    private bool _requestedReduceMotion;

    public LinuxAppearanceService(IPlatformSettings? platformSettings = null, string? gsettingsExecutable = null,
        Action<Action>? dispatch = null, Action<bool>? applyPalette = null)
    {
        _platform = platformSettings;
        _executable = gsettingsExecutable ?? "gsettings";
        _dispatch = dispatch ?? (action => Dispatcher.UIThread.Post(action));
        _applyPalette = applyPalette ?? Ui.ApplyAppearance;
        if (_platform is not null)
        {
            _platformDark = _platform.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark;
            _platform.ColorValuesChanged += PlatformChanged;
        }
        _observer = Task.Run(() => ObserveAsync(_stop.Token));
    }

    public bool IsDark { get; private set; } = true;
    public ThemeVariant ResolvedTheme => IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
    public bool EffectiveReduceMotion { get; private set; }
    public bool SystemReduceMotion => _systemReduceMotion;
    public EffectsIntensity Effects => _effects;
    public string Status { get; private set; } = "正在读取系统外观设置";
    public Task Completion => _observer;
    public event Action? Changed;

    public void SetOptions(AppearanceMode appearance, EffectsIntensity effects, bool reduceMotion)
    {
        if (_disposed) return;
        appearance = Enum.IsDefined(appearance) ? appearance : AppearanceMode.Dark;
        effects = Enum.IsDefined(effects) ? effects : EffectsIntensity.Standard;
        var changed = _appearance != appearance || _effects != effects || _requestedReduceMotion != reduceMotion;
        _appearance = appearance;
        _effects = effects;
        _requestedReduceMotion = reduceMotion;
        Resolve(changed);
    }

    private void PlatformChanged(object? sender, PlatformColorValues values) => Post(() =>
    {
        _platformDark = values.ThemeVariant == PlatformThemeVariant.Dark;
        Resolve();
    });

    private void Resolve(bool force = false)
    {
        var dark = _appearance switch { AppearanceMode.Dark => true, AppearanceMode.Light => false, _ => _gnomeDark ?? _platformDark };
        var reduce = _requestedReduceMotion || _systemReduceMotion;
        var changed = force || dark != IsDark || reduce != EffectiveReduceMotion;
        IsDark = dark;
        EffectiveReduceMotion = reduce;
        _applyPalette(dark);
        if (changed) Changed?.Invoke();
    }

    private async Task ObserveAsync(CancellationToken stop)
    {
        Process? process = null;
        try
        {
            // Start the live monitor first; then read initial values, closing the launch-time observation gap.
            process = CreateProcess("monitor", Schema);
            lock (_processGate)
            {
                if (_disposed) return;
                process.Start();
                _monitor = process;
            }
            var stderr = process.StandardError.ReadToEndAsync(stop);
            var colorRead = ReadAsync("color-scheme", stop);
            var motionRead = ReadAsync("enable-animations", stop);
            await Task.WhenAll(colorRead, motionRead).ConfigureAwait(false);
            var color = colorRead.Result;
            var animations = motionRead.Result;
            Post(() =>
            {
                _gnomeDark = ParseColorScheme(color);
                if (ParseAnimations(animations) is { } enabled) _systemReduceMotion = !enabled;
                Status = "跟随 GNOME 系统外观与动画设置";
                Resolve(true);
            });
            while (!stop.IsCancellationRequested && await process.StandardOutput.ReadLineAsync(stop).ConfigureAwait(false) is { } line)
            {
                var colon = line.IndexOf(':');
                if (colon < 0) continue;
                var key = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                if (key == "color-scheme") Post(() => { _gnomeDark = ParseColorScheme(value); Resolve(); });
                else if (key == "enable-animations" && ParseAnimations(value) is { } enabled)
                    Post(() => { _systemReduceMotion = !enabled; Resolve(); });
            }
            await process.WaitForExitAsync(stop).ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            if (!stop.IsCancellationRequested) Post(() => { _gnomeDark = null; Status = "系统外观监听已结束；使用平台主题与已读取的动画设置"; Resolve(true); });
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
        {
            Post(() => { _gnomeDark = null; Status = "使用平台主题；GNOME 设置监听不可用"; Resolve(true); });
        }
        finally
        {
            lock (_processGate)
            {
                if (process is not null) StopProcess(process);
                _monitor = null;
            }
            if (process is not null)
            {
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                catch (Exception exception) when (exception is InvalidOperationException or TimeoutException) { }
                process.Dispose();
            }
        }
    }

    private async Task<string?> ReadAsync(string key, CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var process = CreateProcess("get", Schema, key);
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await error.ConfigureAwait(false);
            return process.ExitCode == 0 ? (await output.ConfigureAwait(false)).Trim() : null;
        }
        catch (OperationCanceledException) { return null; }
        finally { StopProcess(process); }
    }

    private Process CreateProcess(params string[] arguments)
    {
        var start = new ProcessStartInfo(_executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return new() { StartInfo = start };
    }

    public static bool? ParseColorScheme(string? value) => value?.Trim().Trim('\'', '"') switch
    {
        "prefer-dark" => true, "prefer-light" => false, _ => null,
    };
    public static bool? ParseAnimations(string? value) => value?.Trim() switch { "true" => true, "false" => false, _ => null };

    private void Post(Action action) => _dispatch(() => { if (!_disposed) action(); });
    private static void StopProcess(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_platform is not null) _platform.ColorValuesChanged -= PlatformChanged;
        _stop.Cancel();
        lock (_processGate) { if (_monitor is { } monitor) StopProcess(monitor); }
    }

    public async ValueTask DisposeAsync() { Dispose(); await _observer.ConfigureAwait(false); _stop.Dispose(); }
}
