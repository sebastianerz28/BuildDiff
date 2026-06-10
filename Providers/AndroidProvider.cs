using System.Text.Json;
using System.Text.RegularExpressions;

namespace BuildDiff;

public sealed class AndroidPayload
{
    public string? SdkRoot { get; set; }
    public string? SdkRootSource { get; set; }            // env:ANDROID_HOME | env:ANDROID_SDK_ROOT | local.properties | default | none
    public string? AndroidHomeEnv { get; set; }
    public string? AndroidSdkRootEnv { get; set; }
    public List<string> BuildTools { get; set; } = new();
    public List<string> Platforms { get; set; } = new();
    public bool PlatformToolsPresent { get; set; }
    public string? PlatformToolsVersion { get; set; }
    public List<string> CmdlineToolsChannels { get; set; } = new();
    public string? SdkmanagerVersion { get; set; }
    public List<string> NdkVersions { get; set; } = new();
    public bool NdkEnvOutsideSdk { get; set; }
    public bool EmulatorPresent { get; set; }
    public string? EmulatorVersion { get; set; }
    public Dictionary<string, bool> LicensesAccepted { get; set; } = new();
}

/// <summary>
/// Android SDK stack: SDK root resolution, build-tools, platforms, platform-tools,
/// cmdline-tools/sdkmanager, NDK revisions, emulator, and license-file presence.
/// Safety-first: records only paths, version strings, directory names and
/// license-file presence booleans — never reads license/keystore/signing contents,
/// and never runs <c>sdkmanager --list</c> or <c>adb devices</c>. Cross-platform.
/// </summary>
public sealed class AndroidProvider : IEnvironmentProvider
{
    public string Id => "android-sdk";
    public string DisplayName => "Android SDK, NDK, build-tools, platforms";
    public bool AppliesTo(OsPlatform os) => true;

    public object? Capture(CaptureContext ctx)
    {
        var androidHome = Environment.GetEnvironmentVariable("ANDROID_HOME");
        var androidSdkRoot = Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT");

        var (sdkRoot, source) = ResolveSdkRoot(ctx, androidHome, androidSdkRoot);

        // Absent ecosystem: no resolvable SDK root AND neither env var is set.
        if (sdkRoot is null && string.IsNullOrEmpty(androidHome) && string.IsNullOrEmpty(androidSdkRoot))
            return null;

        var p = new AndroidPayload
        {
            SdkRoot = sdkRoot,
            SdkRootSource = source,
            AndroidHomeEnv = NullIfEmpty(androidHome),
            AndroidSdkRootEnv = NullIfEmpty(androidSdkRoot),
        };

        if (sdkRoot is not null)
        {
            p.BuildTools = DirNames(Path.Combine(sdkRoot, "build-tools"))
                .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase).ToList();

            p.Platforms = DirNames(Path.Combine(sdkRoot, "platforms"))
                .Select(n => Regex.Match(n, @"^android-(.+)$"))
                .Where(m => m.Success)
                .Select(m => m.Groups[1].Value)
                .ToList();

            CapturePlatformTools(p, sdkRoot);
            CaptureCmdlineTools(p, sdkRoot);
            CaptureEmulator(p, sdkRoot);

            p.LicensesAccepted["android-sdk-license"] =
                File.Exists(Path.Combine(sdkRoot, "licenses", "android-sdk-license"));
            p.LicensesAccepted["android-sdk-preview-license"] =
                File.Exists(Path.Combine(sdkRoot, "licenses", "android-sdk-preview-license"));
        }

        CaptureNdk(p, sdkRoot);

