using System.Diagnostics;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Linux.Tests;

public sealed class AppearanceAndTrayTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(0.4, "<1")]
    [InlineData(408, "408")]
    [InlineData(999, "999")]
    [InlineData(999.6, "1K")]
    [InlineData(12400, "12K")]
    [InlineData(100000, ".1M")]
    [InlineData(950000, "1M")]
    [InlineData(1000000, "1M")]
    [InlineData(100000000, ".1G")]
    public void TrayReadoutKeepsSmallPanelNumbersCompact(double value, string expected) =>
        Assert.Equal(expected, TrayReadoutRenderer.Compact(value));

    [Fact]
    public void UnknownTrayValuesRemainUnknown()
    {
        Assert.Equal("—", TrayReadoutRenderer.Compact(null));
        Assert.Equal("—", TrayReadoutRenderer.Compact(-1));
        Assert.Equal("—", TrayReadoutRenderer.Compact(double.NaN));
        Assert.Equal("—", TrayReadoutRenderer.Compact(double.PositiveInfinity));
    }

    [Fact]
    public async Task GnomeMonitorFollowsChangesExplicitThemeWinsAndDisposalStopsChild()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Path.Combine(Path.GetTempPath(), "agent-speed-appearance-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var script = Path.Combine(root, "fake-gsettings");
        var change = Path.Combine(root, "change");
        var pidFile = Path.Combine(root, "pid");
        await File.WriteAllTextAsync(script, $$"""
            #!/bin/sh
            if [ "$1" = get ]; then
                if [ "$3" = color-scheme ]; then echo "'prefer-dark'"; else echo true; fi
            else
                echo $$ > '{{pidFile}}'
                while [ ! -f '{{change}}' ]; do sleep 0.02; done
                echo "color-scheme: 'prefer-light'"
                echo 'enable-animations: false'
                while :; do sleep 1; done
            fi
            """);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await using var service = new LinuxAppearanceService(gsettingsExecutable: script, dispatch: action => action(), applyPalette: _ => { });
        try
        {
            service.SetOptions(AppearanceMode.Auto, EffectsIntensity.Max, false);
            await Until(() => service.Status.StartsWith("跟随", StringComparison.Ordinal) && File.Exists(pidFile));
            Assert.True(service.IsDark);
            Assert.False(service.EffectiveReduceMotion);
            var pid = int.Parse(await File.ReadAllTextAsync(pidFile));
            await File.WriteAllTextAsync(change, "change");
            await Until(() => !service.IsDark && service.EffectiveReduceMotion);
            Assert.True(service.SystemReduceMotion);
            service.SetOptions(AppearanceMode.Dark, EffectsIntensity.Off, false);
            Assert.True(service.IsDark);
            Assert.True(service.EffectiveReduceMotion);
            Assert.Equal(EffectsIntensity.Off, service.Effects);
            await service.DisposeAsync();
            await Until(() => !Alive(pid));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task MissingGsettingsKeepsExplicitPreferenceAndDisposesCleanly()
    {
        await using var service = new LinuxAppearanceService(gsettingsExecutable: "/nonexistent/agent-speed-gsettings", dispatch: action => action(), applyPalette: _ => { });
        service.SetOptions(AppearanceMode.Light, EffectsIntensity.Soft, true);
        await service.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(service.IsDark);
        Assert.True(service.EffectiveReduceMotion);
        Assert.Contains("不可用", service.Status);
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Appearance observer did not reach the expected state.");
            await Task.Delay(20);
        }
    }

    private static bool Alive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
}
