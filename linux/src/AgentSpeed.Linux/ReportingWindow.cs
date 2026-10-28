using System.Diagnostics;
using AgentSpeed.Core.Reporting;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace AgentSpeed.Linux;

public sealed class ReportingWindow : Window
{
    private readonly LinuxReportService _service;
    private readonly TextBox _endpoint = new() { Name = "ReportEndpoint", PlaceholderText = "https://你的上报服务" };
    private readonly TextBox _username = new() { Name = "ReportUsername", PlaceholderText = "你选择的公开用户名" };
    private readonly TextBox _apiKey = new() { Name = "ReportApiKey", PasswordChar = '●', PlaceholderText = "服务要求时填写 API 密钥" };
    private readonly CheckBox _consent = new() { Name = "ReportConsent", Content = "我已阅读服务声明，同意向列出的 API 发送所选数字指标" };
    private readonly CheckBox _sources = new() { Content = "各来源数字" };
    private readonly CheckBox _models = new() { Content = "模型名称与调用数字" };
    private readonly CheckBox _cost = new() { Content = "API 等值费用" };
    private readonly CheckBox _traffic = new() { Content = "可用的 agent 流量（不发送整机流量）" };
    private readonly ComboBox _platform = new() { Name = "ReportPlatform", ItemsSource = new[] { "Linux（原生标识）", "Windows（兼容旧服务）" } };
    private LinuxReportPlatform _boundPlatform;
    private readonly TextBlock _declaration = Ui.Label("请先检查服务声明。", 11, Ui.Muted);
    private readonly TextBlock _status = Ui.Label("", 12, Ui.Muted);
    private readonly TextBlock _diagnostics = Ui.Label("", 11, Ui.Muted);
    private readonly TextBlock _device = Ui.Label("", 11, Ui.Muted);
    private readonly TextBlock _capabilities = Ui.Label("", 11, Ui.Muted);
    private readonly Image _avatar = new() { Width = 100, Height = 100 };
    private readonly TextBox _displayName = new() { PlaceholderText = "共享显示名称" };
    private readonly TextBox _pairCode = new() { Name = "ReportPairCode", PlaceholderText = "输入另一设备的一次性配对码" };
    private readonly TextBlock _pairing = Ui.Label("", 12);
    private readonly StackPanel _devices = new() { Spacing = 5 };
    private readonly Button _enable;
    private readonly Button _pause;
    private readonly Button _upload;
    private readonly Button _approve;
    private readonly List<(Button Button, Func<bool> Allowed)> _actions = [];
    private byte[]? _pendingAvatar;
    private ReportDiscovery? _shownDeclaration;
    private byte[]? _shownAvatar;

