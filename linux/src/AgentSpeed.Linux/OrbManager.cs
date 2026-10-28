using Avalonia.Controls;
using AgentSpeed.Core;
using Avalonia;

namespace AgentSpeed.Linux;

internal sealed class OrbManager : IDisposable
{
    private readonly SpeedController _controller;
    private readonly Action _dashboard;
    private readonly Action _history;
    private readonly Action _quit;
    private readonly Dictionary<string, OrbWindow> _windows = [];
    private OrbManagementWindow? _management;
    private bool _reconciling;
    private bool _disposed;
    private readonly List<IReadOnlyList<LinuxOrbDefinition>> _undo = [];
    public bool CanUndo => _undo.Count > 0;
    public string? OperationStatus { get; private set; }

    public OrbManager(SpeedController controller, Action dashboard, Action history, Action quit)
    {
        _controller = controller; _dashboard = dashboard; _history = history; _quit = quit;
        if (controller.Settings.Orbs is null || controller.Settings.Orbs.Count == 0)
            controller.SetOrbs([LinuxOrbDefinition.FromLegacy(controller.Settings)]);
        Reconcile();
        controller.Changed += Reconcile;
    }

    public OrbWindow Main => _windows[_controller.OrbDefinitions[0].Id];
    public IReadOnlyList<OrbWindow> Windows => _controller.OrbDefinitions.Where(definition => _windows.ContainsKey(definition.Id)).Select(definition => _windows[definition.Id]).ToArray();
    public int Count => _windows.Count;
    public event Action? Changed;
    public void ToggleMain() => SetVisible(Main.Definition.Id, !Main.IsVisible);
    public void ShowMain() => SetVisible(Main.Definition.Id, true);
    public void HideMain() => SetVisible(Main.Definition.Id, false);
    public void SetVisible(string id, bool visible)
    {
        if (_controller.OrbDefinitions.FirstOrDefault(orb => orb.Id == id) is { } definition)
            _controller.SaveOrbDefinition(definition with { Visible = visible });
    }
    public void ShowAll() => _controller.SetOrbs(_controller.OrbDefinitions.Select(orb => orb with { Visible = true }));
    public void HideAll() => _controller.SetOrbs(_controller.OrbDefinitions.Select(orb => orb with { Visible = false }));
    public void SavePositions() { foreach (var window in Windows) window.SavePosition(); }

    public string? Add(string? templateId = null)
    {
        if (_controller.OrbDefinitions.Count >= LinuxOrbDefinition.MaximumCount) { OperationStatus = "最多创建 8 个悬浮窗。"; return null; }
        SavePositions();
        var template = _controller.OrbDefinitions.FirstOrDefault(orb => orb.Id == templateId) ?? _controller.OrbDefinitions[0];
        var available = SourceSet.From(_controller.Speed.Snapshot.Sources.Where(source => source.Enabled && source.Available).Select(source => source.Kind));
        var tracked = SourceSet.From(_controller.OrbDefinitions.Where(orb => orb.Sources is not null).SelectMany(orb => orb.Sources!));
        var free = available.Except(tracked);
        var orb = template with { Id = Guid.NewGuid().ToString("N"), Name = null, Sources = free.IsEmpty ? null : [free.First()],
            RedlineOutput = null, RedlineTotal = null, Visible = true, Placements = null, OrbX = null, OrbY = null };
        return Commit(_controller.OrbDefinitions.Append(orb).ToArray(), "已新增悬浮窗，可撤销。") ? orb.Id : null;
    }

    public string? Duplicate(string id)
    {
        SavePositions();
        var result = LinuxOrbEditing.Duplicate(_controller.OrbDefinitions, id);
        return result is not null && Commit(result.Orbs, "已复制悬浮窗，可撤销。") ? result.Created[0] : null;
    }

    public bool Remove(string id)
    {
        if (_controller.OrbDefinitions.Count <= 1) { OperationStatus = "保留至少一个悬浮窗；可以取消显示。"; return false; }
        SavePositions();
        return Commit(_controller.OrbDefinitions.Where(orb => orb.Id != id).ToArray(), "已删除悬浮窗，可撤销恢复。");
    }

    public bool Split(string id)
    {
        SavePositions();
        var available = SourceSet.From(_controller.Speed.Snapshot.Sources.Where(source => source.Enabled && source.Available).Select(source => source.Kind));
        var result = LinuxOrbEditing.Split(_controller.OrbDefinitions, id, available);
        if (result is null) { OperationStatus = "需要至少两个有记录的启用来源，并保留一个空位。"; return false; }
        return Commit(result.Orbs, result.Limited ? "已拆分；达到 8 个上限，剩余来源保留在最后一窗。可撤销。" : "已按来源拆分，可撤销。");
    }

