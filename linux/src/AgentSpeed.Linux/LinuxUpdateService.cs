using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AgentSpeed.Core.Network;
using AgentSpeed.Presentation.Updates;

namespace AgentSpeed.Linux;

public sealed record LinuxReleaseInfo(string Version, Uri DebUrl, string Sha256, string Notes);
public sealed record LinuxDebPackage(string Path, string Version, string Architecture, string Sha256, bool PublisherHashVerified);

public interface IUpdateInstaller
{
    Task InstallAsync(string debPath, IProgress<string> progress, CancellationToken cancellationToken = default);
}

/// <summary>PackageKit delegates administrator authentication to the desktop's existing PolicyKit agent.</summary>
public sealed class PackageKitUpdateInstaller : IUpdateInstaller
{
    public async Task InstallAsync(string debPath, IProgress<string> progress, CancellationToken cancellationToken = default)
    {
        if (!File.Exists("/usr/bin/pkcon")) throw new IOException("系统缺少 PackageKit 工具，请先安装 packagekit-tools 后重试。");
        var start = new ProcessStartInfo("/usr/bin/pkcon")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var argument in new[] { "--plain", "install-local", debPath }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 PackageKit 安装程序。");
        // The user already chose the explicit Install button. This answers PackageKit's transaction simulation prompt.
        await process.StandardInput.WriteLineAsync("y");
        await process.StandardInput.FlushAsync(cancellationToken);
        async Task Read(StreamReader reader)
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                progress.Report(line.Length > 500 ? line[..500] : line);
        }
        try
        {
            await Task.WhenAll(Read(process.StandardOutput), Read(process.StandardError), process.WaitForExitAsync(cancellationToken)).ConfigureAwait(false);
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        if (process.ExitCode != 0) throw new IOException("安装未完成（PackageKit 退出码 " + process.ExitCode + "）。当前应用保持运行，可处理认证后重试。");
    }
}

/// <summary>Linux releases use Debian versions and verified .deb files; the original Windows ZIP feed is not used.</summary>
public sealed class LinuxUpdateService : IDisposable
{
    public const int MaxFeedBytes = 64 * 1024;
    public const int MaxPackageBytes = 300 * 1024 * 1024;
    private readonly AppStorage _storage;
    private readonly IHttpTransport _transport;
    private readonly bool _ownsTransport;
    private readonly IUpdateInstaller _installer;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _cache;
    private bool _disposed;
    private bool _auto;
    private string _feed = "";
    private Timer? _timer;
    private DateTimeOffset _due;

    public LinuxUpdateService(AppStorage storage, string? currentVersion = null, IHttpTransport? transport = null,
        IUpdateInstaller? installer = null, Func<DateTimeOffset>? now = null)
    {
        _storage = storage;
        CurrentVersion = ValidVersion(currentVersion ?? BuildInformation.Version) ? currentVersion ?? BuildInformation.Version : "0";
        _transport = transport ?? new HttpClientTransport();
        _ownsTransport = transport is null;
        _installer = installer ?? new PackageKitUpdateInstaller();
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _cache = System.IO.Path.Combine(storage.DataDirectory, "updates");
        _due = UpdateSchedule.FirstCheck(_now(), null);
    }

    public string CurrentVersion { get; }
    public string Status { get; private set; } = "更新源未配置；可选择本地 deb 安装。";
    public bool IsBusy { get; private set; }
    public LinuxReleaseInfo? Release { get; private set; }
    public LinuxDebPackage? StagedPackage { get; private set; }
    public DateTimeOffset? LastCheck { get; private set; }
    public bool Installed { get; private set; }
    public event Action? Changed;

    public void SetOptions(bool automatic, string feedUrl)
    {
        var normalized = string.IsNullOrWhiteSpace(feedUrl) ? "" : FeedUri(feedUrl).AbsoluteUri;
        if (_feed != normalized) Release = null;
        if (_auto != automatic || _feed != normalized) _due = UpdateSchedule.FirstCheck(_now(), LastCheck);
        _auto = automatic; _feed = normalized;
        if (!IsBusy) Status = normalized.Length == 0 ? "更新源未配置；当前没有可检查的 Ubuntu 发布源。"
            : CurrentVersion == "0" ? "开发构建未确认发行版本，仅支持手动检查；安装前请核对本机版本。"
            : automatic ? "已启用定时检查；下载与安装仍需手动确认。" : "自动检查已关闭，可手动检查。";
        Changed?.Invoke();
    }