    public ReportingWindow(LinuxReportService service)
    {
        _service = service;
        Title = "Agent Speed · 上报与多设备";
        Width = 780; Height = 850; MinWidth = 600; MinHeight = 600;
        Background = Ui.Background; Foreground = Ui.Text;
        _endpoint.Text = service.Endpoint; _username.Text = service.Username;
        _boundPlatform = service.PlatformMode;
        _platform.SelectedIndex = service.PlatformMode == LinuxReportPlatform.WindowsCompatibility ? 1 : 0;
        foreach (var (box, section) in new[] { (_sources, ReportSection.Sources), (_models, ReportSection.Models), (_cost, ReportSection.Cost), (_traffic, ReportSection.Traffic) })
            box.IsChecked = service.SelectedSections.Contains(section);
        var content = new StackPanel { Spacing = 14, Margin = new Thickness(24) };
        content.Children.Add(Ui.Label("上报与多设备", 26));
        Note(content, "默认关闭。开始后只分享数字指标，不发送对话、工具命令、文件或项目名；头像与配对仅在你点击时操作。");
        _status.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        content.Children.Add(_status);
        content.Children.Add(_endpoint); content.Children.Add(_username); content.Children.Add(_apiKey);
        content.Children.Add(Ui.Label("上报平台标识", 12, Ui.Muted)); content.Children.Add(_platform);
        Note(content, "仅为所选服务选择兼容标识；Windows 模式将注册、报告和请求标识为 Windows 10，实际系统仍为 Ubuntu。修改后点击“同意并开始上报”应用，设备身份和已选内容继续保留。");
        content.Children.Add(Action("检查服务声明", () => service.DiscoverAsync(_endpoint.Text ?? ""), () => true));
        _declaration.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        content.Children.Add(_declaration);
        var links = new WrapPanel { ItemSpacing = 8 };
        links.Children.Add(Ui.Button("查看服务条款", () => Open(service.DraftDiscovery?.Terms)));
        links.Children.Add(Ui.Button("查看隐私声明", () => Open(service.DraftDiscovery?.Privacy)));
        content.Children.Add(links);
        content.Children.Add(_consent);
        var sections = new WrapPanel { ItemSpacing = 14, LineSpacing = 8 };
        foreach (var control in new[] { _sources, _models, _cost, _traffic }) sections.Children.Add(control);
        content.Children.Add(sections);
        _enable = Action("同意并开始上报", async () =>
        {
            var selected = new HashSet<ReportSection>();
            if (_sources.IsChecked == true) selected.Add(ReportSection.Sources);
            if (_models.IsChecked == true) selected.Add(ReportSection.Models);
            if (_cost.IsChecked == true) selected.Add(ReportSection.Cost);
            if (_traffic.IsChecked == true) selected.Add(ReportSection.Traffic);
            await service.EnableWithPlatformAsync(_username.Text ?? "", _apiKey.Text, selected,
                _platform.SelectedIndex == 1 ? LinuxReportPlatform.WindowsCompatibility : LinuxReportPlatform.Linux);
            if (service.Enabled) _apiKey.Text = "";
        }, () => _consent.IsChecked == true && service.DraftDiscovery is not null
            && ReportServiceUrl.Parse(_endpoint.Text)?.Text == service.DraftEndpoint);
        content.Children.Add(_enable);
        _consent.IsCheckedChanged += (_, _) => Update();
        _endpoint.TextChanged += (_, _) => { _consent.IsChecked = false; Update(); };
        _platform.SelectionChanged += (_, _) => { _consent.IsChecked = false; Update(); };
        var controls = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        _pause = Ui.Button("暂停", () => service.Pause(!service.Paused));
        controls.Children.Add(_pause);
        controls.Children.Add(Ui.Button("停用上报", service.Disable));
        controls.Children.Add(Action("从服务移除此设备并删除本机密钥", service.ForgetAsync, () => service.HasIdentity));
        content.Children.Add(controls);
        _status.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        _device.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        _diagnostics.Name = "ReportDiagnostics";
        _diagnostics.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        content.Children.Add(_diagnostics); content.Children.Add(_device);

        var profile = new StackPanel { Spacing = 10 };
        profile.Children.Add(Ui.Label("头像与共享身份", 19));
        _capabilities.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        profile.Children.Add(_capabilities);
        profile.Children.Add(Action("刷新资料与设备", service.RefreshProfileAsync, () => service.Discovery?.Extensions.HasProfile == true && service.HasIdentity));
        profile.Children.Add(_avatar);
        var avatarButtons = new WrapPanel { ItemSpacing = 8 };
        avatarButtons.Children.Add(Action("选择并预览头像…", ChooseAvatar, () => service.Discovery?.Extensions.ProfileAvatar == true && service.HasIdentity));
        _upload = Action("上传已预览头像", async () =>
        {
            if (_pendingAvatar is not null) await service.UploadAvatarAsync(_pendingAvatar);
            if (service.Profile?.Avatar is not null) _pendingAvatar = null;
        }, () => _pendingAvatar is not null && service.Discovery?.Extensions.ProfileAvatar == true);
        avatarButtons.Children.Add(_upload);
        avatarButtons.Children.Add(Action("移除头像", async () => { await service.RemoveAvatarAsync(); _pendingAvatar = null; }, () => service.Discovery?.Extensions.ProfileAvatar == true && service.HasIdentity));
        profile.Children.Add(avatarButtons);
        profile.Children.Add(_displayName);
        profile.Children.Add(Action("保存共享显示名称", () => service.RenameProfileAsync(_displayName.Text ?? ""), () => service.Discovery?.Extensions.SharedIdentity == true));
        profile.Children.Add(Action("创建共享身份", service.CreateIdentityAsync, () => service.Discovery?.Extensions.SharedIdentity == true && service.Profile?.SharedUserId is null));
        profile.Children.Add(Action("解绑本机共享身份", service.UnlinkIdentityAsync, () => service.Discovery?.Extensions.SharedIdentity == true && service.Profile?.SharedUserId is not null));
        content.Children.Add(Ui.Card(profile));

        var pairing = new StackPanel { Spacing = 10 };
        pairing.Children.Add(Ui.Label("设备配对", 19));
        Note(pairing, "另一设备认领配对码后，需要在原设备核对并批准。解绑只影响本机身份关联，不删除其他设备的密钥。");
        pairing.Children.Add(Action("生成一次性配对码", service.CreatePairingAsync, () => service.Discovery?.Extensions.SharedIdentity == true && service.Profile?.SharedUserId is not null));
        pairing.Children.Add(_pairCode);
        pairing.Children.Add(Action("认领输入的配对码", async () => { await service.ClaimPairingAsync(_pairCode.Text ?? ""); if (service.Pairing is not null) _pairCode.Text = ""; }, () => service.Discovery?.Extensions.SharedIdentity == true));
        _pairing.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        pairing.Children.Add(_pairing);
        var pairButtons = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        pairButtons.Children.Add(Action("刷新配对状态", service.RefreshPairingAsync, () => service.Pairing is not null));
        _approve = Action("核对设备后批准配对", service.ApprovePairingAsync, () => service.CanApprovePairing);
        pairButtons.Children.Add(_approve);
        pairButtons.Children.Add(Action("取消配对", service.CancelPairingAsync, () => service.Pairing is not null));
        pairing.Children.Add(pairButtons); pairing.Children.Add(_devices);
        content.Children.Add(Ui.Card(pairing));
        Content = new ScrollViewer { Content = content };
        service.Changed += Changed;
        Closed += (_, _) => { service.Changed -= Changed; if (_avatar.Source is Bitmap bitmap) bitmap.Dispose(); };
        Update();
    }

