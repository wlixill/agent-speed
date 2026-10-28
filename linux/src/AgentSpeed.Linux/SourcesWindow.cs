using AgentSpeed.Core;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AgentSpeed.Linux;

internal sealed class SourcesWindow : Window
{
    private readonly SpeedController _controller;
    private readonly Dictionary<SourceKind, (CheckBox Enabled, TextBox Path, TextBlock Status)> _rows = [];
    private readonly TextBlock _warning = Ui.Label("", 11, Ui.Warning);
    public SourcesWindow(SpeedController controller)
    {
        _controller = controller;
        Title = "Agent Speed · 工具来源"; Width = 820; Height = 780; MinWidth = 620; MinHeight = 520;
        Background = Ui.Background; Foreground = Ui.Text; FontFamily = new FontFamily("Noto Sans CJK SC");
        var content = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
        content.Children.Add(Ui.Label("工具来源与记录路径", 24));
        var note = Ui.Label("Codex 为默认来源；其他工具按需开启。路径留空使用默认位置，未安装或没有本地记录时显示未发现。Claude Code 与 Claude Desktop 共用记录目录。停用来源的历史保留在数据库，重新开启后可查看。", 11, Ui.Muted);
        note.TextWrapping = Avalonia.Media.TextWrapping.Wrap; content.Children.Add(note);
        foreach (var kind in Enum.GetValues<SourceKind>())
        {
            var box = new CheckBox { Name = "SourceEnabled" + kind, Content = LinuxSources.Name(kind), IsChecked = LinuxSources.Enabled(controller.Settings).Contains(kind) };
            var path = new TextBox { Name = "SourcePath" + kind, Text = LinuxSources.Override(controller.Settings, kind)
                ?? (kind == SourceKind.Codex ? controller.CodexDirectory : null), PlaceholderText = LinuxSources.DefaultRoot(kind), FontSize = 11 };
            var status = Ui.Label("", 10, Ui.Muted);
            var card = new StackPanel { Spacing = 6 }; card.Children.Add(box); card.Children.Add(path); card.Children.Add(status);
            content.Children.Add(Ui.Card(card, new Thickness(14))); _rows.Add(kind, (box, path, status));
        }
        var apply = Ui.Button("应用来源与路径", () => controller.SetSources(_rows.Where(row => row.Value.Enabled.IsChecked == true).Select(row => row.Key),
            _rows.Where(row => !string.IsNullOrWhiteSpace(row.Value.Path.Text)).ToDictionary(row => row.Key, row => row.Value.Path.Text!)));
        apply.Name = "ApplySources"; content.Children.Add(apply); _warning.TextWrapping = TextWrapping.Wrap; content.Children.Add(_warning);
        Content = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        controller.Changed += UpdateStatus; Closed += (_, _) => controller.Changed -= UpdateStatus;
        Opened += (_, _) =>
        {
            foreach (var (kind, row) in _rows)
            {
                row.Enabled.IsChecked = LinuxSources.Enabled(controller.Settings).Contains(kind);
                row.Path.Text = LinuxSources.Override(controller.Settings, kind) ?? (kind == SourceKind.Codex ? controller.CodexDirectory : null);
            }
            UpdateStatus();
        };
        UpdateStatus();
    }
    private void UpdateStatus()
    {
        foreach (var (kind, row) in _rows)
        {
            var source = _controller.Speed.Snapshot.Sources.FirstOrDefault(source => source.Kind == kind);
            row.Status.Text = source is null ? "尚未读取" : !source.Enabled ? "当前未启用"
                : source.Available ? $"目录可读 · {source.SessionCount} 个会话 · {UsageProjection.Compact(source.Today.Output)} 输出 token／今日" : "未发现可读取记录；请核对路径或工具是否支持本地日志。";
        }
        _warning.Text = _controller.Error ?? "";
    }
}
