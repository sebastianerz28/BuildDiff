using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BuildDiff;

/// <summary>
/// Reads a repo's manifests to learn what versions/toolchains the project
/// declares it needs. Used by <c>--project</c> mode; the result lets compare
/// flag machine-vs-declared drift (see <see cref="ProjectCompare"/>).
/// </summary>
public static class ProjectScan
{
    public static ProjectInfo Scan(string root)
    {
        var info = new ProjectInfo { Root = root };
        if (!Directory.Exists(root)) return info;

        Add(info, root, ".nvmrc", "node-version", txt => One("node", txt.Trim()));
        Add(info, root, ".node-version", "node-version", txt => One("node", txt.Trim()));
        Add(info, root, ".python-version", "python-version", txt => One("python", txt.Trim().Split('\n').FirstOrDefault()?.Trim()));
        Add(info, root, ".ruby-version", "ruby-version", txt => One("ruby", txt.Trim()));
        Add(info, root, ".java-version", "java-version", txt => One("java", txt.Trim()));
        Add(info, root, "global.json", "dotnet-global-json", ParseGlobalJson);
        Add(info, root, "package.json", "package-json", ParsePackageJson);
        Add(info, root, "go.mod", "go-mod", ParseGoMod);
        Add(info, root, "rust-toolchain.toml", "rust-toolchain", ParseRustToolchain);
        Add(info, root, "rust-toolchain", "rust-toolchain", txt => One("rust", txt.Trim()));
        Add(info, root, "Package.swift", "swift-package", ParseSwiftPackage);
        Add(info, root, "CMakeLists.txt", "cmake", ParseCMake);
        Add(info, root, ".tool-versions", "asdf", ParseToolVersions);

        ScanLockfiles(info, root);
        return info;
    }

    // Dependency lockfiles: a content hash is a cheap, privacy-safe drift signal —
    // two machines whose same-named lockfile hashes differ are resolving against
    // different locked dependency sets. Contents are never stored.
    private static readonly (string File, string Ecosystem, Func<string, string?>? Marker)[] LockfileSpecs =
    {
        ("package-lock.json", "node", t => JsonField(t, "lockfileVersion")),
        ("npm-shrinkwrap.json", "node", t => JsonField(t, "lockfileVersion")),
        ("pnpm-lock.yaml", "node", t => YamlTop(t, "lockfileVersion")),
        ("yarn.lock", "node", t => t.Contains("__metadata:") ? "berry" : "v1"),
        ("bun.lockb", "node", _ => "binary"),
        ("bun.lock", "node", null),
        ("Cargo.lock", "rust", t => TomlTop(t, "version")),
        ("go.sum", "go", null),
        ("Gemfile.lock", "ruby", t => Match(t, @"BUNDLED WITH\s*\n\s*([0-9][0-9.]*)")),
        ("composer.lock", "php", t => JsonField(t, "content-hash")),
        ("poetry.lock", "python", t => TomlTop(t, "lock-version")),
        ("Pipfile.lock", "python", PipfileHash),
        ("uv.lock", "python", t => TomlTop(t, "version")),
        ("packages.lock.json", "dotnet", t => JsonField(t, "version")),
        ("Package.resolved", "swift", t => JsonField(t, "version")),
        ("requirements.txt", "python", null),
    };

    private static void ScanLockfiles(ProjectInfo info, string root)
    {
        foreach (var (file, eco, marker) in LockfileSpecs)
        {
            var path = Path.Combine(root, file);
            if (!File.Exists(path)) continue;
            try
            {
                string? mk = null;
                if (marker is not null)
                {
                    // Don't read a binary lockfile as text for marker extraction.
                    var text = file.EndsWith(".lockb", StringComparison.OrdinalIgnoreCase) ? "" : File.ReadAllText(path);
                    try { mk = marker(text); } catch { }
                }
                info.Lockfiles.Add(new ProjectLockfile { File = file, Ecosystem = eco, Hash = Sha256Hex(path), Marker = mk });
            }
            catch { }
        }
    }

