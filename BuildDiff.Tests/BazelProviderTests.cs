using System.Linq;
using BuildDiff;
using Xunit;

namespace BuildDiff.Tests;

public class BazelProviderTests
{
    private static readonly BazelProvider Provider = new();
    private static readonly CompareContext Ctx = new() { A = "A", B = "B" };

    private static System.Collections.Generic.List<Diff> Compare(BazelPayload a, BazelPayload b)
        => Provider.Compare(Json.ToElement(a), Json.ToElement(b), Ctx).ToList();

    [Fact]
    public void Unmanaged_bazel_ignoring_concrete_pin_is_critical()
    {
        var a = new BazelPayload
        {
            BazelPresent = true,
            BazelVersion = "6.5.0",
            IsBazelisk = false,
            BazelversionPin = "7.4.1", // major mismatch, raw bazel -> pin ignored
            PinIsAlias = false,
        };
        var b = new BazelPayload { BazelPresent = true, BazelVersion = "6.5.0" };

        var diffs = Compare(a, b);
        Assert.Contains(diffs, d => d.Severity == Severity.Critical
            && d.Category == "Bazel config"
            && d.Message.Contains("ignores the pin"));
    }

    [Fact]
    public void Bazel_present_on_one_side_only_is_critical()
    {
        var a = new BazelPayload { BazelPresent = true, BazelVersion = "7.4.1" };
        var b = new BazelPayload { BazelPresent = false };

        var diffs = Compare(a, b);
        Assert.Contains(diffs, d => d.Severity == Severity.Critical
            && d.Category == "Bazel"
            && d.Message.Contains("NOT FOUND on B"));
    }

    [Fact]
    public void Bazel_major_version_difference_is_critical()
    {
        var a = new BazelPayload { BazelPresent = true, BazelVersion = "7.4.1", IsBazelisk = true };
        var b = new BazelPayload { BazelPresent = true, BazelVersion = "6.5.0", IsBazelisk = true };

        var diffs = Compare(a, b);
        Assert.Contains(diffs, d => d.Severity == Severity.Critical
            && d.Category == "Bazel"
            && d.Message.Contains("MAJOR version differs"));
    }

    [Fact]
    public void Resolution_mode_difference_is_critical()
    {
        var a = new BazelPayload { ResolutionMode = "bzlmod", HasModuleBazel = true };
        var b = new BazelPayload { ResolutionMode = "workspace", HasWorkspace = true };

        var diffs = Compare(a, b);
        Assert.Contains(diffs, d => d.Severity == Severity.Critical
            && d.Category == "Bazel module"
            && d.Message.Contains("resolution mode differs"));
    }

    [Fact]
    public void Has_module_bazel_difference_is_critical()
    {
        // Same nominal resolution_mode unknown on the workspace-less side, so the
        // has_module_bazel asymmetry rule fires.
        var a = new BazelPayload { HasModuleBazel = true, ResolutionMode = "bzlmod" };
        var b = new BazelPayload { HasModuleBazel = false, ResolutionMode = "unknown" };

        var diffs = Compare(a, b);
        Assert.Contains(diffs, d => d.Severity == Severity.Critical
            && d.Category == "Bazel module");
    }

    [Fact]
    public void Same_major_different_minor_is_high()
    {
        var a = new BazelPayload { BazelPresent = true, BazelVersion = "7.4.1", IsBazelisk = true };
        var b = new BazelPayload { BazelPresent = true, BazelVersion = "7.1.0", IsBazelisk = true };

        var diffs = Compare(a, b);
        var d = Assert.Single(diffs, x => x.Category == "Bazel" && x.Message.Contains("version differs"));
        Assert.Equal(Severity.High, d.Severity);
    }

    [Fact]
    public void Use_bazel_version_env_one_side_is_high()
    {
        var a = new BazelPayload { BazelPresent = true, BazelVersion = "7.4.1", UseBazelVersionEnv = "6.0.0" };
        var b = new BazelPayload { BazelPresent = true, BazelVersion = "7.4.1" };

        var diffs = Compare(a, b);
        Assert.Contains(diffs, d => d.Severity == Severity.High
            && d.Category == "Bazelisk"
            && d.Message.Contains("USE_BAZEL_VERSION"));
    }

    [Fact]
    public void Concrete_pin_with_raw_bazel_matching_major_is_high()
    {
        // Major matches the pin so the Critical pin-ignored rule does NOT fire,
        // but raw bazel under a pin is still High.
        var a = new BazelPayload
        {
            BazelPresent = true,
            BazelVersion = "7.4.1",
            IsBazelisk = false,
            BazelversionPin = "7.4.1",
            PinIsAlias = false,
        };
        var b = new BazelPayload { BazelPresent = true, BazelVersion = "7.4.1", IsBazelisk = true };

        var diffs = Compare(a, b);
        Assert.DoesNotContain(diffs, d => d.Severity == Severity.Critical && d.Message.Contains("ignores the pin"));
        Assert.Contains(diffs, d => d.Severity == Severity.High
            && d.Category == "Bazel config"
            && d.Message.Contains("raw bazel"));
    }

