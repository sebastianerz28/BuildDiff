namespace BuildDiff;

public enum Severity { Critical, High, Medium, Low }

public sealed record Diff(Severity Severity, string Category, string Message, string? Hint = null);

public static class Compare
{
    public static List<Diff> Run(Snapshot a, Snapshot b)
    {
        var diffs = new List<Diff>();

        // OS
        if (!string.Equals(a.Os.Arch, b.Os.Arch, StringComparison.OrdinalIgnoreCase))
            diffs.Add(new Diff(Severity.High, "OS",
                $"Architecture differs: {a.Machine}={a.Os.Arch}, {b.Machine}={b.Os.Arch}"));
        if (!string.Equals(a.Os.Version, b.Os.Version, StringComparison.Ordinal))
            diffs.Add(new Diff(Severity.Low, "OS",
                $"OS version differs: {a.Machine}={a.Os.Version}; {b.Machine}={b.Os.Version}"));

        // MSBuild
        var amb = a.MsBuild?.Version;
        var bmb = b.MsBuild?.Version;
        if (amb is null && bmb is not null)
            diffs.Add(new Diff(Severity.Critical, "MSBuild",
                $"MSBuild present on {b.Machine} ({bmb}) but NOT FOUND on {a.Machine}"));
        else if (bmb is null && amb is not null)
            diffs.Add(new Diff(Severity.Critical, "MSBuild",
                $"MSBuild present on {a.Machine} ({amb}) but NOT FOUND on {b.Machine}"));
        else if (amb is not null && bmb is not null && amb != bmb)
            diffs.Add(new Diff(Severity.High, "MSBuild",
                $"MSBuild version differs: {a.Machine}={amb}, {b.Machine}={bmb}"));

        // Toolsets — CRITICAL if missing on one side
        DiffSets(diffs, "MSVC toolset", a.Toolsets, b.Toolsets, a.Machine, b.Machine,
            Severity.Critical, hint: id =>
                id.StartsWith("Microsoft.VisualStudio.Component.VC", StringComparison.OrdinalIgnoreCase)
                    ? $"Install via Visual Studio Installer: {id}"
                    : null);

        // Windows SDKs — CRITICAL if missing
        DiffSets(diffs, "Windows SDK", a.WindowsSdks, b.WindowsSdks, a.Machine, b.Machine,
            Severity.Critical);

        // .NET SDKs — CRITICAL if missing entirely, HIGH if version mismatch
        DiffSets(diffs, ".NET SDK", a.Dotnet.Sdks, b.Dotnet.Sdks, a.Machine, b.Machine,
            Severity.Critical);
        DiffSets(diffs, ".NET runtime", a.Dotnet.Runtimes, b.Dotnet.Runtimes, a.Machine, b.Machine,
            Severity.High);

        // NuGet sources — CRITICAL if missing on one side, HIGH if same name different URL
        DiffNuGet(diffs, a, b);

        // PATH-resolved tools — HIGH (missing) or HIGH (version/path differ)
        DiffResolvedTools(diffs, a, b);

        // Build-relevant env vars — HIGH
        DiffEnvDict(diffs, a.Env.BuildRelevant, b.Env.BuildRelevant, a.Machine, b.Machine,
            Severity.High, "Build env var");

        // Other env vars — LOW (presence diffs only, not value diffs, to keep noise down)
        DiffEnvPresence(diffs, a.Env.Other, b.Env.Other, a.Machine, b.Machine, Severity.Low, "Env var");

        // PATH entries — MEDIUM, only flag entries unique to one side (order is noisy)
        DiffPathSets(diffs, a.Env.Path, b.Env.Path, a.Machine, b.Machine);

        // VS components — MEDIUM (less load-bearing than the targeted toolset/SDK diffs above)
        var aComps = a.VisualStudio.SelectMany(v => v.Components).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bComps = b.VisualStudio.SelectMany(v => v.Components).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var c in aComps.Except(bComps).OrderBy(x => x))
        {
            if (c.StartsWith("Microsoft.VisualStudio.Component.VC", StringComparison.OrdinalIgnoreCase)) continue;
            if (c.Contains("Windows", StringComparison.OrdinalIgnoreCase) && c.Contains("SDK", StringComparison.OrdinalIgnoreCase)) continue;
            diffs.Add(new Diff(Severity.Medium, "VS component",
                $"{c} present on {a.Machine}, absent on {b.Machine}"));
        }
        foreach (var c in bComps.Except(aComps).OrderBy(x => x))
        {
            if (c.StartsWith("Microsoft.VisualStudio.Component.VC", StringComparison.OrdinalIgnoreCase)) continue;
            if (c.Contains("Windows", StringComparison.OrdinalIgnoreCase) && c.Contains("SDK", StringComparison.OrdinalIgnoreCase)) continue;
            diffs.Add(new Diff(Severity.Medium, "VS component",
                $"{c} present on {b.Machine}, absent on {a.Machine}"));
        }

