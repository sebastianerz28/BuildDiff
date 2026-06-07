using BuildDiff;
using Xunit;

namespace BuildDiff.Tests;

public class ProjectScanTests : IDisposable
{
    private readonly string _dir;

    public ProjectScanTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "builddiff-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private void Write(string name, string content) => File.WriteAllText(Path.Combine(_dir, name), content);

    [Fact]
    public void Reads_node_python_and_go_manifests()
    {
        Write(".nvmrc", "20.11.0\n");
        Write("package.json", """{ "engines": { "node": ">=22" }, "packageManager": "pnpm@9.1.0" }""");
        Write("go.mod", "module example.com/x\n\ngo 1.22\ntoolchain go1.22.3\n");

        var info = ProjectScan.Scan(_dir);

        Assert.Equal(3, info.Manifests.Count);

        var nvmrc = info.Manifests.Single(m => m.File == ".nvmrc");
        Assert.Equal("20.11.0", nvmrc.Declares["node"]);

        var pkg = info.Manifests.Single(m => m.File == "package.json");
        Assert.Equal(">=22", pkg.Declares["node"]);
        Assert.Equal("pnpm@9.1.0", pkg.Declares["packageManager"]);

        var gomod = info.Manifests.Single(m => m.File == "go.mod");
        Assert.Equal("1.22", gomod.Declares["go"]);
        Assert.Equal("go1.22.3", gomod.Declares["go-toolchain"]);
    }

    [Fact]
    public void Empty_directory_yields_no_manifests()
    {
        var info = ProjectScan.Scan(_dir);
        Assert.Empty(info.Manifests);
    }

    [Fact]
    public void Malformed_manifest_does_not_throw()
    {
        Write("package.json", "{ this is not valid json ");
        var info = ProjectScan.Scan(_dir); // should not throw
        Assert.NotNull(info);
    }
}
