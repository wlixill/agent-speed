using System.Collections.Immutable;
using AgentSpeed.Core;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Titles;
using AgentSpeed.Presentation.Settings.Orbs;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AgentSpeed.Linux;

internal sealed class OrbManagementWindow : Window
{
    private readonly SpeedController _controller;
    private readonly OrbManager _manager;
    private readonly ComboBox _orbs = new() { Name = "ManagedOrbs", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _name = new() { Name = "OrbName", PlaceholderText = "自动命名" };
    private readonly ComboBox _style = new() { Name = "OrbStyle", ItemsSource = new[] { "LED 发光表盘", "指针转速表", "液态能量球", "反应堆", "极简进度环" } };
    private readonly ComboBox _titles = new() { Name = "OrbTitleTheme" };
    private readonly ComboBox _metric = new() { Name = "OrbMetric", ItemsSource = new[] { "跟随全局指标", "输出速度", "输入＋输出吞吐量" } };
    private readonly Slider _size = new() { Name = "ManagedOrbSize", Minimum = 96, Maximum = 300 };
    private readonly Slider _opacity = new() { Name = "ManagedOrbOpacity", Minimum = .4, Maximum = 1 };
    private readonly CheckBox _visible = new() { Content = "显示这个悬浮窗" };
    private readonly CheckBox _through = new() { Content = "鼠标穿透（可从此处恢复）" };
    private readonly CheckBox _snap = new() { Content = "贴近屏幕边缘时吸附" };
    private readonly CheckBox _snapOrbs = new() { Name = "OrbSnapToOrbs", Content = "与其他悬浮窗吸附、对齐并避开重叠" };
    private readonly CheckBox _topmost = new() { Name = "OrbAlwaysOnTop", Content = "始终显示在其他窗口上方" };
    private readonly CheckBox _fade = new() { Name = "OrbFadeWhenIdle", Content = "空闲 10 秒后淡出；鼠标移入恢复" };
    private readonly CheckBox _showName = new() { Name = "OrbShowName", Content = "显示名称／来源名牌" };
    private readonly TextBox _redlineOutput = new() { Name = "OrbRedlineOutput", PlaceholderText = "留空跟随全局输出刻度" };
    private readonly TextBox _redlineTotal = new() { Name = "OrbRedlineTotal", PlaceholderText = "留空跟随全局吞吐量刻度" };
    private readonly ComboBox _unit = new() { Name = "OrbUnit", ItemsSource = new[] { "token 速度主读数", "API 等值费用主读数" } };
    private readonly ComboBox _traffic = new() { Name = "OrbTraffic", ItemsSource = new[] { "所选来源流量", "整机网卡流量", "不显示流量" } };
    private readonly ComboBox _mergeTarget = new() { Name = "OrbMergeTarget", MinWidth = 220 };
    private readonly Button _undo;
    private readonly CheckBox _all = new() { Content = "跟随所有已启用的数据源" };
    private readonly Dictionary<SourceKind, CheckBox> _sources = [];
    private readonly TextBlock _status = Ui.Label("", 11, Ui.Muted);
    private string? _id;
    private string _signature = "";
    private bool _binding;

    public OrbManagementWindow(SpeedController controller, OrbManager manager)
    {
        _controller = controller; _manager = manager;
        Title = "Agent Speed · 悬浮窗与标题主题"; Width = 680; Height = 840; MinWidth = 540; MinHeight = 580;
        Background = Ui.Background; Foreground = Ui.Text; FontFamily = new FontFamily("Noto Sans CJK SC");
        var content = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
        content.Children.Add(Ui.Label("悬浮窗管理", 25));
        content.Children.Add(Ui.Label("最多 8 个悬浮窗，分别保存来源、样式、标题、显示状态和位置。", 11, Ui.Muted));
        content.Children.Add(_orbs);
        var commands = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6 };
        Button Command(string name, string label, Action action) { var button = Ui.Button(label, action); button.Name = name; commands.Children.Add(button); return button; }
        Command("AddManagedOrb", "新增", Add); Command("DuplicateManagedOrb", "复制", Duplicate);
        Command("RemoveManagedOrb", "删除", Remove); Command("SplitManagedOrb", "按来源拆分", () => { if (_id is { } id) { manager.Split(id); ShowOperation(); } });
        Command("MergeAllManagedOrbs", "合并全部到当前窗", () => { if (_id is { } id) { manager.Merge(id); ShowOperation(); } });
        Command("ArrangeManagedOrbs", "排开悬浮窗", () => { manager.Arrange(); ShowOperation(); });
        _undo = Command("UndoManagedOrb", "撤销", () => { manager.Undo(); ShowOperation(); });
        Command("ShowAllManagedOrbs", "全部显示", manager.ShowAll); Command("HideAllManagedOrbs", "全部隐藏", manager.HideAll);
        content.Children.Add(commands);
        var merge = new WrapPanel { ItemSpacing = 8 }; merge.Children.Add(_mergeTarget);
        var mergeOne = Ui.Button("合并此窗到当前窗", () => { if (_id is { } id && _mergeTarget.SelectedItem is Choice other) { manager.Merge(id, other.Id); ShowOperation(); } });
        mergeOne.Name = "MergeSelectedOrb"; merge.Children.Add(mergeOne); content.Children.Add(merge);
        AddField(content, "名称", _name); AddField(content, "表盘样式", _style); AddField(content, "标题主题", _titles);
        var themeActions = new WrapPanel();
        themeActions.Children.Add(Ui.Button("新建自定义主题", () => EditTheme(false)));
        themeActions.Children.Add(Ui.Button("编辑自定义主题", () => EditTheme(true)));
        themeActions.Children.Add(Ui.Button("删除自定义主题", DeleteTheme));
        themeActions.Children.Add(Ui.Button("设为全局标题", SetGeneralTheme));
        content.Children.Add(themeActions);
        AddField(content, "指标", _metric); AddField(content, "主读数", _unit); AddField(content, "流量读数（需先在设置开启采集）", _traffic);
        AddField(content, "本窗输出满量程 · tok/s", _redlineOutput); AddField(content, "本窗吞吐量满量程 · tok/s", _redlineTotal);
        AddField(content, "表盘尺寸 · 96–300 DIP", _size); AddField(content, "不透明度 · 40%–100%", _opacity);
        content.Children.Add(_visible); content.Children.Add(_topmost); content.Children.Add(_fade); content.Children.Add(_showName);
        content.Children.Add(_through); content.Children.Add(_snap); content.Children.Add(_snapOrbs); content.Children.Add(_all);
        var sourceGrid = new WrapPanel();
        foreach (var source in Enum.GetValues<SourceKind>())
        {
            var check = new CheckBox { Content = LinuxSources.Name(source), Width = 185 };
            _sources[source] = check; sourceGrid.Children.Add(check);
        }
        content.Children.Add(sourceGrid);
        content.Children.Add(Ui.Label("来源过滤不会自动启用数据源；请先在通用设置中启用所需来源。", 11, Ui.Muted));
        var save = Ui.Button("应用这个悬浮窗", Save); save.Name = "SaveManagedOrb"; content.Children.Add(save);
        _status.TextWrapping = TextWrapping.Wrap; content.Children.Add(_status);
        Content = new ScrollViewer { Content = content };
        _orbs.SelectionChanged += (_, _) => { if (!_binding && _orbs.SelectedItem is Choice item) Load(item.Id); };
        _all.IsCheckedChanged += (_, _) => { foreach (var check in _sources.Values) check.IsEnabled = _all.IsChecked != true; };
        controller.Changed += Refresh;
        manager.Changed += Refresh;
        Closed += (_, _) => { controller.Changed -= Refresh; manager.Changed -= Refresh; };
        Refresh();
    }

