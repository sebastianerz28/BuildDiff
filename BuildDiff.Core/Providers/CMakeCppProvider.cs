using System.Text.Json;
using System.Text.RegularExpressions;

namespace BuildDiff;

public sealed class CMakeCppPayload
{
    public string? CMakeVersion { get; set; }
    public string? NinjaVersion { get; set; }
    public string? MakeVersion { get; set; }
    public Dictionary<string, string?> Compilers { get; set; } = new();
    public string? VcpkgVersion { get; set; }
    public string? VcpkgRoot { get; set; }
    public string? ConanVersion { get; set; }
    public string? CcacheVersion { get; set; }
}

/// <summary>
/// Native C/C++ build stack: CMake, generators (ninja/make), compilers
/// (gcc/clang/cl), and dependency managers (vcpkg/conan). Cross-platform.
/// </summary>
public sealed class CMakeCppProvider : IEnvironmentProvider
{
    public string Id => "cmake-cpp";
    public string DisplayName => "CMake, ninja/make, C/C++ compilers, vcpkg/conan";
    public bool AppliesTo(OsPlatform os) => true;

    public object? Capture(CaptureContext ctx)
    {
        var p = new CMakeCppPayload();

        p.CMakeVersion = Extract(Tool("cmake", "--version"), @"cmake version ([0-9][0-9.]*)");
        p.NinjaVersion = Tool("ninja", "--version")?.FirstLine;
        p.MakeVersion = Extract(Tool("make", "--version"), @"Make ([0-9][0-9.]*)");

        foreach (var cc in new[] { "gcc", "g++", "clang", "clang++" })
        {
            var r = Tool(cc, "--version");
            if (r is not null) p.Compilers[cc] = r.FirstLine;
        }
        if (OperatingSystem.IsWindows())
        {
            var cl = Tool("cl.exe", "");
            var v = cl is null ? null : Regex.Match(cl.Combined, @"Version ([0-9][0-9.]*)");
            if (v is { Success: true }) p.Compilers["cl"] = v.Groups[1].Value;
        }

        p.VcpkgRoot = Environment.GetEnvironmentVariable("VCPKG_ROOT");
        p.VcpkgVersion = Extract(Tool("vcpkg", "version"), @"version ([0-9][0-9.\-a-z]*)");
        p.ConanVersion = Extract(Tool("conan", "--version"), @"Conan version ([0-9][0-9.]*)");
        p.CcacheVersion = Extract(Tool("ccache", "--version"), @"ccache version ([0-9][0-9.]*)");

        bool empty = p.CMakeVersion is null && p.NinjaVersion is null && p.MakeVersion is null
            && p.Compilers.Count == 0 && p.VcpkgVersion is null && p.ConanVersion is null;
        return empty ? null : p;
    }

    public IEnumerable<Diff> Compare(JsonElement? a, JsonElement? b, CompareContext ctx)
    {
        var pa = Json.To<CMakeCppPayload>(a) ?? new CMakeCppPayload();
        var pb = Json.To<CMakeCppPayload>(b) ?? new CMakeCppPayload();

        foreach (var d in VersionDiff("CMake", "cmake", pa.CMakeVersion, pb.CMakeVersion, ctx, Severity.Critical)) yield return d;
        foreach (var d in DiffHelp.Scalar(Id, "Build tool", "ninja", pa.NinjaVersion, pb.NinjaVersion, ctx, Severity.High, Severity.Low)) yield return d;
        foreach (var d in DiffHelp.Scalar(Id, "Build tool", "make", pa.MakeVersion, pb.MakeVersion, ctx, Severity.Medium, Severity.Low)) yield return d;

        foreach (var d in DiffHelp.Dict(Id, "C/C++ compiler", pa.Compilers, pb.Compilers, ctx, Severity.High)) yield return d;

        foreach (var d in DiffHelp.Scalar(Id, "vcpkg", "vcpkg", pa.VcpkgVersion, pb.VcpkgVersion, ctx, Severity.High, Severity.Medium,
            "C/C++ dependency restore will diverge without matching vcpkg.")) yield return d;
        foreach (var d in DiffHelp.Scalar(Id, "vcpkg", "VCPKG_ROOT", pa.VcpkgRoot, pb.VcpkgRoot, ctx, Severity.Medium, Severity.Medium)) yield return d;
        foreach (var d in DiffHelp.Scalar(Id, "conan", "conan", pa.ConanVersion, pb.ConanVersion, ctx, Severity.High, Severity.Medium)) yield return d;
        foreach (var d in DiffHelp.Scalar(Id, "ccache", "ccache", pa.CcacheVersion, pb.CcacheVersion, ctx, Severity.Low, Severity.Low)) yield return d;
    }

    private IEnumerable<Diff> VersionDiff(string category, string subject, string? a, string? b, CompareContext ctx, Severity missing)
    {
        bool aHas = !string.IsNullOrEmpty(a), bHas = !string.IsNullOrEmpty(b);
        if (aHas && !bHas) yield return new Diff(missing, category, $"{subject} present on {ctx.A} ({a}) but NOT FOUND on {ctx.B}", null, Id);
        else if (bHas && !aHas) yield return new Diff(missing, category, $"{subject} present on {ctx.B} ({b}) but NOT FOUND on {ctx.A}", null, Id);
        else if (aHas && bHas && !string.Equals(a, b, StringComparison.Ordinal))
        {
            var sameMajor = string.Equals(a!.Split('.').FirstOrDefault(), b!.Split('.').FirstOrDefault(), StringComparison.Ordinal);
            yield return new Diff(sameMajor ? Severity.Low : Severity.High, category, $"{subject} version differs: {ctx.A}={a}, {ctx.B}={b}", null, Id);
        }
    }

    private static Proc.Result? Tool(string name, string args)
    {
        var path = Proc.Which(name);
        return path is null ? null : Proc.Run(path, args, timeoutMs: 12_000);
    }

    private static string? Extract(Proc.Result? r, string pattern)
    {
        if (r is null) return null;
        var m = Regex.Match(r.Combined, pattern, RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : r.FirstLine;
    }
}
