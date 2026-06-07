using System.Text.Json;

namespace BuildDiff;

public sealed class PythonPayload
{
    public List<PythonEnv> Envs { get; set; } = new();
}

public sealed class PythonEnv
{
    public string? Path { get; set; }
    public string? Version { get; set; }
    public string? Prefix { get; set; }
    public string? BasePrefix { get; set; }
    public bool InVirtualEnv { get; set; }
    public string? Architecture { get; set; }
    public List<PyPackage> Packages { get; set; } = new();
    public bool PackagesTruncated { get; set; }
}

public sealed class PyPackage
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
}

/// <summary>Python interpreters reachable on PATH and their installed packages. Cross-platform.</summary>
public sealed class PythonProvider : IEnvironmentProvider
{
    private const int MaxPyPackages = 200;

    public string Id => "python";
    public string DisplayName => "Python interpreters & packages";
    public bool AppliesTo(OsPlatform os) => true;

    public object? Capture(CaptureContext ctx)
    {
        var payload = new PythonPayload();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in new[] { "python3", "python" })
            foreach (var path in Proc.WhichAll(name))
            {
                if (path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) continue; // alias stubs
                if (!seen.Add(path)) continue;
                var env = Probe(path);
                if (env is not null) payload.Envs.Add(env);
            }

        return payload.Envs.Count == 0 ? null : payload;
    }

    private static PythonEnv? Probe(string path)
    {
        const string script =
            "import sys,json,platform;" +
            "print(json.dumps({" +
            "'version':platform.python_version()," +
            "'prefix':sys.prefix," +
            "'base_prefix':getattr(sys,'base_prefix',sys.prefix)," +
            "'arch':platform.architecture()[0]}))";
        var r = Proc.Run(path, $"-c \"{script}\"", timeoutMs: 10_000);
        if (r is null || r.ExitCode != 0 || string.IsNullOrWhiteSpace(r.Stdout)) return null;

        var env = new PythonEnv { Path = path };
        try
        {
            using var doc = JsonDocument.Parse(r.Stdout.Trim());
            var root = doc.RootElement;
            env.Version = GetStr(root, "version");
            env.Prefix = GetStr(root, "prefix");
            env.BasePrefix = GetStr(root, "base_prefix");
            env.Architecture = GetStr(root, "arch");
            env.InVirtualEnv = !string.Equals(env.Prefix, env.BasePrefix, StringComparison.OrdinalIgnoreCase);
        }
        catch { }

        env.Packages = ProbePackages(path, out var truncated);
        env.PackagesTruncated = truncated;
        return env;
    }

    private static List<PyPackage> ProbePackages(string path, out bool truncated)
    {
        truncated = false;
        var pkgs = new List<PyPackage>();
        var r = Proc.Run(path, "-m pip list --format json --disable-pip-version-check", timeoutMs: 20_000);
        if (r is null || r.ExitCode != 0 || string.IsNullOrWhiteSpace(r.Stdout)) return pkgs;
        try
        {
            using var doc = JsonDocument.Parse(r.Stdout.Trim());
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (pkgs.Count >= MaxPyPackages) { truncated = true; break; }
                var name = GetStr(el, "name");
                if (!string.IsNullOrEmpty(name))
                    pkgs.Add(new PyPackage { Name = name!, Version = GetStr(el, "version") ?? "" });
            }
        }
        catch { }
        return pkgs.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IEnumerable<Diff> Compare(JsonElement? a, JsonElement? b, CompareContext ctx)
    {
        var pa = Json.To<PythonPayload>(a) ?? new PythonPayload();
        var pb = Json.To<PythonPayload>(b) ?? new PythonPayload();
        var aPrimary = pa.Envs.FirstOrDefault();
        var bPrimary = pb.Envs.FirstOrDefault();

        if (aPrimary is null && bPrimary is null) yield break;
        if (aPrimary is null)
        {
            yield return new Diff(Severity.High, "Python", $"Python resolves on {ctx.B} ({bPrimary!.Version}) but NOT FOUND on {ctx.A}", null, Id);
            yield break;
        }
        if (bPrimary is null)
        {
            yield return new Diff(Severity.High, "Python", $"Python resolves on {ctx.A} ({aPrimary.Version}) but NOT FOUND on {ctx.B}", null, Id);
            yield break;
        }

        foreach (var d in DiffHelp.Scalar(Id, "Python", "Python version", aPrimary.Version, bPrimary.Version, ctx,
            Severity.High, Severity.High, "SWIG/native bindings often pin to a specific Python version.")) yield return d;
        foreach (var d in DiffHelp.Scalar(Id, "Python", "Python architecture", aPrimary.Architecture, bPrimary.Architecture, ctx,
            Severity.High, Severity.High)) yield return d;

        if (aPrimary.InVirtualEnv != bPrimary.InVirtualEnv)
            yield return new Diff(Severity.Medium, "Python",
                $"virtualenv state differs: {ctx.A} {(aPrimary.InVirtualEnv ? "in venv" : "system")}, {ctx.B} {(bPrimary.InVirtualEnv ? "in venv" : "system")}", null, Id);

        var aPkgs = aPrimary.Packages.ToDictionary(p => p.Name, p => p.Version, StringComparer.OrdinalIgnoreCase);
        var bPkgs = bPrimary.Packages.ToDictionary(p => p.Name, p => p.Version, StringComparer.OrdinalIgnoreCase);
        if (aPkgs.Count > 0 && bPkgs.Count > 0)
        {
            foreach (var name in aPkgs.Keys.Except(bPkgs.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
                yield return new Diff(Severity.Medium, "Python package", $"{name} {aPkgs[name]} installed on {ctx.A}, missing on {ctx.B}", null, Id);
            foreach (var name in bPkgs.Keys.Except(aPkgs.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
                yield return new Diff(Severity.Medium, "Python package", $"{name} {bPkgs[name]} installed on {ctx.B}, missing on {ctx.A}", null, Id);
            foreach (var name in aPkgs.Keys.Intersect(bPkgs.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
                if (!string.Equals(aPkgs[name], bPkgs[name], StringComparison.Ordinal))
                    yield return new Diff(Severity.Low, "Python package", $"{name} version differs: {ctx.A}={aPkgs[name]}, {ctx.B}={bPkgs[name]}", null, Id);
        }
    }

    private static string? GetStr(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
