using System.Text.Json;
using System.Text.RegularExpressions;

namespace BuildDiff;

public sealed class RubyPayload
{
    public string? Version { get; set; }
    public string? Path { get; set; }
    public string? GemVersion { get; set; }
    public string? BundlerVersion { get; set; }
}

/// <summary>Ruby, RubyGems and Bundler. Cross-platform (mostly mac/linux).</summary>
public sealed class RubyProvider : IEnvironmentProvider
{
    public string Id => "ruby";
    public string DisplayName => "Ruby, RubyGems, Bundler";
    public bool AppliesTo(OsPlatform os) => true;

    public object? Capture(CaptureContext ctx)
    {
        var ruby = Proc.Which("ruby.exe") ?? Proc.Which("ruby");
        if (ruby is null) return null;

        var p = new RubyPayload { Path = ruby };
        var r = Proc.Run(ruby, "-v", timeoutMs: 12_000);
        if (r is not null)
        {
            var m = Regex.Match(r.Combined, @"ruby ([0-9][0-9.]*)");
            p.Version = m.Success ? m.Groups[1].Value : r.FirstLine;
        }

        var gem = Proc.Which("gem") ?? Proc.Which("gem.cmd");
        if (gem is not null) p.GemVersion = Proc.Run(gem, "-v", timeoutMs: 12_000)?.FirstLine;

        var bundle = Proc.Which("bundle") ?? Proc.Which("bundle.cmd") ?? Proc.Which("bundler");
        if (bundle is not null)
        {
            var b = Proc.Run(bundle, "-v", timeoutMs: 12_000);
            p.BundlerVersion = b is null ? null : Regex.Match(b.Combined, @"Bundler version ([0-9][0-9.]*)") is { Success: true } m
                ? m.Groups[1].Value : b.FirstLine;
        }
        return p;
    }

    public IEnumerable<Diff> Compare(JsonElement? a, JsonElement? b, CompareContext ctx)
    {
        var pa = Json.To<RubyPayload>(a) ?? new RubyPayload();
        var pb = Json.To<RubyPayload>(b) ?? new RubyPayload();

        foreach (var d in DiffHelp.Scalar(Id, "Ruby", "ruby", pa.Version, pb.Version, ctx, Severity.Critical, Severity.High,
            "Native gems compile against a specific Ruby ABI; mismatched versions break bundle install.")) yield return d;
        foreach (var d in DiffHelp.Scalar(Id, "Ruby", "rubygems", pa.GemVersion, pb.GemVersion, ctx, Severity.Medium, Severity.Low)) yield return d;
        foreach (var d in DiffHelp.Scalar(Id, "Ruby", "bundler", pa.BundlerVersion, pb.BundlerVersion, ctx, Severity.High, Severity.Medium,
            "Bundler version is pinned in Gemfile.lock (BUNDLED WITH); a mismatch can refuse to install.")) yield return d;
    }
}
