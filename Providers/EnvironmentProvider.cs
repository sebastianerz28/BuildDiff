using System.Text.Json;

namespace BuildDiff;

public sealed class EnvPayload
{
    public List<string> Path { get; set; } = new();
    public Dictionary<string, string?> BuildRelevant { get; set; } = new();
    public Dictionary<string, string?> Other { get; set; } = new();
}

/// <summary>PATH entries + build-relevant environment variables. Cross-platform.</summary>
public sealed class EnvironmentProvider : IEnvironmentProvider
{
    public string Id => "environment";
    public string DisplayName => "PATH & build-relevant environment variables";
    public bool AppliesTo(OsPlatform os) => true;

    private static readonly HashSet<string> BuildRelevantKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "INCLUDE", "LIB", "LIBPATH",
        "VCINSTALLDIR", "VCToolsInstallDir", "VCToolsVersion", "VCToolsRedistDir",
        "VSINSTALLDIR", "VS170COMNTOOLS", "VS180COMNTOOLS", "DevEnvDir",
        "WindowsSdkDir", "WindowsSDKVersion", "WindowsSdkBinPath", "WindowsLibPath",
        "UCRTVersion", "UniversalCRTSdkDir",
        "DOTNET_ROOT", "DOTNET_CLI_TELEMETRY_OPTOUT", "DOTNET_MULTILEVEL_LOOKUP",
        "MSBuildSDKsPath", "MSBuildExtensionsPath",
        "NUGET_PACKAGES", "NUGET_HTTP_CACHE_PATH", "NUGET_FALLBACK_PACKAGES",
        "PYTHONPATH", "PYTHONHOME", "PROCESSOR_ARCHITECTURE",
        // cross-platform build vars
        "CC", "CXX", "LD", "LDFLAGS", "CFLAGS", "CXXFLAGS", "CPPFLAGS",
        "PKG_CONFIG_PATH", "LD_LIBRARY_PATH", "DYLD_LIBRARY_PATH", "DYLD_FRAMEWORK_PATH",
        "JAVA_HOME", "GRADLE_HOME", "MAVEN_HOME", "M2_HOME", "GOROOT", "GOPATH", "GOTOOLCHAIN",
        "CARGO_HOME", "RUSTUP_HOME", "NODE_OPTIONS", "NPM_CONFIG_PREFIX", "VOLTA_HOME",
        "NVM_DIR", "FNM_DIR", "SDKMAN_DIR", "ANDROID_HOME", "ANDROID_SDK_ROOT",
        "DEVELOPER_DIR", "MACOSX_DEPLOYMENT_TARGET",
    };

    private static readonly string[] SecretMarkers =
    {
        "TOKEN", "SECRET", "PASSWORD", "PASSWD", "APIKEY", "API_KEY", "KEY",
        "PAT", "CREDENTIAL", "AUTH", "BEARER",
    };

    public object? Capture(CaptureContext ctx)
    {
        var env = new EnvPayload();

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        env.Path = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().TrimEnd('\\', '/'))
            .Where(p => p.Length > 0)
            .ToList();

        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            var key = e.Key?.ToString();
            if (string.IsNullOrEmpty(key)) continue;
            if (key.Equals("PATH", StringComparison.OrdinalIgnoreCase)) continue;
            var val = e.Value?.ToString();
            var safeVal = LooksSecret(key) ? "<redacted>" : val;
            if (IsBuildRelevant(key)) env.BuildRelevant[key] = safeVal;
            else env.Other[key] = safeVal;
        }
        return env;
    }

    private static bool IsBuildRelevant(string key)
    {
        if (BuildRelevantKeys.Contains(key)) return true;
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

    public IEnumerable<Diff> Compare(JsonElement? a, JsonElement? b, CompareContext ctx)
    {
        var pa = Json.To<EnvPayload>(a) ?? new EnvPayload();
        var pb = Json.To<EnvPayload>(b) ?? new EnvPayload();

        foreach (var d in DiffHelp.Dict(Id, "Build env var", pa.BuildRelevant, pb.BuildRelevant, ctx, Severity.High))
            yield return d;
        foreach (var d in DiffHelp.Presence(Id, "Env var", pa.Other.Keys, pb.Other.Keys, ctx, Severity.Low))
            yield return d;

        // PATH entries — flag entries unique to one side (order is noisy).
        var aSet = new HashSet<string>(pa.Path, StringComparer.OrdinalIgnoreCase);
        var bSet = new HashSet<string>(pb.Path, StringComparer.OrdinalIgnoreCase);
        var aOnly = aSet.Except(bSet, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        var bOnly = bSet.Except(aSet, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        if (aOnly.Count > 0)
            yield return new Diff(Severity.Medium, "PATH",
                $"{aOnly.Count} entries on {ctx.A} only: {string.Join("; ", aOnly.Take(3))}{(aOnly.Count > 3 ? " ..." : "")}", null, Id);
        if (bOnly.Count > 0)
            yield return new Diff(Severity.Medium, "PATH",
                $"{bOnly.Count} entries on {ctx.B} only: {string.Join("; ", bOnly.Take(3))}{(bOnly.Count > 3 ? " ..." : "")}", null, Id);
    }
}
