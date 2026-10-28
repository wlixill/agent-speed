using AgentSpeed.Core;

namespace AgentSpeed.Linux;

/// <summary>Atomic orb operations; counters are never rewritten or divided when a view is split or merged.</summary>
public static class LinuxOrbEditing
{
    public sealed record Result(IReadOnlyList<LinuxOrbDefinition> Orbs, IReadOnlyList<string> Created, bool Limited = false);

    public static Result? Split(IReadOnlyList<LinuxOrbDefinition> orbs, string id, SourceSet available, Func<string>? newId = null)
    {
        var index = Index(orbs, id);
        if (index < 0 || orbs.Count >= LinuxOrbDefinition.MaximumCount) return null;
        var original = orbs[index];
        var sources = available.Intersect(original.SourceSet ?? SourceSet.Everything).ToArray();
        if (sources.Length < 2) return null;
        var slots = Math.Min(sources.Length, LinuxOrbDefinition.MaximumCount - orbs.Count + 1);
        var next = orbs.ToList(); var created = new List<string>();
        for (var i = 0; i < slots; i++)
        {
            // At the cap the last orb retains the remaining sources; splitting never silently drops a source.
            var selected = i == slots - 1 ? sources.Skip(i).ToArray() : [sources[i]];
            var child = original with { Id = i == 0 ? original.Id : Fresh(next, newId), Name = null, Sources = selected };
            if (i == 0) next[index] = child;
            else
            {
                child = child with { Placements = null, OrbX = null, OrbY = null };
                next.Insert(index + i, child); created.Add(child.Id);
            }
        }
        return new(next, created, slots < sources.Length);
    }

    public static IReadOnlyList<LinuxOrbDefinition>? Merge(IReadOnlyList<LinuxOrbDefinition> orbs, string keepId, string? otherId = null)
    {
        if (orbs.Count < 2 || orbs.FirstOrDefault(orb => orb.Id == keepId) is not { } kept) return null;
        var merging = otherId is null ? orbs : orbs.Where(orb => orb.Id == keepId || orb.Id == otherId).ToArray();
        if (merging.Count < 2) return null;
        var sources = otherId is null || merging.Any(orb => orb.Sources is null) ? null
            : merging.SelectMany(orb => orb.Sources!).Distinct().Order().ToArray();
        var merged = kept with { Name = null, Sources = sources, Visible = true };
        var ids = merging.Select(orb => orb.Id).ToHashSet(StringComparer.Ordinal);
        return orbs.Where(orb => !ids.Contains(orb.Id) || orb.Id == keepId).Select(orb => orb.Id == keepId ? merged : orb).ToArray();
    }

    public static Result? Duplicate(IReadOnlyList<LinuxOrbDefinition> orbs, string id, Func<string>? newId = null)
    {
        var index = Index(orbs, id);
        if (index < 0 || orbs.Count >= LinuxOrbDefinition.MaximumCount) return null;
        var original = orbs[index];
        var copy = original with { Id = Fresh(orbs, newId), Name = original.DisplayName + " 副本", Placements = null, OrbX = null, OrbY = null };
        var next = orbs.ToList(); next.Insert(index + 1, copy);
        return new(next, [copy.Id]);
    }

    private static int Index(IReadOnlyList<LinuxOrbDefinition> orbs, string id)
    {
        for (var i = 0; i < orbs.Count; i++) if (orbs[i].Id == id) return i;
        return -1;
    }

    private static string Fresh(IEnumerable<LinuxOrbDefinition> orbs, Func<string>? create)
    {
        var ids = orbs.Select(orb => orb.Id).ToHashSet(StringComparer.Ordinal);
        var proposed = create?.Invoke();
        return !string.IsNullOrWhiteSpace(proposed) && !ids.Contains(proposed) ? proposed : Guid.NewGuid().ToString("N");
    }
}
