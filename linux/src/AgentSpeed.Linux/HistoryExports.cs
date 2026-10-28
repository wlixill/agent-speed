using System.Text;
using AgentSpeed.Core;
using AgentSpeed.Presentation.History;

namespace AgentSpeed.Linux;

/// <summary>Shared raw USD export, with optional display-currency conversion and explicit traffic observation coverage.</summary>
public static class HistoryExports
{
    public static string Daily(IEnumerable<DaySummary> rows, SourceKind? source, HistoryMoney? money = null, bool traffic = false)
    {
        var days = rows.ToArray();
        var csv = HistoryCsv.Build(days, source, money, traffic);
        if (!traffic) return csv;
        var coverage = new List<string> { "traffic_coverage" };
        foreach (var day in days)
        {
            coverage.Add(day.Traffic is null ? "not_recorded" : "observed_intervals");
            if (day.Traffic?.Machine is not null) coverage.Add("observed_intervals");
        }
        // Find record boundaries outside quoted fields; model names can legitimately contain CR/LF or quotes.
        var output = new StringBuilder(csv.Length + coverage.Sum(value => value.Length + 1));
        var quoted = false; var index = 0;
        for (var i = 0; i < csv.Length; i++)
        {
            var c = csv[i];
            if (c == '"')
            {
                if (quoted && i + 1 < csv.Length && csv[i + 1] == '"') { output.Append("\"\""); i++; continue; }
                quoted = !quoted;
            }
            if (!quoted && c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n')
            { output.Append(',').Append(coverage[index++]).Append("\r\n"); i++; }
            else output.Append(c);
        }
        return output.ToString();
    }
}
