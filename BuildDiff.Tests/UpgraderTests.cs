using BuildDiff;
using Xunit;

namespace BuildDiff.Tests;

public class UpgraderTests
{
    // A representative schema-v1 snapshot: note VS install metadata is camelCase
    // (as v1 wrote it) while the rest is snake_case.
    private const string V1Json = """
    {
      "machine": "old-box",
      "captured_at": "2025-01-01T00:00:00Z",
      "schema_version": "1",
      "os": { "version": "Windows 10", "arch": "X64" },
      "visual_studio": [
        {
          "displayName": "Visual Studio 2022",
          "installationVersion": "17.9.0",
          "installationPath": "C:\\VS",
          "components": ["Microsoft.VisualStudio.Component.VC.Tools.x86.x64"]
        }
      ],
      "toolsets": ["MSVC 14.39.33519"],
      "windows_sdks": ["SDK 10.0.22621.0"],
      "msbuild": { "version": "17.9.0", "path": "C:\\msbuild.exe" },
      "dotnet": { "sdks": ["8.0.100 [C:\\sdk]"], "runtimes": ["Microsoft.NETCore.App 8.0.0 [C:\\rt]"] },
      "swig": { "path": "C:\\swig.exe", "version": "4.1.1", "on_path": true },
      "python": [ { "path": "C:\\py.exe", "version": "3.11.0", "in_virtual_env": false } ],
      "native_deps": [ { "name": "msvcp140.dll", "path": "C:\\msvcp140.dll", "file_version": "14.39" } ]
    }
    """;

    [Fact]
    public void Upgrades_v1_to_v2_with_expected_provider_sections()
    {
        var snap = SnapshotUpgrader.Load(V1Json);

        Assert.Equal(2, snap.SchemaVersion);
        Assert.Equal("old-box", snap.Machine);
        Assert.Equal("windows", snap.Os.Platform);
        Assert.Contains("visual-studio", snap.Providers.Keys);
        Assert.Contains("dotnet", snap.Providers.Keys);
        Assert.Contains("swig", snap.Providers.Keys);
        Assert.Contains("python", snap.Providers.Keys);
        Assert.Contains("native-runtime", snap.Providers.Keys);
    }

    [Fact]
    public void Upgrades_camelCase_vs_install_metadata_without_losing_fields()
    {
        var snap = SnapshotUpgrader.Load(V1Json);
        var vs = Json.To<VsPayload>(snap.Providers["visual-studio"]);

        Assert.NotNull(vs);
        var install = Assert.Single(vs!.Installs);
        // This is the regression guard for the camelCase->snake_case upgrade fix.
        Assert.Equal("Visual Studio 2022", install.DisplayName);
        Assert.Equal("17.9.0", install.InstallationVersion);
        Assert.Contains("Microsoft.VisualStudio.Component.VC.Tools.x86.x64", install.Components);
        Assert.Contains("MSVC 14.39.33519", vs.Toolsets);
        Assert.Equal("17.9.0", vs.MsBuild?.Version);
    }

    [Fact]
    public void Upgraded_dotnet_section_round_trips()
    {
        var snap = SnapshotUpgrader.Load(V1Json);
        var dn = Json.To<DotnetPayload>(snap.Providers["dotnet"]);
        Assert.NotNull(dn);
        Assert.Contains("8.0.100 [C:\\sdk]", dn!.Sdks);
    }

    [Fact]
    public void Roundtrips_a_native_v2_snapshot_unchanged()
    {
        var original = new Snapshot { Machine = "v2box", Os = new OsInfo { Platform = "linux", Arch = "Arm64" } };
        original.Providers["go"] = Json.ToElement(new GoPayload { Version = "1.22.3" });
        var json = System.Text.Json.JsonSerializer.Serialize(original, AppJsonContext.Default.Snapshot);

        var loaded = SnapshotUpgrader.Load(json);
        Assert.Equal(2, loaded.SchemaVersion);
        Assert.Equal("v2box", loaded.Machine);
        Assert.Contains("go", loaded.Providers.Keys);
    }
}
