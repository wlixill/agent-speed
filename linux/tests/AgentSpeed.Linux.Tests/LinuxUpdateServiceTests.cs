using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AgentSpeed.Core.Network;

namespace AgentSpeed.Linux.Tests;

public sealed class LinuxUpdateServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agent-speed-update-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly Uri Feed = new("https://updates.example.test/linux.json");

    [Fact]
    public async Task DebianRevisionOrderingUsesDpkgRatherThanPrereleaseSemantics()
    {
        Assert.True(await LinuxUpdateService.IsNewerAsync("1.4.1-10", "1.4.1-2"));
        Assert.False(await LinuxUpdateService.IsNewerAsync("1.4.1-2", "1.4.1-10"));
        Assert.True(await LinuxUpdateService.IsNewerAsync("1:1.0-1", "2.0-1"));
    }

    [Fact]
    public async Task AnUnconfiguredUpdaterNeverContactsTheOriginalUpstreamFeed()
    {
        var transport = new FakeTransport([]);
        using var service = new LinuxUpdateService(new AppStorage(_root), "1.4.1-2", transport, new DryInstaller());
        service.Start();
        await service.CheckAsync();
        Assert.Equal(0, transport.Calls);
        Assert.Null(service.Release);
        Assert.Contains("请先配置", service.Status);
    }

    [Fact]
    public async Task FeedDownloadVerifiesHashAndMetadataButInstallationWaitsForAnExplicitMethod()
    {
        var package = BuildDeb("agent-speed", "1.4.1-10", "amd64");
        var bytes = await File.ReadAllBytesAsync(package);
        var installer = new DryInstaller();
        var transport = new FakeTransport(bytes);
        var storage = new AppStorage(Path.Combine(_root, "data"));
        storage.Save(new UserSettings());
        using var service = new LinuxUpdateService(storage, "1.4.1-2", transport, installer);
        service.SetOptions(false, Feed.AbsoluteUri);
        await service.CheckAsync();
        Assert.Equal("1.4.1-10", service.Release?.Version);
        await service.DownloadAsync();
        Assert.Equal("1.4.1-10", service.StagedPackage?.Version);
        Assert.True(service.StagedPackage?.PublisherHashVerified);
        Assert.Equal(0, installer.Calls);
        Assert.False(service.Installed);
        if (OperatingSystem.IsLinux()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(service.StagedPackage!.Path));
        await service.InstallAsync();
        Assert.Equal(1, installer.Calls);
        Assert.True(service.Installed);
        Assert.Contains("安装完成", service.Status);
        Assert.Equal(service.StagedPackage!.Path, installer.Path);
        Assert.Single(Directory.GetFiles(Path.Combine(storage.DataDirectory, "updates"), "settings-before-*.json"));
        Assert.Equal(2, transport.Calls);
    }

    [Fact]
    public async Task LocalMetadataHashAndPostReviewTamperingAreCheckedBeforeTheInstaller()
    {
        var path = BuildDeb("agent-speed", "1.4.1-10", "amd64");
        var installer = new DryInstaller();
        using var service = new LinuxUpdateService(new AppStorage(Path.Combine(_root, "data")), "1.4.1-2", new FakeTransport([]), installer);
        await service.StageLocalAsync(path, new string('0', 64));
        Assert.Null(service.StagedPackage);
        Assert.Contains("SHA-256", service.Status);
        await service.StageLocalAsync(path);
        Assert.NotNull(service.StagedPackage);
        await File.AppendAllTextAsync(service.StagedPackage!.Path, "tampered after review");
        await service.InstallAsync();
        Assert.Equal(0, installer.Calls);
        Assert.False(service.Installed);
        Assert.Contains("已变化", service.Status);
    }

    [Theory]
    [InlineData("other-package", "amd64")]
    [InlineData("agent-speed", "arm64")]
    public async Task OtherPackagesOrArchitecturesCannotBeInstalled(string package, string architecture)
    {
        var path = BuildDeb(package, "1.4.1-10", architecture);
        var installer = new DryInstaller();
        using var service = new LinuxUpdateService(new AppStorage(Path.Combine(_root, "data")), "1.4.1-2", new FakeTransport([]), installer);
        await service.StageLocalAsync(path);
        Assert.Null(service.StagedPackage);
        Assert.Equal(0, installer.Calls);
        Assert.Contains("元数据不匹配", service.Status);
    }

    [Fact]
    public void FeedRejectsCrossOriginAssetsAndMissingChecksums()
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, version = "1.4.1-10", package = "agent-speed", architecture = "amd64",
            debUrl = "https://unrelated.example.test/package.deb", sha256 = new string('a', 64) });
        Assert.Throws<FormatException>(() => LinuxUpdateService.ParseRelease(body, Feed));
        Assert.Throws<ArgumentException>(() =>
        {
            using var updater = new LinuxUpdateService(new AppStorage(_root), "1.4.1-2", new FakeTransport([]), new DryInstaller());
            updater.SetOptions(true, "http://updates.example.test/feed");
        });
    }

    [Fact]
    public void AHostableManifestResolvesItsRelativeDebWithinTheSameHttpsOrigin()
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, version = "1.4.1-10", package = "agent-speed", architecture = "amd64",
            debUrl = "agent-speed_1.4.1-10_amd64.deb", sha256 = new string('a', 64) });
        Assert.Equal("https://updates.example.test/agent-speed_1.4.1-10_amd64.deb", LinuxUpdateService.ParseRelease(body, Feed).DebUrl.AbsoluteUri);
    }

    private string BuildDeb(string name, string version, string architecture)
    {
        var payload = Path.Combine(_root, "payload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(payload, "DEBIAN"));
        File.WriteAllText(Path.Combine(payload, "DEBIAN/control"), $"Package: {name}\nVersion: {version}\nArchitecture: {architecture}\nMaintainer: Synthetic test <test@example.invalid>\nDescription: synthetic update fixture\n");
        var output = payload + ".deb";
        var start = new ProcessStartInfo("/usr/bin/dpkg-deb") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "--build", "--root-owner-group", "-Znone", payload, output }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEnd(); process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
        return output;
    }
    private sealed class DryInstaller : IUpdateInstaller
    {
        public int Calls { get; private set; }
        public string? Path { get; private set; }
        public Task InstallAsync(string debPath, IProgress<string> progress, CancellationToken cancellationToken = default)
        { Calls++; Path = debPath; progress.Report("synthetic dry-run only"); return Task.CompletedTask; }
    }
    private sealed class FakeTransport(byte[] deb) : IHttpTransport
    {
        public int Calls { get; private set; }
        public Task<HttpFetchResponse> GetAsync(HttpFetchRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            Assert.True(request.Permits(request.Url));
            var body = request.Url == Feed ? JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, version = "1.4.1-10", package = "agent-speed", architecture = "amd64",
                debUrl = "https://updates.example.test/package.deb", sha256 = Convert.ToHexStringLower(SHA256.HashData(deb)) }) : deb;
            return Task.FromResult(new HttpFetchResponse(200, body, finalUrl: request.Url));
        }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
