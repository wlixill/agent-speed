using AgentSpeed.Core.History;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Tests;

public sealed class HistoryModelTokenTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private string StorePath => _temp.Combine("store", "history.sqlite");
    private SourceLocations Locations => new(_temp.Combine("codex", "sessions"), _temp.Combine("claude"),
        _temp.Combine("dsh"), _temp.Combine("opencode"), []);
    private static readonly IReadOnlySet<SourceKind> CodexOnly = new HashSet<SourceKind> { SourceKind.Codex };

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void CodexModelsKeepExactInputAndCacheReadAcrossRequestsAndStoreReopening()
    {
        var t = CodexLines.T0;
        _temp.WriteFile("codex/sessions/models.jsonl", string.Join('\n',
            CodexLines.SessionMeta(t), CodexLines.TurnContext(t, "gpt-6.1-sol"),
            CodexLines.UserMessage(t + 100), CodexLines.UsageRecord(t + 2000, "a1", CodexLines.Usage(9000, 8000, 10, 0)),
            CodexLines.TurnContext(t + 3000, "gpt-6.1-sol-mini"), CodexLines.UserMessage(t + 3100),
            CodexLines.UsageRecord(t + 4000, "b1", CodexLines.Usage(100, 20, 90, 0)),
            CodexLines.TurnContext(t + 5000, "gpt-6.1-sol"), CodexLines.UserMessage(t + 5100),
            CodexLines.UsageRecord(t + 6000, "a2", CodexLines.Usage(30, 0, 1, 0))) + "\n");
        IReadOnlyList<ModelUsage> first;
        using (var store = HistoryStore.Open(StorePath))
        {
            var analyzer = new HistoryAnalyzer(store);
            Assert.Equal(1, Run(analyzer).FilesAnalyzed);
            var day = Assert.Single(analyzer.Days());
            first = day.Models;
            Assert.Equal((9130L, 8020L, 101L), (day.Totals.Input, day.Totals.CachedInput, day.Totals.Output));
            var a = Assert.Single(first, model => model.Model == "gpt-6.1-sol");
            var b = Assert.Single(first, model => model.Model == "gpt-6.1-sol-mini");
            Assert.Equal((9030L, 8000L, 11L, 2L), (a.Input, a.CachedInput, a.Output, a.Requests));
            Assert.Equal((100L, 20L, 90L, 1L), (b.Input, b.CachedInput, b.Output, b.Requests));
            Assert.Equal(0, Run(analyzer).FilesAnalyzed);
        }
        using var reopened = HistoryStore.Open(StorePath);
        var stored = new HistoryAnalyzer(reopened);
        Assert.Equal(first, Assert.Single(stored.Days()).Models);
        Assert.Equal(0, Run(stored).FilesAnalyzed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void LegacySchemaMigrationKeepsGoneLogsUnknownAndReplaysPresentLogsOnlyOnce(int schemaVersion)
    {
        var t = CodexLines.T0;
        WriteCodex("present.jsonl", t, "present", 900, 700, 20);
        var gone = WriteCodex("gone.jsonl", t - 86_400_000, "gone", 100, 10, 30);
        List<DailyUsage> before;
        using (var store = HistoryStore.Open(StorePath))
        {
            var analyzer = new HistoryAnalyzer(store);
            Assert.Equal(2, Run(analyzer).FilesAnalyzed);
            before = analyzer.Days();
            analyzer.RecordPeaks(new Dictionary<string, double> { [before[0].Day] = 123 });
        }
        File.Delete(gone);
        using (var legacy = SqliteConnection.Open(StorePath, SqliteOpenMode.ReadWriteCreate))
        {
            legacy.Execute("""
                CREATE TABLE models_v3 (file_id INTEGER NOT NULL, day TEXT NOT NULL, source TEXT NOT NULL,
                    model TEXT NOT NULL, requests INTEGER NOT NULL, output INTEGER NOT NULL,
                    measured_output INTEGER NOT NULL DEFAULT 0, measured_ms INTEGER NOT NULL DEFAULT 0,
                    cost_micro_usd INTEGER, unpriced_requests INTEGER,
                    PRIMARY KEY (file_id, day, source, model)) WITHOUT ROWID;
                INSERT INTO models_v3 SELECT file_id,day,source,model,requests,output,measured_output,measured_ms,cost_micro_usd,unpriced_requests FROM contribution_models;
                DROP TABLE contribution_models;
                ALTER TABLE models_v3 RENAME TO contribution_models;
                UPDATE files SET version = 4;
                """);
            if (schemaVersion < 3)
                legacy.Execute("ALTER TABLE contribution_models DROP COLUMN cost_micro_usd; ALTER TABLE contribution_models DROP COLUMN unpriced_requests;");
            if (schemaVersion == 1)
                legacy.Execute("ALTER TABLE contribution_models DROP COLUMN measured_output; ALTER TABLE contribution_models DROP COLUMN measured_ms;");
            legacy.Execute($"PRAGMA user_version = {schemaVersion}");
        }

        // The pricing read path must remain read-only and work before the model migration runs.
        Assert.NotEmpty(HistoryStore.StoredPricing(StorePath).Bindings);
        using (var read = SqliteConnection.Open(StorePath, SqliteOpenMode.ReadOnly))
            Assert.Equal(schemaVersion, read.ScalarInt64("PRAGMA user_version"));

        using (var upgraded = HistoryStore.Open(StorePath))
        {
            var analyzer = new HistoryAnalyzer(upgraded);
            Assert.All(analyzer.Days(), day =>
            {
                Assert.Null(Assert.Single(day.Models).Input);
                Assert.Null(Assert.Single(day.Models).CachedInput);
            });
            var report = Run(analyzer);
            Assert.Equal(1, report.FilesAnalyzed);
            var days = analyzer.Days();
            Assert.Equal(2, days.Count);
            Assert.Equal(before[0].Totals, days[0].Totals);
            Assert.Equal(before[0].Cost, days[0].Cost);
            Assert.Equal(schemaVersion == 1 ? 0 : before[0].Models[0].MeasuredMs, days[0].Models[0].MeasuredMs);
            Assert.Equal(123, days[0].Peak);
            Assert.Null(days[0].Models[0].Input);
            Assert.Null(days[0].Models[0].CachedInput);
            Assert.Equal((900L, 700L), (days[1].Models[0].Input, days[1].Models[0].CachedInput));
            Assert.Equal(0, Run(analyzer).FilesAnalyzed);
        }
        using var check = SqliteConnection.Open(StorePath, SqliteOpenMode.ReadOnly);
        Assert.Equal(HistoryStore.SchemaVersion, check.ScalarInt64("PRAGMA user_version"));
        Assert.Equal(2, check.ScalarInt64("SELECT COUNT(*) FROM files"));
        Assert.Equal(1, check.ScalarInt64("SELECT COUNT(*) FROM contribution_models WHERE input IS NULL AND cached_input IS NULL"));
    }

    [Fact]
    public void MergingKnownAndLegacyModelInputPreservesUnknownInEitherOrder()
    {
        var old = new DayAggregate { Requests = 2, Input = 100, CachedInput = 50, Output = 30 };
        old.Models["m"] = new ModelTotals(2, 30, 0, 0);
        var fresh = new DayAggregate();
        fresh.Add(new ParsedResponse("k", SourceKind.Codex, "agent", "m", null, 0,
            new TokenCounts(Input: 900, CachedInput: 700, Output: 20)), 10);
        foreach (var reverse in new[] { false, true })
        {
            var merged = new DayAggregate();
            merged.Merge(reverse ? fresh : old);
            merged.Merge(reverse ? old : fresh);
            var model = Assert.Single(merged.ToUsage(new DayKey("2026-10-26", SourceKind.Codex), null).Models);
            Assert.Null(model.Input);
            Assert.Null(model.CachedInput);
            Assert.Equal((3L, 50L), (model.Requests, model.Output));
            Assert.Equal((1000L, 750L), (merged.Input, merged.CachedInput));
        }
    }

    [Fact]
    public void ZeroCacheReadsAreKnownForFreshDataAndUnknownForLegacyData()
    {
        var fresh = new DayAggregate();
        fresh.Add(new ParsedResponse("k", SourceKind.Codex, "a", "m", null, 0, new TokenCounts(Input: 40, Output: 2)), null);
        var model = Assert.Single(fresh.ToUsage(new DayKey("2026-10-26", SourceKind.Codex), null).Models);
        Assert.Equal(40, model.Input);
        Assert.Equal(0, model.CachedInput);
        Assert.Null(new ModelUsage("legacy", 1, 2).Input);
        Assert.Null(new ModelUsage("legacy", 1, 2).CachedInput);
        var freshTotals = new ModelTotals(1, 2, 0, 0) { Input = 40, CachedInput = 0 };
        Assert.Equal(40, (default(ModelTotals) + freshTotals).Input);
        Assert.Null((new ModelTotals(0, 1, 0, 0) + freshTotals).Input);
    }

    private HistoryReport Run(HistoryAnalyzer analyzer) => analyzer.Run(Locations, CodexOnly, TimeZoneInfo.Utc, CancellationToken.None);
    private string WriteCodex(string file, long t, string id, long input, long cached, long output) =>
        _temp.WriteFile("codex/sessions/" + file, string.Join('\n', CodexLines.SessionMeta(t, "thread-" + id), CodexLines.TurnContext(t),
            CodexLines.UserMessage(t + 100), CodexLines.UsageRecord(t + 2000, id, CodexLines.Usage(input, cached, output, 0), "thread-" + id)) + "\n");
}