    private static string Sha256Hex(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    private static string? JsonField(string text, string name)
    {
        try
        {
            using var d = JsonDocument.Parse(text);
            if (d.RootElement.TryGetProperty(name, out var v))
                return v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
        }
        catch { }
        return null;
    }

    private static string? PipfileHash(string text)
    {
        try
        {
            using var d = JsonDocument.Parse(text);
            if (d.RootElement.TryGetProperty("_meta", out var m) && m.TryGetProperty("hash", out var h) && h.TryGetProperty("sha256", out var s))
                return s.GetString();
        }
        catch { }
        return null;
    }

    private static string? YamlTop(string text, string key)
        => Match(text, $@"(?m)^{Regex.Escape(key)}:\s*'?([^'\r\n]+?)'?\s*$");

    private static string? TomlTop(string text, string key)
        => Match(text, $@"(?m)^{Regex.Escape(key)}\s*=\s*""?([^""\r\n]+?)""?\s*$");

    private static string? Match(string text, string pattern)
    {
        var m = Regex.Match(text, pattern);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static void Add(ProjectInfo info, string root, string file, string kind,
        Func<string, Dictionary<string, string?>> parse)
    {
        var path = Path.Combine(root, file);
        if (!File.Exists(path)) return;
        try
        {
            var declares = parse(File.ReadAllText(path));
            info.Manifests.Add(new ProjectManifest { File = file, Kind = kind, Declares = declares });
        }
        catch { /* a malformed manifest must not abort capture */ }
    }

    private static Dictionary<string, string?> One(string key, string? val)
        => string.IsNullOrWhiteSpace(val) ? new() : new() { [key] = val };

    private static Dictionary<string, string?> ParsePackageJson(string txt)
    {
        var d = new Dictionary<string, string?>();
        try
        {
            using var doc = JsonDocument.Parse(txt);
            var root = doc.RootElement;
            if (root.TryGetProperty("engines", out var eng) && eng.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in eng.EnumerateObject())
                    d[p.Name] = p.Value.GetString();
            }
            if (root.TryGetProperty("packageManager", out var pm) && pm.ValueKind == JsonValueKind.String)
                d["packageManager"] = pm.GetString();
        }
        catch { }
        return d;
    }

    private static Dictionary<string, string?> ParseGlobalJson(string txt)
    {
        var d = new Dictionary<string, string?>();
        try
        {
            using var doc = JsonDocument.Parse(txt);
            if (doc.RootElement.TryGetProperty("sdk", out var sdk) &&
                sdk.TryGetProperty("version", out var ver))
                d["dotnet-sdk"] = ver.GetString();
        }
        catch { }
        return d;
    }

    private static Dictionary<string, string?> ParseGoMod(string txt)
    {
        var d = new Dictionary<string, string?>();
        var go = Regex.Match(txt, @"(?m)^\s*go\s+([0-9][0-9.]*)");
        if (go.Success) d["go"] = go.Groups[1].Value;
        var tc = Regex.Match(txt, @"(?m)^\s*toolchain\s+(\S+)");
        if (tc.Success) d["go-toolchain"] = tc.Groups[1].Value;
        return d;
    }

    private static Dictionary<string, string?> ParseRustToolchain(string txt)
    {
        var d = new Dictionary<string, string?>();
        var ch = Regex.Match(txt, "(?m)^\\s*channel\\s*=\\s*\"([^\"]+)\"");
        if (ch.Success) d["rust"] = ch.Groups[1].Value;
        return d;
    }

    private static Dictionary<string, string?> ParseSwiftPackage(string txt)
    {
        var d = new Dictionary<string, string?>();
        var m = Regex.Match(txt, @"swift-tools-version:\s*([0-9][0-9.]*)");
        if (m.Success) d["swift-tools"] = m.Groups[1].Value;
        return d;
    }

    private static Dictionary<string, string?> ParseCMake(string txt)
    {
        var d = new Dictionary<string, string?>();
        var m = Regex.Match(txt, @"cmake_minimum_required\s*\(\s*VERSION\s+([0-9][0-9.]*)", RegexOptions.IgnoreCase);
        if (m.Success) d["cmake-minimum"] = m.Groups[1].Value;
        return d;
    }

    private static Dictionary<string, string?> ParseToolVersions(string txt)
    {
        var d = new Dictionary<string, string?>();
        foreach (var line in txt.Split('\n'))
        {
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2 && !parts[0].StartsWith('#'))
                d[parts[0]] = parts[1];
        }
        return d;
    }
}