    public void Start() => _timer ??= new Timer(state =>
    {
        if (!_disposed && CurrentVersion != "0" && _auto && _feed.Length > 0 && _now() >= _due) _ = CheckAsync();
    }, null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1));

    public Task CheckAsync() => RunAsync(async token =>
    {
        if (_feed.Length == 0) throw new InvalidOperationException("请先配置你自己的 Ubuntu 更新源。");
        var feed = FeedUri(_feed);
        var response = await Fetch(feed, MaxFeedBytes, feed, token).ConfigureAwait(false);
        if (!response.IsSuccess) throw new IOException("更新源返回 HTTP " + response.Status);
        var release = ParseRelease(response.Body, feed);
        Release = await IsNewerAsync(release.Version, CurrentVersion, token).ConfigureAwait(false) ? release : null;
        LastCheck = _now();
        _due = UpdateSchedule.NextCheck(_now());
        Status = Release is null ? "当前已是更新源提供的最新版本。" : "发现 Ubuntu " + release.Version + "，可下载校验后安装。";
    });

    public Task DownloadAsync() => RunAsync(async token =>
    {
        var release = Release ?? throw new InvalidOperationException("请先检查并选择可用的新版本。");
        var feed = FeedUri(_feed);
        var response = await Fetch(release.DebUrl, MaxPackageBytes, feed, token).ConfigureAwait(false);
        if (!response.IsSuccess) throw new IOException("安装包下载返回 HTTP " + response.Status);
        StagedPackage = await StageBytes(response.Body, release.Sha256, token).ConfigureAwait(false);
        if (StagedPackage.Version != release.Version) { StagedPackage = null; throw new IOException("deb 内部版本与更新源不一致。"); }
        Installed = false;
        Status = "已下载并验证 " + release.Version + "。请检查版本与 SHA-256，然后点击安装。";
    });

    public Task StageLocalAsync(string path, string? expectedSha256 = null) => RunAsync(async token =>
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0 || info.Length > MaxPackageBytes) throw new IOException("请选择有效的 deb 文件（最大 300 MB）。");
        var data = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
        StagedPackage = await StageBytes(data, expectedSha256, token).ConfigureAwait(false);
        Installed = false;
        Status = "已检查本地 deb：" + StagedPackage.Version + (StagedPackage.PublisherHashVerified ? "，发布者校验值匹配。" : "；已计算 SHA-256，未提供发布者校验值。") + " 点击安装才会修改系统。";
    });

    public Task InstallAsync() => RunAsync(async token =>
    {
        var package = StagedPackage ?? throw new InvalidOperationException("请先选择或下载并校验安装包。");
        using var stream = File.OpenRead(package.Path);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        if (hash != package.Sha256) throw new IOException("待安装文件已变化，请重新选择并校验。");
        var metadata = await InspectAsync(package.Path, token).ConfigureAwait(false);
        if (metadata.Version != package.Version || metadata.Architecture != package.Architecture) throw new IOException("待安装文件的元数据已变化。");
        if (File.Exists(_storage.SettingsPath))
        {
            var backup = System.IO.Path.Combine(_cache, "settings-before-" + _now().ToUnixTimeMilliseconds() + "-" + Guid.NewGuid().ToString("N") + ".json");
            File.Copy(_storage.SettingsPath, backup, overwrite: false);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(backup, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        Status = "正在安装；请处理系统管理员认证。认证失败时当前应用会继续运行。";
        Changed?.Invoke();
        await _installer.InstallAsync(package.Path, new InlineProgress(message => { Status = message; Changed?.Invoke(); }), token).ConfigureAwait(false);
        if (_installer is PackageKitUpdateInstaller)
        {
            var installed = await Command("/usr/bin/dpkg-query", ["-W", "-f=${Version}", "agent-speed"], token).ConfigureAwait(false);
            if (installed.Code != 0 || installed.Output.Trim() != package.Version)
                throw new IOException("PackageKit 已返回，但未确认系统已安装目标版本，请核对后再重新启动。");
        }
        Installed = true;
        Status = "安装完成。请从托盘正常退出，再从应用菜单启动新版；现有用量历史保留。";
    });

    private async Task<LinuxDebPackage> StageBytes(byte[] data, string? expected, CancellationToken token)
    {
        if (data.Length is <= 0 or > MaxPackageBytes) throw new IOException("安装包大小不符合限制。");
        var hash = Convert.ToHexStringLower(SHA256.HashData(data));
        var verified = !string.IsNullOrWhiteSpace(expected);
        if (verified && (!ValidHash(expected!) || hash != expected!.ToLowerInvariant())) throw new IOException("SHA-256 校验失败。");
        Directory.CreateDirectory(_cache);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(_cache, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = System.IO.Path.Combine(_cache, Guid.NewGuid().ToString("N") + ".deb");
        try
        {
            await File.WriteAllBytesAsync(temporary, data, token).ConfigureAwait(false);
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var metadata = await InspectAsync(temporary, token).ConfigureAwait(false);
            if (!await IsNewerAsync(metadata.Version, CurrentVersion, token).ConfigureAwait(false)) throw new IOException("安装包版本没有高于当前版本，已阻止降级或重复安装。");
            return new(temporary, metadata.Version, metadata.Architecture, hash, verified);
        }
        catch { File.Delete(temporary); throw; }
    }

    public static LinuxReleaseInfo ParseRelease(ReadOnlySpan<byte> data, Uri feed)
    {
        if (data.Length > MaxFeedBytes) throw new FormatException("更新源文档过大。");
        using var json = JsonDocument.Parse(data.ToArray());
        var root = json.RootElement;
        string Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        if (!root.TryGetProperty("schema", out var schema) || !schema.TryGetInt32(out var number) || number != 1
            || Text("package") != "agent-speed" || Text("architecture") != "amd64" || !ValidVersion(Text("version"))
            || !ValidHash(Text("sha256")) || Text("debUrl").Length == 0 || !Uri.TryCreate(feed, Text("debUrl"), out var asset)
            || !SameOrigin(feed, asset)) throw new FormatException("更新源需要 schema 1、amd64 Agent Speed deb、Debian 版本及 SHA-256；资源必须与更新源同一 HTTPS 来源。");
        var notes = Text("notes");
        return new(Text("version"), asset, Text("sha256").ToLowerInvariant(), notes.Length > 16_384 ? notes[..16_384] : notes);
    }

    public static async Task<bool> IsNewerAsync(string candidate, string current, CancellationToken token = default)
    {
        if (!ValidVersion(candidate) || !ValidVersion(current)) throw new ArgumentException("无效的 Debian 版本。");
        var result = await Command("/usr/bin/dpkg", ["--compare-versions", candidate, "gt", current], token).ConfigureAwait(false);
        if (result.Code > 1) throw new IOException("无法比较 Debian 包版本。");
        return result.Code == 0;
    }

    private static async Task<(string Version, string Architecture)> InspectAsync(string path, CancellationToken token)
    {
        var result = await Command("/usr/bin/dpkg-deb", ["--field", path, "Package", "Version", "Architecture"], token).ConfigureAwait(false);
        var fields = result.Output.Split('\n').Where(line => line.Contains(':')).Select(line => line.Split(':', 2)).ToDictionary(pair => pair[0].Trim(), pair => pair[1].Trim(), StringComparer.Ordinal);
        if (result.Code != 0 || fields.GetValueOrDefault("Package") != "agent-speed" || fields.GetValueOrDefault("Architecture") != "amd64"
            || !ValidVersion(fields.GetValueOrDefault("Version") ?? "")) throw new IOException("deb 元数据不匹配：必须是 amd64 的 agent-speed 安装包。");
        return (fields["Version"], fields["Architecture"]);
    }

    private Task<HttpFetchResponse> Fetch(Uri url, int maximum, Uri feed, CancellationToken token) =>
        _transport.GetAsync(new HttpFetchRequest(url, "AgentSpeed/Linux-Updater", maximum, new HashSet<string> { feed.IdnHost.ToLowerInvariant() }), token);

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_disposed || !await _operation.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            IsBusy = true; Changed?.Invoke();
            await action(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { Status = "操作已取消。"; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or FormatException or JsonException or HttpFetchException or System.ComponentModel.Win32Exception)
        { Status = "更新未完成：" + exception.Message; _due = UpdateSchedule.NextCheck(_now()); }
        finally { IsBusy = false; _operation.Release(); if (!_disposed) Changed?.Invoke(); }
    }

    private static Uri FeedUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme != "https" || url.Port != 443
            || url.UserInfo.Length > 0 || url.Fragment.Length > 0) throw new ArgumentException("更新源必须是不含账号信息的 HTTPS URL（443 端口）。");
        return url;
    }
    private static bool SameOrigin(Uri left, Uri right) => right.Scheme == "https" && right.Port == 443 && right.UserInfo.Length == 0
        && right.Fragment.Length == 0 && left.IdnHost.Equals(right.IdnHost, StringComparison.OrdinalIgnoreCase);
    private static bool ValidHash(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
    private static bool ValidVersion(string value) => value.Length is > 0 and < 80 && char.IsAsciiDigit(value[0])
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '+' or '-' or '~' or ':');

    private static async Task<(int Code, string Output)> Command(string executable, IReadOnlyList<string> arguments, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Debian 包检查工具。");
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false));
        }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _timer?.Dispose(); _lifetime.Cancel();
        if (_ownsTransport && _transport is IDisposable disposable) disposable.Dispose();
    }
}
