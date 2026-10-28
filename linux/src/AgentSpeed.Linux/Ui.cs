using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AgentSpeed.Linux;

internal static class Ui
{
    // Keep brush identity: existing controls hold these objects and repaint when their colors change.
    public static readonly SolidColorBrush Background = new(Color.Parse("#191A20"));
    public static readonly SolidColorBrush Surface = new(Color.Parse("#24262E"));
    public static readonly SolidColorBrush Line = new(Color.Parse("#3B3D49"));
    public static readonly SolidColorBrush Text = new(Color.Parse("#F1F2F7"));
    public static readonly SolidColorBrush Muted = new(Color.Parse("#9C9FAE"));
    public static readonly SolidColorBrush Accent = new(Color.Parse("#8299FF"));
    public static readonly SolidColorBrush GaugeShell = new(Color.Parse("#FC0C1119"));
    public static readonly SolidColorBrush GaugeGlass = new(Color.Parse("#0B111A"));
    public static readonly SolidColorBrush GaugeBorder = new(Color.Parse("#4A374352"));
    public static readonly SolidColorBrush GaugeGlassBorder = new(Color.Parse("#202633"));
    public static readonly SolidColorBrush ChartGrid = new(Color.Parse("#24334A"));
    public static readonly SolidColorBrush ChartReference = new(Color.Parse("#637491"));
    public static readonly SolidColorBrush HistorySpeed = new(Color.Parse("#CBD0DE"));
    public static readonly SolidColorBrush Codex = new(Color.Parse("#83D7AB"));
    public static readonly SolidColorBrush OrbLabel = new(Color.Parse("#F020222A"));
    public static readonly SolidColorBrush Warning = new(Color.Parse("#FFD187"));
    public static readonly SolidColorBrush Peak = new(Color.Parse("#FFE075"));
    public static bool IsDark { get; private set; } = true;
    public static event Action? PaletteChanged;

    public static void ApplyAppearance(bool dark)
    {
        if (IsDark == dark) return;
        IsDark = dark;
        Background.Color = Color.Parse(dark ? "#191A20" : "#F4F5F8");
        Surface.Color = Color.Parse(dark ? "#24262E" : "#FFFFFF");
        Line.Color = Color.Parse(dark ? "#3B3D49" : "#D8DCE6");
        Text.Color = Color.Parse(dark ? "#F1F2F7" : "#202433");
        Muted.Color = Color.Parse(dark ? "#9C9FAE" : "#616B7D");
        Accent.Color = Color.Parse(dark ? "#8299FF" : "#405DD0");
        GaugeShell.Color = Color.Parse(dark ? "#FC0C1119" : "#FCF4F7FC");
        GaugeGlass.Color = Color.Parse(dark ? "#0B111A" : "#FFFFFF");
        GaugeBorder.Color = Color.Parse(dark ? "#4A374352" : "#607A869B");
        GaugeGlassBorder.Color = Color.Parse(dark ? "#202633" : "#D8DFEB");
        ChartGrid.Color = Color.Parse(dark ? "#24334A" : "#DBE1EC");
        ChartReference.Color = Color.Parse(dark ? "#637491" : "#79869B");
        HistorySpeed.Color = Color.Parse(dark ? "#CBD0DE" : "#49556D");
        Codex.Color = Color.Parse(dark ? "#83D7AB" : "#247752");
        OrbLabel.Color = Color.Parse(dark ? "#F020222A" : "#F0FFFFFF");
        Warning.Color = Color.Parse(dark ? "#FFD187" : "#8A4A08");
        Peak.Color = Color.Parse(dark ? "#FFE075" : "#80610A");
        PaletteChanged?.Invoke();
    }

    public static TextBlock Label(string text, double size = 13, IBrush? color = null) => new()
    {
        Text = text, FontSize = size, Foreground = color ?? Text,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static Button Button(string text, Action click)
    {
        var button = new Button { Content = text, Padding = new Thickness(14, 8), CornerRadius = new CornerRadius(8) };
        button.Click += (_, _) => click();
        return button;
    }

    public static Border Card(Control content, Thickness? padding = null) => new()
    {
        Background = Surface, BorderBrush = Line, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(14), Padding = padding ?? new Thickness(18), Child = content,
    };

    public static void Cell(Grid grid, Control control, int column, int row = 0)
    {
        Grid.SetColumn(control, column);
        Grid.SetRow(control, row);
        grid.Children.Add(control);
    }
}
