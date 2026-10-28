using System.Text;

namespace AgentSpeed.Linux;

/// <summary>Opt-in XDG login startup. Only an entry carrying our ownership marker is changed or removed.</summary>
public sealed class LoginStartupService
{
    private const string Executable = "/opt/agent-speed/AgentSpeed.Linux";
    private const string OwnershipMarker = "X-Agent-Speed-Managed=true";
    private const int MaximumEntryBytes = 64 * 1024;
    private readonly LaunchOptions _options;

    public LoginStartupService(LaunchOptions options, AppStorage storage)
    {
        _options = options;
        // Diagnostic sessions must never register themselves in the user's real login startup directory.
        var configRoot = options.DataDirectory is not null
            ? storage.DataDirectory
            : Path.GetDirectoryName(Path.GetDirectoryName(storage.SettingsPath))!;
        EntryPath = Path.Combine(configRoot, "autostart", "agent-speed.desktop");
    }

    public string EntryPath { get; }
    public string? StatusWarning { get; private set; }

    public bool IsEnabled
    {
        get
        {
            try
            {
                StatusWarning = null;
                var content = ReadOwnedEntry();
                return content is not null
                    && !HasLine(content, "Hidden=true")
                    && !HasLine(content, "X-GNOME-Autostart-enabled=false");
            }
            catch (IOException exception)
            {
                StatusWarning = exception.Message;
                return false;
            }
            catch (UnauthorizedAccessException exception)
            {
                StatusWarning = "无法读取登录启动设置：" + exception.Message;
                return false;
            }
        }
    }

    public void SetEnabled(bool enabled, string codexDirectory, bool background = true)
    {
        StatusWarning = null;
        var existing = ReadOwnedEntry();
        if (!enabled)
        {
            if (existing is not null) File.Delete(EntryPath);
            return;
        }

        var content = DesktopEntry(codexDirectory, background);
        if (content == existing) return;
        var directory = Path.GetDirectoryName(EntryPath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".agent-speed-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            // Recheck ownership just before replacement, preserving a concurrently created unrelated entry.
            if (ReadOwnedEntry() != existing)
                throw new IOException("登录启动项已被其他程序更改，请重新打开设置后再试。");
            File.Move(temporary, EntryPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private string? ReadOwnedEntry()
    {
        var info = new FileInfo(EntryPath);
        if (info.LinkTarget is not null || Directory.Exists(EntryPath))
            throw new IOException("登录启动项是链接或目录，未修改：" + EntryPath);
        if (!info.Exists) return null;
        if (info.Length > MaximumEntryBytes)
            throw new IOException("登录启动项过大，未修改：" + EntryPath);
        var content = File.ReadAllText(EntryPath);
        if (!HasLine(content, OwnershipMarker))
            throw new IOException("同名登录启动项不是由 Agent Speed 创建，未修改：" + EntryPath);
        return content;
    }

    private static bool HasLine(string content, string value) =>
        content.Split('\n').Any(line => string.Equals(line.Trim(), value, StringComparison.Ordinal));

    private string DesktopEntry(string codexDirectory, bool background)
    {
        var command = Executable + (background ? " --background" : " --dashboard")
            + " --codex-dir " + DesktopArgument(codexDirectory);
        if (_options.DataDirectory is not null)
            command += " --data-dir " + DesktopArgument(_options.DataDirectory);
        if (_options.SoftwareRendering) command += " --software-rendering";
        return "[Desktop Entry]\nVersion=1.0\nType=Application\nName=Agent Speed\n" +
            "Comment=Codex speed monitor at login\nExec=" + command + "\nTryExec=" + Executable +
            "\nIcon=agent-speed\nTerminal=false\nStartupNotify=false\n" +
            "X-GNOME-Autostart-enabled=true\n" + OwnershipMarker + "\n";
    }

    private static string DesktopArgument(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Any(char.IsControl))
            throw new ArgumentException("登录启动的目录必须是完整路径，且不能包含换行或控制字符。", nameof(path));
        // Desktop Entry is not a shell: quote its argument syntax, then its string-value syntax.
        // %% is the literal-percent field code, so names such as "%U" cannot become launcher arguments.
        var argument = new StringBuilder("\"");
        foreach (var character in path)
        {
            if (character == '%') argument.Append("%%");
            else
            {
                if (character is '\\' or '"' or '`' or '$') argument.Append('\\');
                argument.Append(character);
            }
        }
        argument.Append('"');
        return argument.ToString().Replace("\\", "\\\\", StringComparison.Ordinal);
    }
}
