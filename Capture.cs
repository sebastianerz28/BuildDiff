using System.Runtime.InteropServices;
using System.Text.Json;

namespace BuildDiff;

public static class Capture
{
    public static Snapshot Collect()
    {
        var s = new Snapshot
        {
            Machine = Environment.MachineName,
            CapturedAt = DateTime.UtcNow.ToString("O"),
            Os = new OsInfo
            {
                Version = RuntimeInformation.OSDescription,
                Arch = RuntimeInformation.OSArchitecture.ToString(),
            },
            VisualStudio = CollectVisualStudio(),
            Dotnet = CollectDotnet(),
        };

        s.MsBuild = CollectMsBuild(s.VisualStudio);
        s.Toolsets = DeriveToolsets(s.VisualStudio);
        s.WindowsSdks = DeriveWindowsSdks(s.VisualStudio);
        return s;
    }

    private static MsBuildInfo? CollectMsBuild(List<VisualStudioInstall> vs)
    {
        var path = Proc.Which("msbuild.exe") ?? Proc.Which("msbuild") ?? FindMsBuildInVs(vs);
        string? version = null;
        if (path is not null)
        {
            var r = Proc.Run(path, "-version -nologo", timeoutMs: 15_000);
            if (r is not null && r.ExitCode == 0)
            {
                version = r.Stdout
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .LastOrDefault(l => l.Length > 0 && char.IsDigit(l[0]));
            }
        }
        if (path is null && version is null) return null;
        return new MsBuildInfo { Path = path, Version = version };
    }

    private static string? FindMsBuildInVs(List<VisualStudioInstall> vs)
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

    private static List<VisualStudioInstall> CollectVisualStudio()
    {
        var result = new List<VisualStudioInstall>();
        var vswhere = FindVswhere();
        if (vswhere is null) return result;

        var r = Proc.Run(vswhere, "-all -prerelease -products * -format json -utf8", timeoutMs: 30_000);
        if (r is null || r.ExitCode != 0 || string.IsNullOrWhiteSpace(r.Stdout)) return result;

        try
        {
            using var doc = JsonDocument.Parse(r.Stdout);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var inst = new VisualStudioInstall
                {
                    DisplayName = el.TryGetProperty("displayName", out var dn) ? dn.GetString() : null,
                    InstallationVersion = el.TryGetProperty("installationVersion", out var iv) ? iv.GetString() : null,
                    InstallationPath = el.TryGetProperty("installationPath", out var ip) ? ip.GetString() : null,
                    ProductId = el.TryGetProperty("productId", out var pid) ? pid.GetString() : null,
                    ChannelId = el.TryGetProperty("channelId", out var cid) ? cid.GetString() : null,
                };

                if (inst.InstallationPath is not null)
                {
                    var compRes = Proc.Run(vswhere,
                        $"-path \"{inst.InstallationPath}\" -include packages -format json -utf8",
                        timeoutMs: 30_000);
                    if (compRes is not null && compRes.ExitCode == 0 && !string.IsNullOrWhiteSpace(compRes.Stdout))
                    {
                        try
                        {
                            using var compDoc = JsonDocument.Parse(compRes.Stdout);
                            foreach (var instEl in compDoc.RootElement.EnumerateArray())
                            {
                                if (!instEl.TryGetProperty("packages", out var pkgs)) continue;
                                foreach (var pkg in pkgs.EnumerateArray())
                                {
                                    if (pkg.TryGetProperty("id", out var idEl))
                                    {
                                        var id = idEl.GetString();
                                        if (!string.IsNullOrWhiteSpace(id)) inst.Components.Add(id!);
                                    }
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

    private static List<string> DeriveToolsets(List<VisualStudioInstall> vs)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in vs)
        {
            foreach (var c in v.Components)
            {
                if (c.StartsWith("Microsoft.VisualStudio.Component.VC.", StringComparison.OrdinalIgnoreCase))
                    set.Add(c);
                if (c.StartsWith("Microsoft.VisualStudio.Component.VC.Tools.", StringComparison.OrdinalIgnoreCase))
                    set.Add(c);
            }
            if (v.InstallationPath is not null)
            {
                var msvcDir = Path.Combine(v.InstallationPath, "VC", "Tools", "MSVC");
                if (Directory.Exists(msvcDir))
                {
                    foreach (var d in Directory.GetDirectories(msvcDir))
                        set.Add("MSVC " + Path.GetFileName(d));
                }
            }
        }
        return set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> DeriveWindowsSdks(List<VisualStudioInstall> vs)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in vs)
        {
            foreach (var c in v.Components)
            {
                if (c.StartsWith("Microsoft.VisualStudio.Component.Windows", StringComparison.OrdinalIgnoreCase)
                    && c.Contains("SDK", StringComparison.OrdinalIgnoreCase))
                    set.Add(c);
            }
        }

        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var sdkRoot = Path.Combine(pf86, "Windows Kits", "10", "Include");
        if (Directory.Exists(sdkRoot))
        {
            foreach (var d in Directory.GetDirectories(sdkRoot))
                set.Add("SDK " + Path.GetFileName(d));
        }
        return set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static DotnetInfo CollectDotnet()
    {
        var info = new DotnetInfo();
        var dotnet = Proc.Which("dotnet.exe") ?? Proc.Which("dotnet");
        if (dotnet is null) return info;

        var sdks = Proc.Run(dotnet, "--list-sdks", timeoutMs: 15_000);
        if (sdks is not null && sdks.ExitCode == 0)
        {
            info.Sdks = sdks.Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();
        }

        var runtimes = Proc.Run(dotnet, "--list-runtimes", timeoutMs: 15_000);
        if (runtimes is not null && runtimes.ExitCode == 0)
        {
            info.Runtimes = runtimes.Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToList();
        }

        return info;
    }
}
