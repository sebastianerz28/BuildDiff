using System.Text.Json;

namespace BuildDiff;

public sealed class SwigInfo
{
    public string? Path { get; set; }
    public string? Version { get; set; }
    public bool OnPath { get; set; }
}

/// <summary>SWIG — generates native/Python/other language bindings. Cross-platform.</summary>
public sealed class SwigProvider : IEnvironmentProvider
{
    public string Id => "swig";
    public string DisplayName => "SWIG (native binding generator)";
    public bool AppliesTo(OsPlatform os) => true;

    public object? Capture(CaptureContext ctx)
    {
        var path = Proc.Which("swig.exe") ?? Proc.Which("swig");
        if (path is null) return null;

        string? version = null;
        var r = Proc.Run(path, "-version", timeoutMs: 10_000);
        if (r is not null)
        {
            var line = r.Combined.Split('\n').Select(l => l.Trim())
                .FirstOrDefault(l => l.StartsWith("SWIG Version", StringComparison.OrdinalIgnoreCase));
            version = line?.Replace("SWIG Version", "", StringComparison.OrdinalIgnoreCase).Trim();
        }
        return new SwigInfo { Path = path, Version = version, OnPath = true };
    }

    public IEnumerable<Diff> Compare(JsonElement? a, JsonElement? b, CompareContext ctx)
    {
        var pa = Json.To<SwigInfo>(a);
        var pb = Json.To<SwigInfo>(b);
        var aHas = pa?.OnPath == true;
        var bHas = pb?.OnPath == true;

        if (aHas && !bHas)
            yield return new Diff(Severity.Critical, "SWIG", $"swig present on {ctx.A} ({pa!.Version ?? "?"}) but NOT FOUND on {ctx.B}",
                "Native/Python bindings won't generate on the machine missing SWIG.", Id);
        else if (bHas && !aHas)
            yield return new Diff(Severity.Critical, "SWIG", $"swig present on {ctx.B} ({pb!.Version ?? "?"}) but NOT FOUND on {ctx.A}",
                "Native/Python bindings won't generate on the machine missing SWIG.", Id);
        else if (aHas && bHas && !string.Equals(pa!.Version, pb!.Version, StringComparison.Ordinal))
            yield return new Diff(Severity.High, "SWIG", $"swig version differs: {ctx.A}={pa.Version ?? "?"}, {ctx.B}={pb.Version ?? "?"}", null, Id);
    }
}
