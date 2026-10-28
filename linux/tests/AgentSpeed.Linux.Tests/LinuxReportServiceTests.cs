using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentSpeed.Core;
using AgentSpeed.Core.Reporting;
using SkiaSharp;

namespace AgentSpeed.Linux.Tests;

public sealed class LinuxReportServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agent-speed-report-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Noon = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReportingIsOffWithoutAnEndpointAndDiscoveryDoesNotRegisterOrCreateKeys()
    {
        var server = new FakeServer();
        using var report = Service(server);
        report.Start(); report.Observe(SpeedSnapshot.Empty with { State = MonitorState.Running, Live = new Rate(100) });
        await report.TickAsync();
        Assert.Equal(0, server.Gets); Assert.Empty(server.Requests);
        Assert.False(File.Exists(report.IdentityPath));
        await report.DiscoverAsync("https://report.example.test");
        Assert.NotNull(report.DraftDiscovery);
        Assert.Empty(server.Requests);
        Assert.False(report.Enabled);
        Assert.False(File.Exists(report.IdentityPath));
    }

    [Fact]
    public async Task WindowsCompatibilityUsesOnePlatformForRegistrationReportsProfilesAndRestart()
    {
        var server = new FakeServer(); long monotonic = 0;
        using var report = Service(server, () => monotonic);
        Assert.Equal(LinuxReportPlatform.Linux, report.PlatformMode);
        report.Observe(SpeedSnapshot.Empty with { State = MonitorState.Running, Live = new Rate(100) });
        await report.DiscoverAsync("https://report.example.test");
        await report.EnableWithPlatformAsync("Ubuntu Tester", null, new HashSet<ReportSection> { ReportSection.Sources }, LinuxReportPlatform.WindowsCompatibility);
        Assert.True(report.Enabled); Assert.Equal(LinuxReportPlatform.WindowsCompatibility, report.PlatformMode);
        await report.TickAsync(); monotonic = 5000; await report.TickAsync();
        Assert.Contains(server.Requests, request => request.Url.AbsolutePath == "/v1/reports");
        foreach (var request in server.Requests.Where(request => request.Url.AbsolutePath is "/v1/devices" or "/v1/reports"))
        {
            var client = JsonDocument.Parse(request.Body).RootElement.GetProperty("client");
            Assert.Equal("Windows", client.GetProperty("platform").GetString());
            Assert.Equal(10, client.GetProperty("osMajor").GetInt32());
            Assert.Contains("(Windows)", request.Header("User-Agent"));
        }
        await report.RefreshProfileAsync();
        Assert.Contains("(Windows)", server.Requests.Last().Header("User-Agent"));
        Assert.True(server.AllSignaturesValid);
        var id = report.DeviceId;
        report.Disable();
        using var restored = Service(server);
        Assert.Equal(LinuxReportPlatform.WindowsCompatibility, restored.PlatformMode);
        Assert.Equal(id, restored.DeviceId); Assert.False(restored.Enabled);
        Assert.Equal(new HashSet<ReportSection> { ReportSection.Sources }, restored.SelectedSections);
        await restored.DiscoverAsync("https://report.example.test");
        Assert.Contains("(Windows)", server.DiscoveryAgents.Last());
        await restored.EnableAsync("Ubuntu Tester", null, restored.SelectedSections);
        Assert.True(restored.Enabled);
        Assert.Equal("Windows", JsonDocument.Parse(server.Requests.Last().Body).RootElement.GetProperty("client").GetProperty("platform").GetString());
        await restored.ForgetAsync();
        Assert.False(restored.HasIdentity);
        Assert.Equal(LinuxReportPlatform.Linux, restored.PlatformMode);
        Assert.Empty(restored.SelectedSections);
    }

    [Fact]
    public async Task FailedNativeRegistrationCanUseWindowsCompatibilityWithoutLosingIdentityOrSharingChoices()
    {
        var server = new FakeServer { RegistrationResponse = _ => Response(422, new
            { error = new { code = "payload_invalid", field = "/client/platform" } }) };
        using var report = Service(server);
        await report.DiscoverAsync("https://report.example.test");
        var sections = new HashSet<ReportSection> { ReportSection.Models, ReportSection.Cost };
        await report.EnableAsync("Ubuntu Tester", null, sections);
        var before = JsonDocument.Parse(File.ReadAllBytes(report.IdentityPath)).RootElement;
        server.RegistrationResponse = _ => Response(400, new { error = new { code = "invalid_field", field = "client.version" } });
        await report.EnableWithPlatformAsync(report.Username, null, report.SelectedSections, LinuxReportPlatform.WindowsCompatibility);
        Assert.False(report.Enabled);
        Assert.Equal(LinuxReportPlatform.WindowsCompatibility, report.PlatformMode);
        Assert.DoesNotContain("不支持 Linux", report.Status);
        var after = JsonDocument.Parse(File.ReadAllBytes(report.IdentityPath)).RootElement;
        foreach (var field in new[] { "DeviceId", "Username", "PrivateKey", "Sections" })
            Assert.Equal(before.GetProperty(field).GetRawText(), after.GetProperty(field).GetRawText());
        Assert.Equal(1, after.GetProperty("PlatformMode").GetInt32());
        Assert.False(after.GetProperty("Enabled").GetBoolean());
        server.RegistrationResponse = null;
        await report.EnableAsync(report.Username, null, report.SelectedSections);
        Assert.True(report.Enabled); Assert.True(server.AllSignaturesValid);
        Assert.Equal(before.GetProperty("DeviceId").GetString(), report.DeviceId);
    }

    [Fact]
    public async Task ChangingPlatformCancelsTheOldReportBeforeRegisteringTheSameIdentityAgain()
    {
        var server = new FakeServer { GateReports = true }; long monotonic = 0;
        using var report = Service(server, () => monotonic);
        report.Observe(SpeedSnapshot.Empty with { State = MonitorState.Running, Live = new Rate(100) });
        await report.DiscoverAsync("https://report.example.test");
        await report.EnableAsync("Ubuntu Tester"); var id = report.DeviceId;
        await report.TickAsync(); monotonic = 5000; var oldReport = report.TickAsync();
        await server.ReportStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await report.EnableWithPlatformAsync(report.Username, null, report.SelectedSections, LinuxReportPlatform.WindowsCompatibility).WaitAsync(TimeSpan.FromSeconds(2));
        await oldReport.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(server.ReportCanceled); Assert.True(report.Enabled);
        Assert.Equal(id, report.DeviceId); Assert.Equal(LinuxReportPlatform.WindowsCompatibility, report.PlatformMode);
        Assert.Equal("/v1/devices", server.Requests.Last().Url.AbsolutePath);
        Assert.Equal("Windows", JsonDocument.Parse(server.Requests.Last().Body).RootElement.GetProperty("client").GetProperty("platform").GetString());
    }

    [Fact]
    public async Task LegacyIdentityWithoutAPlatformSettingDefaultsToNativeLinux()
    {
        var server = new FakeServer();
        using (var report = Service(server))
        {
            await report.DiscoverAsync("https://report.example.test");
            await report.EnableAsync("Ubuntu Tester"); report.Disable();
            var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(report.IdentityPath))!;
            document.AsObject().Remove("PlatformMode");
            File.WriteAllText(report.IdentityPath, document.ToJsonString());
        }
        using var restored = Service(server);
        Assert.True(restored.HasIdentity); Assert.Equal(LinuxReportPlatform.Linux, restored.PlatformMode);
        await restored.DiscoverAsync("https://report.example.test");
        Assert.Contains("(Linux)", server.DiscoveryAgents.Last());
    }

    [Fact]
    public async Task ExplicitRegistrationUsesLinuxClientASignedRequestAndPrivateStorage()
    {
        var server = new FakeServer();
        using var report = Service(server);
        await report.DiscoverAsync("https://report.example.test");
        await report.EnableAsync("Ubuntu Tester", "synthetic-api-key");
        Assert.True(report.Enabled);
        var request = Assert.Single(server.Requests);
        Assert.Equal("Bearer synthetic-api-key", request.Header("Authorization"));
        Assert.DoesNotContain("synthetic-api-key", request.ToString());
        Assert.DoesNotContain("synthetic-api-key", report.Status);
        Assert.Equal("Linux", JsonDocument.Parse(request.Body).RootElement.GetProperty("client").GetProperty("platform").GetString());
        Assert.True(server.AllSignaturesValid);
        if (OperatingSystem.IsLinux())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(report.IdentityPath));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(report.IdentityPath)!));
        }
        report.Disable();
        using var restored = Service(server);
        Assert.True(restored.HasIdentity);
        Assert.False(restored.Enabled);
        Assert.Equal(report.DeviceId, restored.DeviceId);
    }

    [Fact]
    public async Task NativeSchedulerReportsOnlyNumbersAndHonorsPauseAndDisable()
    {
        var server = new FakeServer(); long monotonic = 0;
        using var report = Service(server, () => monotonic);
        await report.DiscoverAsync("https://report.example.test");
        await report.EnableAsync("Ubuntu Tester");
        for (var i = 0; i <= 12; i++)
        {
            monotonic = i * 500;
            report.Observe(SpeedSnapshot.Empty with
            {
                State = MonitorState.Running, Timestamp = Noon.AddMilliseconds(monotonic), Live = new Rate(100, 1000),
                Agents = [new AgentStatus("/DO_NOT_SEND_PATH", SourceKind.Codex, "DO_NOT_SEND_PROJECT", "gpt-private-model", AgentPhase.Generating, false, new Rate(100), 100, Noon)],
            });
            await report.TickAsync();
        }
        var sent = server.Requests.Where(request => request.Url.AbsolutePath.EndsWith("/v1/reports", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(sent);
        Assert.All(sent, request =>
        {
            var body = Encoding.UTF8.GetString(request.Body);
            Assert.DoesNotContain("DO_NOT_SEND", body);
            Assert.DoesNotContain("gpt-private-model", body);
            Assert.False(JsonDocument.Parse(request.Body).RootElement.TryGetProperty("models", out _));
        });
        report.Pause(true);
        var before = server.Requests.Count;
        monotonic += 30_000;
        await report.TickAsync();
        Assert.Equal(before, server.Requests.Count);
        report.Disable();
        await report.TickAsync();
        Assert.Equal(before, server.Requests.Count);
        Assert.True(server.AllSignaturesValid);
    }

    [Fact]
    public async Task RadarPlatformRejectionShowsTheActualHttpCodeAndFieldAndKeepsTheIdentity()
    {
        var server = new FakeServer { RegistrationResponse = _ => Response(422, new
            { error = new { code = "payload_invalid", field = "/client/platform", message = "unsupported client" } }) };
        using var report = Service(server);
        await report.DiscoverAsync("https://report.example.test");
        await report.EnableAsync("Ubuntu Tester");
        Assert.False(report.Enabled);
        Assert.True(report.HasIdentity);
        Assert.Contains("服务端不支持 Linux 平台", report.Status);
        Assert.Contains("HTTP 422", report.Status);
        Assert.Contains("payload_invalid", report.Status);
        Assert.Contains("/client/platform", report.Status);
        Assert.Equal(422, report.Diagnostics.HttpStatus);
        Assert.Equal("payload_invalid", report.Diagnostics.ErrorCode);
        Assert.Equal("/client/platform", report.Diagnostics.ErrorField);
        Assert.Equal(0, report.Diagnostics.ReportAttempts);
        Assert.Null(report.Diagnostics.LastReportSuccessAt);
        var saved = JsonDocument.Parse(File.ReadAllBytes(report.IdentityPath)).RootElement;
        Assert.False(saved.GetProperty("Enabled").GetBoolean());
        Assert.Equal(report.DeviceId, saved.GetProperty("DeviceId").GetString());
        Assert.Equal("Linux", JsonDocument.Parse(Assert.Single(server.Requests).Body).RootElement.GetProperty("client").GetProperty("platform").GetString());
    }

    [Fact]
    public async Task GenericRegistrationFailureDoesNotGuessTheCauseOrExposeResponseSecrets()
    {
        const string apiKey = "syntheticsecretapikey";
        var server = new FakeServer { RegistrationResponse = request => Response(400, new
            { error = new { code = "invalid_field", field = "client.version", message = request.Header("Authorization") + " " + request.Header(ReportCanonical.SignatureHeader) } }) };
        using var report = Service(server);
        await report.DiscoverAsync("https://report.example.test");
        await report.EnableAsync("Ubuntu Tester", apiKey);
        Assert.False(report.Enabled);
        Assert.Contains("HTTP 400", report.Status);
        Assert.Contains("invalid_field", report.Status);
        Assert.Contains("client.version", report.Status);
        Assert.DoesNotContain("不支持 Linux", report.Status);
        Assert.DoesNotContain(apiKey, report.Status + report.DiagnosticSummary);
        Assert.DoesNotContain(Assert.Single(server.Requests).Header(ReportCanonical.SignatureHeader)!, report.Status + report.DiagnosticSummary);
    }

    [Fact]
    public async Task ReflectedCredentialsNeverEnterTheDiagnosticCodeOrField()
    {
        const string apiKey = "syntheticsecretapikey";
        var server = new FakeServer { RegistrationResponse = request => Response(400, new
            { error = new { code = apiKey, field = request.Header(ReportCanonical.SignatureHeader), message = Encoding.UTF8.GetString(request.Body) } }) };
        using var report = Service(server);
        await report.DiscoverAsync("https://report.example.test");
        await report.EnableAsync("Ubuntu Tester", apiKey);
        Assert.Null(report.Diagnostics.ErrorCode);
        Assert.Null(report.Diagnostics.ErrorField);
        var diagnostic = report.Status + report.DiagnosticSummary + JsonSerializer.Serialize(report.Diagnostics);
        Assert.DoesNotContain(apiKey, diagnostic);
        Assert.DoesNotContain("publicKey", diagnostic);
        Assert.DoesNotContain(Assert.Single(server.Requests).Header(ReportCanonical.SignatureHeader)!, diagnostic);
    }

    [Fact]
    public async Task ATransportFailureIsNotConfusedWithAnHttpRejectionOrExposedExceptionText()
    {
        const string apiKey = "syntheticsecretapikey";
        var server = new FakeServer { RegistrationFailure = new(ReportTransportErrorKind.Timeout, "secret exception " + apiKey) };
        using var report = Service(server);
        await report.DiscoverAsync("https://report.example.test");
        await report.EnableAsync("Ubuntu Tester", apiKey);
        Assert.False(report.Enabled);
        Assert.Contains("网络请求超时", report.Status);
        Assert.DoesNotContain("HTTP", report.Status);
        Assert.DoesNotContain(apiKey, report.Status + report.DiagnosticSummary);
        Assert.Null(report.Diagnostics.HttpStatus);
        Assert.Equal(ReportTransportErrorKind.Timeout, report.Diagnostics.NetworkFailure);
        Assert.Equal(Noon, report.Diagnostics.LastAttemptAt);
    }

    [Fact]
    public async Task A200ReplyWithoutRegistrationConfirmationIsNotTreatedAsARegisteredDevice()
    {
        var server = new FakeServer { RegistrationResponse = _ => Response(200, new { status = "created" }) };
        using var report = Service(server);
        await report.DiscoverAsync("https://report.example.test");
        await report.EnableAsync("Ubuntu Tester");
        Assert.False(report.Enabled);
        Assert.Contains("HTTP 200", report.Status);
        Assert.Contains("确认字段缺失或不匹配", report.Status);
        Assert.DoesNotContain("不支持 Linux", report.Status);
        Assert.Equal(200, report.Diagnostics.HttpStatus);
        Assert.Equal(0, report.Diagnostics.ReportsAccepted);
    }

    [Fact]
    public async Task RealTimeSuccessAndRejectionCountersDescribeReportsWithoutCountingRegistration()
    {
        var server = new FakeServer(); long monotonic = 0;
        using var report = Service(server, () => monotonic);
        report.Observe(SpeedSnapshot.Empty with { State = MonitorState.Running, Live = new Rate(100) });
        await report.DiscoverAsync("https://report.example.test");
        await report.EnableAsync("Ubuntu Tester");
        Assert.Equal(0, report.Diagnostics.ReportsAccepted);
        Assert.Null(report.Diagnostics.LastReportSuccessAt);
        await report.TickAsync(); monotonic = 5000; await report.TickAsync();
        Assert.Equal(1, report.Diagnostics.ReportAttempts);
        Assert.Equal(1, report.Diagnostics.ReportsAccepted);
        Assert.Equal(Noon, report.Diagnostics.LastReportAttemptAt);
        Assert.Equal(Noon, report.Diagnostics.LastReportSuccessAt);
        Assert.Contains("成功响应", report.Status);
        server.ReportResponse = _ => Response(422, new { error = new { code = "payload_invalid", field = "/live/output" } });
        monotonic = 10000; await report.TickAsync();
        Assert.False(report.Enabled);
        Assert.Equal(2, report.Diagnostics.ReportAttempts);
        Assert.Equal(1, report.Diagnostics.ReportsAccepted);
        Assert.Equal(Noon, report.Diagnostics.LastReportSuccessAt);
        Assert.Contains("HTTP 422", report.Status);
        Assert.Contains("/live/output", report.Status);
        Assert.DoesNotContain("不支持 Linux", report.Status);
    }

    [Fact]
    public async Task AReportRateLimitIsVisibleAndDoesNotIncreaseSuccessfulReports()
    {
        var server = new FakeServer { ReportResponse = _ => Response(429, new { error = new { code = "rate_limited", retryAfterSeconds = 60 } }) };
        long monotonic = 0;
        using var report = Service(server, () => monotonic);
        report.Observe(SpeedSnapshot.Empty with { State = MonitorState.Running, Live = new Rate(100) });
        await report.DiscoverAsync("https://report.example.test");
        await report.EnableAsync("Ubuntu Tester");
        await report.TickAsync(); monotonic = 5000; await report.TickAsync();
        Assert.True(report.Enabled);
        Assert.Contains("HTTP 429", report.Status);
        Assert.Contains("rate_limited", report.Status);
        Assert.Equal(1, report.Diagnostics.ReportAttempts);
        Assert.Equal(0, report.Diagnostics.ReportsAccepted);
        Assert.Null(report.Diagnostics.LastReportSuccessAt);
        monotonic = 6000; await report.TickAsync();
        Assert.Equal(1, report.Diagnostics.ReportAttempts);
    }

    [Fact]
    public async Task UnsupportedProfileCapabilitiesNeverCauseSpeculativeApiWrites()
    {
        var server = new FakeServer(extensions: false);
        using var report = Service(server);
        await report.DiscoverAsync("https://report.example.test");
        await report.EnableAsync("Ubuntu Tester");
        await report.RefreshProfileAsync();
        Assert.Single(server.Requests);
        Assert.Contains("未声明", report.Status);
        Assert.Null(report.Profile);
    }

    [Fact]
    public async Task PausingCancelsAnInFlightReportWithoutChangingThePausedStatusOrRetrying()
    {
        var server = new FakeServer { GateReports = true }; long monotonic = 0;
        using var report = Service(server, () => monotonic);
        report.Observe(SpeedSnapshot.Empty with { State = MonitorState.Running, Live = new Rate(100) });
        await report.DiscoverAsync("https://report.example.test");
        await report.EnableAsync("Ubuntu Tester");
        await report.TickAsync();
        monotonic = 5000;
        var inFlight = report.TickAsync();
        await server.ReportStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        report.Pause(true);
        await inFlight.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(server.ReportCanceled);
        Assert.True(report.Paused);
        Assert.Contains("已暂停", report.Status);
        var before = server.Requests.Count;
        monotonic = 60_000;
        await report.TickAsync();
        Assert.Equal(before, server.Requests.Count);
    }

    [Fact]
    public async Task AChangedPrivacyDeclarationStopsReportingUntilNewConsentIsGiven()
    {
        var server = new FakeServer();
        using var report = Service(server);
        await report.DiscoverAsync("https://report.example.test");
        await report.EnableAsync("Ubuntu Tester");
        server.PrivacyUrl = "https://report.example.test/new-privacy";
        await report.DiscoverAsync("https://report.example.test");
        Assert.False(report.Enabled);
        Assert.False(JsonDocument.Parse(File.ReadAllBytes(report.IdentityPath)).RootElement.GetProperty("Enabled").GetBoolean());
        var before = server.Requests.Count;
        report.Observe(SpeedSnapshot.Empty with { State = MonitorState.Running, Live = new Rate(100) });
        await report.TickAsync();
        Assert.Equal(before, server.Requests.Count);
    }

    [Fact]
    public async Task AvatarPairingApprovalUnlinkAndForgetUseTheSupportedSignedProtocol()
    {
        var server = new FakeServer();
        using var report = Service(server);
        await report.DiscoverAsync("https://report.example.test");
        await report.EnableAsync("Ubuntu Tester");
        await report.RefreshProfileAsync();
        Assert.Equal("Linux", Assert.Single(report.Profile!.Devices).Platform);
        await report.CreateIdentityAsync();
        Assert.NotNull(report.Profile!.SharedUserId);
        await report.RenameProfileAsync("Ubuntu Pair");
        Assert.Equal("Ubuntu Pair", report.Profile!.DisplayName);
        using var bitmap = new SKBitmap(32, 64);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var input = image.Encode(SKEncodedImageFormat.Png, 100);
        var png = LinuxReportService.PrepareAvatar(input.ToArray());
        Assert.True(ReportAvatarImage.IsUpload(png));
        await report.UploadAvatarAsync(png);
        Assert.NotNull(report.Profile!.Avatar);
        await report.RefreshProfileAsync();
        Assert.Equal(png, report.AvatarPng);
        await report.RemoveAvatarAsync();
        Assert.Null(report.Profile!.Avatar);
        await report.CreatePairingAsync();
        Assert.Equal("ABCDEFGH2345", report.Pairing!.Code);
        await report.RefreshPairingAsync();
        Assert.Equal("pending", report.Pairing!.State);
        await report.ApprovePairingAsync();
        Assert.Equal("approved", report.Pairing!.State);
        Assert.Null(report.Pairing.Code);
        await report.CancelPairingAsync();
        Assert.Null(report.Pairing);
        await report.ClaimPairingAsync("ABCD-EFGH-2345");
        Assert.Equal("pending", report.Pairing!.State);
        Assert.Null(report.Pairing.Code);
        Assert.False(report.CanApprovePairing);
        var beforeSelfApproval = server.Requests.Count;
        await report.ApprovePairingAsync();
        Assert.Equal(beforeSelfApproval, server.Requests.Count);
        Assert.Contains("原设备", report.Status);
        await report.UnlinkIdentityAsync();
        Assert.Null(report.Profile!.SharedUserId);
        await report.ForgetAsync();
        Assert.False(report.HasIdentity);
        Assert.False(File.Exists(report.IdentityPath));
        Assert.True(server.AllSignaturesValid);
    }

    private LinuxReportService Service(FakeServer server, Func<long>? monotonic = null) =>
        new(new AppStorage(_root), server, () => Noon, monotonic, "1.4.1-5");

    private sealed class FakeServer(bool extensions = true) : IReportTransport
    {
        public int Gets { get; private set; }
        public List<string> DiscoveryAgents { get; } = [];
        public List<ReportRequest> Requests { get; } = [];
        public bool AllSignaturesValid { get; private set; } = true;
        public string PrivacyUrl { get; set; } = "https://report.example.test/privacy";
        public bool GateReports { get; set; }
        public Func<ReportRequest, ReportHttpResponse>? RegistrationResponse { get; set; }
        public ReportTransportException? RegistrationFailure { get; set; }
        public Func<ReportRequest, ReportHttpResponse>? ReportResponse { get; set; }
        public bool ReportCanceled { get; private set; }
        public TaskCompletionSource ReportStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string _device = "";
        private string _username = "";
        private string _display = "";
        private string? _shared;
        private byte[]? _avatar;
        private ReportPublicKey? _key;
        private string _pairState = "waiting";
        private const string PairId = "11111111-1111-4111-8111-111111111111";
        public Task<ReportHttpResponse> GetAsync(Uri url, string? etag, string userAgent, int maxBytes, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Gets++;
            DiscoveryAgents.Add(userAgent);
            return Task.FromResult(Json(200, new
            {
                protocol = "agent-speed-report", versions = new[] { 1 }, api = "https://report.example.test",
                service = new { name = "Synthetic test service", terms = "https://report.example.test/terms", privacy = PrivacyUrl, retentionDays = 0 },
                username = new { minLength = 2, maxLength = 32, charset = "unicode", spaces = true },
                apiKey = new { mode = "optional" }, sections = new[] { "sources", "models", "cost", "traffic" },
                extensions = extensions ? new Dictionary<string, int> { ["profileAvatar"] = 1, ["sharedIdentity"] = 1 } : [],
            }));
        }
        public Task<ReportHttpResponse> SendAsync(ReportRequest request, int maxBytes, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var path = request.Url.AbsolutePath;
            JsonElement Body() => JsonDocument.Parse(request.Body).RootElement;
            if (path == "/v1/devices" && request.Method == "POST")
            {
                var body = Body(); _device = body.GetProperty("deviceId").GetString()!; _username = body.GetProperty("username").GetString()!;
                _display = _username; _key = ReportPublicKey.ParseJwk(body.GetProperty("publicKey"));
            }
            var canonical = ReportCanonical.Build(request.Method, ReportUrls.Authority(request.Url), ReportUrls.RequestPath(request.Url),
                request.Header(ReportCanonical.TimestampHeader)!, request.Header(ReportCanonical.NonceHeader)!, request.Header(ReportCanonical.DeviceHeader)!, request.Body);
            AllSignaturesValid &= _key?.Verify(Encoding.UTF8.GetBytes(canonical), Base64Url.Decode(request.Header(ReportCanonical.SignatureHeader)!)!) == true;
            if (path == "/v1/devices")
            {
                if (RegistrationFailure is not null) throw RegistrationFailure;
                return Task.FromResult(RegistrationResponse?.Invoke(request) ?? Json(201, new { status = "registered", deviceId = _device, username = _username }));
            }
            if (path == "/v1/reports") return GateReports ? WaitForCancellation(cancellationToken)
                : Task.FromResult(ReportResponse?.Invoke(request) ?? Json(200, new { status = "accepted", intervalSeconds = 5 }));
            if (path.StartsWith("/v1/devices/", StringComparison.Ordinal)) return Task.FromResult(Json(204, new { }));
            if (path == "/v1/profile/identity") _shared = request.Method == "POST" ? "22222222-2222-4222-8222-222222222222" : null;
            if (path == "/v1/profile" && request.Method == "PATCH") _display = Body().GetProperty("displayName").GetString()!;
            if (path == "/v1/profile/avatar")
            {
                if (request.Method == "GET") return Task.FromResult(new ReportHttpResponse(200, new Dictionary<string, string> { ["content-type"] = "image/png" }, _avatar!));
                if (request.Method == "PUT") { Assert.Equal("image/png", request.Header("Content-Type")); _avatar = request.Body; }
                if (request.Method == "DELETE") _avatar = null;
            }
            if (path.StartsWith("/v1/profile/pairings", StringComparison.Ordinal))
            {
                if (request.Method == "DELETE") return Task.FromResult(Json(204, new { }));
                if (request.Method == "GET" || path.EndsWith("/claim", StringComparison.Ordinal)) _pairState = "pending";
                if (path.EndsWith("/approve", StringComparison.Ordinal)) _pairState = "approved";
                return Task.FromResult(Json(path.EndsWith("/claim", StringComparison.Ordinal) ? 202 : path == "/v1/profile/pairings" ? 201 : 200,
                    new { id = PairId, state = _pairState, expiresAt = Noon.AddMinutes(5).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
                        code = path == "/v1/profile/pairings" ? "ABCDEFGH2345" : null,
                        claimant = _pairState == "waiting" ? null : new { deviceId = "33333333-3333-4333-8333-333333333333", platform = "Linux" } }));
            }
            return Task.FromResult(Json(200, new
            {
                sharedUserId = _shared, displayName = _display,
                avatar = _avatar is null ? null : new { url = "https://report.example.test/avatar", sha256 = ReportHash.Sha256Hex(_avatar), contentType = "image/png", width = 256, height = 256 },
                devices = new[] { new { deviceId = _device, platform = "Linux", isCurrent = true, online = true } },
            }));
        }
        private async Task<ReportHttpResponse> WaitForCancellation(CancellationToken token)
        {
            ReportStarted.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) { ReportCanceled = true; throw; }
            throw new InvalidOperationException();
        }
        private static ReportHttpResponse Json(int status, object value) => new(status, new Dictionary<string, string>(), JsonSerializer.SerializeToUtf8Bytes(value));
    }
    private static ReportHttpResponse Response(int status, object value) => new(status, new Dictionary<string, string>(), JsonSerializer.SerializeToUtf8Bytes(value));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
