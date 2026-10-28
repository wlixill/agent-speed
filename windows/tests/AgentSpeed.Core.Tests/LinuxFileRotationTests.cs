using AgentSpeed.Core.IO;

namespace AgentSpeed.Core.Tests;

public sealed class LinuxFileRotationTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    [Fact]
    public void RenameOverSameLengthTranscriptReadsTheReplacementFromItsBeginning()
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = _temp.WriteFile("session.jsonl", "old\n");
        var tailer = TranscriptTailer.For(path);
        var lines = new LineCollector();
        tailer.ReadAppended(lines);
        Assert.Equal(["old"], lines.Take());

        var replacement = _temp.WriteFile("replacement.jsonl", "new\n");
        File.Move(replacement, path, overwrite: true);
        Assert.True(tailer.ReadAppended(lines).WasReset);
        Assert.Equal(["new"], lines.Take());

        File.AppendAllText(path, "next\n");
        Assert.False(tailer.ReadAppended(lines).WasReset);
        Assert.Equal(["next"], lines.Take());
    }

    public void Dispose() => _temp.Dispose();
}
