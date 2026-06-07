using System.Text.Json;
using System.Text.RegularExpressions;

namespace BuildDiff;

/// <summary>
/// Two kinds of <c>--project</c> drift:
///  1. machine-vs-declared — a machine's detected toolchain doesn't satisfy what the
///     repo pins (e.g. .nvmrc says node 20 but the box runs node 25). This is the
///     high-value check.
///  2. declared-between-snapshots — the two machines disagree on what the project
///     declares, which usually means different checkouts.
/// </summary>
public static class ProjectCompare
{
    // Requirement key -> the provider whose payload holds the machine's active version.
    private static readonly (string Key, string ProviderId)[] PinnedTools =
    {
        ("node", "javascript-node"),
        ("python", "python"),
        ("ruby", "ruby"),
        ("java", "jvm"),
        ("go", "go"),
    };

    public static IEnumerable<Diff> Run(Snapshot a, Snapshot b, CompareContext ctx)
    {
        foreach (var d in MachineVsDeclared(a, ctx.A)) yield return d;
        foreach (var d in MachineVsDeclared(b, ctx.B)) yield return d;
        foreach (var d in AmbiguousLockfiles(a, ctx.A)) yield return d;
        foreach (var d in AmbiguousLockfiles(b, ctx.B)) yield return d;

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
                    "The two machines appear to be on different checkouts of this project.", "project");
        }

        foreach (var d in LockfileDrift(a.Project, b.Project, ctx)) yield return d;
    }

    private static readonly string[] NodePmLocks =
        { "package-lock.json", "npm-shrinkwrap.json", "pnpm-lock.yaml", "yarn.lock", "bun.lockb" };

    private static IEnumerable<Diff> AmbiguousLockfiles(Snapshot s, string machine)
    {
        if (s.Project is null) yield break;
        var competing = s.Project.Lockfiles
            .Select(l => l.File)
            .Where(f => NodePmLocks.Contains(f, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (competing.Count > 1)
            yield return new Diff(Severity.Medium, "Lockfile",
                $"{machine}: {competing.Count} competing Node lockfiles present ({string.Join(", ", competing)})",
                "Multiple package-manager lockfiles make installs nondeterministic — keep one.", "project");
    }

    private static IEnumerable<Diff> LockfileDrift(ProjectInfo a, ProjectInfo b, CompareContext ctx)
    {
        var aByFile = a.Lockfiles.GroupBy(l => l.File, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var bByFile = b.Lockfiles.GroupBy(l => l.File, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var file in aByFile.Keys.Union(bByFile.Keys, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            aByFile.TryGetValue(file, out var al);
            bByFile.TryGetValue(file, out var bl);
            if (al is not null && bl is null)
                yield return new Diff(Severity.Medium, "Lockfile", $"{file} present on {ctx.A} only", null, "project");
            else if (bl is not null && al is null)
                yield return new Diff(Severity.Medium, "Lockfile", $"{file} present on {ctx.B} only", null, "project");
            else if (al is not null && bl is not null)
            {
                if (!string.Equals(al.Marker, bl.Marker, StringComparison.OrdinalIgnoreCase) && al.Marker is not null && bl.Marker is not null)
                    yield return new Diff(Severity.High, "Lockfile",
                        $"{file} format differs: {ctx.A}={al.Marker}, {ctx.B}={bl.Marker}",
                        "Different lockfile format/version resolves dependencies differently.", "project");
                else if (!string.Equals(al.Hash, bl.Hash, StringComparison.OrdinalIgnoreCase))
                    yield return new Diff(Severity.High, "Lockfile",
                        $"{file} content differs between machines",
                        "The two machines are locked to different dependency sets — a classic 'works here, not there'.", "project");
            }
        }
    }

    private static IEnumerable<Diff> MachineVsDeclared(Snapshot s, string machine)
    {
        if (s.Project is null) yield break;

        foreach (var m in s.Project.Manifests)
            foreach (var (key, declared) in m.Declares)
            {
                if (declared is null) continue;
                var tool = PinnedTools.FirstOrDefault(t => t.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (tool.ProviderId is null) continue;          // not a tool we can resolve
                if (!IsConcreteVersion(declared)) continue;     // skip ranges / aliases (>=22, lts/iron)

                var captured = CapturedVersion(s, tool.ProviderId, key);
                if (captured is null)
                    yield return new Diff(Severity.High, "Project requirement",
                        $"{machine}: {key} is declared ({declared} in {m.File}) but no {key} was detected", null, "project");
                else if (!SatisfiesPinPrecision(declared, captured))
                    yield return new Diff(Severity.Medium, "Project requirement",
                        $"{machine}: {key} {captured} does not satisfy declared {declared} ({m.File})",
                        $"Switch {key} to {declared} (e.g. via your version manager).", "project");
            }
    }

    private static string? CapturedVersion(Snapshot s, string providerId, string key)
    {
        if (!s.Providers.TryGetValue(providerId, out var el)) return null;
        try
        {
            if (key.Equals("python", StringComparison.OrdinalIgnoreCase))
            {
                if (el.TryGetProperty("envs", out var envs) && envs.ValueKind == JsonValueKind.Array)
                    foreach (var e in envs.EnumerateArray())
                        if (e.TryGetProperty("version", out var v)) return v.GetString();
                return null;
            }
            var field = key.ToLowerInvariant() switch
            {
                "node" => "node_version",
                "java" => "java_version",
                _ => "version",
            };
            return el.TryGetProperty(field, out var f) ? f.GetString() : null;
        }
        catch { return null; }
    }

    // Compare only as precisely as the pin specifies: pin "20" checks the major,
    // "3.11" checks major.minor, "20.11.0" checks all three.
    private static bool SatisfiesPinPrecision(string pin, string captured)
    {
        var pinParts = pin.Split('.');
        var capParts = captured.Split('.', '-', '+');
        for (int i = 0; i < pinParts.Length; i++)
        {
            if (i >= capParts.Length) return false;
            if (!string.Equals(pinParts[i], capParts[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static bool IsConcreteVersion(string v) => Regex.IsMatch(v, @"^\d+(\.\d+)*$");

    private static Dictionary<string, string?> Flatten(ProjectInfo p)
    {
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in p.Manifests)
            foreach (var kv in m.Declares)
                d[$"{m.File}:{kv.Key}"] = kv.Value;
        return d;
    }
}
