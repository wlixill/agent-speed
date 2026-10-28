using System.Text.Json;

namespace AgentSpeed.Linux;

public static class BuildInformation
{
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "build-info.json");
            if (File.Exists(path) && new FileInfo(path).Length < 4096)
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.TryGetProperty("debianVersion", out var value) && value.GetString() is { Length: > 0 and < 80 } version)
                    return version;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { }
        return typeof(BuildInformation).Assembly.GetName().Version?.ToString() + " (源码构建)";
    }
}
