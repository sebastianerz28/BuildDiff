using System.Text.Json;
using System.Text.RegularExpressions;

namespace BuildDiff;

public sealed class BazelPayload
{
    // Bazel binary.
    public bool BazelPresent { get; set; }
    public string? BazelPath { get; set; }
    public List<string> BazelAllPaths { get; set; } = new();
    public string? BazelVersion { get; set; }
    public bool IsBazelisk { get; set; }

    // Bazelisk launcher + env.
    public bool BazeliskPresent { get; set; }
    public string? BazeliskVersion { get; set; }
    public string? BazeliskHome { get; set; }
    public string? UseBazelVersionEnv { get; set; }
    public string? BazeliskBaseUrl { get; set; }

    // Project mode (keyed off ctx.ProjectRoot).
    public string? BazelversionPin { get; set; }
    public bool PinIsAlias { get; set; }
    public string? ResolutionMode { get; set; }
    public bool HasModuleBazel { get; set; }
    public bool HasModuleLock { get; set; }
    public string? ModuleName { get; set; }
    public string? ModuleVersion { get; set; }
    public bool HasWorkspace { get; set; }
    public bool HasRepoBazelrc { get; set; }
    public int RcRepoLineCount { get; set; }
    public bool HasUserBazelrc { get; set; }
    public bool HasSystemBazelrc { get; set; }
}

/// <summary>
/// Bazel / Bazelisk: the launcher (bazelisk) vs raw bazel, the resolved version
/// (honoring a repo <c>.bazelversion</c> pin via the bazelisk shim), the module
/// resolution mode (bzlmod vs WORKSPACE), and the .bazelrc layering — all sources
/// of "builds here but not there" for Bazel repos. Cross-platform.
///
/// SAFETY: only ever runs <c>bazel --version</c> and <c>bazelisk version</c>; never
/// a bare <c>bazel version</c>/<c>info</c>/build/query (those start a server and may
/// download a toolchain). Never reads .bazelrc/MODULE.bazel/WORKSPACE contents beyond
/// presence and a blank/comment-stripped line count — they carry remote-cache and
/// credential tokens.
/// </summary>
public sealed class BazelProvider : IEnvironmentProvider
{
    private static readonly string[] Aliases = { "latest", "last_green", "last_downstream_green" };

    public string Id => "bazel";
    public string DisplayName => "Bazel / Bazelisk";
    public bool AppliesTo(OsPlatform os) => true;

    public object? Capture(CaptureContext ctx)
    {
        var p = new BazelPayload();

        var bazelPaths = Proc.WhichAll("bazel.exe").Concat(Proc.WhichAll("bazel"))
            .Distinct(Os.PathComparer).ToList();
        p.BazelAllPaths = bazelPaths;
        p.BazelPresent = bazelPaths.Count > 0;
        p.BazelPath = bazelPaths.FirstOrDefault();

        if (p.BazelPath is not null)
        {
            // Run with the repo as the working dir so a bazelisk shim resolves the
            // repo pin. ONLY `--version` — never a bare `version`/`info`/build.
            var r = Proc.Run(p.BazelPath, "--version", timeoutMs: 12_000, workingDir: ctx.ProjectRoot);
            if (r is not null)
            {
                var m = Regex.Match(r.Combined,
                    @"(?im)^\s*bazel\s+([0-9]+\.[0-9]+(?:\.[0-9]+)?[-\w.]*)");
                if (m.Success) p.BazelVersion = m.Groups[1].Value;
                if (r.Combined.Contains("bazelisk", StringComparison.OrdinalIgnoreCase))
                    p.IsBazelisk = true;
            }

            if (LooksLikeBazelisk(p.BazelPath)) p.IsBazelisk = true;
        }