    private static void AddField(Panel panel, string label, Control field) { panel.Children.Add(Ui.Label(label, 12, Ui.Muted)); panel.Children.Add(field); }
    private sealed record Choice(string Id, string Name) { public override string ToString() => Name; }
    private LinuxOrbDefinition? Current => _controller.OrbDefinitions.FirstOrDefault(orb => orb.Id == _id);
    private Choice? ThemeChoice => _titles.SelectedItem as Choice;
    public void SelectOrb(string id) { _id = id; _signature = ""; Refresh(); }
    private void ShowOperation() { _status.Text = _controller.Error ?? _manager.OperationStatus; Refresh(); }

    private void Refresh()
    {
        var definitions = _controller.OrbDefinitions;
        _undo.IsEnabled = _manager.CanUndo;
        var signature = string.Join(";", definitions.Select(orb => $"{orb.Id}:{orb.DisplayName}:{orb.Style}:{orb.TitleTheme}:{orb.Visible}:{orb.Size}:{orb.Opacity}:{orb.ClickThrough}:{orb.SnapToEdges}:{orb.Metric}:{orb.Unit}:{orb.Traffic}:{orb.AlwaysOnTop}:{orb.FadeWhenIdle}:{orb.ShowName}:{orb.SnapToOrbs}:{orb.RedlineOutput}:{orb.RedlineTotal}:{string.Join(',', orb.Sources ?? [])}"))
            + ":themes=" + _controller.Settings.TitleThemes.Selected + string.Join(";", _controller.Settings.TitleThemes.Custom.Select(set =>
                set.Id + ":" + set.Name + ":" + set.Base + ":" + set.Idle + ":" + string.Join("|", set.Titles)));
        if (_signature == signature) return;
        _signature = signature; _binding = true;
        try
        {
            var selected = definitions.FirstOrDefault(orb => orb.Id == _id) ?? definitions[0];
            _orbs.ItemsSource = definitions.Select(orb => new Choice(orb.Id, orb.DisplayName + (orb.Visible ? " · 显示" : " · 隐藏"))).ToArray();
            _orbs.SelectedItem = _orbs.Items.Cast<Choice>().First(item => item.Id == selected.Id);
            _titles.ItemsSource = new[] { new Choice("general", "跟随全局主题") }
                .Concat(_controller.Settings.TitleThemes.AllThemes.Select(theme => new Choice(theme.Id, LinuxTitleText.Name(theme)))).ToArray();
            Load(selected.Id);
        }
        finally { _binding = false; }
    }

