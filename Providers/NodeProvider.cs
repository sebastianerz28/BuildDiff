using System.Text.Json;

namespace BuildDiff;

public sealed class NodePayload
{
    public string? NodeVersion { get; set; }
    public string? NodePath { get; set; }
    public string? Arch { get; set; }
    public string? Abi { get; set; }          // process.versions.modules (NODE_MODULE_VERSION)
    public string? V8 { get; set; }
    public string? OpenSsl { get; set; }
    public string? ActiveVersionManager { get; set; }
    public Dictionary<string, string?> PackageManagers { get; set; } = new();
    public string? CorepackVersion { get; set; }
    public List<string> InstalledNodeVersions { get; set; } = new();
    public List<string> GlobalPackages { get; set; } = new();
}

/// <summary>
/// Node.js: the active runtime (version, arch, native-addon ABI), the package
/// managers (npm/pnpm/yarn/bun, including corepack shims), the active version
/// manager, and other installed node versions. Cross-platform.
/// </summary>
public sealed class NodeProvider : IEnvironmentProvider
{
    private const int MaxGlobal = 100;

    public string Id => "javascript-node";
    public string DisplayName => "Node.js, npm/pnpm/yarn/bun, version managers";
    public bool AppliesTo(OsPlatform os) => true;

    public object? Capture(CaptureContext ctx)
    {
        var node = Proc.Which("node.exe") ?? Proc.Which("node");
        var payload = new NodePayload { NodePath = node };

        if (node is not null)
        {
            payload.NodeVersion = TrimV(Proc.Run(node, "-v", timeoutMs: 10_000)?.FirstLine);
            payload.Arch = NodeEval(node, "process.arch");
            payload.Abi = NodeEval(node, "process.versions.modules");
            payload.V8 = NodeEval(node, "process.versions.v8");
            payload.OpenSsl = NodeEval(node, "process.versions.openssl");
            payload.ActiveVersionManager = DetectActiveManager(node);
            payload.GlobalPackages = GlobalPackages(node);
        }

        foreach (var pm in new[] { "npm", "pnpm", "yarn", "bun" })
        {
            var path = Proc.Which(pm) ?? Proc.Which(pm + ".cmd") ?? Proc.Which(pm + ".exe");
            if (path is null) continue;
            var v = Proc.Run(path, "--version", timeoutMs: 12_000)?.FirstLine;
            if (!string.IsNullOrWhiteSpace(v)) payload.PackageManagers[pm] = v.Trim();
        }

        var corepack = Proc.Which("corepack") ?? Proc.Which("corepack.cmd");
        if (corepack is not null)
            payload.CorepackVersion = Proc.Run(corepack, "--version", timeoutMs: 10_000)?.FirstLine;

        payload.InstalledNodeVersions = ScanInstalledNodeVersions();

        if (node is null && payload.PackageManagers.Count == 0) return null;
        return payload;
    }

