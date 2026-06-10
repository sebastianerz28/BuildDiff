using System.Text.Json;

namespace BuildDiff;

public sealed class VsPayload
{
    public List<VsInstall> Installs { get; set; } = new();
    public List<string> Toolsets { get; set; } = new();
    public List<string> WindowsSdks { get; set; } = new();
    public MsBuildInfo? MsBuild { get; set; }
}

public sealed class VsInstall
{
    public string? DisplayName { get; set; }
    public string? InstallationVersion { get; set; }
    public string? InstallationPath { get; set; }
    public string? ProductId { get; set; }
    public string? ChannelId { get; set; }
    public List<string> Components { get; set; } = new();
}

public sealed class MsBuildInfo
{
    public string? Version { get; set; }
    public string? Path { get; set; }
}

/// <summary>
/// Visual Studio installs, MSVC toolsets, Windows SDKs and MSBuild — the heart of
/// the original Windows/C++/C# stack. Windows-only (gated via AppliesTo).
/// </summary>
public sealed class VisualStudioProvider : IEnvironmentProvider
{
    public string Id => "visual-studio";
    public string DisplayName => "Visual Studio, MSVC toolsets, Windows SDK, MSBuild";
    public bool AppliesTo(OsPlatform os) => os == OsPlatform.Windows;

