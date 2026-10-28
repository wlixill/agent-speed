using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentSpeed.Core;
using AgentSpeed.Core.Reporting;
using AgentSpeed.Core.Traffic;
using SkiaSharp;

namespace AgentSpeed.Linux;

public enum LinuxReportPlatform { Linux, WindowsCompatibility }

/// <summary>Local request metadata only. Never contains report bodies, credentials or signatures.</summary>
public sealed record LinuxReportDiagnostics
{
    public DateTimeOffset? LastAttemptAt { get; init; }
    public DateTimeOffset? LastReportAttemptAt { get; init; }
    public DateTimeOffset? LastReportSuccessAt { get; init; }
    public string LastOperation { get; init; } = "尚未发送";
    public int? HttpStatus { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorField { get; init; }
    public ReportTransportErrorKind? NetworkFailure { get; init; }
    public long ReportAttempts { get; init; }
    public long ReportsAccepted { get; init; }
    public string LastResult { get; init; } = "尚无请求记录";
}

/// <summary>Opt-in signed reporting. Discovery is read-only; registration and profile writes require explicit methods.</summary>
public sealed class LinuxReportService : IDisposable
{
    private sealed record Identity(string Service, string DeviceId, string Username, string PrivateKey, string? ApiKey,
        string Discovery, bool Enabled, bool Paused, string[] Sections)
    {
        public LinuxReportPlatform PlatformMode { get; init; } = LinuxReportPlatform.Linux;
        public override string ToString() => "Report identity (private)";
    }
    private readonly object _snapshotGate = new();
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _reportRound;
    private readonly IReportTransport _transport;
    private readonly bool _ownsTransport;
    private ReportApi _api;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<long> _monotonic;
    private ReportClientInfo _client;
    private readonly string _version;
    private readonly string _directory;
    private readonly string _identityPath;
    private Identity? _identity;
    private SoftwareReportSigner? _signer;
    private ReportServiceUrl? _service;
    private ReportServiceUrl? _draftService;
    private ReportDiscovery? _discovery;
    private DateTimeOffset? _discoveredAt;
    private ReportScheduler? _scheduler;
    private AgentSpeed.Core.Reporting.ReportWindow? _window;
    private readonly Dictionary<int, byte[]> _bodies = [];
    private ReportSample _sample = ReportSample.Empty;
    private string _runId = ReportCanonical.NewUuid();
    private bool _restoreEnabled;
    private bool _stateUnreadable;
    private bool _disposed;
    private Timer? _timer;
    private bool _resetRun;
    private ReportTitleTracker _title = new();
    private bool _pairingInviter;
    private LinuxReportDiagnostics _diagnostics = new();

    public LinuxReportService(AppStorage storage, IReportTransport? transport = null, Func<DateTimeOffset>? now = null,
        Func<long>? monotonic = null, string? version = null)
    {
        _transport = transport ?? new HttpReportTransport();
        _ownsTransport = transport is null;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        var watch = Stopwatch.StartNew();
        _monotonic = monotonic ?? (() => watch.ElapsedMilliseconds);
        _version = version ?? BuildInformation.Version;
        _client = new(_version, "Linux", UbuntuMajor());
        _api = new(_transport, _client.UserAgent);
        _directory = Path.Combine(storage.DataDirectory, "reporting");
        _identityPath = Path.Combine(_directory, "identity.json");
        LoadIdentity();
    }