    public IEnumerable<Diff> Compare(JsonElement? a, JsonElement? b, CompareContext ctx)
    {
        var pa = Json.To<NodePayload>(a) ?? new NodePayload();
        var pb = Json.To<NodePayload>(b) ?? new NodePayload();

        bool aHas = !string.IsNullOrEmpty(pa.NodeVersion);
        bool bHas = !string.IsNullOrEmpty(pb.NodeVersion);
        if (aHas && !bHas)
            yield return new Diff(Severity.Critical, "Node", $"node present on {ctx.A} ({pa.NodeVersion}) but NOT FOUND on {ctx.B}", null, Id);
        else if (bHas && !aHas)
            yield return new Diff(Severity.Critical, "Node", $"node present on {ctx.B} ({pb.NodeVersion}) but NOT FOUND on {ctx.A}", null, Id);
        else if (aHas && bHas && !string.Equals(pa.NodeVersion, pb.NodeVersion, StringComparison.Ordinal))
        {
            var sameMajor = string.Equals(Major(pa.NodeVersion), Major(pb.NodeVersion), StringComparison.Ordinal);
            yield return new Diff(sameMajor ? Severity.High : Severity.Critical, "Node",
                $"node version differs: {ctx.A}={pa.NodeVersion}, {ctx.B}={pb.NodeVersion}",
                sameMajor ? null : "Different Node MAJOR versions frequently break native addons, lockfile resolution and engines constraints.", Id);
        }

        foreach (var d in DiffHelp.Scalar(Id, "Node", "node arch", pa.Arch, pb.Arch, ctx, Severity.High, Severity.High)) yield return d;
        foreach (var d in DiffHelp.Scalar(Id, "Node", "node ABI (NODE_MODULE_VERSION)", pa.Abi, pb.Abi, ctx, Severity.High, Severity.High,
            "Prebuilt native addons compiled for a different ABI fail to load (NODE_MODULE_VERSION mismatch).")) yield return d;

        foreach (var d in DiffHelp.Scalar(Id, "Node", "active version manager", pa.ActiveVersionManager, pb.ActiveVersionManager, ctx, Severity.Low, Severity.Low)) yield return d;

        // Package managers.
        foreach (var pm in pa.PackageManagers.Keys.Union(pb.PackageManagers.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
        {
            pa.PackageManagers.TryGetValue(pm, out var av);
            pb.PackageManagers.TryGetValue(pm, out var bv);
            if (av is not null && bv is null)
                yield return new Diff(Severity.Medium, "Node package manager", $"{pm} {av} on {ctx.A}, absent on {ctx.B}", null, Id);
            else if (bv is not null && av is null)
                yield return new Diff(Severity.Medium, "Node package manager", $"{pm} {bv} on {ctx.B}, absent on {ctx.A}", null, Id);
            else if (!string.Equals(av, bv, StringComparison.Ordinal))
            {
                var sameMajor = string.Equals(Major(av), Major(bv), StringComparison.Ordinal);
                yield return new Diff(sameMajor ? Severity.Low : Severity.Medium, "Node package manager",
                    $"{pm} version differs: {ctx.A}={av}, {ctx.B}={bv}",
                    sameMajor ? null : "Major package-manager version changes (e.g. yarn 1 → 4, pnpm 8 → 9) change lockfile format & resolution.", Id);
            }
        }
    }

    // ---- capture internals --------------------------------------------------

    private static string? NodeEval(string node, string expr)
        => Proc.Run(node, $"-p \"{expr}\"", timeoutMs: 10_000)?.FirstLine?.Trim();

    private static string? TrimV(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim().TrimStart('v', 'V');
    private static string? Major(string? v) => v?.Split('.', '-').FirstOrDefault();

    private static string DetectActiveManager(string nodePath)
    {
        var p = nodePath.Replace('\\', '/');
        if (p.Contains("/.nvm/", StringComparison.OrdinalIgnoreCase)) return "nvm";
        if (p.Contains("/fnm", StringComparison.OrdinalIgnoreCase) || p.Contains("/.fnm/", StringComparison.OrdinalIgnoreCase)) return "fnm";
        if (p.Contains("/.volta/", StringComparison.OrdinalIgnoreCase)) return "volta";
        if (p.Contains("/.asdf/", StringComparison.OrdinalIgnoreCase)) return "asdf";
        if (p.Contains("nvm", StringComparison.OrdinalIgnoreCase)) return "nvm-windows";
        if (p.Contains("homebrew", StringComparison.OrdinalIgnoreCase) || p.Contains("/Cellar/", StringComparison.OrdinalIgnoreCase)) return "homebrew";
        return "system";
    }

    private static List<string> GlobalPackages(string node)
    {
        var npm = Proc.Which("npm") ?? Proc.Which("npm.cmd");
        if (npm is null) return new();
        var r = Proc.Run(npm, "ls -g --depth=0 --json", timeoutMs: 20_000);
        if (r is null || string.IsNullOrWhiteSpace(r.Stdout)) return new();
        var list = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(r.Stdout.Trim());
            if (doc.RootElement.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Object)
                foreach (var p in deps.EnumerateObject())
                {
                    if (list.Count >= MaxGlobal) break;
                    var ver = p.Value.TryGetProperty("version", out var v) ? v.GetString() : null;
                    list.Add(ver is null ? p.Name : $"{p.Name}@{ver}");
                }
        }
        catch { }
        return list.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> ScanInstalledNodeVersions()
    {
        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        void ScanDir(string dir, Func<string, string?> nameToVer)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var d in Directory.GetDirectories(dir))
                {
                    var v = nameToVer(Path.GetFileName(d));
                    if (!string.IsNullOrWhiteSpace(v)) versions.Add(v!.TrimStart('v', 'V'));
                }
            }
            catch { }
        }

        var nvmDir = Environment.GetEnvironmentVariable("NVM_DIR") ?? Path.Combine(home, ".nvm");
        ScanDir(Path.Combine(nvmDir, "versions", "node"), n => n);
        var voltaDir = Environment.GetEnvironmentVariable("VOLTA_HOME") ?? Path.Combine(home, ".volta");
        ScanDir(Path.Combine(voltaDir, "tools", "image", "node"), n => n);
        ScanDir(Path.Combine(home, ".asdf", "installs", "nodejs"), n => n);
        var fnmDir = Environment.GetEnvironmentVariable("FNM_DIR") ?? Path.Combine(home, ".fnm");
        ScanDir(Path.Combine(fnmDir, "node-versions"), n => n);
        var nvmWin = Environment.GetEnvironmentVariable("NVM_HOME");
        if (nvmWin is not null) ScanDir(nvmWin, n => n.StartsWith('v') ? n : null);

        return versions.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
