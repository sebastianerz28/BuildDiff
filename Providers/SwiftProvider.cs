using System.Text.Json;
using System.Text.RegularExpressions;

namespace BuildDiff;

public sealed class SwiftPayload
{
    public string? SwiftVersion { get; set; }
    public string? SwiftPath { get; set; }
    public string? XcodeVersion { get; set; }
    public string? XcodeBuild { get; set; }
    public string? XcodeSelectPath { get; set; }
    public bool CommandLineTools { get; set; }
    public string? MacOsSdkVersion { get; set; }
    public string? CocoaPodsVersion { get; set; }
    public List<string> InstalledXcodes { get; set; } = new();
    public List<string> SimulatorRuntimes { get; set; } = new();
}

/// <summary>
/// Swift + the Apple toolchain (Xcode, Command Line Tools, SDKs, SwiftPM, CocoaPods).
/// Primarily macOS; Swift-only toolchains on Linux/Windows are still detected.
/// Never captures signing identities or provisioning profiles.
/// </summary>
public sealed class SwiftProvider : IEnvironmentProvider
{
    public string Id => "swift-apple";
    public string DisplayName => "Swift, Xcode, Apple SDKs, CocoaPods";
    public bool AppliesTo(OsPlatform os) => true; // Swift exists on Linux/Windows; Xcode bits gate to macOS

    public object? Capture(CaptureContext ctx)
    {
        var payload = new SwiftPayload();

        var swift = Proc.Which("swift");
        if (swift is not null)
        {
            payload.SwiftPath = swift;
            var r = Proc.Run(swift, "--version", timeoutMs: 15_000);
            if (r is not null)
            {
                var m = Regex.Match(r.Combined, @"Swift version ([0-9][0-9.]*)");
                payload.SwiftVersion = m.Success ? m.Groups[1].Value : null;
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            var xcb = Proc.Run("xcodebuild", "-version", timeoutMs: 20_000);
            if (xcb is not null && xcb.ExitCode == 0)
            {
                var xm = Regex.Match(xcb.Stdout, @"Xcode\s+([0-9][0-9.]*)");
                if (xm.Success) payload.XcodeVersion = xm.Groups[1].Value;
                var bm = Regex.Match(xcb.Stdout, @"Build version\s+(\S+)");
                if (bm.Success) payload.XcodeBuild = bm.Groups[1].Value;
            }

            var sel = Proc.Run("xcode-select", "-p", timeoutMs: 10_000);
            payload.XcodeSelectPath = sel?.ExitCode == 0 ? sel.FirstLine : null;
            payload.CommandLineTools = Directory.Exists("/Library/Developer/CommandLineTools")
                || payload.XcodeSelectPath is not null;

            var sdk = Proc.Run("xcrun", "--sdk macosx --show-sdk-version", timeoutMs: 10_000);
            if (sdk?.ExitCode == 0) payload.MacOsSdkVersion = sdk.FirstLine;

            try
            {
                if (Directory.Exists("/Applications"))
                    foreach (var app in Directory.GetDirectories("/Applications", "Xcode*.app"))
                        payload.InstalledXcodes.Add(Path.GetFileName(app));
            }
            catch { }

            var rt = Proc.Run("xcrun", "simctl list runtimes", timeoutMs: 15_000);
            if (rt?.ExitCode == 0)
                foreach (var line in rt.Stdout.Split('\n'))
                {
                    var m = Regex.Match(line.Trim(), @"^((iOS|watchOS|tvOS|xrOS|visionOS)\s+[0-9][0-9.]*)");
                    if (m.Success) payload.SimulatorRuntimes.Add(m.Groups[1].Value);
                }
        }

        var pod = Proc.Which("pod");
        if (pod is not null)
            payload.CocoaPodsVersion = Proc.Run(pod, "--version", timeoutMs: 15_000)?.FirstLine;

        bool empty = payload.SwiftVersion is null && payload.XcodeVersion is null
            && payload.CocoaPodsVersion is null && !payload.CommandLineTools;
        return empty ? null : payload;
    }

    public IEnumerable<Diff> Compare(JsonElement? a, JsonElement? b, CompareContext ctx)
    {
        var pa = Json.To<SwiftPayload>(a) ?? new SwiftPayload();
        var pb = Json.To<SwiftPayload>(b) ?? new SwiftPayload();

        foreach (var d in DiffHelp.Scalar(Id, "Swift", "Swift toolchain", pa.SwiftVersion, pb.SwiftVersion, ctx,
            Severity.Critical, Severity.High,
            "SwiftPM resolution and language-mode behavior is tied to the Swift compiler version.")) yield return d;

        foreach (var d in DiffHelp.Scalar(Id, "Xcode", "Xcode", pa.XcodeVersion, pb.XcodeVersion, ctx,
            Severity.Critical, Severity.High,
            "Different Xcode versions ship different SDKs, clang and Swift — a frequent CI-vs-local break.")) yield return d;

        if (pa.CommandLineTools != pb.CommandLineTools)
            yield return new Diff(Severity.Critical, "Xcode CLT",
                $"Command Line Tools present on {(pa.CommandLineTools ? ctx.A : ctx.B)} only",
                "Install with: xcode-select --install", Id);

        foreach (var d in DiffHelp.Scalar(Id, "Apple SDK", "macOS SDK", pa.MacOsSdkVersion, pb.MacOsSdkVersion, ctx,
            Severity.Medium, Severity.Medium)) yield return d;
        foreach (var d in DiffHelp.Scalar(Id, "Xcode", "active developer dir", pa.XcodeSelectPath, pb.XcodeSelectPath, ctx,
            Severity.Medium, Severity.Medium, "Set with: sudo xcode-select -s <path>")) yield return d;
        foreach (var d in DiffHelp.Scalar(Id, "CocoaPods", "CocoaPods", pa.CocoaPodsVersion, pb.CocoaPodsVersion, ctx,
            Severity.High, Severity.Medium)) yield return d;

        foreach (var d in DiffHelp.Sets(Id, "Simulator runtime", pa.SimulatorRuntimes, pb.SimulatorRuntimes, ctx, Severity.Low)) yield return d;
    }
}