        return diffs
            .OrderBy(d => (int)d.Severity)
            .ThenBy(d => d.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.Message, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void DiffSets(List<Diff> diffs, string category,
        List<string> aItems, List<string> bItems,
        string aMachine, string bMachine,
        Severity sev, Func<string, string?>? hint = null)
    {
        var aSet = new HashSet<string>(aItems, StringComparer.OrdinalIgnoreCase);
        var bSet = new HashSet<string>(bItems, StringComparer.OrdinalIgnoreCase);
        foreach (var item in aSet.Except(bSet).OrderBy(x => x))
            diffs.Add(new Diff(sev, category,
                $"{item} present on {aMachine}, MISSING on {bMachine}", hint?.Invoke(item)));
        foreach (var item in bSet.Except(aSet).OrderBy(x => x))
            diffs.Add(new Diff(sev, category,
                $"{item} present on {bMachine}, MISSING on {aMachine}", hint?.Invoke(item)));
    }

    public static string? LikelyCause(List<Diff> diffs)
    {
        var crits = diffs.Where(d => d.Severity == Severity.Critical).ToList();
        if (crits.Count == 0)
        {
            var highs = diffs.Where(d => d.Severity == Severity.High).Take(2).ToList();
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

    private static void DiffNuGet(List<Diff> diffs, Snapshot a, Snapshot b)
    {
        var aByName = a.NuGet.Sources.Where(s => !string.IsNullOrEmpty(s.Name))
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var bByName = b.NuGet.Sources.Where(s => !string.IsNullOrEmpty(s.Name))
            .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var name in aByName.Keys.Except(bByName.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
            diffs.Add(new Diff(Severity.Critical, "NuGet source",
                $"'{name}' ({aByName[name].Url}) configured on {a.Machine}, absent on {b.Machine}",
                "Restore likely fails for packages hosted on this feed."));
        foreach (var name in bByName.Keys.Except(aByName.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
            diffs.Add(new Diff(Severity.Critical, "NuGet source",
                $"'{name}' ({bByName[name].Url}) configured on {b.Machine}, absent on {a.Machine}",
                "Restore likely fails for packages hosted on this feed."));

        foreach (var name in aByName.Keys.Intersect(bByName.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var av = aByName[name]; var bv = bByName[name];
            if (!string.Equals(av.Url, bv.Url, StringComparison.OrdinalIgnoreCase))
                diffs.Add(new Diff(Severity.High, "NuGet source",
                    $"'{name}' URL differs: {a.Machine}={av.Url}, {b.Machine}={bv.Url}"));
            else if (av.Enabled != bv.Enabled)
                diffs.Add(new Diff(Severity.High, "NuGet source",
                    $"'{name}' enabled state differs: {a.Machine}={av.Enabled}, {b.Machine}={bv.Enabled}"));
        }
    }

    private static void DiffResolvedTools(List<Diff> diffs, Snapshot a, Snapshot b)
    {
        var names = a.ResolvedTools.Keys.Union(b.ResolvedTools.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var name in names.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            a.ResolvedTools.TryGetValue(name, out var av);
            b.ResolvedTools.TryGetValue(name, out var bv);
            if (av is null && bv is null) continue;

            if (av is null)
                diffs.Add(new Diff(Severity.High, "Resolved tool",
                    $"{name} resolves on {b.Machine} ({bv!.Path}) but NOT FOUND on {a.Machine}"));
            else if (bv is null)
                diffs.Add(new Diff(Severity.High, "Resolved tool",
                    $"{name} resolves on {a.Machine} ({av.Path}) but NOT FOUND on {b.Machine}"));
            else
            {
                if (!string.Equals(av.Path, bv.Path, StringComparison.OrdinalIgnoreCase))
                    diffs.Add(new Diff(Severity.Medium, "Resolved tool",
                        $"{name} resolves to different paths: {a.Machine}={av.Path}, {b.Machine}={bv.Path}"));
                if (!string.Equals(av.Version, bv.Version, StringComparison.Ordinal))
                    diffs.Add(new Diff(Severity.High, "Resolved tool",
                        $"{name} version differs: {a.Machine}={av.Version ?? "?"}, {b.Machine}={bv.Version ?? "?"}"));
            }
        }
    }

    private static void DiffEnvDict(List<Diff> diffs,
        Dictionary<string, string?> aDict, Dictionary<string, string?> bDict,
        string aMachine, string bMachine, Severity sev, string category)
    {
        var keys = aDict.Keys.Union(bDict.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var k in keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            aDict.TryGetValue(k, out var av);
            bDict.TryGetValue(k, out var bv);
            var aHas = aDict.ContainsKey(k);
            var bHas = bDict.ContainsKey(k);
            if (aHas && !bHas)
                diffs.Add(new Diff(sev, category, $"{k} set on {aMachine}, unset on {bMachine}"));
            else if (bHas && !aHas)
                diffs.Add(new Diff(sev, category, $"{k} set on {bMachine}, unset on {aMachine}"));
            else if (!string.Equals(av, bv, StringComparison.Ordinal))
                diffs.Add(new Diff(sev, category,
                    $"{k} differs: {aMachine}={Truncate(av)}, {bMachine}={Truncate(bv)}"));
        }
    }

    private static void DiffEnvPresence(List<Diff> diffs,
        Dictionary<string, string?> aDict, Dictionary<string, string?> bDict,
        string aMachine, string bMachine, Severity sev, string category)
    {
        foreach (var k in aDict.Keys.Except(bDict.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
            diffs.Add(new Diff(sev, category, $"{k} set on {aMachine} only"));
        foreach (var k in bDict.Keys.Except(aDict.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
            diffs.Add(new Diff(sev, category, $"{k} set on {bMachine} only"));
    }

    private static void DiffPathSets(List<Diff> diffs, List<string> aPath, List<string> bPath,
        string aMachine, string bMachine)
    {
        var aSet = new HashSet<string>(aPath, StringComparer.OrdinalIgnoreCase);
        var bSet = new HashSet<string>(bPath, StringComparer.OrdinalIgnoreCase);
        var aOnly = aSet.Except(bSet, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        var bOnly = bSet.Except(aSet, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        if (aOnly.Count > 0)
            diffs.Add(new Diff(Severity.Medium, "PATH",
                $"{aOnly.Count} entries on {aMachine} only: {string.Join("; ", aOnly.Take(3))}{(aOnly.Count > 3 ? " ..." : "")}"));
        if (bOnly.Count > 0)
            diffs.Add(new Diff(Severity.Medium, "PATH",
                $"{bOnly.Count} entries on {bMachine} only: {string.Join("; ", bOnly.Take(3))}{(bOnly.Count > 3 ? " ..." : "")}"));
    }

    private static string Truncate(string? s, int max = 80)
    {
        if (s is null) return "<unset>";
        return s.Length > max ? s.Substring(0, max - 1) + "…" : s;
    }
}
