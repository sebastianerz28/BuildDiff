using System.Text.Json;
using System.Text.RegularExpressions;

namespace BuildDiff;

public sealed class PhpPayload
{
    public string? Version { get; set; }
    public string? Path { get; set; }
    public string? ComposerVersion { get; set; }
    public List<string> Extensions { get; set; } = new();
}

/// <summary>PHP runtime, loaded extensions and Composer. Cross-platform.</summary>
public sealed class PhpProvider : IEnvironmentProvider
{
    public string Id => "php";
    public string DisplayName => "PHP, extensions, Composer";
    public bool AppliesTo(OsPlatform os) => true;

    public object? Capture(CaptureContext ctx)
    {
        var php = Proc.Which("php.exe") ?? Proc.Which("php");
        if (php is null) return null;

        var p = new PhpPayload { Path = php };
        var v = Proc.Run(php, "-v", timeoutMs: 12_000);
        if (v is not null)
        {
            var m = Regex.Match(v.Combined, @"PHP ([0-9][0-9.]*)");
            p.Version = m.Success ? m.Groups[1].Value : v.FirstLine;
        }

        var mods = Proc.Run(php, "-m", timeoutMs: 12_000);
        if (mods is not null && mods.ExitCode == 0)
            p.Extensions = mods.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('[') && !l.Contains(' '))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        var composer = Proc.Which("composer") ?? Proc.Which("composer.bat") ?? Proc.Which("composer.phar");
        if (composer is not null)
            p.ComposerVersion = Extract(Proc.Run(composer, "--version", timeoutMs: 15_000)?.Combined, @"Composer version ([0-9][0-9.]*)");

        return p;
    }

    public IEnumerable<Diff> Compare(JsonElement? a, JsonElement? b, CompareContext ctx)
    {
        var pa = Json.To<PhpPayload>(a) ?? new PhpPayload();
        var pb = Json.To<PhpPayload>(b) ?? new PhpPayload();

        foreach (var d in DiffHelp.Scalar(Id, "PHP", "php", pa.Version, pb.Version, ctx, Severity.Critical, Severity.High,
            "composer.json platform requirements pin a PHP version; a mismatch fails install/build.")) yield return d;
        foreach (var d in DiffHelp.Scalar(Id, "PHP", "composer", pa.ComposerVersion, pb.ComposerVersion, ctx, Severity.High, Severity.Medium)) yield return d;
        foreach (var d in DiffHelp.Sets(Id, "PHP extension", pa.Extensions, pb.Extensions, ctx, Severity.High,
            e => $"A required PHP extension ({e}) is loaded on one machine only.")) yield return d;
    }

    private static string? Extract(string? text, string pattern)
    {
        if (text is null) return null;
        var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }
}
