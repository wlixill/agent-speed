namespace AgentSpeed.Linux.Tests;

public sealed class LoginStartupTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "agent-speed-startup-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExplicitDashboardWinsRegardlessOfArgumentOrderAndBackgroundPreservesOrbPreference()
    {
        var settings = new UserSettings(OrbVisible: false) { StartInBackground = false };
        Assert.False(LaunchOptions.Parse(["--background"]).ShouldShowDashboard(settings));
        Assert.True(LaunchOptions.Parse(["--background", "--dashboard"]).ShouldShowDashboard(settings));
        Assert.True(LaunchOptions.Parse(["--dashboard", "--background"]).ShouldShowDashboard(settings));
        Assert.True(new LaunchOptions().ShouldShowDashboard(settings));
        Assert.False(new LaunchOptions().ShouldShowDashboard(new UserSettings()));
        Assert.False(settings.OrbVisible);
    }

    [Fact]
    public void LegacySettingsKeepOrbAndAcquireSafeStartupDefaults()
    {
        var storage = new AppStorage(_directory);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(storage.SettingsPath, "{\"OrbVisible\":false,\"OrbX\":-500,\"Redline\":800}");
        var settings = storage.Load();
        Assert.False(settings.StartAtLogin);
        Assert.True(settings.StartInBackground);
        Assert.False(settings.OrbVisible);
        Assert.Equal(-500, settings.OrbX);
        Assert.Equal(800, settings.Redline);
    }

    [Fact]
    public void LoginStartupIsOptInAndIsolatedSessionsNeverTouchNormalAutostart()
    {
        var service = Service();
        Assert.False(service.IsEnabled);
        Assert.False(Directory.Exists(_directory));
        Assert.Equal(Path.Combine(_directory, "autostart", "agent-speed.desktop"), service.EntryPath);
        service.SetEnabled(false, "/unused");
        Assert.False(Directory.Exists(_directory));
        service.SetEnabled(true, "/tmp/codex/sessions");
        Assert.True(service.IsEnabled);
        var content = File.ReadAllText(service.EntryPath);
        Assert.Contains("Exec=/opt/agent-speed/AgentSpeed.Linux --background --codex-dir \"/tmp/codex/sessions\"", content);
        Assert.Contains(" --data-dir \"" + _directory + "\"", content);
        Assert.DoesNotContain("--dashboard", content);
        service.SetEnabled(false, "/unused");
        Assert.False(service.IsEnabled);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(service.EntryPath)!));
    }

    [Fact]
    public void DesktopEntryQuotesLiteralSpacesUnicodeAndFieldCodesWithoutAShell()
    {
        var service = Service(software: true);
        service.SetEnabled(true, "/tmp/中文 空格/$HOME/`echo x`/100%/%U/quote\"/slash\\/sessions");
        var content = File.ReadAllText(service.EntryPath);
        Assert.Contains("中文 空格/\\\\$HOME/\\\\`echo x\\\\`/100%%/%%U/quote\\\\\"/slash\\\\\\\\/sessions\"", content);
        Assert.Contains(" --software-rendering\n", content);
        Assert.DoesNotContain("sh -c", content);
        Assert.DoesNotContain("env ", content);
        Assert.DoesNotContain("--capture-dir", content);
        Assert.DoesNotContain("--exit-after", content);
    }

    [Fact]
    public void LoginEntryFollowsTheChosenDashboardOrBackgroundBehavior()
    {
        var service = Service();
        service.SetEnabled(true, "/tmp/codex/sessions", background: false);
        var dashboard = File.ReadAllText(service.EntryPath);
        Assert.Contains(" --dashboard --codex-dir ", dashboard);
        Assert.DoesNotContain("--background", dashboard);
        Assert.True(service.IsEnabled);
        service.SetEnabled(true, "/tmp/codex/sessions", background: true);
        var background = File.ReadAllText(service.EntryPath);
        Assert.Contains(" --background --codex-dir ", background);
        Assert.DoesNotContain("--dashboard", background);
        Assert.True(service.IsEnabled);
    }

    [Fact]
    public void UnrelatedSameNameAndOtherStartupEntriesAreNeverOverwrittenOrRemoved()
    {
        var service = Service();
        var root = Path.GetDirectoryName(service.EntryPath)!;
        Directory.CreateDirectory(root);
        var other = Path.Combine(root, "other.desktop");
        File.WriteAllText(other, "unrelated application");
        File.WriteAllText(service.EntryPath, "[Desktop Entry]\nExec=manual-command\n");
        Assert.False(service.IsEnabled);
        Assert.NotNull(service.StatusWarning);
        Assert.Throws<IOException>(() => service.SetEnabled(true, "/tmp/sessions"));
        Assert.Throws<IOException>(() => service.SetEnabled(false, "/tmp/sessions"));
        Assert.Contains("manual-command", File.ReadAllText(service.EntryPath));
        Assert.Equal("unrelated application", File.ReadAllText(other));
        Assert.Equal(2, Directory.GetFiles(root).Length);
    }

    [Theory]
    [InlineData("relative/sessions")]
    [InlineData("/tmp/sessions\nExec=other")]
    [InlineData("/tmp/sessions\rHidden=true")]
    [InlineData("/tmp/sessions\tother")]
    public void InvalidInputCannotReplaceAnExistingWorkingEntry(string path)
    {
        var service = Service();
        service.SetEnabled(true, "/tmp/working/sessions");
        var before = File.ReadAllText(service.EntryPath);
        Assert.Throws<ArgumentException>(() => service.SetEnabled(true, path));
        Assert.Equal(before, File.ReadAllText(service.EntryPath));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(service.EntryPath)!));
    }

    [Fact]
    public void SymlinksAndDirectoryCollisionsRemainUntouched()
    {
        var service = Service();
        var root = Path.GetDirectoryName(service.EntryPath)!;
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(service.EntryPath);
        Assert.Throws<IOException>(() => service.SetEnabled(true, "/tmp/sessions"));
        Assert.True(Directory.Exists(service.EntryPath));
        Directory.Delete(service.EntryPath);
        var target = Path.Combine(_directory, "manual.desktop");
        File.WriteAllText(target, "keep me");
        File.CreateSymbolicLink(service.EntryPath, target);
        Assert.Throws<IOException>(() => service.SetEnabled(false, "/tmp/sessions"));
        Assert.Equal("keep me", File.ReadAllText(target));
        Assert.NotNull(new FileInfo(service.EntryPath).LinkTarget);
    }

    private LoginStartupService Service(bool software = false) =>
        new(new LaunchOptions(DataDirectory: _directory, SoftwareRendering: software), new AppStorage(_directory));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