        var bazelisk = Proc.Which("bazelisk.exe") ?? Proc.Which("bazelisk");
        if (bazelisk is not null && !PathEquals(bazelisk, p.BazelPath))
        {
            p.BazeliskPresent = true;
            var r = Proc.Run(bazelisk, "version", timeoutMs: 12_000, workingDir: ctx.ProjectRoot);
            if (r is not null)
            {
                var m = Regex.Match(r.Combined, @"(?im)^Bazelisk version:\s*v?([0-9][0-9.]+)");
                if (m.Success) p.BazeliskVersion = m.Groups[1].Value;
            }
        }

        p.BazeliskHome = NullIfEmpty(Environment.GetEnvironmentVariable("BAZELISK_HOME"));
        if (p.BazeliskHome is not null && p.BazelPath is not null &&
            p.BazelPath.Replace('\\', '/').Contains(p.BazeliskHome.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            p.IsBazelisk = true;

        p.UseBazelVersionEnv = NullIfEmpty(Environment.GetEnvironmentVariable("USE_BAZEL_VERSION"));

        var baseUrl = NullIfEmpty(Environment.GetEnvironmentVariable("BAZELISK_BASE_URL"))
            ?? NullIfEmpty(Environment.GetEnvironmentVariable("BAZELISK_FORMAT_URL"));
        p.BazeliskBaseUrl = StripUserInfo(baseUrl);

        p.HasUserBazelrc = UserBazelrcExists();
        p.HasSystemBazelrc = SystemBazelrcExists();

        CaptureProject(ctx, p);

        bool anyMarker = p.BazelPresent || p.BazeliskPresent
            || p.BazelversionPin is not null || p.HasModuleBazel || p.HasWorkspace
            || p.HasRepoBazelrc;
        return anyMarker ? p : null;
    }

    private static void CaptureProject(CaptureContext ctx, BazelPayload p)
    {
        var root = ctx.ProjectRoot;
        if (string.IsNullOrEmpty(root)) return;

        try
        {
            var pinFile = Path.Combine(root, ".bazelversion");
            if (File.Exists(pinFile))
            {
                var pin = File.ReadLines(pinFile)
                    .Select(l => l.Trim())
                    .FirstOrDefault(l => l.Length > 0);
                p.BazelversionPin = NullIfEmpty(pin);
                if (p.BazelversionPin is not null)
                {
                    bool concrete = char.IsDigit(p.BazelversionPin[0])
                        && !Aliases.Contains(p.BazelversionPin, StringComparer.OrdinalIgnoreCase);
                    p.PinIsAlias = !concrete;
                }
            }
        }
        catch { }

        p.HasModuleBazel = SafeExists(root, "MODULE.bazel");
        p.HasModuleLock = SafeExists(root, "MODULE.bazel.lock");
        p.HasWorkspace = SafeExists(root, "WORKSPACE") || SafeExists(root, "WORKSPACE.bazel");

        p.ResolutionMode = p.HasModuleBazel ? "bzlmod"
            : p.HasWorkspace ? "workspace"
            : "unknown";

        if (p.HasModuleBazel)
            ReadModuleIdentity(root, p);

        try
        {
            var rc = Path.Combine(root, ".bazelrc");
            if (File.Exists(rc))
            {
                p.HasRepoBazelrc = true;
                // Count ONLY — never store .bazelrc content (it carries cache/cred flags).
                p.RcRepoLineCount = File.ReadLines(rc)
                    .Select(l => l.Trim())
                    .Count(l => l.Length > 0 && !l.StartsWith('#'));
            }
        }
        catch { }
    }

    private static void ReadModuleIdentity(string root, BazelPayload p)
    {
        try
        {
            // module(name=,version=) only — no other content is retained.
            var text = File.ReadAllText(Path.Combine(root, "MODULE.bazel"));
            var name = Regex.Match(text, @"(?is)\bmodule\s*\(.*?\bname\s*=\s*[""']([^""']+)[""']");
            if (name.Success) p.ModuleName = name.Groups[1].Value;
            var ver = Regex.Match(text, @"(?is)\bmodule\s*\(.*?\bversion\s*=\s*[""']([^""']+)[""']");
            if (ver.Success) p.ModuleVersion = ver.Groups[1].Value;
        }
        catch { }
    }

    public IEnumerable<Diff> Compare(JsonElement? a, JsonElement? b, CompareContext ctx)
    {
        var pa = Json.To<BazelPayload>(a) ?? new BazelPayload();
        var pb = Json.To<BazelPayload>(b) ?? new BazelPayload();

        // --- Critical: unmanaged bazel ignores a concrete pin ----------------
        foreach (var d in PinIgnored(pa, ctx.A, ctx.B)) yield return d;
        foreach (var d in PinIgnored(pb, ctx.B, ctx.A)) yield return d;

        // --- Critical: bazel present on exactly one side ---------------------
        if (pa.BazelPresent && !pb.BazelPresent)
            yield return new Diff(Severity.Critical, "Bazel",
                $"bazel present on {ctx.A} ({pa.BazelVersion ?? "?"}) but NOT FOUND on {ctx.B}",
                "Install bazelisk so both machines resolve the same Bazel.", Id);
        else if (pb.BazelPresent && !pa.BazelPresent)
            yield return new Diff(Severity.Critical, "Bazel",
                $"bazel present on {ctx.B} ({pb.BazelVersion ?? "?"}) but NOT FOUND on {ctx.A}",
                "Install bazelisk so both machines resolve the same Bazel.", Id);

        // --- bazel version differences ---------------------------------------
        bool aHasVer = !string.IsNullOrEmpty(pa.BazelVersion);
        bool bHasVer = !string.IsNullOrEmpty(pb.BazelVersion);
        if (aHasVer && bHasVer && !string.Equals(pa.BazelVersion, pb.BazelVersion, StringComparison.Ordinal))
        {
            bool sameMajor = string.Equals(Major(pa.BazelVersion), Major(pb.BazelVersion), StringComparison.Ordinal);
            if (!sameMajor)
                yield return new Diff(Severity.Critical, "Bazel",
                    $"bazel MAJOR version differs: {ctx.A}={pa.BazelVersion}, {ctx.B}={pb.BazelVersion}",
                    "Pin via .bazelversion + bazelisk so both resolve the same Bazel.", Id);
            else if (BothBazeliskPinned(pa, pb) && PatchOnly(pa.BazelVersion, pb.BazelVersion))
                yield return new Diff(Severity.Low, "Bazel",
                    $"bazel patch version differs under a bazelisk pin: {ctx.A}={pa.BazelVersion}, {ctx.B}={pb.BazelVersion}",
                    null, Id);
            else
                yield return new Diff(Severity.High, "Bazel",
                    $"bazel version differs: {ctx.A}={pa.BazelVersion}, {ctx.B}={pb.BazelVersion}",
                    "Adopt .bazelversion via bazelisk so both resolve the same Bazel.", Id);
        }

        // --- resolution mode / module graph ----------------------------------
        if (!string.IsNullOrEmpty(pa.ResolutionMode) && !string.IsNullOrEmpty(pb.ResolutionMode)
            && !string.Equals(pa.ResolutionMode, pb.ResolutionMode, StringComparison.Ordinal))
            yield return new Diff(Severity.Critical, "Bazel module",
                $"resolution mode differs: {ctx.A}={pa.ResolutionMode}, {ctx.B}={pb.ResolutionMode}",
                "Check out the same revision on both — bzlmod and WORKSPACE produce different dependency graphs.", Id);
        else if (pa.HasModuleBazel != pb.HasModuleBazel)
            yield return new Diff(Severity.Critical, "Bazel module",
                $"MODULE.bazel present on {(pa.HasModuleBazel ? ctx.A : ctx.B)} only",
                "Check out the same revision on both — bzlmod and WORKSPACE produce different dependency graphs.", Id);

        // --- USE_BAZEL_VERSION ----------------------------------------------
        foreach (var d in DiffHelp.Scalar(Id, "Bazelisk", "USE_BAZEL_VERSION",
            pa.UseBazelVersionEnv, pb.UseBazelVersionEnv, ctx, Severity.High, Severity.High,
            "USE_BAZEL_VERSION silently overrides .bazelversion; unset it for reproducible builds.")) yield return d;

        // --- High: concrete pin but raw bazel (not bazelisk) -----------------
        foreach (var d in RawBazelUnderPin(pa, ctx.A)) yield return d;
        foreach (var d in RawBazelUnderPin(pb, ctx.B)) yield return d;

        // --- High: floating (alias) pin --------------------------------------
        if (pa.PinIsAlias)
            yield return new Diff(Severity.High, "Bazel config",
                $"{ctx.A} pins a floating .bazelversion alias ({pa.BazelversionPin})",
                "Pin a concrete Bazel version instead of a floating alias.", Id);
        if (pb.PinIsAlias)
            yield return new Diff(Severity.High, "Bazel config",
                $"{ctx.B} pins a floating .bazelversion alias ({pb.BazelversionPin})",
                "Pin a concrete Bazel version instead of a floating alias.", Id);

        // --- Medium: multiple bazel installs ---------------------------------
        if (pa.BazelAllPaths.Count > 1)
            yield return new Diff(Severity.Medium, "Bazel",
                $"{ctx.A} has multiple bazel installs ({pa.BazelAllPaths.Count})", null, Id);
        if (pb.BazelAllPaths.Count > 1)
            yield return new Diff(Severity.Medium, "Bazel",
                $"{ctx.B} has multiple bazel installs ({pb.BazelAllPaths.Count})", null, Id);

        // --- Medium: bazelisk version / base url -----------------------------
        foreach (var d in DiffHelp.Scalar(Id, "Bazelisk", "bazelisk",
            pa.BazeliskVersion, pb.BazeliskVersion, ctx, Severity.Medium, Severity.Medium)) yield return d;
        foreach (var d in DiffHelp.Scalar(Id, "Bazelisk", "BAZELISK_BASE_URL",
            pa.BazeliskBaseUrl, pb.BazeliskBaseUrl, ctx, Severity.Medium, Severity.Medium,
            "A custom Bazelisk mirror changes which Bazel binaries are downloaded.")) yield return d;

        // --- Medium: user/system bazelrc one side only -----------------------
        if (pa.HasUserBazelrc != pb.HasUserBazelrc)
            yield return new Diff(Severity.Medium, "Bazel config",
                $"user ~/.bazelrc present on {(pa.HasUserBazelrc ? ctx.A : ctx.B)} only",
                "A per-user .bazelrc can silently change flags on one machine.", Id);
        if (pa.HasSystemBazelrc != pb.HasSystemBazelrc)
            yield return new Diff(Severity.Medium, "Bazel config",
                $"system bazelrc present on {(pa.HasSystemBazelrc ? ctx.A : ctx.B)} only",
                "A system-wide bazelrc can silently change flags on one machine.", Id);

        // --- Medium: repo bazelrc line count differs -------------------------
        if (pa.HasRepoBazelrc && pb.HasRepoBazelrc && pa.RcRepoLineCount != pb.RcRepoLineCount)
            yield return new Diff(Severity.Medium, "Bazel config",
                $"repo .bazelrc line count differs: {ctx.A}={pa.RcRepoLineCount}, {ctx.B}={pb.RcRepoLineCount}",
                "The checked-out .bazelrc differs between machines — sync to the same revision.", Id);

        // --- Low: module lock / bazelisk home --------------------------------
        if (pa.HasModuleBazel && pb.HasModuleBazel && pa.HasModuleLock != pb.HasModuleLock)
            yield return new Diff(Severity.Low, "Bazel module",
                $"MODULE.bazel.lock present on {(pa.HasModuleLock ? ctx.A : ctx.B)} only", null, Id);
        foreach (var d in DiffHelp.Scalar(Id, "Bazelisk", "BAZELISK_HOME",
            pa.BazeliskHome, pb.BazeliskHome, ctx, Severity.Low, Severity.Low)) yield return d;
    }

    // --- compare helpers -----------------------------------------------------

    private IEnumerable<Diff> PinIgnored(BazelPayload p, string side, string other)
    {
        if (string.IsNullOrEmpty(p.BazelversionPin) || p.PinIsAlias || p.IsBazelisk) yield break;
        bool majorMismatch = !p.BazelPresent
            || !string.Equals(Major(p.BazelVersion), Major(p.BazelversionPin), StringComparison.Ordinal);
        if (majorMismatch)
            yield return new Diff(Severity.Critical, "Bazel config",
                $"{side} pins .bazelversion {p.BazelversionPin} but runs unmanaged bazel ({p.BazelVersion ?? "absent"}) which ignores the pin",
                "Install bazelisk so the .bazelversion pin is honored.", Id);
    }

    private IEnumerable<Diff> RawBazelUnderPin(BazelPayload p, string side)
    {
        if (string.IsNullOrEmpty(p.BazelversionPin) || p.PinIsAlias || p.IsBazelisk) yield break;
        bool majorMatches = p.BazelPresent
            && string.Equals(Major(p.BazelVersion), Major(p.BazelversionPin), StringComparison.Ordinal);
        // Only emit High here when the Critical pin-ignored rule did NOT already fire.
        if (majorMatches)
            yield return new Diff(Severity.High, "Bazel config",
                $"{side} has a .bazelversion pin ({p.BazelversionPin}) but uses raw bazel, not bazelisk",
                "Replace raw bazel with bazelisk so the pin is always honored.", Id);
    }

    private static bool BothBazeliskPinned(BazelPayload a, BazelPayload b)
        => a.IsBazelisk && b.IsBazelisk;

    private static bool PatchOnly(string? a, string? b)
    {
        var pa = (a ?? "").Split('.', '-');
        var pb = (b ?? "").Split('.', '-');
        if (pa.Length < 2 || pb.Length < 2) return false;
        return string.Equals(pa[0], pb[0], StringComparison.Ordinal)
            && string.Equals(pa[1], pb[1], StringComparison.Ordinal);
    }

    private static string? Major(string? v)
        => string.IsNullOrEmpty(v) ? null : v.Split('.', '-').FirstOrDefault();

    // --- capture helpers -----------------------------------------------------

    private static bool LooksLikeBazelisk(string path)
        => path.Replace('\\', '/').Contains("bazelisk", StringComparison.OrdinalIgnoreCase);

    private static bool PathEquals(string a, string? b)
        => b is not null && Os.PathComparer.Equals(a, b);

    private static bool SafeExists(string root, string file)
    {
        try { return File.Exists(Path.Combine(root, file)); }
        catch { return false; }
    }

    private static bool UserBazelrcExists()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return !string.IsNullOrEmpty(home) && File.Exists(Path.Combine(home, ".bazelrc"));
        }
        catch { return false; }
    }

    private static bool SystemBazelrcExists()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var bazel = Proc.Which("bazel.exe") ?? Proc.Which("bazel");
                var dir = bazel is null ? null : Path.GetDirectoryName(bazel);
                return dir is not null && File.Exists(Path.Combine(dir, "bazel.bazelrc"));
            }
            return File.Exists("/etc/bazel.bazelrc");
        }
        catch { return false; }
    }

    private static string? StripUserInfo(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        // Strip any 'user:pass@' userinfo from a scheme://user:pass@host URL.
        var m = Regex.Match(url, @"^([a-zA-Z][a-zA-Z0-9+.\-]*://)[^/@]*@(.*)$");
        return m.Success ? m.Groups[1].Value + m.Groups[2].Value : url;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
