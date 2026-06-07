using System.Linq;
using BuildDiff;
using Xunit;

namespace BuildDiff.Tests;

public class AndroidProviderTests
{
    private static readonly CompareContext Ctx = new() { A = "m1", B = "m2" };
    private static readonly AndroidProvider Provider = new();

    private static System.Collections.Generic.List<Diff> Compare(AndroidPayload a, AndroidPayload b)
        => Provider.Compare(Json.ToElement(a), Json.ToElement(b), Ctx).ToList();

    private static AndroidPayload Full() => new()
    {
        SdkRoot = "/opt/android",
        SdkRootSource = "env:ANDROID_HOME",
        BuildTools = new() { "34.0.0" },
        Platforms = new() { "34" },
        PlatformToolsPresent = true,
        PlatformToolsVersion = "35.0.2",
        NdkVersions = new() { "26.1.10909125" },
        LicensesAccepted = new() { ["android-sdk-license"] = true, ["android-sdk-preview-license"] = false },
    };

    [Fact]
    public void Sdk_present_on_one_side_only_is_critical()
    {
        var a = Full();
        var b = new AndroidPayload(); // no sdk_root
        var diffs = Compare(a, b);
        Assert.Contains(diffs, d => d.Severity == Severity.Critical && d.Category == "Android SDK"
            && d.Message.Contains("NOT FOUND on m2"));
    }

    [Fact]
    public void Build_tools_present_vs_none_is_critical()
    {
        var a = Full();
        var b = Full();
        b.BuildTools = new();
        var diffs = Compare(a, b);
        var d = Assert.Single(diffs, x => x.Category == "Android build-tools");
        Assert.Equal(Severity.Critical, d.Severity);
        Assert.Contains("NONE on m2", d.Message);
    }

    [Fact]
    public void Build_tools_differ_both_present_is_high()
    {
        var a = Full();
        a.BuildTools = new() { "34.0.0" };
        var b = Full();
        b.BuildTools = new() { "33.0.1" };
        var diffs = Compare(a, b);
        Assert.All(diffs.Where(x => x.Category == "Android build-tools"),
            x => Assert.Equal(Severity.High, x.Severity));
        Assert.Contains(diffs, x => x.Category == "Android build-tools" && x.Message.Contains("34.0.0"));
        Assert.Contains(diffs, x => x.Category == "Android build-tools" && x.Message.Contains("33.0.1"));
    }

    [Fact]
    public void Platforms_present_vs_none_is_critical()
    {
        var a = Full();
        var b = Full();
        b.Platforms = new();
        var diffs = Compare(a, b);
        var d = Assert.Single(diffs, x => x.Category == "Android platform");
        Assert.Equal(Severity.Critical, d.Severity);
    }

    [Fact]
    public void Platforms_differ_both_present_is_high()
    {
        var a = Full();
        a.Platforms = new() { "34" };
        var b = Full();
        b.Platforms = new() { "33" };
        var diffs = Compare(a, b);
        Assert.Contains(diffs, x => x.Category == "Android platform");
        Assert.All(diffs.Where(x => x.Category == "Android platform"),
            x => Assert.Equal(Severity.High, x.Severity));
    }

    [Fact]
    public void Licenses_accepted_differs_is_high()
    {
        var a = Full();
        a.LicensesAccepted = new() { ["android-sdk-license"] = true };
        var b = Full();
        b.LicensesAccepted = new() { ["android-sdk-license"] = false };
        var diffs = Compare(a, b);
        var d = Assert.Single(diffs, x => x.Category == "Android SDK license");
        Assert.Equal(Severity.High, d.Severity);
        Assert.Contains("blocks auto-download", d.Message);
    }

    [Fact]
    public void Platform_tools_version_differs_is_high()
    {
        var a = Full();
        a.PlatformToolsVersion = "35.0.2";
        var b = Full();
        b.PlatformToolsVersion = "34.0.5";
        var diffs = Compare(a, b);
        var d = Assert.Single(diffs, x => x.Category == "Android platform-tools");
        Assert.Equal(Severity.High, d.Severity);
    }

    [Fact]
    public void Platform_tools_present_differs_is_high()
    {
        var a = Full();
        a.PlatformToolsPresent = true;
        a.PlatformToolsVersion = "35.0.2";
        var b = Full();
        b.PlatformToolsPresent = false;
        b.PlatformToolsVersion = null;
        var diffs = Compare(a, b);
        var d = Assert.Single(diffs, x => x.Category == "Android platform-tools");
        Assert.Equal(Severity.High, d.Severity);
        Assert.Contains("MISSING on m2", d.Message);
    }

    [Fact]
    public void Ndk_versions_differ_both_present_is_high()
    {
        var a = Full();
        a.NdkVersions = new() { "26.1.10909125" };
        var b = Full();
        b.NdkVersions = new() { "25.2.9519653" };
        var diffs = Compare(a, b);
        Assert.Contains(diffs, x => x.Category == "Android NDK");
        Assert.All(diffs.Where(x => x.Category == "Android NDK"),
            x => Assert.Equal(Severity.High, x.Severity));
    }

    [Fact]
    public void Env_home_and_sdk_root_diverge_is_high()
    {
        var a = Full();
        a.AndroidHomeEnv = "/opt/a";
        a.AndroidSdkRootEnv = "/opt/b";
        var b = Full();
        b.AndroidHomeEnv = "/opt/same";
        b.AndroidSdkRootEnv = "/opt/same";
        var diffs = Compare(a, b);
        var d = Assert.Single(diffs, x => x.Message.Contains("ANDROID_HOME and ANDROID_SDK_ROOT differ"));
        Assert.Equal(Severity.High, d.Severity);
        Assert.Contains("m1", d.Message);
    }

    [Fact]
    public void Ndk_env_outside_sdk_on_one_side_is_medium()
    {
        var a = Full();
        a.NdkEnvOutsideSdk = true;
        var b = Full();
        b.NdkEnvOutsideSdk = false;
        var diffs = Compare(a, b);
        var d = Assert.Single(diffs, x => x.Message.Contains("NDK env path points outside"));
        Assert.Equal(Severity.Medium, d.Severity);
    }

    [Fact]
    public void Sdk_root_source_differs_is_medium()
    {
        var a = Full();
        a.SdkRootSource = "env:ANDROID_HOME";
        var b = Full();
        b.SdkRootSource = "local.properties";
        var diffs = Compare(a, b);
        var d = Assert.Single(diffs, x => x.Message.Contains("sdk_root source"));
        Assert.Equal(Severity.Medium, d.Severity);
    }

    [Fact]
    public void Emulator_version_differs_is_low()
    {
        var a = Full();
        a.EmulatorPresent = true;
        a.EmulatorVersion = "35.1.4";
        var b = Full();
        b.EmulatorPresent = true;
        b.EmulatorVersion = "34.2.16";
        var diffs = Compare(a, b);
        var d = Assert.Single(diffs, x => x.Category == "Android emulator");
        Assert.Equal(Severity.Low, d.Severity);
    }

    [Fact]
    public void Sdk_root_path_string_differs_is_low()
    {
        var a = Full();
        a.SdkRoot = "/opt/android-a";
        var b = Full();
        b.SdkRoot = "/opt/android-b";
        var diffs = Compare(a, b);
        var d = Assert.Single(diffs, x => x.Category == "Android SDK" && x.Message.Contains("sdk_root path differs"));
        Assert.Equal(Severity.Low, d.Severity);
    }

    [Fact]
    public void Identical_payloads_produce_no_diffs()
    {
        var diffs = Compare(Full(), Full());
        Assert.Empty(diffs);
    }
}
