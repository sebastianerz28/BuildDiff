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

        return info;
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