    private Button Action(string label, Func<Task> action, Func<bool> allowed)
    {
        var button = Ui.Button(label, async () => { await action(); Update(); });
        _actions.Add((button, allowed));
        return button;
    }
    private async Task ChooseAvatar()
    {
        var paths = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择头像", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("静态 PNG／JPEG") { Patterns = ["*.png", "*.jpg", "*.jpeg"] }],
        });
        if (paths.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        try
        {
            if (new FileInfo(path).Length > ReportAvatarImage.MaxInputBytes) throw new ArgumentException("头像图片不能超过 10 MB。");
            _pendingAvatar = LinuxReportService.PrepareAvatar(await File.ReadAllBytesAsync(path));
            ShowAvatar(_pendingAvatar); Update();
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException)
        { _status.Text = "头像未处理：" + exception.Message; }
    }
    private void Changed() => Dispatcher.UIThread.Post(Update);
    private void Update()
    {
        _status.Text = _service.Status;
        _platform.IsEnabled = !_service.IsBusy;
        if (_boundPlatform != _service.PlatformMode)
        {
            _boundPlatform = _service.PlatformMode;
            _platform.SelectedIndex = _boundPlatform == LinuxReportPlatform.WindowsCompatibility ? 1 : 0;
        }
        _diagnostics.Text = _service.DiagnosticSummary;
        _device.Text = _service.HasIdentity ? "当前服务：" + _service.Endpoint + "\n设备：" + _service.DeviceId + " · 用户名：" + _service.Username : "尚未注册设备。";
        _pause.Content = _service.Paused ? "恢复上报" : "暂停上报";
        _pause.IsEnabled = _service.Enabled;
        var declaration = _service.DraftDiscovery;
        if (!ReferenceEquals(declaration, _shownDeclaration))
        {
            _shownDeclaration = declaration;
            _consent.IsChecked = false;
        }
        _declaration.Text = declaration is null ? "尚未取得服务声明，无法注册或开始上报。"
            : $"服务：{declaration.Name}\nAPI：{declaration.Api.Text}\n条款：{declaration.Terms}\n隐私：{declaration.Privacy}\n声明的数据保留：{declaration.RetentionDays?.ToString() ?? "未说明"} 天";
        foreach (var (checkbox, section) in new[] { (_sources, ReportSection.Sources), (_models, ReportSection.Models), (_cost, ReportSection.Cost), (_traffic, ReportSection.Traffic) })
        {
            checkbox.IsEnabled = declaration?.Sections.Contains(section) == true;
            if (!checkbox.IsEnabled) checkbox.IsChecked = false;
            ToolTip.SetTip(checkbox, checkbox.IsEnabled ? "仅在勾选并开始后分享" : "服务未声明支持此项");
        }
        var extensions = _service.Discovery?.Extensions;
        _capabilities.Text = extensions is null ? "服务能力未知。请先完成检查与注册。"
            : "头像：" + (extensions.ProfileAvatar ? "服务支持" : "服务未声明支持") + "；共享身份／配对：" + (extensions.SharedIdentity ? "服务支持" : "服务未声明支持");
        foreach (var (button, allowed) in _actions) button.IsEnabled = !_service.IsBusy && allowed();
        if (!_displayName.IsFocused) _displayName.Text = _service.Profile?.DisplayName ?? _service.Username;
        if (_pendingAvatar is null)
        {
            if (_service.AvatarPng is { } png) ShowAvatar(png);
            else if (_avatar.Source is Bitmap previous) { previous.Dispose(); _avatar.Source = null; _shownAvatar = null; }
        }
        _pairing.Text = _service.Pairing is { } pair ? $"状态：{pair.State} · 过期：{pair.ExpiresAt.ToLocalTime():HH:mm:ss}\n"
            + (pair.Code is null ? "" : "一次性配对码：" + pair.Code + "\n")
            + (pair.Claimant is null ? "" : "待批准设备：" + pair.Claimant.Platform + " · " + pair.Claimant.DeviceId) : "当前没有配对。";
        _devices.Children.Clear();
        foreach (var device in _service.Profile?.Devices ?? [])
            _devices.Children.Add(Ui.Label(device.Platform + " · " + (device.IsCurrent ? "本机" : device.DeviceId[..8]) + " · " + (device.Online ? "在线" : "离线"), 11, Ui.Muted));
    }
    private void ShowAvatar(byte[] png)
    {
        if (ReferenceEquals(png, _shownAvatar)) return;
        if (_avatar.Source is Bitmap previous) previous.Dispose();
        _avatar.Source = new Bitmap(new MemoryStream(png));
        _shownAvatar = png;
    }
    private static void Note(Panel panel, string value)
    {
        var text = Ui.Label(value, 11, Ui.Muted); text.TextWrapping = Avalonia.Media.TextWrapping.Wrap; panel.Children.Add(text);
    }
    private static void Open(string? url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0)
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}
