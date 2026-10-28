using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace AgentSpeed.Linux;

/// <summary>A compact numeric icon carried by StatusNotifier/AppIndicator IconPixmap on GNOME.</summary>
public sealed class TrayReadoutRenderer
{
    private string? _key;
    private WindowIcon? _icon;
    public string LastText { get; private set; } = "—";
    public long Revision { get; private set; }
    public byte[] LastPng { get; private set; } = [];

    public WindowIcon Render(string value, bool estimated, bool dark)
    {
        var text = Normalize(value);
        var key = text + ":" + estimated + ":" + dark;
        if (_icon is not null && _key == key) return _icon;
        using var bitmap = RenderBitmap(text, estimated, dark);
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        LastPng = stream.ToArray();
        stream.Position = 0;
        _icon = new WindowIcon(stream);
        _key = key;
        LastText = (estimated ? "≈" : "") + text;
        Revision++;
        return _icon;
    }

    public WindowIcon RenderLabeled(string value, string label, bool dark, bool estimated = false)
    {
        var key = "label:" + value + ":" + label + ":" + dark + ":" + estimated;
        if (_icon is not null && _key == key) return _icon;
        var visual = new ReadoutVisual(value, estimated, dark, label) { Width = 64, Height = 64 };
        visual.Measure(new Size(64, 64)); visual.Arrange(new Rect(0, 0, 64, 64));
        using var bitmap = new RenderTargetBitmap(new PixelSize(64, 64)); bitmap.Render(visual);
        using var stream = new MemoryStream(); bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        LastPng = stream.ToArray(); stream.Position = 0; _icon = new(stream);
        _key = key; LastText = value + " " + label; Revision++; return _icon;
    }

    public static string CompactMoney(double? amount)
    {
        if (amount is not { } value || !double.IsFinite(value) || value < 0) return "—";
        if (value == 0) return "0";
        if (value < .01) return value.ToString("0e0", CultureInfo.InvariantCulture);
        return value < 100 ? value.ToString("0.##", CultureInfo.InvariantCulture) : Compact(value);
    }

    /// <summary>Rounded to at most three characters for a 16–22 pixel panel; the tooltip retains precise values.</summary>
    public static string Compact(double? value)
    {
        if (value is not { } number || !double.IsFinite(number) || number < 0) return "—";
        if (number is > 0 and < 1) return "<1";
        if (number < 999.5) return Math.Round(number).ToString("0", CultureInfo.InvariantCulture);
        if (number < 99_500) return Math.Round(number / 1_000).ToString("0", CultureInfo.InvariantCulture) + "K";
        if (number < 950_000) return (number / 1_000_000).ToString(".0", CultureInfo.InvariantCulture) + "M";
        if (number < 99_500_000) return Math.Round(number / 1_000_000).ToString("0", CultureInfo.InvariantCulture) + "M";
        if (number < 950_000_000) return (number / 1_000_000_000).ToString(".0", CultureInfo.InvariantCulture) + "G";
        if (number < 99_500_000_000) return Math.Round(number / 1_000_000_000).ToString("0", CultureInfo.InvariantCulture) + "G";
        return "99G";
    }

    private static string Normalize(string value)
    {
        var text = value.Trim().TrimStart('≈').Trim();
        if (text == "<1") return text;
        var scale = 1d;
        if (text.EndsWith("K", StringComparison.OrdinalIgnoreCase)) { scale = 1_000; text = text[..^1]; }
        else if (text.EndsWith("M", StringComparison.OrdinalIgnoreCase)) { scale = 1_000_000; text = text[..^1]; }
        else if (text.EndsWith("G", StringComparison.OrdinalIgnoreCase)) { scale = 1_000_000_000; text = text[..^1]; }
        return double.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var number)
            || double.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out number) ? Compact(number * scale) : "—";
    }

    public static RenderTargetBitmap RenderBitmap(string text, bool estimated, bool dark, int pixelSize = 64)
    {
        pixelSize = Math.Clamp(pixelSize, 16, 128);
        var visual = new ReadoutVisual(text, estimated, dark) { Width = 64, Height = 64 };
        visual.Measure(new Size(64, 64));
        visual.Arrange(new Rect(0, 0, 64, 64));
        var bitmap = new RenderTargetBitmap(new PixelSize(pixelSize, pixelSize), new Vector(96d * pixelSize / 64, 96d * pixelSize / 64));
        bitmap.Render(visual);
        return bitmap;
    }

    private sealed class ReadoutVisual(string text, bool estimated, bool dark, string? label = null) : Control
    {
        private static readonly Typeface Face = new("DejaVu Sans", FontStyle.Normal, FontWeight.Bold);
        public override void Render(DrawingContext context)
        {
            var foreground = new SolidColorBrush(Color.Parse(dark ? "#FFFFFF" : "#111827"));
            var background = new SolidColorBrush(Color.Parse(dark ? "#202633" : "#F1F5FF"));
            context.DrawRectangle(background, new Pen(new SolidColorBrush(Color.Parse(dark ? "#9AA7C0" : "#46516A")), 2),
                new Rect(1, 1, 62, 62), 10, 10);
            // A small speedometer glyph leaves the full lower row for three large, high-contrast characters.
            if (label is null)
            {
                var center = new Point(32, 16);
                context.DrawEllipse(null, new Pen(foreground, 2), center, 9, 9);
                context.DrawRectangle(background, null, new Rect(21, 17, 22, 10));
                context.DrawLine(new Pen(foreground, 2.8, lineCap: PenLineCap.Round), new(32, 17), new(38, 11));
            }
            else
            {
                var header = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, label.Length > 6 ? 10 : 12, foreground);
                context.DrawText(header, new((64 - header.Width) / 2, 5));
            }
            if (estimated)
            {
                var mark = new FormattedText(label is null ? "≈" : "≥", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 19, foreground);
                context.DrawText(mark, new(4, 3));
            }
            var fontSize = text.Length > 4 ? 19 : text.Length == 4 ? 23 : text.Length >= 3 ? 27 : 31;
            var number = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, fontSize, foreground);
            context.DrawText(number, new((64 - number.Width) / 2, 26));
        }
    }
}
