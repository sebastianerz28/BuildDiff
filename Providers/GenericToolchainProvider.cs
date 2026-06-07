using System.Text.Json;
using System.Text.RegularExpressions;

namespace BuildDiff;

public sealed class GenericPayload
{
    /// <summary>tool name → detected version string.</summary>
    public Dictionary<string, string?> Tools { get; set; } = new();
}

/// <summary>
/// Breadth provider: detects any build-relevant binary on PATH that doesn't have
/// a dedicated provider, recording presence + version. No hand-tuned severity —
/// presence-on-one-side is Medium, version drift is Low. This is what lets the
/// tool feel universal across the long tail of enterprise stacks.
/// </summary>
public sealed class GenericToolchainProvider : IEnvironmentProvider
{
    public string Id => "generic-toolchain";
    public string DisplayName => "Other build tools on PATH (deno, sbt, ghc, dart, meson, …)";
    public bool AppliesTo(OsPlatform os) => true;

    // name, version-args, regex (group 1 = version). Tools already owned by a
    // dedicated provider are intentionally excluded to avoid double-reporting.
    private static readonly (string Name, string Args, string Pattern)[] Registry =
    {
        // bazel/bazelisk -> BazelProvider; terraform/pulumi/kubectl/helm/aws/gcloud/az -> InfraIacProvider.
        ("buck2", "--version", @"([0-9a-f.]+)"),
        ("meson", "--version", @"([0-9][0-9.]+)"),
        ("scons", "--version", @"v([0-9][0-9.]+)"),
        ("sbt", "--version", @"([0-9][0-9.]+)"),
        ("scala", "-version", @"version ([0-9][0-9.]+)"),
        ("lein", "--version", @"Leiningen ([0-9][0-9.]+)"),
        ("deno", "--version", @"deno ([0-9][0-9.]+)"),
        ("elixir", "--version", @"Elixir ([0-9][0-9.]+)"),
        ("mix", "--version", @"Mix ([0-9][0-9.]+)"),
        ("erl", "-version", @"([0-9][0-9.]+)"),
        ("ghc", "--version", @"version ([0-9][0-9.]+)"),
        ("cabal", "--version", @"cabal-install version ([0-9][0-9.]+)"),
        ("stack", "--version", @"Version ([0-9][0-9.]+)"),
        ("dart", "--version", @"version: ([0-9][0-9.]+)"),
        ("flutter", "--version", @"Flutter ([0-9][0-9.]+)"),
        ("perl", "--version", @"\(v([0-9][0-9.]+)\)"),
        ("lua", "-v", @"Lua ([0-9][0-9.]+)"),
        ("R", "--version", @"R version ([0-9][0-9.]+)"),
        ("protoc", "--version", @"libprotoc ([0-9][0-9.]+)"),
        ("pkg-config", "--version", @"([0-9][0-9.]+)"),
        ("autoconf", "--version", @"autoconf.*?([0-9][0-9.]+)"),
        ("nuget", "help", @"NuGet Version: ([0-9][0-9.]+)"),
        ("packer", "version", @"Packer v([0-9][0-9.]+)"),
        ("ansible", "--version", @"ansible \[?core ?([0-9][0-9.]+)"),
    };

    public object? Capture(CaptureContext ctx)
    {
        var payload = new GenericPayload();
        foreach (var (name, args, pattern) in Registry)
        {
            var path = Proc.Which(name) ?? Proc.Which(name + ".exe") ?? Proc.Which(name + ".cmd");
            if (path is null) continue;
            var r = Proc.Run(path, args, timeoutMs: 10_000);
            if (r is null) { payload.Tools[name] = "present"; continue; }
            var m = Regex.Match(r.Combined, pattern, RegexOptions.IgnoreCase);
            payload.Tools[name] = m.Success ? m.Groups[1].Value : (r.FirstLine ?? "present");
        }
        return payload.Tools.Count == 0 ? null : payload;
    }

    public IEnumerable<Diff> Compare(JsonElement? a, JsonElement? b, CompareContext ctx)
    {
        var pa = (Json.To<GenericPayload>(a) ?? new GenericPayload()).Tools;
        var pb = (Json.To<GenericPayload>(b) ?? new GenericPayload()).Tools;

        foreach (var name in pa.Keys.Union(pb.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            pa.TryGetValue(name, out var av);
            pb.TryGetValue(name, out var bv);
            bool aHas = pa.ContainsKey(name), bHas = pb.ContainsKey(name);
            if (aHas && !bHas)
                yield return new Diff(Severity.Medium, "Tool", $"{name} ({av}) present on {ctx.A}, absent on {ctx.B}", null, Id);
            else if (bHas && !aHas)
                yield return new Diff(Severity.Medium, "Tool", $"{name} ({bv}) present on {ctx.B}, absent on {ctx.A}", null, Id);
            else if (!string.Equals(av, bv, StringComparison.Ordinal))
                yield return new Diff(Severity.Low, "Tool", $"{name} version differs: {ctx.A}={av}, {ctx.B}={bv}", null, Id);
        }
    }
}
