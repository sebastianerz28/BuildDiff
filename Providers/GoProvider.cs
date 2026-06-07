using System.Text.Json;
using System.Text.RegularExpressions;

namespace BuildDiff;

public sealed class GoPayload
{
    public string? Version { get; set; }
    public string? Path { get; set; }
    public string? GoRoot { get; set; }
    public string? GoPath { get; set; }
    public string? GoToolchain { get; set; }
    public string? CgoEnabled { get; set; }
    public string? GoOs { get; set; }
    public string? GoArch { get; set; }
}

/// <summary>Go toolchain + go env. Cross-platform.</summary>
public sealed class GoProvider : IEnvironmentProvider
{
    public string Id => "go";
    public string DisplayName => "Go toolchain";
    public bool AppliesTo(OsPlatform os) => true;

    public object? Capture(CaptureContext ctx)
    {
        var go = Proc.Which("go.exe") ?? Proc.Which("go");
        if (go is null) return null;

        var p = new GoPayload { Path = go };
        var ver = Proc.Run(go, "version", timeoutMs: 12_000);
        if (ver is not null)
        {
            var m = Regex.Match(ver.Combined, @"go version go([0-9][0-9A-Za-z.\-]*)");
            p.Version = m.Success ? m.Groups[1].Value : ver.FirstLine;
        }

        var env = Proc.Run(go, "env GOROOT GOPATH GOTOOLCHAIN CGO_ENABLED GOOS GOARCH", timeoutMs: 12_000);
        if (env is not null && env.ExitCode == 0)
        {
            var lines = env.Stdout.Split('\n').Select(l => l.Trim().Trim('"')).ToArray();
            if (lines.Length >= 6)
            {
                p.GoRoot = NullIfEmpty(lines[0]);
                p.GoPath = NullIfEmpty(lines[1]);
                p.GoToolchain = NullIfEmpty(lines[2]);
                p.CgoEnabled = NullIfEmpty(lines[3]);
                p.GoOs = NullIfEmpty(lines[4]);
                p.GoArch = NullIfEmpty(lines[5]);
            }
        }
        return p;
    }

    public IEnumerable<Diff> Compare(JsonElement? a, JsonElement? b, CompareContext ctx)
    {
        var pa = Json.To<GoPayload>(a) ?? new GoPayload();
        var pb = Json.To<GoPayload>(b) ?? new GoPayload();

        bool aHas = !string.IsNullOrEmpty(pa.Version), bHas = !string.IsNullOrEmpty(pb.Version);
        if (aHas && !bHas) yield return new Diff(Severity.Critical, "Go", $"go present on {ctx.A} ({pa.Version}) but NOT FOUND on {ctx.B}", null, Id);
        else if (bHas && !aHas) yield return new Diff(Severity.Critical, "Go", $"go present on {ctx.B} ({pb.Version}) but NOT FOUND on {ctx.A}", null, Id);
        else if (aHas && bHas && !string.Equals(pa.Version, pb.Version, StringComparison.Ordinal))
            yield return new Diff(Severity.High, "Go", $"go version differs: {ctx.A}={pa.Version}, {ctx.B}={pb.Version}",
                "Go enforces minimum toolchain via go.mod; mismatched versions can refuse to build or auto-download.", Id);

        foreach (var d in DiffHelp.Scalar(Id, "Go", "GOARCH", pa.GoArch, pb.GoArch, ctx, Severity.High, Severity.High)) yield return d;
        foreach (var d in DiffHelp.Scalar(Id, "Go", "GOOS", pa.GoOs, pb.GoOs, ctx, Severity.Medium, Severity.Medium)) yield return d;
        foreach (var d in DiffHelp.Scalar(Id, "Go", "CGO_ENABLED", pa.CgoEnabled, pb.CgoEnabled, ctx, Severity.Medium, Severity.Medium,
            "CGO on/off changes whether a C toolchain is required and which packages compile.")) yield return d;
        foreach (var d in DiffHelp.Scalar(Id, "Go", "GOTOOLCHAIN", pa.GoToolchain, pb.GoToolchain, ctx, Severity.Low, Severity.Low)) yield return d;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
