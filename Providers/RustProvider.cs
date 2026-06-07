using System.Text.Json;
using System.Text.RegularExpressions;

namespace BuildDiff;

public sealed class RustPayload
{
    public string? RustcVersion { get; set; }
    public string? CargoVersion { get; set; }
    public string? ActiveToolchain { get; set; }
    public string? HostTriple { get; set; }
    public List<string> Toolchains { get; set; } = new();
    public List<string> Targets { get; set; } = new();
}

/// <summary>Rust via rustup: rustc/cargo, active + installed toolchains, targets. Cross-platform.</summary>
public sealed class RustProvider : IEnvironmentProvider
{
    public string Id => "rust";
    public string DisplayName => "Rust, Cargo, rustup toolchains & targets";
    public bool AppliesTo(OsPlatform os) => true;

    public object? Capture(CaptureContext ctx)
    {
        var rustc = Proc.Which("rustc.exe") ?? Proc.Which("rustc");
        var cargo = Proc.Which("cargo.exe") ?? Proc.Which("cargo");
        var rustup = Proc.Which("rustup.exe") ?? Proc.Which("rustup");
        if (rustc is null && cargo is null && rustup is null) return null;

        var p = new RustPayload();
        if (rustc is not null)
        {
            var r = Proc.Run(rustc, "--version", timeoutMs: 12_000);
            p.RustcVersion = r is null ? null : Extract(r.Combined, @"rustc ([0-9][0-9.\-a-z]*)") ?? r.FirstLine;
            var host = Proc.Run(rustc, "-vV", timeoutMs: 12_000);
            if (host is not null)
            {
                var m = Regex.Match(host.Combined, @"(?m)^host:\s*(\S+)");
                if (m.Success) p.HostTriple = m.Groups[1].Value;
            }
        }
        if (cargo is not null)
        {
            var r = Proc.Run(cargo, "--version", timeoutMs: 12_000);
            p.CargoVersion = r is null ? null : Extract(r.Combined, @"cargo ([0-9][0-9.\-a-z]*)") ?? r.FirstLine;
        }
        if (rustup is not null)
        {
            var active = Proc.Run(rustup, "show active-toolchain", timeoutMs: 12_000);
            if (active?.ExitCode == 0) p.ActiveToolchain = active.FirstLine?.Split(' ').FirstOrDefault();

            var tc = Proc.Run(rustup, "toolchain list", timeoutMs: 12_000);
            if (tc?.ExitCode == 0)
                p.Toolchains = tc.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Replace("(default)", "").Replace("(active)", "").Trim())
                    .Where(l => l.Length > 0).ToList();

            var tg = Proc.Run(rustup, "target list --installed", timeoutMs: 12_000);
            if (tg?.ExitCode == 0)
                p.Targets = tg.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        }
        return p;
    }

    public IEnumerable<Diff> Compare(JsonElement? a, JsonElement? b, CompareContext ctx)
    {
        var pa = Json.To<RustPayload>(a) ?? new RustPayload();
        var pb = Json.To<RustPayload>(b) ?? new RustPayload();

        foreach (var d in DiffHelp.Scalar(Id, "Rust", "rustc", pa.RustcVersion, pb.RustcVersion, ctx, Severity.Critical, Severity.High,
            "Rust pins edition/toolchain behavior; mismatched compilers can fail on newer syntax or lints.")) yield return d;
        foreach (var d in DiffHelp.Scalar(Id, "Rust", "cargo", pa.CargoVersion, pb.CargoVersion, ctx, Severity.High, Severity.Medium)) yield return d;
        foreach (var d in DiffHelp.Scalar(Id, "Rust", "active toolchain", pa.ActiveToolchain, pb.ActiveToolchain, ctx, Severity.Medium, Severity.Medium)) yield return d;
        foreach (var d in DiffHelp.Scalar(Id, "Rust", "host triple", pa.HostTriple, pb.HostTriple, ctx, Severity.High, Severity.High)) yield return d;

        foreach (var d in DiffHelp.Sets(Id, "Rust target", pa.Targets, pb.Targets, ctx, Severity.High,
            t => $"Install with: rustup target add {t}")) yield return d;
        foreach (var d in DiffHelp.Sets(Id, "Rust toolchain", pa.Toolchains, pb.Toolchains, ctx, Severity.Low)) yield return d;
    }

    private static string? Extract(string text, string pattern)
    {
        var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }
}
