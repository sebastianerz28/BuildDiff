using BuildDiff;
using Xunit;

namespace BuildDiff.Tests;

public class LockfileTests : IDisposable
{
    private readonly string _dir;

    public LockfileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "builddiff-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static Snapshot SnapWithLocks(string machine, params ProjectLockfile[] locks)
    {
        var s = new Snapshot { Machine = machine, Os = new OsInfo { Platform = "linux", Arch = "X64", Version = "v" } };
        s.Project = new ProjectInfo { Root = "/repo" };
        s.Project.Lockfiles.AddRange(locks);
        return s;
    }

    private static ProjectLockfile Lock(string file, string hash, string? marker = null)
        => new() { File = file, Ecosystem = "node", Hash = hash, Marker = marker };

    [Fact]
    public void Differing_lockfile_hash_is_high()
    {
        var a = SnapWithLocks("m1", Lock("package-lock.json", "aaaa", "3"));
        var b = SnapWithLocks("m2", Lock("package-lock.json", "bbbb", "3"));
        var diffs = Compare.Run(a, b);
        Assert.Contains(diffs, d => d.Category == "Lockfile" && d.Severity == Severity.High
            && d.Message.Contains("package-lock.json content differs"));
    }

    [Fact]
    public void Differing_lockfile_format_marker_is_high()
    {
        var a = SnapWithLocks("m1", Lock("yarn.lock", "aaaa", "v1"));
        var b = SnapWithLocks("m2", Lock("yarn.lock", "bbbb", "berry"));
        var diffs = Compare.Run(a, b);
        Assert.Contains(diffs, d => d.Category == "Lockfile" && d.Severity == Severity.High
            && d.Message.Contains("format differs"));
    }

    [Fact]
    public void Lockfile_present_on_one_side_is_medium()
    {
        var a = SnapWithLocks("m1", Lock("Cargo.lock", "aaaa"));
        var b = SnapWithLocks("m2");
        var diffs = Compare.Run(a, b);
        Assert.Contains(diffs, d => d.Category == "Lockfile" && d.Severity == Severity.Medium
            && d.Message.Contains("Cargo.lock present on m1 only"));
    }

    [Fact]
    public void Competing_node_lockfiles_flagged_as_ambiguous()
    {
        var a = SnapWithLocks("m1", Lock("package-lock.json", "a"), Lock("yarn.lock", "b"));
        var b = SnapWithLocks("m2", Lock("package-lock.json", "a"), Lock("yarn.lock", "b"));
        var diffs = Compare.Run(a, b);
        Assert.Contains(diffs, d => d.Category == "Lockfile"
            && d.Message.Contains("competing Node lockfiles"));
    }

    [Fact]
    public void ProjectScan_hashes_a_real_lockfile_and_reads_its_marker()
    {
        File.WriteAllText(Path.Combine(_dir, "package-lock.json"), """{ "name": "x", "lockfileVersion": 3, "packages": {} }""");
        var info = ProjectScan.Scan(_dir);

        var lf = Assert.Single(info.Lockfiles, l => l.File == "package-lock.json");
        Assert.Equal("node", lf.Ecosystem);
        Assert.Equal(64, lf.Hash.Length);                 // sha256 hex
        Assert.Matches("^[0-9a-f]+$", lf.Hash);
        Assert.Equal("3", lf.Marker);
    }
}
