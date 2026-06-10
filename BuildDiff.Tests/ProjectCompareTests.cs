using BuildDiff;
using Xunit;

namespace BuildDiff.Tests;

public class ProjectCompareTests
{
    private static Snapshot WithProject(string machine, string file, string key, string? declared)
    {
        var s = new Snapshot { Machine = machine, Os = new OsInfo { Platform = "linux", Arch = "X64", Version = "v" } };
        var manifest = new ProjectManifest { File = file, Kind = file.TrimStart('.') };
        manifest.Declares[key] = declared;
        s.Project = new ProjectInfo { Root = "/repo", Manifests = { manifest } };
        return s;
    }

    [Fact]
    public void Flags_machine_that_does_not_satisfy_a_concrete_node_pin()
    {
        var a = WithProject("m1", ".nvmrc", "node", "20.11.0");
        a.Providers["javascript-node"] = Json.ToElement(new NodePayload { NodeVersion = "25.8.2", Abi = "141" });
        var b = new Snapshot { Machine = "m2", Os = new OsInfo { Platform = "linux", Arch = "X64", Version = "v" } };

        var diffs = Compare.Run(a, b);
        Assert.Contains(diffs, d => d.Category == "Project requirement"
            && d.Message.Contains("m1: node 25.8.2 does not satisfy declared 20.11.0"));
    }

    [Fact]
    public void Does_not_flag_when_machine_satisfies_the_pin_precision()
    {
        var a = WithProject("m1", ".nvmrc", "node", "20"); // pin only the major
        a.Providers["javascript-node"] = Json.ToElement(new NodePayload { NodeVersion = "20.11.0", Abi = "115" });
        var b = new Snapshot { Machine = "m2", Os = new OsInfo { Platform = "linux", Arch = "X64", Version = "v" } };

        var diffs = Compare.Run(a, b);
        Assert.DoesNotContain(diffs, d => d.Category == "Project requirement");
    }

    [Fact]
    public void Skips_non_concrete_range_pins()
    {
        var a = WithProject("m1", "package.json", "node", ">=22");
        a.Providers["javascript-node"] = Json.ToElement(new NodePayload { NodeVersion = "18.0.0", Abi = "108" });
        var b = new Snapshot { Machine = "m2", Os = new OsInfo { Platform = "linux", Arch = "X64", Version = "v" } };

        var diffs = Compare.Run(a, b);
        Assert.DoesNotContain(diffs, d => d.Category == "Project requirement");
    }

    [Fact]
    public void Flags_declared_tool_that_is_entirely_absent()
    {
        var a = WithProject("m1", ".python-version", "python", "3.11.0"); // no python provider captured
        var b = new Snapshot { Machine = "m2", Os = new OsInfo { Platform = "linux", Arch = "X64", Version = "v" } };

        var diffs = Compare.Run(a, b);
        Assert.Contains(diffs, d => d.Category == "Project requirement"
            && d.Severity == Severity.High
            && d.Message.Contains("no python was detected"));
    }

    [Fact]
    public void Python_pin_precision_checks_major_minor()
    {
        var a = WithProject("m1", ".python-version", "python", "3.11");
        a.Providers["python"] = Json.ToElement(new PythonPayload { Envs = { new PythonEnv { Version = "3.14.3" } } });
        var b = new Snapshot { Machine = "m2", Os = new OsInfo { Platform = "linux", Arch = "X64", Version = "v" } };

        var diffs = Compare.Run(a, b);
        Assert.Contains(diffs, d => d.Category == "Project requirement"
            && d.Message.Contains("python 3.14.3 does not satisfy declared 3.11"));
    }

    [Fact]
    public void Java_8_pin_is_satisfied_by_the_legacy_1_8_scheme()
    {
        var a = WithProject("m1", ".java-version", "java", "8");
        a.Providers["jvm"] = Json.ToElement(new JvmPayload { JavaVersion = "1.8.0_452" });
        var b = new Snapshot { Machine = "m2", Os = new OsInfo { Platform = "linux", Arch = "X64", Version = "v" } };

        var diffs = Compare.Run(a, b);
        Assert.DoesNotContain(diffs, d => d.Category == "Project requirement"); // "8" matches 1.8.0_452
    }

    [Fact]
    public void Pin_is_satisfied_when_any_installed_interpreter_matches()
    {
        var a = WithProject("m1", ".python-version", "python", "3.11");
        a.Providers["python"] = Json.ToElement(new PythonPayload
        {
            Envs = { new PythonEnv { Version = "3.12.0" }, new PythonEnv { Version = "3.11.5" } },
        });
        var b = new Snapshot { Machine = "m2", Os = new OsInfo { Platform = "linux", Arch = "X64", Version = "v" } };

        var diffs = Compare.Run(a, b);
        Assert.DoesNotContain(diffs, d => d.Category == "Project requirement"); // 3.11 present as a non-first env
    }
}