    public object? Capture(CaptureContext ctx)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var installs = CollectVisualStudio();
        var payload = new VsPayload
        {
            Installs = installs,
            Toolsets = DeriveToolsets(installs),
            WindowsSdks = DeriveWindowsSdks(installs),
            MsBuild = CollectMsBuild(installs),
        };
        if (payload.Installs.Count == 0 && payload.MsBuild is null &&
            payload.Toolsets.Count == 0 && payload.WindowsSdks.Count == 0)
            return null;
        return payload;
    }

    public IEnumerable<Diff> Compare(JsonElement? a, JsonElement? b, CompareContext ctx)
    {
        var pa = Json.To<VsPayload>(a) ?? new VsPayload();
        var pb = Json.To<VsPayload>(b) ?? new VsPayload();

        foreach (var d in DiffHelp.Scalar(Id, "MSBuild", "MSBuild",
            pa.MsBuild?.Version, pb.MsBuild?.Version, ctx, Severity.Critical, Severity.High))
            yield return d;

        foreach (var d in DiffHelp.Sets(Id, "MSVC toolset", pa.Toolsets, pb.Toolsets, ctx, Severity.Critical,
            id => id.StartsWith("Microsoft.VisualStudio.Component.VC", StringComparison.OrdinalIgnoreCase)
                ? $"Install via Visual Studio Installer: {id}" : null))
            yield return d;

        foreach (var d in DiffHelp.Sets(Id, "Windows SDK", pa.WindowsSdks, pb.WindowsSdks, ctx, Severity.Critical))
            yield return d;

        // VS components (excluding toolsets/SDKs, which are escalated above) — MEDIUM.
        var aComps = pa.Installs.SelectMany(v => v.Components)
            .Where(NotEscalated).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bComps = pb.Installs.SelectMany(v => v.Components)
            .Where(NotEscalated).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var d in DiffHelp.Sets(Id, "VS component", aComps, bComps, ctx, Severity.Medium))
            yield return d;
    }

    private static bool NotEscalated(string c)
        => !c.StartsWith("Microsoft.VisualStudio.Component.VC", StringComparison.OrdinalIgnoreCase)
           && !(c.Contains("Windows", StringComparison.OrdinalIgnoreCase) && c.Contains("SDK", StringComparison.OrdinalIgnoreCase));

    // ---- capture internals (ported from v1) ---------------------------------

    private static List<VsInstall> CollectVisualStudio()
    {
        var result = new List<VsInstall>();
        var vswhere = FindVswhere();
        if (vswhere is null) return result;

        var r = Proc.Run(vswhere, "-all -prerelease -products * -format json -utf8", timeoutMs: 30_000);
        if (r is null || r.ExitCode != 0 || string.IsNullOrWhiteSpace(r.Stdout)) return result;

        try
        {
            using var doc = JsonDocument.Parse(r.Stdout);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var inst = new VsInstall
                {
                    DisplayName = GetStr(el, "displayName"),
                    InstallationVersion = GetStr(el, "installationVersion"),
                    InstallationPath = GetStr(el, "installationPath"),
                    ProductId = GetStr(el, "productId"),
                    ChannelId = GetStr(el, "channelId"),
                };

                if (inst.InstallationPath is not null)
                {
                    var compRes = Proc.Run(vswhere,
                        $"-path \"{inst.InstallationPath}\" -include packages -format json -utf8", timeoutMs: 30_000);
                    if (compRes is not null && compRes.ExitCode == 0 && !string.IsNullOrWhiteSpace(compRes.Stdout))
                    {
                        try
                        {
                            using var compDoc = JsonDocument.Parse(compRes.Stdout);
                            foreach (var instEl in compDoc.RootElement.EnumerateArray())
                            {
                                if (!instEl.TryGetProperty("packages", out var pkgs)) continue;
                                foreach (var pkg in pkgs.EnumerateArray())
                                    if (pkg.TryGetProperty("id", out var idEl))
                                    {
                                        var id = idEl.GetString();
                                        if (!string.IsNullOrWhiteSpace(id)) inst.Components.Add(id!);
                                    }
                            }
                        }
                        catch { }
                    }
                }
                result.Add(inst);
            }
        }
        catch { }
        return result;
    }

    private static string? FindVswhere()
    {
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var candidate = Path.Combine(pf86, "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (File.Exists(candidate)) return candidate;
        return Proc.Which("vswhere.exe");
    }

    private static List<string> DeriveToolsets(List<VsInstall> vs)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in vs)
        {
            foreach (var c in v.Components)
                if (c.StartsWith("Microsoft.VisualStudio.Component.VC.", StringComparison.OrdinalIgnoreCase))
                    set.Add(c);
            if (v.InstallationPath is not null)
            {
                var msvcDir = Path.Combine(v.InstallationPath, "VC", "Tools", "MSVC");
                if (Directory.Exists(msvcDir))
                    foreach (var d in Directory.GetDirectories(msvcDir))
                        set.Add("MSVC " + Path.GetFileName(d));
            }
        }
        return set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> DeriveWindowsSdks(List<VsInstall> vs)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in vs)
            foreach (var c in v.Components)
                if (c.StartsWith("Microsoft.VisualStudio.Component.Windows", StringComparison.OrdinalIgnoreCase)
                    && c.Contains("SDK", StringComparison.OrdinalIgnoreCase))
                    set.Add(c);

        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var sdkRoot = Path.Combine(pf86, "Windows Kits", "10", "Include");
        if (Directory.Exists(sdkRoot))
            foreach (var d in Directory.GetDirectories(sdkRoot))
                set.Add("SDK " + Path.GetFileName(d));

        return set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static MsBuildInfo? CollectMsBuild(List<VsInstall> vs)
    {
        var path = Proc.Which("msbuild.exe") ?? Proc.Which("msbuild") ?? FindMsBuildInVs(vs);
        string? version = null;
        if (path is not null)
        {
            var r = Proc.Run(path, "-version -nologo", timeoutMs: 15_000);
            if (r is not null && r.ExitCode == 0)
                version = r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .LastOrDefault(l => l.Length > 0 && char.IsDigit(l[0]));
        }
        if (path is null && version is null) return null;
        return new MsBuildInfo { Path = path, Version = version };
    }

    private static string? FindMsBuildInVs(List<VsInstall> vs)
    {
        foreach (var v in vs)
        {
            if (v.InstallationPath is null) continue;
            var msbuildDir = Path.Combine(v.InstallationPath, "MSBuild");
            if (!Directory.Exists(msbuildDir)) continue;
            try
            {
                var found = Directory.EnumerateFiles(msbuildDir, "MSBuild.exe", SearchOption.AllDirectories)
                    .Where(p => p.Contains(Path.DirectorySeparatorChar + "Bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(p => p.Contains("amd64", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();
                if (found is not null) return found;
            }
            catch { }
        }
        return null;
    }

    private static string? GetStr(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) ? v.GetString() : null;
}
