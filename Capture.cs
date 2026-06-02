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
        s.Env = CollectEnv();
        s.NuGet = CollectNuGet();
        s.ResolvedTools = CollectResolvedTools();
        s.Python = CollectPython();
        s.Swig = CollectSwig();
        s.NativeDeps = CollectNativeDeps();
        return s;
    }

    private static readonly HashSet<string> BuildRelevantEnvKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "INCLUDE", "LIB", "LIBPATH",
        "VCINSTALLDIR", "VCToolsInstallDir", "VCToolsVersion", "VCToolsRedistDir",
        "VSINSTALLDIR", "VS170COMNTOOLS", "VS180COMNTOOLS", "DevEnvDir",
        "WindowsSdkDir", "WindowsSDKVersion", "WindowsSdkBinPath", "WindowsLibPath",
        "UCRTVersion", "UniversalCRTSdkDir",
        "DOTNET_ROOT", "DOTNET_CLI_TELEMETRY_OPTOUT", "DOTNET_MULTILEVEL_LOOKUP",
        "MSBuildSDKsPath", "MSBuildExtensionsPath",
        "NUGET_PACKAGES", "NUGET_HTTP_CACHE_PATH", "NUGET_FALLBACK_PACKAGES",
        "PYTHONPATH", "PYTHONHOME",
        "PROCESSOR_ARCHITECTURE",
    };

    private static readonly string[] SecretMarkers =
    {
        "TOKEN", "SECRET", "PASSWORD", "PASSWD", "APIKEY", "API_KEY", "KEY",
        "PAT", "CREDENTIAL", "AUTH", "BEARER",
    };

    private static EnvInfo CollectEnv()
    {
        var env = new EnvInfo();

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        env.Path = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().TrimEnd('\\', '/'))
            .Where(p => p.Length > 0)
            .ToList();

        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            var key = e.Key?.ToString();
            var val = e.Value?.ToString();
            if (string.IsNullOrEmpty(key)) continue;
            if (key.Equals("PATH", StringComparison.OrdinalIgnoreCase)) continue;

            var safeVal = LooksSecret(key) ? "<redacted>" : val;
            if (IsBuildRelevant(key))
                env.BuildRelevant[key] = safeVal;
            else
                env.Other[key] = safeVal;
        }
        return env;
    }

    private static bool IsBuildRelevant(string key)
    {
        if (BuildRelevantEnvKeys.Contains(key)) return true;
        if (key.EndsWith("_HOME", StringComparison.OrdinalIgnoreCase)) return true;
        if (key.EndsWith("_ROOT", StringComparison.OrdinalIgnoreCase)) return true;
        if (key.StartsWith("VCPKG", StringComparison.OrdinalIgnoreCase)) return true;
        if (key.StartsWith("CMAKE_", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool LooksSecret(string key)
    {
        var upper = key.ToUpperInvariant();
        return SecretMarkers.Any(m => upper.Contains(m));
    }

    private static NuGetInfo CollectNuGet()
    {
        var info = new NuGetInfo();
        var dotnet = Proc.Which("dotnet.exe") ?? Proc.Which("dotnet");
        if (dotnet is null) return info;

        var listed = Proc.Run(dotnet, "nuget list source --format short", timeoutMs: 15_000);
        if (listed is not null && listed.ExitCode == 0)
        {
            foreach (var line in listed.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length < 3) continue;
                var enabled = trimmed[0] != 'D';
                var rest = trimmed.Substring(1).Trim();
                info.Sources.Add(new NuGetSource { Name = "", Url = rest, Enabled = enabled });
            }
        }

        var detailed = Proc.Run(dotnet, "nuget list source", timeoutMs: 15_000);
        if (detailed is not null && detailed.ExitCode == 0)
        {
            string? curName = null;
            int idx = 0;
            foreach (var rawLine in detailed.Stdout.Split('\n'))
            {
                var line = rawLine.TrimEnd();
                var match = System.Text.RegularExpressions.Regex.Match(line,
                    @"^\s*(\d+)\.\s+(?<name>.+?)\s+\[(?<state>Enabled|Disabled)\]");
                if (match.Success)
                {
                    curName = match.Groups["name"].Value.Trim();
                    continue;
                }
                if (curName is not null && line.Trim().Length > 0)
                {
                    var url = line.Trim();
                    if (idx < info.Sources.Count) info.Sources[idx].Name = curName;
                    else info.Sources.Add(new NuGetSource { Name = curName, Url = url });
                    idx++;
                    curName = null;
                }
            }
        }

        var locals = Proc.Run(dotnet, "nuget locals global-packages --list", timeoutMs: 10_000);
        if (locals is not null && locals.ExitCode == 0)
        {
            var line = locals.Stdout.Split('\n').FirstOrDefault(l => l.Contains("global-packages", StringComparison.OrdinalIgnoreCase));
            if (line is not null)
            {
                var colon = line.IndexOf(':');
                if (colon >= 0) info.GlobalPackages = line.Substring(colon + 1).Trim();
            }
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "NuGet", "NuGet.Config"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "NuGet", "NuGet.Config"),
            Path.Combine(Environment.CurrentDirectory, "NuGet.config"),
            Path.Combine(Environment.CurrentDirectory, "nuget.config"),
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) info.Configs.Add(c);
        }
        info.Configs = info.Configs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return info;
    }

    private static readonly (string Name, string VersionArgs)[] InterestingTools =
    {
        ("cl.exe",      ""),
        ("link.exe",    ""),
        ("cmake.exe",   "--version"),
        ("ninja.exe",   "--version"),
        ("git.exe",     "--version"),
        ("python.exe",  "--version"),
        ("python3.exe", "--version"),
        ("node.exe",    "--version"),
        ("npm.cmd",     "--version"),
        ("dotnet.exe",  "--version"),
        ("nuget.exe",   ""),
        ("swig.exe",    "-version"),
    };

    private static Dictionary<string, ResolvedTool?> CollectResolvedTools()
    {
        var result = new Dictionary<string, ResolvedTool?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, args) in InterestingTools)
        {
            var path = Proc.Which(name);
            if (path is null)
            {
                result[name] = null;
                continue;
            }
            string? version = null;
            if (args.Length > 0)
            {
                var r = Proc.Run(path, args, timeoutMs: 10_000);
                if (r is not null)
                {
                    var raw = (r.Stdout + r.Stderr).Trim();
                    var firstLine = raw.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
                    version = firstLine;
                }
            }
            else
            {
                var r = Proc.Run(path, "", timeoutMs: 5_000);
                if (r is not null)
                {
                    var raw = (r.Stdout + r.Stderr).Trim();
                    var firstLine = raw.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
                    version = firstLine;
                }
            }
            result[name] = new ResolvedTool { Path = path, Version = version };
        }
        return result;
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

    // ---- Tier 3 -------------------------------------------------------------

    private const int MaxPyPackages = 200;

    private static List<PythonEnv> CollectPython()
    {
        var result = new List<PythonEnv>();
        // Distinct python interpreters reachable on PATH (where lists all matches).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "python.exe", "python3.exe" })
        {
            var listing = Proc.Run("where.exe", name, timeoutMs: 5_000);
            if (listing is null || listing.ExitCode != 0) continue;
            foreach (var raw in listing.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var path = raw.Trim();
                if (path.Length == 0) continue;
                // Skip the WindowsApps execution-alias stubs; they aren't real interpreters.
                if (path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(path)) continue;
                var env = ProbePython(path);
                if (env is not null) result.Add(env);
            }
        }
        return result;
    }

    private static PythonEnv? ProbePython(string path)
    {
        // One round-trip: print a JSON blob describing the interpreter.
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
            env.Version = root.TryGetProperty("version", out var v) ? v.GetString() : null;
            env.Prefix = root.TryGetProperty("prefix", out var p) ? p.GetString() : null;
            env.BasePrefix = root.TryGetProperty("base_prefix", out var bp) ? bp.GetString() : null;
            env.Architecture = root.TryGetProperty("arch", out var a) ? a.GetString() : null;
            env.InVirtualEnv = !string.Equals(env.Prefix, env.BasePrefix, StringComparison.OrdinalIgnoreCase);
        }
        catch { }

        env.Packages = ProbePyPackages(path, out var truncated);
        env.PackagesTruncated = truncated;
        return env;
    }

    private static List<PyPackage> ProbePyPackages(string path, out bool truncated)
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
                var name = el.TryGetProperty("name", out var n) ? n.GetString() : null;
                var ver = el.TryGetProperty("version", out var v) ? v.GetString() : null;
                if (!string.IsNullOrEmpty(name))
                    pkgs.Add(new PyPackage { Name = name!, Version = ver ?? "" });
            }
        }
        catch { }
        return pkgs.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static SwigInfo? CollectSwig()
    {
        var path = Proc.Which("swig.exe") ?? Proc.Which("swig");
        if (path is null) return new SwigInfo { Path = null, Version = null, OnPath = false };

        string? version = null;
        var r = Proc.Run(path, "-version", timeoutMs: 10_000);
        if (r is not null)
        {
            var raw = (r.Stdout + r.Stderr);
            var line = raw.Split('\n').Select(l => l.Trim())
                .FirstOrDefault(l => l.StartsWith("SWIG Version", StringComparison.OrdinalIgnoreCase));
            version = line?.Replace("SWIG Version", "", StringComparison.OrdinalIgnoreCase).Trim();
        }
        return new SwigInfo { Path = path, Version = version, OnPath = true };
    }

    // Native DLLs the build silently assumes are present, in well-known locations.
    private static List<NativeDep> CollectNativeDeps()
    {
        var result = new List<NativeDep>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        // VC++ runtime + common native bits whose absence breaks load-time linking.
        string[] knownDlls =
        {
            "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll",
            "concrt140.dll", "msvcp140_1.dll", "msvcp140_2.dll",
            "ucrtbase.dll", "vcomp140.dll",
        };
        foreach (var dll in knownDlls)
        {
            var full = Path.Combine(system32, dll);
            if (File.Exists(full) && seen.Add(full))
                result.Add(DescribeNative(dll, full));
        }
        return result;
    }

    private static NativeDep DescribeNative(string name, string full)
    {
        string? fileVersion = null;
        string? arch = null;
        try
        {
            var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(full);
            fileVersion = fvi.FileVersion;
        }
        catch { }
        try
        {
            arch = ReadPeArchitecture(full);
        }
        catch { }
        return new NativeDep { Name = name, Path = full, FileVersion = fileVersion, Architecture = arch };
    }

    // Minimal PE header read for machine type — no dependencies.
    private static string? ReadPeArchitecture(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        if (br.ReadUInt16() != 0x5A4D) return null;       // 'MZ'
        fs.Position = 0x3C;
        var peOffset = br.ReadInt32();
        fs.Position = peOffset;
        if (br.ReadUInt32() != 0x00004550) return null;   // 'PE\0\0'
        var machine = br.ReadUInt16();
        return machine switch
        {
            0x8664 => "x64",
            0x014c => "x86",
            0xAA64 => "arm64",
            0x01c0 or 0x01c4 => "arm",
            _ => $"0x{machine:X4}",
        };
    }
}
