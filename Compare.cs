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
        var crits = diffs.Where(d => d.Severity == Severity.Critical).Take(2).ToList();
        if (crits.Count == 0) return null;
        var bullets = string.Join("; ", crits.Select(c => c.Message));
        return crits.Count == 1
            ? $"Most likely cause: {bullets}."
            : $"Most likely cause: {bullets}.";
    }
}
