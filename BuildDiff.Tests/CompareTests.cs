using BuildDiff;
using Xunit;

namespace BuildDiff.Tests;

public class CompareTests
{
    private static Snapshot Snap(string machine, string platform = "windows")
        => new() { Machine = machine, Os = new OsInfo { Platform = platform, Arch = "X64", Version = "v" } };

    [Fact]
    public void No_dotnet_sdk_on_one_side_is_critical()
    {
        var a = Snap("m1");
        a.Providers["dotnet"] = Json.ToElement(new DotnetPayload { CliVersion = "10", Sdks = new() { "10.0.100" }, Runtimes = new() { "r" } });
        var b = Snap("m2");
        b.Providers["dotnet"] = Json.ToElement(new DotnetPayload { CliVersion = "10", Sdks = new(), Runtimes = new() { "r" } });

        var diffs = Compare.Run(a, b);
        Assert.Contains(diffs, d => d.Severity == Severity.Critical && d.Category == ".NET SDK" && d.Message.Contains("no .NET SDK on m2"));
    }

    [Fact]
    public void Extra_dotnet_sdk_version_is_only_medium()
    {
        var a = Snap("m1");
        a.Providers["dotnet"] = Json.ToElement(new DotnetPayload { Sdks = new() { "8.0.100", "10.0.100" }, Runtimes = new() });
        var b = Snap("m2");
        b.Providers["dotnet"] = Json.ToElement(new DotnetPayload { Sdks = new() { "8.0.100" }, Runtimes = new() });

        var diffs = Compare.Run(a, b);
        var sdkDiff = Assert.Single(diffs, d => d.Category == ".NET SDK");
        Assert.Equal(Severity.Medium, sdkDiff.Severity);
    }

    [Fact]
    public void Node_major_version_mismatch_is_critical_same_major_is_high()
    {
        Severity NodeSeverity(string va, string vb)
        {
            var a = Snap("m1");
            a.Providers["javascript-node"] = Json.ToElement(new NodePayload { NodeVersion = va, Abi = "141" });
            var b = Snap("m2");
            b.Providers["javascript-node"] = Json.ToElement(new NodePayload { NodeVersion = vb, Abi = "141" });
            return Compare.Run(a, b).Single(d => d.Category == "Node" && d.Message.Contains("node version differs")).Severity;
        }

        Assert.Equal(Severity.Critical, NodeSeverity("25.8.2", "20.11.0"));
        Assert.Equal(Severity.High, NodeSeverity("25.8.2", "25.7.0"));
    }

    [Fact]
    public void Diffs_are_sorted_by_severity_and_likely_cause_summarizes_criticals()
    {
        var a = Snap("m1");
        a.Providers["dotnet"] = Json.ToElement(new DotnetPayload { Sdks = new() { "10.0.100" }, Runtimes = new() });
        var b = Snap("m2");
        b.Providers["dotnet"] = Json.ToElement(new DotnetPayload { Sdks = new(), Runtimes = new() });

        var diffs = Compare.Run(a, b);
        for (int i = 1; i < diffs.Count; i++)
            Assert.True((int)diffs[i - 1].Severity <= (int)diffs[i].Severity, "diffs must be severity-ordered");

        var likely = Compare.LikelyCause(diffs);
        Assert.NotNull(likely);
        Assert.Contains(".net sdk", likely!, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Platform_difference_is_reported()
    {
        var diffs = Compare.Run(Snap("m1", "windows"), Snap("m2", "linux"));
        Assert.Contains(diffs, d => d.Category == "OS" && d.Message.Contains("Platform differs"));
    }
}
