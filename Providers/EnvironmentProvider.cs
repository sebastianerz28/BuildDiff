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
        "CONNECTIONSTRING", "CONN_STR", "DSN", "WEBHOOK", "SIGNING", "CERT",
        "KEYSTORE", "KEYCHAIN", "PRIVATE", "ACCESS", "SAS", "COOKIE", "SESSION",
        "OTP", "PASSPHRASE",
    };

    // Vars whose VALUE meaningfully changes how a build compiles/links — worth a HIGH
    // diff. Everything else build-relevant is mostly an install path (expected to differ).
    private static readonly HashSet<string> HighImpactKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "INCLUDE", "LIB", "LIBPATH", "CC", "CXX", "LD", "LDFLAGS", "CFLAGS", "CXXFLAGS",
        "CPPFLAGS", "PKG_CONFIG_PATH", "LD_LIBRARY_PATH", "DYLD_LIBRARY_PATH",
        "DYLD_FRAMEWORK_PATH", "PYTHONPATH", "PYTHONHOME", "NODE_OPTIONS",
        "VCToolsVersion", "WindowsSDKVersion", "UCRTVersion",
    };

    public object? Capture(CaptureContext ctx)
    {
        var env = new EnvPayload();

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        env.Path = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => { var t = p.Trim(); var n = t.TrimEnd('\\', '/'); return n.Length > 0 ? n : t; })
            .Where(p => p.Length > 0)
            .ToList();

        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            var key = e.Key?.ToString();
            if (string.IsNullOrEmpty(key)) continue;
            if (key.Equals("PATH", StringComparison.OrdinalIgnoreCase)) continue;
            var val = e.Value?.ToString();
            if (IsBuildRelevant(key))
                env.BuildRelevant[key] = (LooksSecret(key) || LooksSecretValue(val)) ? "<redacted>" : val;
            else
                // Presence-only for the uncurated long tail: storing values of arbitrary
                // env vars risks leaking secrets whose names lack a marker keyword, and
                // Compare only diffs Other by key anyway.
                env.Other[key] = null;
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

    // Defense-in-depth: redact a value that looks like a credential even if the key
    // name was innocuous (embedded user:pass@, password=/pwd= assignments).
    // (internal for testing)
    internal static bool LooksSecretValue(string? val)
    {
        if (string.IsNullOrEmpty(val)) return false;
        if (System.Text.RegularExpressions.Regex.IsMatch(val, @"://[^/@\s]+:[^/@\s]+@")) return true;
        if (System.Text.RegularExpressions.Regex.IsMatch(val, @"(?i)\b(password|pwd|passwd)\s*=")) return true;
        return false;
    }

    public IEnumerable<Diff> Compare(JsonElement? a, JsonElement? b, CompareContext ctx)
    {
        var pa = Json.To<EnvPayload>(a) ?? new EnvPayload();
        var pb = Json.To<EnvPayload>(b) ?? new EnvPayload();

        // High for vars whose value changes how code compiles/links; Low for the rest
        // (mostly install paths, which are expected to differ between machines).
        foreach (var d in DiffHelp.Dict(Id, "Build env var", Split(pa.BuildRelevant, true), Split(pb.BuildRelevant, true), ctx, Severity.High))
            yield return d;
        foreach (var d in DiffHelp.Dict(Id, "Build env var", Split(pa.BuildRelevant, false), Split(pb.BuildRelevant, false), ctx, Severity.Low))
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

    private static Dictionary<string, string?> Split(Dictionary<string, string?> d, bool highImpact)
        => d.Where(kv => HighImpactKeys.Contains(kv.Key) == highImpact)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
}
