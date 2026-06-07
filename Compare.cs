using System.Text.Json;

namespace BuildDiff;

/// <summary>
/// Orchestrates compare: OS-level diffs, then routes each provider's payload to
/// the provider that owns it, then project-requirement drift. Everything is
/// sorted by severity and summarized into a single "most likely cause" line —
/// the ranking is the product.
/// </summary>
public static class Compare
{
    public static List<Diff> Run(Snapshot a, Snapshot b, bool verbose = false)
    {
        var ctx = new CompareContext { A = a.Machine, B = b.Machine, Verbose = verbose };
        var diffs = new List<Diff>();

        // ---- OS level -------------------------------------------------------
        if (!string.Equals(a.Os.Arch, b.Os.Arch, StringComparison.OrdinalIgnoreCase))
            diffs.Add(new Diff(Severity.High, "OS",
                $"Architecture differs: {a.Machine}={a.Os.Arch}, {b.Machine}={b.Os.Arch}", null, "os"));
        if (!string.Equals(a.Os.Platform, b.Os.Platform, StringComparison.OrdinalIgnoreCase))
            diffs.Add(new Diff(Severity.High, "OS",
                $"Platform differs: {a.Machine}={a.Os.Platform}, {b.Machine}={b.Os.Platform}",
                "Snapshots were captured on different operating systems; some differences below are expected.", "os"));
        if (!string.Equals(a.Os.Version, b.Os.Version, StringComparison.Ordinal))
            diffs.Add(new Diff(Severity.Low, "OS",
                $"OS version differs: {a.Machine}={a.Os.Version}; {b.Machine}={b.Os.Version}", null, "os"));

        // ---- Providers ------------------------------------------------------
        var byId = ProviderRegistry.All.ToDictionary(p => p.Id, p => p, StringComparer.OrdinalIgnoreCase);
        var ids = a.Providers.Keys.Union(b.Providers.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

        foreach (var id in ids)
        {
            JsonElement? ae = a.Providers.TryGetValue(id, out var av) ? av : null;
            JsonElement? be = b.Providers.TryGetValue(id, out var bv) ? bv : null;

            if (byId.TryGetValue(id, out var provider))
            {
                try { diffs.AddRange(provider.Compare(ae, be, ctx)); }
                catch { }
            }
            else
            {
                // Section from a newer/unknown provider — note presence generically.
                if (ae is not null && be is null)
                    diffs.Add(new Diff(Severity.Medium, id, $"'{id}' data present on {a.Machine} only", null, id));
                else if (be is not null && ae is null)
                    diffs.Add(new Diff(Severity.Medium, id, $"'{id}' data present on {b.Machine} only", null, id));
            }
        }

        // ---- Project requirements ------------------------------------------
        diffs.AddRange(ProjectCompare.Run(a, b, ctx));

        return diffs
            .OrderBy(d => (int)d.Severity)
            .ThenBy(d => d.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Message, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string? LikelyCause(List<Diff> diffs)
    {
        var crits = diffs.Where(d => d.Severity == Severity.Critical).ToList();
        if (crits.Count == 0)
        {
            var highs = diffs.Where(d => d.Severity == Severity.High).Take(3).ToList();
            if (highs.Count == 0) return null;
            return "Most likely cause: " + SummarizeCategories(highs) + ".";
        }
        return "Most likely cause: " + SummarizeCategories(crits.Take(3).ToList()) + ".";
    }

    private static string SummarizeCategories(List<Diff> ds)
    {
        var byCat = ds
            .GroupBy(d => d.Category, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Count() == 1
                ? g.Key.ToLowerInvariant()
                : $"{g.Count()} {g.Key.ToLowerInvariant()} differences")
            .ToList();
        return string.Join(" + ", byCat);
    }
}
