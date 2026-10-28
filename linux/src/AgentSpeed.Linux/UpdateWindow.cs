using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace AgentSpeed.Linux;

public sealed class UpdateWindow : Window
{
    private readonly LinuxUpdateService _service;
    private readonly TextBlock _status = Ui.Label("", 12, Ui.Muted);
    private readonly TextBlock _package = Ui.Label("", 12);
    private readonly TextBox _expected = new() { PlaceholderText = "可选：发布者提供的 SHA-256", Name = "UpdateExpectedSha256" };
    private readonly Button _download;
    private readonly Button _install;
    private readonly Button _restart;
    private readonly Action? _restartAction;
    public UpdateWindow(LinuxUpdateService service, Action? restart = null)
    {
        _service = service;
        _restartAction = restart;
        Title = "Agent Speed · Ubuntu 更新"; Width = 650; Height = 520;
        MinWidth = 530; MinHeight = 400;
        Background = Ui.Background; Foreground = Ui.Text;
        var content = new StackPanel { Spacing = 16, Margin = new Thickness(24) };
        content.Children.Add(Ui.Label("Ubuntu 更新", 26));
        var note = Ui.Label("当前版本：" + (service.CurrentVersion == "0" ? "开发构建，未确认发行版本" : service.CurrentVersion)
            + "\n自动检查仅查找更新。下载、校验和系统安装需要分别点击；不会自动发布或替换程序。", 12, Ui.Muted);
        note.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        content.Children.Add(note);
        content.Children.Add(Ui.Button("检查已配置的更新源", async () => await service.CheckAsync()));
        _download = Ui.Button("下载并校验新版", async () => await service.DownloadAsync());
        content.Children.Add(_download);
        content.Children.Add(_expected);
        content.Children.Add(Ui.Button("选择本地 deb…", async () =>
        {
            var chosen = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择 Ubuntu amd64 的 Agent Speed deb", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Debian 安装包") { Patterns = ["*.deb"] }],
            });
            if (chosen.FirstOrDefault()?.TryGetLocalPath() is { } path) await service.StageLocalAsync(path, _expected.Text);
        }));
        _package.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        content.Children.Add(_package);
        _install = Ui.Button("安装已校验的 deb（需要系统认证）", async () => await service.InstallAsync());
        content.Children.Add(_install);
        _restart = Ui.Button("正常退出并启动已安装的新版", () => _restartAction?.Invoke());
        content.Children.Add(_restart);
        _status.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        content.Children.Add(_status);
        Content = new ScrollViewer { Content = content };
        service.Changed += Changed;
        Closed += (_, _) => service.Changed -= Changed;
        Update();
    }
    private void Changed() => Dispatcher.UIThread.Post(Update);
    private void Update()
    {
        _status.Text = _service.Status;
        _download.IsEnabled = !_service.IsBusy && _service.Release is not null;
        _install.IsEnabled = !_service.IsBusy && _service.StagedPackage is not null && !_service.Installed;
        _restart.IsVisible = _service.Installed;
        _restart.IsEnabled = !_service.IsBusy && _service.Installed && _restartAction is not null;
        _package.Text = _service.StagedPackage is { } package ? $"待安装：{package.Version} · {package.Architecture}\nSHA-256：{package.Sha256}" : "尚未选择安装包。";
    }
}