    private void Load(string id)
    {
        var definition = _controller.OrbDefinitions.First(orb => orb.Id == id); _id = id;
        _name.Text = definition.Name ?? ""; _style.SelectedIndex = (int)definition.Style;
        _titles.SelectedItem = _titles.Items.Cast<Choice>().FirstOrDefault(theme => theme.Id == definition.TitleTheme) ?? _titles.Items.Cast<Choice>().First();
        _metric.SelectedIndex = definition.Metric is null ? 0 : definition.Metric == SpeedMetric.Output ? 1 : 2;
        _size.Value = definition.Size; _opacity.Value = definition.Opacity;
        _unit.SelectedIndex = (int)definition.Unit; _traffic.SelectedIndex = (int)definition.Traffic;
        _redlineOutput.Text = definition.RedlineOutput?.ToString("0.######", CultureInfo.InvariantCulture) ?? "";
        _redlineTotal.Text = definition.RedlineTotal?.ToString("0.######", CultureInfo.InvariantCulture) ?? "";
        _topmost.IsChecked = definition.AlwaysOnTop; _fade.IsChecked = definition.FadeWhenIdle; _showName.IsChecked = definition.ShowName; _snapOrbs.IsChecked = definition.SnapToOrbs;
        _visible.IsChecked = definition.Visible; _through.IsChecked = definition.ClickThrough; _snap.IsChecked = definition.SnapToEdges;
        _all.IsChecked = definition.Sources is null;
        foreach (var (source, check) in _sources) { check.IsChecked = definition.Sources?.Contains(source) == true; check.IsEnabled = definition.Sources is not null; }
        _mergeTarget.ItemsSource = _controller.OrbDefinitions.Where(orb => orb.Id != id).Select(orb => new Choice(orb.Id, orb.DisplayName)).ToArray();
        _mergeTarget.SelectedIndex = _mergeTarget.ItemCount > 0 ? 0 : -1;
    }
    private void Save()
    {
        if (Current is not { } current) return;
        if (!TryRedline(_redlineOutput.Text, out var output) || !TryRedline(_redlineTotal.Text, out var total))
        { _status.Text = "满量程请输入正数；留空则跟随全局。"; return; }
        _controller.SaveOrbDefinition(current with
        {
            Name = _name.Text, Style = (OrbStyle)Math.Max(0, _style.SelectedIndex), TitleTheme = ThemeChoice?.Id ?? "general",
            Metric = _metric.SelectedIndex == 1 ? SpeedMetric.Output : _metric.SelectedIndex == 2 ? SpeedMetric.Total : null,
            Size = _size.Value, Opacity = _opacity.Value, Visible = _visible.IsChecked == true,
            ClickThrough = _through.IsChecked == true, SnapToEdges = _snap.IsChecked == true,
            SnapToOrbs = _snapOrbs.IsChecked == true, AlwaysOnTop = _topmost.IsChecked == true,
            FadeWhenIdle = _fade.IsChecked == true, ShowName = _showName.IsChecked == true,
            RedlineOutput = output, RedlineTotal = total, Unit = (OrbUnit)Math.Max(0, _unit.SelectedIndex), Traffic = (OrbTraffic)Math.Max(0, _traffic.SelectedIndex),
            Sources = _all.IsChecked == true ? null : _sources.Where(pair => pair.Value.IsChecked == true).Select(pair => pair.Key).ToArray(),
        });
        _status.Text = _controller.Error ?? "已保存；修改只作用于这个悬浮窗。";
    }
    private static bool TryRedline(string? text, out double? value)
    {
        value = null; if (string.IsNullOrWhiteSpace(text)) return true;
        if (!(double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out number)) || !double.IsFinite(number) || number <= 0) return false;
        value = number; return true;
    }
    private void Add()
    {
        if (_manager.Add(_id) is { } id) { _id = id; _signature = ""; Refresh(); }
        ShowOperation();
    }
    private void Duplicate()
    {
        if (_id is { } selected && _manager.Duplicate(selected) is { } id) { _id = id; _signature = ""; Refresh(); }
        ShowOperation();
    }
    private void Remove()
    {
        if (_id is { } id) _manager.Remove(id);
        ShowOperation();
    }
    private void EditTheme(bool existing)
    {
        var document = _controller.Settings.TitleThemes;
        var editedOrbId = _id;
        var theme = document.OrbTheme(ThemeChoice?.Id);
        var custom = existing ? theme.Custom : null;
        if (existing && custom is null) { _status.Text = "预设主题不可直接修改，请先新建副本。"; return; }
        if (custom is null && !document.CanAddCustom) { _status.Text = "最多保存 8 个自定义标题主题。"; return; }
        custom ??= new(Guid.NewGuid().ToString("N"), LinuxTitleText.Name(theme) + " 副本", theme.Base);
        var editor = new TitleThemeEditorWindow(custom, saved =>
        {
            var latest = _controller.Settings.TitleThemes;
            var index = Array.FindIndex(latest.Custom.ToArray(), set => set.Id == saved.Id);
            if (index < 0 && !latest.CanAddCustom) { _status.Text = "最多保存 8 个自定义主题，请先删除一个。"; return false; }
            var list = index >= 0 ? latest.Custom.SetItem(index, saved) : latest.Custom.Add(saved);
            _controller.SetTitleThemes(latest with { Custom = list });
            if (_controller.Error is not null) { _status.Text = _controller.Error; return false; }
            if (_controller.OrbDefinitions.FirstOrDefault(orb => orb.Id == editedOrbId) is { } orb)
                _controller.SaveOrbDefinition(orb with { TitleTheme = saved.Id });
            return _controller.Error is null;
        }) { Icon = Icon };
        editor.Show();
    }
    private void DeleteTheme()
    {
        if (_controller.Settings.TitleThemes.CustomSet(ThemeChoice?.Id) is not { } custom) return;
        _controller.SetTitleThemes(_controller.Settings.TitleThemes with { Custom = _controller.Settings.TitleThemes.Custom.Remove(custom) });
    }
    private void SetGeneralTheme()
    {
        if (ThemeChoice is not { Id: not "general" } theme) return;
        _controller.SetTitleThemes(_controller.Settings.TitleThemes with { Selected = theme.Id });
    }
}

