using Avalonia;
using Avalonia.X11;

namespace AgentSpeed.Linux;

internal static class Program
{
    internal static LaunchOptions Options { get; private set; } = new();

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            Options = LaunchOptions.Parse(args);
            if (Options.Help)
            {
                Console.WriteLine(LaunchOptions.Usage);
                return 0;
            }
            if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
                throw new ArgumentException("Agent Speed Ubuntu 初版需要 GNOME/X11 桌面和 DISPLAY 环境变量。");

            var builder = AppBuilder.Configure<SpeedApplication>().UsePlatformDetect()
                .With(new X11PlatformOptions
                {
                    WmClass = "agent-speed",
                    RenderingMode = Options.SoftwareRendering
                        ? [X11RenderingMode.Software]
                        : [X11RenderingMode.Glx, X11RenderingMode.Software],
                })
                .LogToTrace();
            return builder.StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}

public sealed record LaunchOptions(string? CodexDirectory = null, string? DataDirectory = null,
    bool Dashboard = false, bool SoftwareRendering = false, string? CaptureDirectory = null, int? ExitAfter = null,
    bool Help = false, bool Background = false)
{
    public const string Usage = "AgentSpeed.Linux [--codex-dir PATH] [--data-dir PATH] [--dashboard | --background] [--software-rendering]\n" +
        "默认读取 $CODEX_HOME/sessions 或 ~/.codex/sessions，同时读取相邻的 archived_sessions。\n" +
        "--background 只在后台和托盘运行（保留悬浮窗设置）；--dashboard 显式打开仪表。\n" +
        "开发验证：--capture-dir PATH --exit-after SECONDS（使用隔离的 --data-dir）。";

    /// <summary>An explicit menu launch wins over background preferences, regardless of argument order.</summary>
    public bool ShouldShowDashboard(UserSettings settings) => Dashboard || (!Background && !settings.StartInBackground);

    public static LaunchOptions Parse(string[] args)
    {
        var options = new LaunchOptions();
        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            string PathValue()
            {
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("缺少路径参数。");
                return Path.GetFullPath(args[i]);
            }
            options = argument switch
            {
                "--codex-dir" => options with { CodexDirectory = PathValue() },
                "--data-dir" => options with { DataDirectory = PathValue() },
                "--capture-dir" => options with { CaptureDirectory = PathValue() },
                "--dashboard" => options with { Dashboard = true },
                "--background" => options with { Background = true },
                "--software-rendering" => options with { SoftwareRendering = true },
                "--help" or "-h" => options with { Help = true },
                "--exit-after" when ++i < args.Length && int.TryParse(args[i], out var seconds) && seconds is >= 1 and <= 3600
                    => options with { ExitAfter = seconds },
                _ => throw new ArgumentException("无法识别参数：" + argument + "\n" + Usage),
            };
        }
        return options;
    }
}