    public bool Merge(string keepId, string? otherId = null)
    {
        SavePositions();
        var next = LinuxOrbEditing.Merge(_controller.OrbDefinitions, keepId, otherId);
        return next is not null && Commit(next, "已合并来源，保留目标悬浮窗的样式与刻度。可撤销。");
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var previous = _undo[^1];
        _controller.SetOrbs(previous);
        if (_controller.Error is not null) return false;
        _undo.RemoveAt(_undo.Count - 1); OperationStatus = "已撤销上一步悬浮窗操作。"; Changed?.Invoke();
        return true;
    }

    public void Arrange()
    {
        SavePositions();
        Remember();
        var placed = new List<OrbObstacle>(); var failed = false;
        foreach (var window in Windows.Where(window => window.IsVisible))
        {
            var result = OrbWindowArrangement.Place(window.ClientPosition, window.ClientPixelSize, window.ConnectedDisplays,
                placed, snapEdges: false, snapOrbs: false);
            window.MoveToPosition(result.Position);
            placed.Add(new(window.Definition.Id, new(result.Position, window.ClientPixelSize)));
            failed |= !result.IsClear;
        }
        OperationStatus = failed ? "屏幕空间不足，已尽量排开；可缩小表盘或隐藏部分悬浮窗。" : "已排开可见悬浮窗，可撤销。";
        Changed?.Invoke();
    }

    private bool Commit(IReadOnlyList<LinuxOrbDefinition> next, string status)
    {
        var previous = Clone(_controller.OrbDefinitions);
        _controller.SetOrbs(next);
        if (_controller.Error is not null) { OperationStatus = _controller.Error; return false; }
        _undo.Add(previous); if (_undo.Count > 20) _undo.RemoveAt(0);
        OperationStatus = status; Changed?.Invoke(); return true;
    }

    private void Remember() { _undo.Add(Clone(_controller.OrbDefinitions)); if (_undo.Count > 20) _undo.RemoveAt(0); }
    private static IReadOnlyList<LinuxOrbDefinition> Clone(IReadOnlyList<LinuxOrbDefinition> source) => source.Select(orb => orb with
    { Sources = orb.Sources?.ToArray(), Placements = orb.Placements is null ? null : new(orb.Placements) }).ToArray();

    private void Settle(OrbWindow window)
    {
        if (_disposed || !window.IsVisible || !window.Definition.SnapToOrbs) return;
        var others = Windows.Where(other => other != window && other.IsVisible)
            .Select(other => new OrbObstacle(other.Definition.Id, new(other.ClientPosition, other.ClientPixelSize))).ToArray();
        var result = OrbWindowArrangement.Place(window.ClientPosition, window.ClientPixelSize, window.ConnectedDisplays,
            others, window.Definition.SnapToEdges, window.Definition.SnapToOrbs);
        window.MoveToPosition(result.Position);
    }

    public void OpenManagementWindow() => OpenManagementWindow(null);
    private void OpenManagementWindow(string? selectedId)
    {
        if (_disposed) return;
        if (_management is null)
        {
            _management = new(_controller, this) { Icon = Main.Icon };
            _management.Closed += (_, _) => _management = null;
        }
        if (selectedId is not null) _management.SelectOrb(selectedId);
        _management.Show(); _management.WindowState = WindowState.Normal; _management.Activate();
    }

    private void Reconcile()
    {
        if (_reconciling || _disposed) return;
        _reconciling = true;
        try
        {
            var definitions = _controller.OrbDefinitions;
            var ids = definitions.Select(orb => orb.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in _windows.Keys.Where(id => !ids.Contains(id)).ToArray())
            {
                var removed = _windows[id]; _windows.Remove(id);
                removed.Tag = "manager-closing"; removed.Close();
            }
            foreach (var definition in definitions)
            {
                if (!_windows.TryGetValue(definition.Id, out var window))
                {
                    var id = definition.Id;
                    window = new(_controller, definition, _dashboard, _history, () => SetVisible(id, false), _quit);
                    window.Icon = _windows.Values.FirstOrDefault()?.Icon;
                    window.Closing += (_, e) =>
                    {
                        if (_disposed || Equals(window.Tag, "manager-closing")) return;
                        e.Cancel = true; SetVisible(id, false);
                    };
                    _windows.Add(id, window);
                    window.DragSettled += () => Settle(window);
                    window.ConfigureManagement(() => OpenManagementWindow(id), () => Split(id), () => Merge(id), () => Duplicate(id), () => Remove(id), Undo);
                }
                window.ApplyDefinition(definition);
                if (definition.Visible && !window.IsVisible) { window.ShowFloating(); Settle(window); }
                else if (!definition.Visible && window.IsVisible) window.HideFloating();
            }
        }
        finally { _reconciling = false; }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _controller.Changed -= Reconcile;
        _management?.Close();
        foreach (var window in _windows.Values) window.Close();
        _windows.Clear();
    }
}