    public bool Enabled { get; private set; }
    public bool Paused { get; private set; }
    public bool IsBusy { get; private set; }
    public string Status { get; private set; } = "上报默认关闭；填写服务地址并检查声明后才能主动开始。";
    public string Endpoint => _service?.Text ?? "";
    public string Username => _identity?.Username ?? "";
    public string DeviceId => _identity?.DeviceId ?? "";
    public bool HasIdentity => _identity is not null;
    public string IdentityPath => _identityPath;
    public LinuxReportPlatform PlatformMode { get; private set; }
    public IReadOnlySet<ReportSection> SelectedSections => (_identity?.Sections ?? [])
        .Select(ReportSections.Parse).Where(section => section.HasValue).Select(section => section!.Value).ToHashSet();
    public LinuxReportDiagnostics Diagnostics => Volatile.Read(ref _diagnostics);
    public string DiagnosticSummary
    {
        get
        {
            var value = Diagnostics;
            static string When(DateTimeOffset? time) => time?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "尚无";
            return "当前上报平台：" + (PlatformMode == LinuxReportPlatform.WindowsCompatibility ? "Windows（兼容模式，实际系统为 Ubuntu）" : "Linux")
                + "\n最近请求：" + When(value.LastAttemptAt) + " · " + value.LastOperation + " · " + value.LastResult
                + $"\n本次启动的实时报告：尝试 {value.ReportAttempts:N0} 次 · 成功响应 {value.ReportsAccepted:N0} 次"
                + "\n最近实时报告成功响应：" + When(value.LastReportSuccessAt)
                + "\n注册成功后，只有新活动才会持续上报；不会补传本地历史。";
        }
    }
    public ReportDiscovery? DraftDiscovery { get; private set; }
    public string DraftEndpoint => _draftService?.Text ?? "";
    public ReportDiscovery? Discovery => _discovery;
    public ReportProfile? Profile { get; private set; }
    public ReportPairing? Pairing { get; private set; }
    public bool CanApprovePairing => _pairingInviter && Pairing is { State: "pending", Claimant: not null }
        && Pairing.ExpiresAt > _now() && Profile?.SharedUserId is not null;
    public byte[]? AvatarPng { get; private set; }
    public SpeedMetric Metric { get; set; } = SpeedMetric.Output;
    public double Redline { get; set; } = 400;
    public int PriceVersion { get; set; } = 1;
    public event Action? Changed;

