namespace BuildDiff;

/// <summary>
/// Diffs the <c>--project</c> declared requirements between two snapshots.
/// When both snapshots were captured against the same repo with <c>--project</c>,
/// any divergence in declared versions usually means the two machines are on
/// different checkouts of the project itself.
/// </summary>
public static class ProjectCompare
{
    public static IEnumerable<Diff> Run(Snapshot a, Snapshot b, CompareContext ctx)
    {
        if (a.Project is null || b.Project is null) yield break;

        var aDecl = Flatten(a.Project);
        var bDecl = Flatten(b.Project);

        foreach (var key in aDecl.Keys.Union(bDecl.Keys, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            aDecl.TryGetValue(key, out var av);
            bDecl.TryGetValue(key, out var bv);
            if (!string.Equals(av, bv, StringComparison.OrdinalIgnoreCase))
                yield return new Diff(Severity.Medium, "Project requirement",
                    $"declared {key} differs: {ctx.A}={av ?? "<none>"}, {ctx.B}={bv ?? "<none>"}",
                    "The two machines appear to be on different checkouts of this project.",
                    "project");
        }
    }

    private static Dictionary<string, string?> Flatten(ProjectInfo p)
    {
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in p.Manifests)
            foreach (var kv in m.Declares)
                d[$"{m.File}:{kv.Key}"] = kv.Value;
        return d;
    }
}
