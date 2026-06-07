using System.Text.Json;

namespace BuildDiff;

public sealed class DotnetPayload
{
    public string? CliVersion { get; set; }
    public string? Path { get; set; }
    public List<string> Sdks { get; set; } = new();
    public List<string> Runtimes { get; set; } = new();
}

/// <summary>.NET SDKs and runtimes. Cross-platform — dotnet runs everywhere.</summary>
public sealed class DotnetProvider : IEnvironmentProvider
{
    public string Id => "dotnet";
    public string DisplayName => ".NET SDKs & runtimes";
    public bool AppliesTo(OsPlatform os) => true;

    public object? Capture(CaptureContext ctx)
    {
        var dotnet = Proc.Which("dotnet.exe") ?? Proc.Which("dotnet");
        if (dotnet is null) return null;

        var payload = new DotnetPayload { Path = dotnet };

        var ver = Proc.Run(dotnet, "--version", timeoutMs: 15_000);
        if (ver is not null && ver.ExitCode == 0) payload.CliVersion = ver.FirstLine;

        var sdks = Proc.Run(dotnet, "--list-sdks", timeoutMs: 15_000);
        if (sdks is not null && sdks.ExitCode == 0)
            payload.Sdks = Lines(sdks.Stdout);

        var runtimes = Proc.Run(dotnet, "--list-runtimes", timeoutMs: 15_000);
        if (runtimes is not null && runtimes.ExitCode == 0)
            payload.Runtimes = Lines(runtimes.Stdout);

        return payload;
    }

    public IEnumerable<Diff> Compare(JsonElement? a, JsonElement? b, CompareContext ctx)
    {
        var pa = Json.To<DotnetPayload>(a) ?? new DotnetPayload();
        var pb = Json.To<DotnetPayload>(b) ?? new DotnetPayload();

        foreach (var d in DiffHelp.Scalar(Id, ".NET CLI", ".NET CLI", pa.CliVersion, pb.CliVersion, ctx,
            Severity.Critical, Severity.Medium)) yield return d;

        // Having NO SDK at all (e.g. a runtime-only box) is what breaks a build —
        // an *extra* SDK version on one side is not. Don't let it hijack the headline.
        if (pa.Sdks.Count > 0 && pb.Sdks.Count == 0)
            yield return new Diff(Severity.Critical, ".NET SDK", $"no .NET SDK on {ctx.B} ({pa.Sdks.Count} installed on {ctx.A})", "Install the .NET SDK (a runtime-only install can't build).", Id);
        else if (pb.Sdks.Count > 0 && pa.Sdks.Count == 0)
            yield return new Diff(Severity.Critical, ".NET SDK", $"no .NET SDK on {ctx.A} ({pb.Sdks.Count} installed on {ctx.B})", "Install the .NET SDK (a runtime-only install can't build).", Id);
        else
            foreach (var d in DiffHelp.Sets(Id, ".NET SDK", pa.Sdks, pb.Sdks, ctx, Severity.Medium)) yield return d;

        if (pa.Runtimes.Count > 0 && pb.Runtimes.Count == 0)
            yield return new Diff(Severity.High, ".NET runtime", $"no .NET runtime on {ctx.B}", null, Id);
        else if (pb.Runtimes.Count > 0 && pa.Runtimes.Count == 0)
            yield return new Diff(Severity.High, ".NET runtime", $"no .NET runtime on {ctx.A}", null, Id);
        else
            foreach (var d in DiffHelp.Sets(Id, ".NET runtime", pa.Runtimes, pb.Runtimes, ctx, Severity.Medium)) yield return d;
    }

    private static List<string> Lines(string s) => s
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
}
