using System.Text.Json;

namespace BuildDiff;

public sealed class NativeRuntimePayload
{
    public List<NativeDep> Deps { get; set; } = new();
}

public sealed class NativeDep
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string? FileVersion { get; set; }
    public string? Architecture { get; set; }
}

/// <summary>
/// VC++ runtime DLLs whose absence breaks load-time linking of native binaries.
/// Windows-only (gated via AppliesTo).
/// </summary>
public sealed class NativeRuntimeProvider : IEnvironmentProvider
{
    public string Id => "native-runtime";
    public string DisplayName => "Native runtime DLLs (VC++ redistributable)";
    public bool AppliesTo(OsPlatform os) => os == OsPlatform.Windows;

    private static readonly string[] KnownDlls =
    {
        "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll",
        "concrt140.dll", "msvcp140_1.dll", "msvcp140_2.dll",
        "ucrtbase.dll", "vcomp140.dll",
    };

    public object? Capture(CaptureContext ctx)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var payload = new NativeRuntimePayload();
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dll in KnownDlls)
        {
            var full = Path.Combine(system32, dll);
            if (File.Exists(full) && seen.Add(full))
                payload.Deps.Add(Describe(dll, full));
        }
        return payload.Deps.Count == 0 ? null : payload;
    }

    public IEnumerable<Diff> Compare(JsonElement? a, JsonElement? b, CompareContext ctx)
    {
        var pa = Json.To<NativeRuntimePayload>(a) ?? new NativeRuntimePayload();
        var pb = Json.To<NativeRuntimePayload>(b) ?? new NativeRuntimePayload();
        var aByName = pa.Deps.ToDictionary(d => d.Name, d => d, StringComparer.OrdinalIgnoreCase);
        var bByName = pb.Deps.ToDictionary(d => d.Name, d => d, StringComparer.OrdinalIgnoreCase);

        foreach (var name in aByName.Keys.Except(bByName.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
            yield return new Diff(Severity.High, "Native dep", $"{name} present on {ctx.A}, MISSING on {ctx.B}",
                "Load-time linking will fail without this runtime DLL.", Id);
        foreach (var name in bByName.Keys.Except(aByName.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
            yield return new Diff(Severity.High, "Native dep", $"{name} present on {ctx.B}, MISSING on {ctx.A}",
                "Load-time linking will fail without this runtime DLL.", Id);

        foreach (var name in aByName.Keys.Intersect(bByName.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
            if (!string.Equals(aByName[name].FileVersion, bByName[name].FileVersion, StringComparison.Ordinal))
                yield return new Diff(Severity.Low, "Native dep",
                    $"{name} version differs: {ctx.A}={aByName[name].FileVersion ?? "?"}, {ctx.B}={bByName[name].FileVersion ?? "?"}", null, Id);
    }

    private static NativeDep Describe(string name, string full)
    {
        string? fileVersion = null, arch = null;
        try { fileVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(full).FileVersion; } catch { }
        try { arch = ReadPeArchitecture(full); } catch { }
        return new NativeDep { Name = name, Path = full, FileVersion = fileVersion, Architecture = arch };
    }

    private static string? ReadPeArchitecture(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        if (br.ReadUInt16() != 0x5A4D) return null;   // 'MZ'
        fs.Position = 0x3C;
        var peOffset = br.ReadInt32();
        fs.Position = peOffset;
        if (br.ReadUInt32() != 0x00004550) return null;  // 'PE\0\0'
        return br.ReadUInt16() switch
        {
            0x8664 => "x64",
            0x014c => "x86",
            0xAA64 => "arm64",
            0x01c0 or 0x01c4 => "arm",
            var m => $"0x{m:X4}",
        };
    }
}