        return p;
    }

    public IEnumerable<Diff> Compare(JsonElement? a, JsonElement? b, CompareContext ctx)
    {
        var pa = Json.To<AndroidPayload>(a) ?? new AndroidPayload();
        var pb = Json.To<AndroidPayload>(b) ?? new AndroidPayload();

        bool aRoot = !string.IsNullOrEmpty(pa.SdkRoot), bRoot = !string.IsNullOrEmpty(pb.SdkRoot);

        // [Critical] sdk_root present on one machine, null on the other.
        if (aRoot && !bRoot)
            yield return new Diff(Severity.Critical, "Android SDK",
                $"Android SDK present on {ctx.A} ({pa.SdkRoot}) but NOT FOUND on {ctx.B} — no Android build possible",
                "Install the SDK and set ANDROID_HOME on the machine that lacks it.", Id);
        else if (bRoot && !aRoot)
            yield return new Diff(Severity.Critical, "Android SDK",
                $"Android SDK present on {ctx.B} ({pb.SdkRoot}) but NOT FOUND on {ctx.A} — no Android build possible",
                "Install the SDK and set ANDROID_HOME on the machine that lacks it.", Id);

        // [Critical] one side has build-tools, the other has NONE.
        foreach (var d in EmptyVsPresent("Android build-tools", "build-tools",
            pa.BuildTools, pb.BuildTools, ctx, Severity.Critical,
            "AGP cannot run aapt2/d8 without build-tools — run sdkmanager \"build-tools;<ver>\".")) yield return d;
        // [High] build-tools sets differ (both non-empty).
        foreach (var d in SetsIfBothPresent("Android build-tools", pa.BuildTools, pb.BuildTools, ctx, Severity.High,
            _ => "Install matching build-tools;<ver>.")) yield return d;

        // [Critical] one side has platforms, the other has NONE.
        foreach (var d in EmptyVsPresent("Android platform", "platforms",
            pa.Platforms, pb.Platforms, ctx, Severity.Critical,
            "Compilation halts without a platform — run sdkmanager \"platforms;android-<api>\".")) yield return d;
        // [High] platforms sets differ (both non-empty).
        foreach (var d in SetsIfBothPresent("Android platform", pa.Platforms, pb.Platforms, ctx, Severity.High,
            _ => "sdkmanager \"platforms;android-<api>\".")) yield return d;

        // [High] licenses_accepted differs.
        foreach (var d in LicenseDiffs(pa.LicensesAccepted, pb.LicensesAccepted, ctx)) yield return d;

        // [High] platform_tools_present differs OR platform_tools_version differs.
        if (pa.PlatformToolsPresent != pb.PlatformToolsPresent)
        {
            var has = pa.PlatformToolsPresent ? ctx.A : ctx.B;
            var lacks = pa.PlatformToolsPresent ? ctx.B : ctx.A;
            yield return new Diff(Severity.High, "Android platform-tools",
                $"platform-tools present on {has}, MISSING on {lacks}",
                "sdkmanager \"platform-tools\".", Id);
        }
        else
        {
            foreach (var d in DiffHelp.Scalar(Id, "Android platform-tools", "platform-tools version",
                pa.PlatformToolsVersion, pb.PlatformToolsVersion, ctx, Severity.High, Severity.High,
                "sdkmanager \"platform-tools\".")) yield return d;
        }

        // [High] ndk_versions sets differ (both non-empty).
        foreach (var d in SetsIfBothPresent("Android NDK", pa.NdkVersions, pb.NdkVersions, ctx, Severity.High,
            _ => "Pin ndkVersion in build.gradle and install the exact revision.")) yield return d;

        // [High] ANDROID_HOME and ANDROID_SDK_ROOT both set but differ (on either machine).
        foreach (var d in EnvDivergence(pa, ctx.A)) yield return d;
        foreach (var d in EnvDivergence(pb, ctx.B)) yield return d;

        // [Medium] ndk_env_outside_sdk on one side only.
        if (pa.NdkEnvOutsideSdk != pb.NdkEnvOutsideSdk)
        {
            var side = pa.NdkEnvOutsideSdk ? ctx.A : ctx.B;
            yield return new Diff(Severity.Medium, "Android NDK",
                $"NDK env path points outside the SDK on {side} only",
                "Point the NDK env var inside the SDK, or unset it and rely on sdk/ndk/<ver>.", Id);
        }

        // [Medium] sdkmanager_version or cmdline_tools_channels differ.
        foreach (var d in DiffHelp.Scalar(Id, "Android cmdline-tools", "sdkmanager version",
            pa.SdkmanagerVersion, pb.SdkmanagerVersion, ctx, Severity.Medium, Severity.Medium)) yield return d;
        foreach (var d in DiffHelp.Sets(Id, "Android cmdline-tools channel",
            pa.CmdlineToolsChannels, pb.CmdlineToolsChannels, ctx, Severity.Medium)) yield return d;

        // [Medium] sdk_root_source differs.
        foreach (var d in DiffHelp.Scalar(Id, "Android SDK", "sdk_root source",
            pa.SdkRootSource, pb.SdkRootSource, ctx, Severity.Medium, Severity.Medium)) yield return d;

        // [Low] emulator_present / emulator_version differ.
        if (pa.EmulatorPresent != pb.EmulatorPresent)
        {
            var has = pa.EmulatorPresent ? ctx.A : ctx.B;
            var lacks = pa.EmulatorPresent ? ctx.B : ctx.A;
            yield return new Diff(Severity.Low, "Android emulator",
                $"emulator present on {has}, MISSING on {lacks}", null, Id);
        }
        else
        {
            foreach (var d in DiffHelp.Scalar(Id, "Android emulator", "emulator version",
                pa.EmulatorVersion, pb.EmulatorVersion, ctx, Severity.Low, Severity.Low)) yield return d;
        }

        // [Low] sdk_root path string differs.
        if (aRoot && bRoot && !string.Equals(pa.SdkRoot, pb.SdkRoot, StringComparison.Ordinal))
            yield return new Diff(Severity.Low, "Android SDK",
                $"sdk_root path differs: {ctx.A}={pa.SdkRoot}, {ctx.B}={pb.SdkRoot}", null, Id);
    }

    // ---- capture internals --------------------------------------------------

    private static (string? root, string source) ResolveSdkRoot(CaptureContext ctx, string? androidHome, string? androidSdkRoot)
    {
        // Match AGP precedence: a project's local.properties `sdk.dir` wins over the
        // environment variables (that is the SDK the build actually uses).
        var candidates = new List<(string? path, string source)>();
        if (ctx.ProjectRoot is not null)
            candidates.Add((ReadLocalPropertiesSdkDir(ctx.ProjectRoot), "local.properties"));
        candidates.Add((NullIfEmpty(androidHome), "env:ANDROID_HOME"));
        candidates.Add((NullIfEmpty(androidSdkRoot), "env:ANDROID_SDK_ROOT"));
        candidates.Add((OsDefaultSdk(), "default"));

        foreach (var (path, source) in candidates)
        {
            if (path is null) continue;
            try { if (Directory.Exists(path)) return (path, source); }
            catch { }
        }
        return (null, "none");
    }

    private static string? ReadLocalPropertiesSdkDir(string projectRoot)
    {
        try
        {
            var file = Path.Combine(projectRoot, "local.properties");
            if (!File.Exists(file)) return null;
            foreach (var raw in File.ReadAllLines(file))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('!')) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim();
                if (!string.Equals(key, "sdk.dir", StringComparison.Ordinal)) continue;
                var value = line[(eq + 1)..].Trim();
                // java-properties escaping: '\:' -> ':', '\\' -> '\'.
                value = value.Replace("\\:", ":").Replace("\\\\", "\\");
                return NullIfEmpty(value);
            }
        }
        catch { }
        return null;
    }

    private static string? OsDefaultSdk()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return string.IsNullOrEmpty(local) ? null : Path.Combine(local, "Android", "Sdk");
            }
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) return null;
            return OperatingSystem.IsMacOS()
                ? Path.Combine(home, "Library", "Android", "sdk")
                : Path.Combine(home, "Android", "Sdk");
        }
        catch { return null; }
    }

    private static void CapturePlatformTools(AndroidPayload p, string sdkRoot)
    {
        var dir = Path.Combine(sdkRoot, "platform-tools");
        p.PlatformToolsPresent = SafeDirExists(dir);
        if (!p.PlatformToolsPresent) return;

        var adb = Path.Combine(dir, OperatingSystem.IsWindows() ? "adb.exe" : "adb");
        if (!SafeFileExists(adb)) { p.PlatformToolsVersion = "1.0.41"; return; }

        var r = Proc.Run(adb, "version", timeoutMs: 10_000);
        if (r is not null)
        {
            var m = Regex.Match(r.Combined, @"Version\s+([0-9][0-9.]+(?:-[0-9]+)?)");
            p.PlatformToolsVersion = m.Success ? m.Groups[1].Value : "1.0.41";
        }
        else
        {
            p.PlatformToolsVersion = "1.0.41";
        }
    }

    private static void CaptureCmdlineTools(AndroidPayload p, string sdkRoot)
    {
        var root = Path.Combine(sdkRoot, "cmdline-tools");
        p.CmdlineToolsChannels = DirNames(root);
        if (p.CmdlineToolsChannels.Count == 0) return;

        // Prefer the "latest" channel, otherwise the highest-named channel.
        var channel = p.CmdlineToolsChannels.FirstOrDefault(c => string.Equals(c, "latest", StringComparison.OrdinalIgnoreCase))
            ?? p.CmdlineToolsChannels.OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase).First();

        var sdkmanager = Path.Combine(root, channel, "bin",
            OperatingSystem.IsWindows() ? "sdkmanager.bat" : "sdkmanager");
        if (!SafeFileExists(sdkmanager)) return;

        var r = Proc.Run(sdkmanager, "--version", timeoutMs: 15_000);
        if (r is null) return;
        foreach (var line in r.Combined.Split('\n').Select(l => l.Trim()))
            if (Regex.IsMatch(line, @"^[0-9][0-9.]+$"))
            {
                p.SdkmanagerVersion = line;
                break;
            }
    }

    private static void CaptureNdk(AndroidPayload p, string? sdkRoot)
    {
        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (sdkRoot is not null)
        {
            // subdir names of <sdk>/ndk
            foreach (var name in DirNames(Path.Combine(sdkRoot, "ndk")))
                versions.Add(name);

            // <sdk>/ndk-bundle/source.properties Pkg.Revision
            var bundleRev = ReadPkgRevision(Path.Combine(sdkRoot, "ndk-bundle", "source.properties"));
            if (bundleRev is not null) versions.Add(bundleRev);
        }

        foreach (var envName in new[] { "ANDROID_NDK_HOME", "ANDROID_NDK_ROOT", "NDK_HOME", "NDK_ROOT" })
        {
            var envPath = Environment.GetEnvironmentVariable(envName);
            if (string.IsNullOrEmpty(envPath)) continue;
            var rev = ReadPkgRevision(Path.Combine(envPath, "source.properties"));
            if (rev is not null) versions.Add(rev);
            if (sdkRoot is not null && !IsInside(envPath, sdkRoot)) p.NdkEnvOutsideSdk = true;
        }

        p.NdkVersions = versions.OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void CaptureEmulator(AndroidPayload p, string sdkRoot)
    {
        var dir = Path.Combine(sdkRoot, "emulator");
        p.EmulatorPresent = SafeDirExists(dir);
        if (!p.EmulatorPresent) return;

        var emu = Path.Combine(dir, OperatingSystem.IsWindows() ? "emulator.exe" : "emulator");
        if (!SafeFileExists(emu)) return;

        // Tolerate hang/fail — Proc.Run returns null on timeout.
        var r = Proc.Run(emu, "-version", timeoutMs: 10_000);
        if (r is null) return;
        var m = Regex.Match(r.Combined, @"Android emulator version ([0-9][0-9.]+)");
        if (m.Success) p.EmulatorVersion = m.Groups[1].Value;
    }

    private static string? ReadPkgRevision(string sourceProperties)
    {
        try
        {
            if (!File.Exists(sourceProperties)) return null;
            var text = File.ReadAllText(sourceProperties);
            var m = Regex.Match(text, @"Pkg\.Revision\s*=\s*([0-9][0-9.]+)");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch { return null; }
    }

    private static bool IsInside(string child, string parent)
    {
        try
        {
            var c = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var pRoot = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return c.Equals(pRoot, Os.PathComparer == StringComparer.Ordinal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
                || c.StartsWith(pRoot + Path.DirectorySeparatorChar,
                    Os.PathComparer == StringComparer.Ordinal ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static List<string> DirNames(string dir)
    {
        var list = new List<string>();
        try
        {
            if (!Directory.Exists(dir)) return list;
            foreach (var d in Directory.GetDirectories(dir))
            {
                var name = Path.GetFileName(d);
                if (!string.IsNullOrEmpty(name)) list.Add(name);
            }
        }
        catch { }
        return list;
    }

    private static bool SafeDirExists(string path)
    {
        try { return Directory.Exists(path); } catch { return false; }
    }

    private static bool SafeFileExists(string path)
    {
        try { return File.Exists(path); } catch { return false; }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // ---- compare internals --------------------------------------------------

    /// <summary>One side has items, the other has NONE — a build-stopping absence.</summary>
    private IEnumerable<Diff> EmptyVsPresent(string category, string subject,
        List<string> a, List<string> b, CompareContext ctx, Severity sev, string? hint)
    {
        if (a.Count > 0 && b.Count == 0)
            yield return new Diff(sev, category, $"{subject} present on {ctx.A} but NONE on {ctx.B}", hint, Id);
        else if (b.Count > 0 && a.Count == 0)
            yield return new Diff(sev, category, $"{subject} present on {ctx.B} but NONE on {ctx.A}", hint, Id);
    }

    /// <summary>Set differences, but only when BOTH sides are non-empty (empty-vs-present handled separately).</summary>
    private IEnumerable<Diff> SetsIfBothPresent(string category,
        List<string> a, List<string> b, CompareContext ctx, Severity sev, Func<string, string?> hint)
    {
        if (a.Count == 0 || b.Count == 0) yield break;
        foreach (var d in DiffHelp.Sets(Id, category, a, b, ctx, sev, hint)) yield return d;
    }

    private IEnumerable<Diff> LicenseDiffs(Dictionary<string, bool> a, Dictionary<string, bool> b, CompareContext ctx)
    {
        foreach (var key in a.Keys.Union(b.Keys, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal))
        {
            bool av = a.TryGetValue(key, out var avv) && avv;
            bool bv = b.TryGetValue(key, out var bvv) && bvv;
            if (av == bv) continue;
            var accepted = av ? ctx.A : ctx.B;
            var notAccepted = av ? ctx.B : ctx.A;
            yield return new Diff(Severity.High, "Android SDK license",
                $"{key} accepted on {accepted} but NOT on {notAccepted} — blocks auto-download",
                "Run: yes | sdkmanager --licenses", Id);
        }
    }

    private IEnumerable<Diff> EnvDivergence(AndroidPayload p, string machine)
    {
        if (!string.IsNullOrEmpty(p.AndroidHomeEnv) && !string.IsNullOrEmpty(p.AndroidSdkRootEnv)
            && !string.Equals(p.AndroidHomeEnv, p.AndroidSdkRootEnv, StringComparison.Ordinal))
            yield return new Diff(Severity.High, "Android SDK",
                $"ANDROID_HOME and ANDROID_SDK_ROOT differ on {machine}: {p.AndroidHomeEnv} vs {p.AndroidSdkRootEnv}",
                "Set ANDROID_HOME and ANDROID_SDK_ROOT to the same path.", Id);
    }
}