    public void Start()
    {
        if (_disposed || _timer is not null) return;
        _timer = new Timer(state => { _ = TickAsync(); }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        if (_restoreEnabled) _ = RunAsync(async token =>
        {
            if (_service is null || _discovery is null) return;
            var found = await _api.DiscoverAsync(_service, cancellationToken: token).ConfigureAwait(false);
            if (found is not ReportDiscoveryOutcome.Document document || !document.Discovery.SupportsThisClient
                || ReportConsentPin.Of(_discovery).NeedsReview(document.Discovery, !string.IsNullOrEmpty(_identity?.ApiKey)))
            { Status = "服务未确认或声明有变化，保持暂停。请检查服务并重新同意后开始。"; return; }
            _discovery = document.Discovery;
            _discoveredAt = _now();
            Enabled = true;
            InitializeRun();
            Status = Paused ? "已恢复本机身份，当前暂停。" : "已恢复已同意的服务；工作时按服务限速上报。";
        });
    }

    public void Observe(SpeedSnapshot snapshot, TrafficSnapshot? traffic = null)
    {
        var trafficSample = traffic is null ? ReportTrafficSample.Unavailable
            : ReportTrafficSample.From(traffic, includeTools: true, SourceSet.Of(SourceKind.Codex));
        lock (_snapshotGate) _sample = ReportSample.From(snapshot, trafficSample);
        if (Enabled && !Paused) _ = TickAsync();
    }

    public Task DiscoverAsync(string endpoint) => RunAsync(async token =>
    {
        var service = ReportServiceUrl.Parse(endpoint) ?? throw new ArgumentException("请填写有效的 HTTPS 服务地址。");
        if (service.IsLoopbackHttp && _ownsTransport) throw new ArgumentException("正式上报只接受 HTTPS；HTTP 仅用于注入的本地测试服务。");
        var outcome = await _api.DiscoverAsync(service, cancellationToken: token).ConfigureAwait(false);
        if (outcome is not ReportDiscoveryOutcome.Document document || !document.Discovery.SupportsThisClient)
            throw new InvalidOperationException("服务没有提供受支持的 Agent Speed API 声明，不能开始上报。");
        _draftService = service; DraftDiscovery = document.Discovery;
        if (Enabled && _service?.Origin == service.Origin && _discovery is not null
            && ReportConsentPin.Of(_discovery).NeedsReview(document.Discovery, !string.IsNullOrEmpty(_identity?.ApiKey)))
        {
            Enabled = false; _restoreEnabled = false; _resetRun = true;
            if (_identity is not null) TrySaveIdentity(_identity with { Enabled = false });
        }
        Status = "已检查服务。请阅读名称、API 地址、条款、隐私声明和可分享项目，再勾选同意并开始。";
    });

    public Task EnableAsync(string username, string? apiKey = null, IReadOnlySet<ReportSection>? sections = null) =>
        EnableWithPlatformAsync(username, apiKey, sections, PlatformMode);

    public Task EnableWithPlatformAsync(string username, string? apiKey, IReadOnlySet<ReportSection>? sections, LinuxReportPlatform mode)
    {
        if (Enum.IsDefined(mode) && mode != PlatformMode) CancelReport();
        return RunAsync(async token =>
        {
            if (!Enum.IsDefined(mode)) throw new ArgumentException("未知的上报平台模式。");
            if (_stateUnreadable) throw new InvalidOperationException("本机身份文件无法读取，已保留。请检查或手动重置后再开始。");
            Enabled = false;
            _restoreEnabled = false; CancelReport(); _resetRun = true;
            var document = DraftDiscovery ?? throw new InvalidOperationException("请先检查服务并阅读声明。");
            var service = _draftService ?? throw new InvalidOperationException("尚未检查服务地址。");
            if (_service is not null && _identity is not null && _service.Origin != service.Origin)
                throw new InvalidOperationException("当前身份属于另一服务；请先停用并移除原服务中的设备。");
            username = username.Trim();
            if (ReportUsername.Validate(username, document.UsernameRules) is not null) throw new ArgumentException("用户名不符合服务声明的长度或字符要求。");
            apiKey = string.IsNullOrWhiteSpace(apiKey) ? _identity?.ApiKey : apiKey.Trim();
            if (apiKey is not null && (apiKey.Length > 4096 || apiKey.Any(character => character is < '!' or > '~')))
                throw new ArgumentException("API 密钥格式不合法。");
            if (document.ApiKeyMode == ReportApiKeyMode.Required && string.IsNullOrEmpty(apiKey)) throw new ArgumentException("此服务需要 API 密钥。");
            _signer ??= new SoftwareReportSigner();
            var device = _identity?.DeviceId ?? ReportCanonical.NewUuid();
            var selected = (sections ?? new HashSet<ReportSection>()).Where(document.Sections.Contains).Select(ReportSections.Name).ToArray();
            var draft = new Identity(service.Text, device, username, Convert.ToBase64String(_signer.ExportPkcs8()), apiKey,
                Encoding.UTF8.GetString(document.ToJson().ToCompactBytes()), false, false, selected) { PlatformMode = mode };
            // Persist the signing identity before an external registration, so a disk error cannot orphan a remote device.
            SaveIdentity(draft);
            _identity = draft; _service = service; _discovery = document;
            ApplyPlatform(mode);
            var reply = await CallAsync("POST", ReportEndpoint.Devices,
                ReportDocuments.Registration(device, username, _signer.PublicKey, _client), token).ConfigureAwait(false);
            if (reply?.IsSuccess != true)
            {
                var diagnostic = Diagnostics;
                if (diagnostic.HttpStatus == 422 && diagnostic.ErrorField?.TrimStart('/').Replace('/', '.') == "client.platform")
                    throw new InvalidOperationException((mode == LinuxReportPlatform.Linux
                        ? "服务端不支持 Linux 平台，设备注册被拒绝；请联系服务维护者添加 Linux 支持。"
                        : "服务端拒绝 Windows 兼容平台标识，设备尚未注册。")
                        + "（" + diagnostic.LastResult + "）本机身份已保留。");
                throw new InvalidOperationException("设备注册未完成：" + diagnostic.LastResult + "。本机身份已保留，可检查后重试。");
            }
            if (reply.DeviceId != device || reply.Username != username)
                throw new InvalidOperationException($"服务返回 HTTP {reply.Status}，但注册确认字段缺失或不匹配，尚未启用上报。本机身份已保留。");
            SaveIdentity(_identity with { Enabled = true, Paused = false });
            Enabled = true; Paused = false; _restoreEnabled = true; _discoveredAt = _now();
            InitializeRun();
            Status = "已开始；仅发送已选数字指标，空闲时停止持续上报。";
        }, wait: true);
    }

    public void Pause(bool paused)
    {
        Paused = paused;
        if (paused) CancelReport();
        _resetRun = true;
        var saved = _identity is null || TrySaveIdentity(_identity with { Enabled = Enabled, Paused = paused });
        if (saved) Status = paused ? "已暂停；不再发送新的实时报告。" : "已恢复；从新的统计窗口开始，不补发暂停期间数据。";
        Changed?.Invoke();
    }

    public void Disable()
    {
        Enabled = false; _restoreEnabled = false;
        CancelReport();
        _resetRun = true;
        var saved = _identity is null || TrySaveIdentity(_identity with { Enabled = false, Paused = Paused });
        if (saved) Status = "上报已停用；本机身份保留，便于再次启用或从服务移除设备。";
        Changed?.Invoke();
    }

    public Task ForgetAsync() => RunAsync(async token =>
    {
        if (_identity is null || _discovery is null) throw new InvalidOperationException("尚无已注册设备。");
        Enabled = false; _restoreEnabled = false; _resetRun = true; CancelReport();
        SaveIdentity(_identity with { Enabled = false });
        var reply = await CallAsync("DELETE", ReportEndpoint.Device(_identity.DeviceId), [], token).ConfigureAwait(false);
        if (reply?.IsSuccess != true) throw new InvalidOperationException("服务尚未确认移除设备；本机密钥保留，可稍后重试。");
        _identity = null; _service = null; _discovery = null; DraftDiscovery = null;
        ApplyPlatform(LinuxReportPlatform.Linux);
        _signer?.Dispose(); _signer = null; Profile = null; Pairing = null; AvatarPng = null;
        _scheduler = null; _window = null; _bodies.Clear(); _restoreEnabled = false;
        File.Delete(_identityPath);
        Status = "服务已确认移除设备，本机身份和密钥已删除。";
    });

    public Task RefreshProfileAsync() => RunAsync(async token =>
    {
        await ProfileChange("GET", "/v1/profile", [], token).ConfigureAwait(false);
        if (Profile?.Avatar is { } avatar && _discovery?.Extensions.ProfileAvatar == true)
        {
            var response = await ProfileCall("GET", "/v1/profile/avatar", [], token).ConfigureAwait(false);
            if (response.Status != 200 || response.Headers.GetValueOrDefault("content-type")?.Split(';')[0].Trim() != "image/png"
                || ReportHash.Sha256Hex(response.Body) != avatar.Sha256 || !ReportAvatarImage.IsUpload(response.Body))
                throw new InvalidOperationException("头像下载或校验失败。");
            AvatarPng = response.Body;
        }
        else AvatarPng = null;
        Status = "已刷新资料与设备列表。";
    });
    public Task CreateIdentityAsync() => ChangeProfile("POST", "/v1/profile/identity", "{}"u8.ToArray());
    public Task UnlinkIdentityAsync() => ChangeProfile("DELETE", "/v1/profile/identity", []);
    public Task RenameProfileAsync(string name) => RunAsync(async token =>
    {
        if (_discovery is null || ReportUsername.Validate(name, _discovery.UsernameRules) is not null) throw new ArgumentException("显示名称不符合服务要求。");
        await ProfileChange("PATCH", "/v1/profile", ReportJson.Object(("displayName", ReportJson.Of(name))).ToCompactBytes(), token).ConfigureAwait(false);
        Status = "服务已更新共享显示名称。";
    });
    public Task UploadAvatarAsync(byte[] png) => RunAsync(async token =>
    {
        if (!ReportAvatarImage.IsUpload(png)) throw new ArgumentException("头像必须是已预览的 256×256 PNG，且不超过 512 KB。");
        await ProfileChange("PUT", "/v1/profile/avatar", png, token).ConfigureAwait(false);
        AvatarPng = png.ToArray(); Status = "头像已上传。";
    });
    public Task RemoveAvatarAsync() => RunAsync(async token =>
    { await ProfileChange("DELETE", "/v1/profile/avatar", [], token).ConfigureAwait(false); AvatarPng = null; Status = "头像已移除。"; });
    public Task CreatePairingAsync() => RunAsync(async token =>
    {
        if (Profile?.SharedUserId is null) throw new InvalidOperationException("请先刷新并创建共享身份，再邀请其他设备。");
        await PairChange("POST", "/v1/profile/pairings", "{}"u8.ToArray(), 201, token).ConfigureAwait(false);
        _pairingInviter = true;
    });
    public Task ClaimPairingAsync(string code) => RunAsync(async token =>
    {
        var normalized = ReportProfileJson.NormalizeCode(code) ?? throw new ArgumentException("配对码格式无效。");
        await PairChange("POST", "/v1/profile/pairings/claim", ReportJson.Object(("code", ReportJson.Of(normalized))).ToCompactBytes(), 202, token).ConfigureAwait(false);
        _pairingInviter = false;
        Pairing = Pairing! with { Code = null };
    });
    public Task RefreshPairingAsync() => RunAsync(token => PairChange("GET", PairPath(), [], 200, token));
    public Task ApprovePairingAsync() => RunAsync(async token =>
    {
        if (!CanApprovePairing) throw new InvalidOperationException("只有生成配对码的原设备能够批准有效的待配对设备，请先刷新并核对设备。");
        await PairChange("POST", PairPath() + "/approve", "{}"u8.ToArray(), 200, token).ConfigureAwait(false);
        if (Pairing?.State != "approved") throw new InvalidOperationException("服务尚未确认配对批准。");
        Pairing = Pairing with { Code = null }; Status = "配对已批准，可刷新资料查看设备。";
    });
    public Task CancelPairingAsync() => RunAsync(async token =>
    {
        var response = await ProfileCall("DELETE", PairPath(), [], token).ConfigureAwait(false);
        if (response.Status != 204) throw new InvalidOperationException("服务未确认取消配对。");
        Pairing = null; _pairingInviter = false; Status = "配对已取消。";
    });

    public static byte[] PrepareAvatar(byte[] input)
    {
        if (ReportAvatarImage.ReadHeader(input) is not { } dimensions) throw new ArgumentException("图片过大或格式不受支持，请选择静态 PNG／JPEG。");
        using var bitmap = SKBitmap.Decode(input) ?? throw new ArgumentException("无法解码头像图片。");
        if (bitmap.Width != dimensions.Width || bitmap.Height != dimensions.Height) throw new ArgumentException("图片尺寸信息不一致。");
        using var output = new SKBitmap(256, 256, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(output))
        {
            canvas.Clear(SKColors.Transparent);
            var side = Math.Min(bitmap.Width, bitmap.Height);
            var source = new SKRect((bitmap.Width - side) / 2f, (bitmap.Height - side) / 2f, (bitmap.Width + side) / 2f, (bitmap.Height + side) / 2f);
            canvas.DrawBitmap(bitmap, source, new SKRect(0, 0, 256, 256));
        }
        using var image = SKImage.FromBitmap(output);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var png = encoded.ToArray();
        if (!ReportAvatarImage.IsUpload(png)) throw new ArgumentException("处理后的头像仍不符合上传限制。");
        return png;
    }

    public async Task TickAsync()
    {
        if (_disposed || !Enabled || Paused || !await _serial.WaitAsync(0).ConfigureAwait(false)) return;
        if (_disposed) { _serial.Release(); return; }
        IsBusy = true;
        try
        {
            if (!Enabled || Paused || _discovery is null || _signer is null) return;
            if (_discoveredAt is not { } fetched || _now() - fetched > TimeSpan.FromHours(24))
            {
                var fresh = await _api.DiscoverAsync(_service!, cancellationToken: _lifetime.Token).ConfigureAwait(false);
                if (fresh is not ReportDiscoveryOutcome.Document document || !document.Discovery.SupportsThisClient
                    || ReportConsentPin.Of(_discovery).NeedsReview(document.Discovery, _identity?.ApiKey is not null))
                { Enabled = false; Status = "服务声明需要重新确认，上报已停止。"; return; }
                _discovery = document.Discovery; _discoveredAt = _now();
            }
            InitializeRun();
            ReportSample sample; lock (_snapshotGate) sample = _sample;
            _title.Update(sample.Value(Metric), Redline, _monotonic() / 1000d);
            var active = sample.IsWorking || sample.Live.Output > 0 || _window!.HasTokenIncrement(sample);
            await ApplyActions(_scheduler!.Tick(_monotonic(), active, blocked: false), sample, _lifetime.Token).ConfigureAwait(false);
            _window?.Observe(sample);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is IOException or CryptographicException or ReportTransportException or InvalidOperationException)
        { Status = "上报暂未完成：" + exception.Message; }
        finally { IsBusy = false; _serial.Release(); if (!_disposed) Changed?.Invoke(); }
    }

    private async Task ApplyActions(IReadOnlyList<ReportAction> actions, ReportSample sample, CancellationToken token)
    {
        foreach (var action in actions)
        {
            if (!Enabled || Paused || _scheduler is null || _window is null) return;
            switch (action)
            {
                case ReportAction.Discard discard: _window.Discard(sample, discard.KeepingTick); break;
                case ReportAction.Build build:
                    var options = new ReportOptions((_identity?.Sections ?? []).Select(ReportSections.Parse).Where(section => section.HasValue)
                        .Select(section => section!.Value).Where(_discovery!.Sections.Contains).ToHashSet(),
                        _discovery!.Limits.MaxModels, _discovery.Limits.SendLimitBytes, Metric, Redline, PriceVersion)
                    { Extensions = _discovery.Extensions };
                    _bodies[build.Ticket.Seq] = ReportBody.Data(new(_client, DeviceId, Username, _runId, build.Ticket.Seq,
                        _now().ToUnixTimeMilliseconds(), build.Ticket.WindowMs, build.Ticket.Activity, _title.Level), _window.Take(sample), sample, options);
                    break;
                case ReportAction.Send send when _bodies.TryGetValue(send.Ticket.Seq, out var body):
                    using (var round = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        lock (_snapshotGate)
                        {
                            if (!Enabled || Paused) return;
                            _reportRound = round;
                        }
                        try
                        {
                            round.Token.ThrowIfCancellationRequested();
                            var reply = await CallAsync("POST", ReportEndpoint.Reports, body, round.Token).ConfigureAwait(false);
                            round.Token.ThrowIfCancellationRequested();
                            if (!Enabled || Paused || _scheduler is null) return;
                            var result = ReportOutcome.Classify(reply, hasPendingKey: false);
                            await ApplyActions(_scheduler.Finish(send.Ticket, result.Outcome, _monotonic()), sample, token).ConfigureAwait(false);
                            if (result.Reaction is not ReportReaction.NoReaction)
                            { Enabled = false; Status = "服务要求重新确认身份或声明，上报已停止：" + Diagnostics.LastResult + "。请检查后重新开始。"; }
                            else Status = reply?.IsSuccess == true ? "实时报告收到成功响应；服务间隔与退避规则生效。"
                                : "实时报告暂未成功：" + Diagnostics.LastResult + "，按服务退避规则重试。";
                        }
                        finally { lock (_snapshotGate) _reportRound = null; }
                    }
                    break;
                case ReportAction.Delivered delivered: _bodies.Remove(delivered.Ticket.Seq); break;
                case ReportAction.Lost lost: _bodies.Remove(lost.Ticket.Seq); break;
            }
        }
    }

    private void InitializeRun()
    {
        if (_resetRun) { _scheduler = null; _window = null; _bodies.Clear(); _resetRun = false; }
        if (_scheduler is not null || _discovery is null) return;
        ReportSample sample; lock (_snapshotGate) sample = _sample;
        _scheduler = new(_discovery.Limits, jitter: () => 1);
        _title = new();
        _scheduler.StartRun(); _window = new(sample); _runId = ReportCanonical.NewUuid(); _bodies.Clear();
    }
    private Task ChangeProfile(string method, string path, byte[] body) => RunAsync(async token =>
    { await ProfileChange(method, path, body, token).ConfigureAwait(false); Status = "服务已确认资料变更。"; });
    private async Task ProfileChange(string method, string path, byte[] body, CancellationToken token)
    {
        var response = await ProfileCall(method, path, body, token).ConfigureAwait(false);
        Profile = response.Status is 200 or 201 ? ReportProfileJson.Profile(response.Body, _discovery!.UsernameRules) : null;
        if (Profile is null) throw new InvalidOperationException($"服务未返回有效资料（HTTP {response.Status}），请核对接口声明与服务响应。");
    }
    private Task Pair(string method, string path, byte[] body, int status) => RunAsync(token => PairChange(method, path, body, status, token));
    private async Task PairChange(string method, string path, byte[] body, int status, CancellationToken token)
    {
        var response = await ProfileCall(method, path, body, token).ConfigureAwait(false);
        Pairing = response.Status == status ? ReportProfileJson.Pairing(response.Body, _now()) : null;
        if (Pairing is null) throw new InvalidOperationException("服务未返回有效配对状态。");
        Status = "配对状态：" + Pairing.State + "；只有原设备明确批准后才会合并身份。";
    }
    private string PairPath() => "/v1/profile/pairings/" + (Pairing?.Id ?? throw new InvalidOperationException("请先创建或认领配对码。"));
    private async Task<ReportHttpResponse> ProfileCall(string method, string path, byte[] body, CancellationToken token)
    {
        if (_identity is null || _discovery is null || _signer is null || !ReportProfileEndpoint.Allows(_discovery.Extensions, method, path))
            throw new InvalidOperationException("此服务未声明支持该资料功能。不会尝试未声明的接口。");
        var request = Request(method, path, body);
        var avatar = path == "/v1/profile/avatar";
        if (avatar)
        {
            var headers = request.Headers.Where(header => header.Key is not ("Accept" or "Content-Type")).ToList();
            headers.Add(new("Accept", method == "GET" ? "image/png" : "application/json"));
            if (method == "PUT") headers.Add(new("Content-Type", "image/png"));
            request = request with { Headers = headers };
        }
        return await new ReportProfileApi(_transport).SendAsync(request, avatar && method == "GET", token).ConfigureAwait(false)
            ?? throw new IOException("资料接口暂时无法访问。");
    }
    private ReportRequest Request(string method, string path, byte[] body) =>
        new ReportRequestBuilder(_client.UserAgent, DeviceId, _identity?.ApiKey)
            .Make(method, _discovery!.Api.Endpoint(path), body, _signer!, _now().ToUnixTimeSeconds());
    private async Task<ReportReply?> CallAsync(string method, string path, byte[] body, CancellationToken token)
    {
        var isReport = path == ReportEndpoint.Reports;
        var request = Request(method, path, body);
        var previous = Diagnostics;
        Volatile.Write(ref _diagnostics, previous with
        {
            LastAttemptAt = _now(), LastReportAttemptAt = isReport ? _now() : previous.LastReportAttemptAt,
            LastOperation = isReport ? "实时上报" : path == ReportEndpoint.Devices ? "注册设备" : "设备操作",
            HttpStatus = null, ErrorCode = null, ErrorField = null, NetworkFailure = null,
            ReportAttempts = previous.ReportAttempts + (isReport ? 1 : 0), LastResult = "等待响应",
        });
        Changed?.Invoke();
        ReportCallResult result;
        try { result = await _api.CallAsync(request, token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            Volatile.Write(ref _diagnostics, Diagnostics with { LastResult = "请求已取消" });
            throw;
        }
        var reply = result.Reply;
        var code = SafeDiagnosticCode(reply?.Code, request);
        var field = SafeDiagnosticField(reply?.Field, request);
        var network = result.Error?.Kind;
        var text = reply is not null ? "HTTP " + reply.Status + (code is null ? "" : " · " + code)
            + (field is null ? "" : " · 字段 " + field) : NetworkFailureText(network);
        var sent = Diagnostics;
        Volatile.Write(ref _diagnostics, sent with
        {
            HttpStatus = reply?.Status, ErrorCode = code, ErrorField = field, NetworkFailure = network, LastResult = text,
            ReportsAccepted = sent.ReportsAccepted + (isReport && reply?.IsSuccess == true ? 1 : 0),
            LastReportSuccessAt = isReport && reply?.IsSuccess == true ? _now() : sent.LastReportSuccessAt,
        });
        return reply;
    }

    private bool ContainsSecret(string text, ReportRequest request) =>
        new[] { _identity?.ApiKey, _identity?.PrivateKey, request.Header(ReportCanonical.SignatureHeader), request.Header("Authorization") }
            .Any(secret => !string.IsNullOrEmpty(secret) && text.Contains(secret, StringComparison.Ordinal));

    private string? SafeDiagnosticCode(string? code, ReportRequest request) =>
        code is not null && ReportReply.IsCode(code) && !ContainsSecret(code, request) ? code : null;

    private string? SafeDiagnosticField(string? field, ReportRequest request)
    {
        if (field is null || field.Length > 96 || ContainsSecret(field, request)) return null;
        // Only schema member paths are eligible; never display arbitrary server text or a reflected value.
        var members = new HashSet<string>(StringComparer.Ordinal)
        {
            "schema", "client", "app", "version", "platform", "osMajor", "deviceId", "username", "publicKey", "kty", "crv", "x", "y",
            "runId", "seq", "sentAtMs", "windowMs", "activity", "title", "level", "metric", "redline", "live", "output", "total",
            "estimatedShare", "agents", "generating", "tools", "usage", "requests", "input", "cachedInput", "cacheWriteInput", "reasoning",
            "measuredOutput", "measuredMs", "cost", "knownShare", "unpricedRequests", "priceVersion", "sources", "models", "modelId",
            "source", "traffic", "status", "includesTools", "rx", "tx", "bytes", "peak", "executionModes", "modelHarnessRequests",
        };
        var parts = field.TrimStart('/').Replace('/', '.').Replace('[', '.').Replace("]", "", StringComparison.Ordinal).Split('.');
        return parts.Length > 0 && parts.All(part => members.Contains(part) || part.Length is > 0 and <= 3 && part.All(char.IsAsciiDigit))
            ? field : null;
    }

    private static string NetworkFailureText(ReportTransportErrorKind? kind) => kind switch
    {
        ReportTransportErrorKind.Timeout => "网络请求超时",
        ReportTransportErrorKind.Offline => "网络连接失败",
        ReportTransportErrorKind.Tls => "TLS 或证书验证失败",
        ReportTransportErrorKind.DisallowedRedirect => "服务返回了不允许的重定向",
        ReportTransportErrorKind.TooLarge => "服务响应超过大小限制",
        _ => "网络请求未完成",
    };

    private async Task RunAsync(Func<CancellationToken, Task> action, bool wait = false)
    {
        if (_disposed) return;
        if (wait)
        {
            try { await _serial.WaitAsync(_lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
        else if (!await _serial.WaitAsync(0).ConfigureAwait(false)) return;
        if (_disposed) { _serial.Release(); return; }
        try { IsBusy = true; Changed?.Invoke(); await action(_lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { Status = "操作已取消。"; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or CryptographicException or ReportTransportException)
        { Status = "操作未完成：" + exception.Message; }
        finally { IsBusy = false; _serial.Release(); if (!_disposed) Changed?.Invoke(); }
    }
    private void LoadIdentity()
    {
        if (!File.Exists(_identityPath)) return;
        try
        {
            if (new FileInfo(_identityPath) is { Length: > 65_536 } || new FileInfo(_identityPath).LinkTarget is not null) throw new IOException();
            var state = JsonSerializer.Deserialize<Identity>(File.ReadAllText(_identityPath)) ?? throw new IOException();
            if (!Enum.IsDefined(state.PlatformMode)) throw new IOException();
            var service = ReportServiceUrl.Parse(state.Service) ?? throw new IOException();
            if (service.IsLoopbackHttp && _ownsTransport) throw new IOException();
            var discovery = ReportDiscovery.Parse(Encoding.UTF8.GetBytes(state.Discovery), service, out _) ?? throw new IOException();
            if (!ReportCanonical.IsDeviceId(state.DeviceId) || ReportUsername.Validate(state.Username, discovery.UsernameRules) is not null) throw new IOException();
            _signer = SoftwareReportSigner.FromPkcs8(Convert.FromBase64String(state.PrivateKey));
            _identity = state; _service = service; _discovery = discovery; DraftDiscovery = discovery; _draftService = service;
            ApplyPlatform(state.PlatformMode);
            Paused = state.Paused; _restoreEnabled = state.Enabled;
            if (OperatingSystem.IsLinux()) File.SetUnixFileMode(_identityPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Status = "已载入本机身份；联网检查服务声明后才恢复已同意的上报。";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or FormatException or CryptographicException)
        { _stateUnreadable = true; Status = "本机身份文件无法读取，保持关闭且未修改，请保留文件后检查。"; }
    }
    private void ApplyPlatform(LinuxReportPlatform mode)
    {
        PlatformMode = mode;
        _client = mode == LinuxReportPlatform.WindowsCompatibility
            ? new(_version, "Windows", 10) : new(_version, "Linux", UbuntuMajor());
        _api = new(_transport, _client.UserAgent);
    }
    private bool TrySaveIdentity(Identity state)
    {
        try { SaveIdentity(state); return true; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { Status = "本机身份设置未能保存：" + exception.Message; return false; }
    }
    private void SaveIdentity(Identity state)
    {
        Directory.CreateDirectory(_directory);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var temporary = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options)) JsonSerializer.Serialize(stream, state);
            File.Move(temporary, _identityPath, overwrite: true);
            _identity = state;
        }
        finally { File.Delete(temporary); }
    }
    private static int UbuntuMajor()
    {
        try
        {
            var value = File.ReadLines("/etc/os-release").FirstOrDefault(line => line.StartsWith("VERSION_ID=", StringComparison.Ordinal))?.Split('=', 2)[1].Trim('"').Split('.')[0];
            if (int.TryParse(value, out var major) && major is >= 10 and <= 99) return major;
        }
        catch (IOException) { }
        return 22;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _timer?.Dispose(); CancelReport(); _lifetime.Cancel();
        if (_ownsTransport && _transport is IDisposable disposable) disposable.Dispose();
        _ = DisposeSecretsWhenIdle();
    }
    private void CancelReport()
    {
        CancellationTokenSource? round; lock (_snapshotGate) round = _reportRound;
        try { round?.Cancel(); }
        catch (ObjectDisposedException) { }
    }
    private async Task DisposeSecretsWhenIdle()
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try { _signer?.Dispose(); _lifetime.Dispose(); }
        finally { _serial.Release(); }
    }
}
