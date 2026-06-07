using System.Text.Json;
using System.Text.RegularExpressions;

namespace BuildDiff;

public sealed class NuGetPayload
{
    public List<NuGetSource> Sources { get; set; } = new();
    public string? GlobalPackages { get; set; }
    public List<string> Configs { get; set; } = new();
}

public sealed class NuGetSource
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

/// <summary>NuGet feeds + config + global package cache. Cross-platform (dotnet).</summary>
public sealed class NuGetProvider : IEnvironmentProvider
{
    public string Id => "nuget";
    public string DisplayName => "NuGet feeds & config";
    public bool AppliesTo(OsPlatform os) => true;

    public object? Capture(CaptureContext ctx)
    {
        var dotnet = Proc.Which("dotnet.exe") ?? Proc.Which("dotnet");
        if (dotnet is null) return null;

        var info = new NuGetPayload();

        var listed = Proc.Run(dotnet, "nuget list source --format short", timeoutMs: 15_000);
        if (listed is not null && listed.ExitCode == 0)
        {
            foreach (var line in listed.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length < 3) continue;
                var enabled = trimmed[0] != 'D';
                // Strip the leading status-flag run (E/D enabled/disabled, M machine-wide)
                // rather than assuming a single char — otherwise a machine-wide source
                // leaves a stray 'M' glued onto the URL.
                int i = 0;
                while (i < trimmed.Length && trimmed[i] is 'E' or 'D' or 'M') i++;
                var rest = RedactCredentials(trimmed[i..].Trim());
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
                var match = Regex.Match(line, @"^\s*(\d+)\.\s+(?<name>.+?)\s+\[(?<state>Enabled|Disabled)\]");
                if (match.Success) { curName = match.Groups["name"].Value.Trim(); continue; }
                if (curName is not null && line.Trim().Length > 0)
                {
                    var url = RedactCredentials(line.Trim());
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
                if (colon >= 0) info.GlobalPackages = line[(colon + 1)..].Trim();
            }
        }

        foreach (var c in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NuGet", "NuGet.Config"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "NuGet", "NuGet.Config"),
            Path.Combine(Environment.CurrentDirectory, "NuGet.config"),
            Path.Combine(Environment.CurrentDirectory, "nuget.config"),
        })
            if (File.Exists(c)) info.Configs.Add(c);
        info.Configs = info.Configs.Distinct(Os.PathComparer).ToList();

        if (info.Sources.Count == 0 && info.Configs.Count == 0 && info.GlobalPackages is null) return null;
        return info;
    }

    public IEnumerable<Diff> Compare(JsonElement? a, JsonElement? b, CompareContext ctx)
    {
        var pa = Json.To<NuGetPayload>(a) ?? new NuGetPayload();
        var pb = Json.To<NuGetPayload>(b) ?? new NuGetPayload();

        var aByName = ByName(pa);
        var bByName = ByName(pb);

        foreach (var name in aByName.Keys.Except(bByName.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
            yield return new Diff(Severity.Critical, "NuGet source",
                $"'{name}' ({aByName[name].Url}) configured on {ctx.A}, absent on {ctx.B}",
                "Restore likely fails for packages hosted on this feed.", Id);
        foreach (var name in bByName.Keys.Except(aByName.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
            yield return new Diff(Severity.Critical, "NuGet source",
                $"'{name}' ({bByName[name].Url}) configured on {ctx.B}, absent on {ctx.A}",
                "Restore likely fails for packages hosted on this feed.", Id);

        foreach (var name in aByName.Keys.Intersect(bByName.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var av = aByName[name]; var bv = bByName[name];
            if (!string.Equals(av.Url, bv.Url, StringComparison.OrdinalIgnoreCase))
                yield return new Diff(Severity.High, "NuGet source",
                    $"'{name}' URL differs: {ctx.A}={av.Url}, {ctx.B}={bv.Url}", null, Id);
            else if (av.Enabled != bv.Enabled)
                yield return new Diff(Severity.High, "NuGet source",
                    $"'{name}' enabled state differs: {ctx.A}={av.Enabled}, {ctx.B}={bv.Enabled}", null, Id);
        }
    }

    // Feed URLs can embed credentials (https://user:PAT@feed/...). Never let those
    // land in a snapshot that gets shared between machines. (internal for testing)
    internal static string RedactCredentials(string url)
        => Regex.Replace(url, @"://[^/@\s]+@", "://<redacted>@");

    private static Dictionary<string, NuGetSource> ByName(NuGetPayload p) => p.Sources
        .Where(s => !string.IsNullOrEmpty(s.Name))
        .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
}