internal sealed class TitleThemeEditorWindow : Window
{
    public TitleThemeEditorWindow(CustomTitleSet original, Func<CustomTitleSet, bool> save)
    {
        Title = "编辑标题主题"; Width = 570; Height = 760; Background = Ui.Background; Foreground = Ui.Text;
        var content = new StackPanel { Spacing = 10, Margin = new Thickness(24) };
        var name = new TextBox { Text = original.Name, MaxLength = 128, Name = "CustomThemeName" };
        var basis = new ComboBox { ItemsSource = TitlePresets.All.Select(preset => LinuxTitleText.Name(TitleTheme.Of(preset))).ToArray(), SelectedIndex = (int)original.Base };
        content.Children.Add(Ui.Label("自定义标题", 24)); content.Children.Add(name); content.Children.Add(Ui.Label("空白字段使用所选基础主题的名称", 11, Ui.Muted)); content.Children.Add(basis);
        var fields = new TextBox[14];
        for (var level = 0; level <= 13; level++)
        {
            content.Children.Add(Ui.Label(level == 0 ? "待机" : "等级 " + level, 11, Ui.Muted));
            fields[level] = new() { Name = "CustomTitle" + level, Text = original.Field(level), PlaceholderText = LinuxTitleText.Label(TitleTheme.Of(original.Base), level), MaxLength = 256 };
            content.Children.Add(fields[level]);
        }
        basis.SelectionChanged += (_, _) => { for (var level = 0; level < fields.Length; level++) fields[level].PlaceholderText = LinuxTitleText.Label(TitleTheme.Of((TitlePreset)Math.Max(0, basis.SelectedIndex)), level); };
        var error = Ui.Label("", 11, Ui.Warning); error.TextWrapping = TextWrapping.Wrap;
        content.Children.Add(error);
        var saveButton = Ui.Button("保存主题", () =>
        {
            var result = new CustomTitleSet(original.Id, name.Text ?? "", (TitlePreset)Math.Max(0, basis.SelectedIndex), fields[0].Text, fields.Skip(1).Select(field => field.Text));
            if (save(result)) Close(); else error.Text = "保存未完成，请查看管理窗口中的提示。";
        });
        saveButton.Name = "SaveCustomTheme"; content.Children.Add(saveButton);
        Content = new ScrollViewer { Content = content };
    }
}