    [Fact]
    public void Floating_alias_pin_is_high()
    {
        var a = new BazelPayload { BazelPresent = true, BazelVersion = "7.4.1", IsBazelisk = true, BazelversionPin = "latest", PinIsAlias = true };
        var b = new BazelPayload { BazelPresent = true, BazelVersion = "7.4.1", IsBazelisk = true };

        var diffs = Compare(a, b);
        Assert.Contains(diffs, d => d.Severity == Severity.High
            && d.Category == "Bazel config"
            && d.Message.Contains("floating .bazelversion alias"));
    }

    [Fact]
    public void Multiple_bazel_installs_is_medium()
    {
        var a = new BazelPayload
        {
            BazelPresent = true,
            BazelVersion = "7.4.1",
            IsBazelisk = true,
            BazelAllPaths = new() { "/usr/bin/bazel", "/usr/local/bin/bazel" },
        };
        var b = new BazelPayload { BazelPresent = true, BazelVersion = "7.4.1", IsBazelisk = true, BazelAllPaths = new() { "/usr/bin/bazel" } };

        var diffs = Compare(a, b);
        Assert.Contains(diffs, d => d.Severity == Severity.Medium
            && d.Category == "Bazel"
            && d.Message.Contains("multiple bazel installs"));
    }

    [Fact]
    public void Bazelisk_base_url_difference_is_medium()
    {
        var a = new BazelPayload { BazelPresent = true, BazelVersion = "7.4.1", IsBazelisk = true, BazeliskBaseUrl = "https://mirror.example.com/bazel" };
        var b = new BazelPayload { BazelPresent = true, BazelVersion = "7.4.1", IsBazelisk = true };

        var diffs = Compare(a, b);
        Assert.Contains(diffs, d => d.Severity == Severity.Medium
            && d.Category == "Bazelisk"
            && d.Message.Contains("BAZELISK_BASE_URL"));
    }

    [Fact]
    public void User_bazelrc_one_side_is_medium()
    {
        var a = new BazelPayload { BazelPresent = true, BazelVersion = "7.4.1", IsBazelisk = true, HasUserBazelrc = true };
        var b = new BazelPayload { BazelPresent = true, BazelVersion = "7.4.1", IsBazelisk = true, HasUserBazelrc = false };

        var diffs = Compare(a, b);
        Assert.Contains(diffs, d => d.Severity == Severity.Medium
            && d.Category == "Bazel config"
            && d.Message.Contains("user ~/.bazelrc present on A only"));
    }

    [Fact]
    public void Repo_bazelrc_line_count_difference_is_medium()
    {
        var a = new BazelPayload { BazelPresent = true, BazelVersion = "7.4.1", IsBazelisk = true, HasRepoBazelrc = true, RcRepoLineCount = 12 };
        var b = new BazelPayload { BazelPresent = true, BazelVersion = "7.4.1", IsBazelisk = true, HasRepoBazelrc = true, RcRepoLineCount = 7 };

        var diffs = Compare(a, b);
        Assert.Contains(diffs, d => d.Severity == Severity.Medium
            && d.Category == "Bazel config"
            && d.Message.Contains("line count differs"));
    }

    [Fact]
    public void Module_lock_one_side_is_low()
    {
        var a = new BazelPayload { HasModuleBazel = true, ResolutionMode = "bzlmod", HasModuleLock = true };
        var b = new BazelPayload { HasModuleBazel = true, ResolutionMode = "bzlmod", HasModuleLock = false };

        var diffs = Compare(a, b);
        Assert.Contains(diffs, d => d.Severity == Severity.Low
            && d.Category == "Bazel module"
            && d.Message.Contains("MODULE.bazel.lock"));
    }

    [Fact]
    public void Patch_only_under_bazelisk_pin_is_low()
    {
        var a = new BazelPayload { BazelPresent = true, BazelVersion = "7.4.1", IsBazelisk = true };
        var b = new BazelPayload { BazelPresent = true, BazelVersion = "7.4.0", IsBazelisk = true };

        var diffs = Compare(a, b);
        var d = Assert.Single(diffs, x => x.Category == "Bazel" && x.Message.Contains("version differs"));
        Assert.Equal(Severity.Low, d.Severity);
    }

    [Fact]
    public void Identical_payloads_produce_no_diffs()
    {
        var a = new BazelPayload { BazelPresent = true, BazelVersion = "7.4.1", IsBazelisk = true, ResolutionMode = "bzlmod", HasModuleBazel = true };
        var b = new BazelPayload { BazelPresent = true, BazelVersion = "7.4.1", IsBazelisk = true, ResolutionMode = "bzlmod", HasModuleBazel = true };

        Assert.Empty(Compare(a, b));
    }

    [Fact]
    public void Every_diff_carries_provider_id()
    {
        var a = new BazelPayload { BazelPresent = true, BazelVersion = "7.4.1", IsBazelisk = false, BazelversionPin = "6.0.0", PinIsAlias = false };
        var b = new BazelPayload { BazelPresent = false };

        var diffs = Compare(a, b);
        Assert.NotEmpty(diffs);
        Assert.All(diffs, d => Assert.Equal("bazel", d.ProviderId));
    }
}
