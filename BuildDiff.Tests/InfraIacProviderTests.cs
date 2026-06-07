using System.Linq;
using System.Text.Json;
using BuildDiff;
using Xunit;

namespace BuildDiff.Tests;

public class InfraIacProviderTests
{
    private static readonly InfraIacProvider Provider = new();
    private static readonly CompareContext Ctx = new() { A = "m1", B = "m2" };

    private static (JsonElement, JsonElement) Pair(IaCPayload a, IaCPayload b)
        => (Json.ToElement(a), Json.ToElement(b));

    private static System.Collections.Generic.List<Diff> Compare(IaCPayload a, IaCPayload b)
    {
        var (ea, eb) = Pair(a, b);
        return Provider.Compare(ea, eb, Ctx).ToList();
    }

    private static IaCPayload Tf(string? version, string? required = null, string? pin = null)
    {
        var p = new IaCPayload();
        if (version is not null || required is not null || pin is not null)
            p.Terraform = new TerraformInfo { Version = version, RequiredVersion = required, VersionPin = pin };
        return p;
    }

    // ---- terraform presence / version --------------------------------------

    [Fact]
    public void Terraform_present_on_one_side_only_is_critical()
    {
        var diffs = Compare(Tf("1.9.5"), Tf(null));
        var d = Assert.Single(diffs, x => x.Category == "Terraform");
        Assert.Equal(Severity.Critical, d.Severity);
        Assert.Contains("NOT FOUND on m2", d.Message);
    }

    [Fact]
    public void Terraform_major_minor_differs_is_critical()
    {
        var diffs = Compare(Tf("1.8.5"), Tf("1.9.2"));
        var d = Assert.Single(diffs, x => x.Category == "Terraform" && x.Message.Contains("version differs"));
        Assert.Equal(Severity.Critical, d.Severity);
    }

    [Fact]
    public void Terraform_patch_only_differs_is_low()
    {
        var diffs = Compare(Tf("1.9.1"), Tf("1.9.5"));
        var d = Assert.Single(diffs, x => x.Category == "Terraform" && x.Message.Contains("version differs"));
        Assert.Equal(Severity.Low, d.Severity);
    }

    // ---- opentofu mirrors terraform ----------------------------------------

    [Fact]
    public void OpenTofu_present_one_side_only_is_critical()
    {
        var a = new IaCPayload { Opentofu = new OpenTofuInfo { Version = "1.7.0" } };
        var b = new IaCPayload();
        var d = Assert.Single(Compare(a, b), x => x.Category == "OpenTofu");
        Assert.Equal(Severity.Critical, d.Severity);
    }

    [Fact]
    public void OpenTofu_minor_differs_is_critical_patch_is_low()
    {
        var aMinor = new IaCPayload { Opentofu = new OpenTofuInfo { Version = "1.7.0" } };
        var bMinor = new IaCPayload { Opentofu = new OpenTofuInfo { Version = "1.8.0" } };
        Assert.Equal(Severity.Critical, Compare(aMinor, bMinor).Single(x => x.Category == "OpenTofu").Severity);

        var aPatch = new IaCPayload { Opentofu = new OpenTofuInfo { Version = "1.7.0" } };
        var bPatch = new IaCPayload { Opentofu = new OpenTofuInfo { Version = "1.7.3" } };
        Assert.Equal(Severity.Low, Compare(aPatch, bPatch).Single(x => x.Category == "OpenTofu").Severity);
    }

    // ---- pulumi -------------------------------------------------------------

    [Fact]
    public void Pulumi_present_one_side_is_high()
    {
        var a = new IaCPayload { Pulumi = new PulumiInfo { Version = "3.120.0" } };
        var b = new IaCPayload();
        var d = Assert.Single(Compare(a, b), x => x.Category == "Pulumi");
        Assert.Equal(Severity.High, d.Severity);
    }

    [Fact]
    public void Pulumi_major_differs_is_high_minor_is_low()
    {
        var aMaj = new IaCPayload { Pulumi = new PulumiInfo { Version = "3.120.0" } };
        var bMaj = new IaCPayload { Pulumi = new PulumiInfo { Version = "4.0.0" } };
        Assert.Equal(Severity.High, Compare(aMaj, bMaj).Single(x => x.Category == "Pulumi").Severity);

        var aMin = new IaCPayload { Pulumi = new PulumiInfo { Version = "3.120.0" } };
        var bMin = new IaCPayload { Pulumi = new PulumiInfo { Version = "3.121.0" } };
        Assert.Equal(Severity.Low, Compare(aMin, bMin).Single(x => x.Category == "Pulumi").Severity);
    }

    // ---- cloud CLIs ---------------------------------------------------------

    [Fact]
    public void Cloud_cli_present_one_side_only_is_low()
    {
        var a = new IaCPayload { CloudClis = { ["kubectl"] = "1.30.0" } };
        var b = new IaCPayload();
        var d = Assert.Single(Compare(a, b), x => x.Category == "Cloud CLI");
        Assert.Equal(Severity.Low, d.Severity);
    }

    [Fact]
    public void Aws_major_differs_is_medium()
    {
        var a = new IaCPayload { CloudClis = { ["aws"] = "1.29.0" } };
        var b = new IaCPayload { CloudClis = { ["aws"] = "2.15.0" } };
        var d = Assert.Single(Compare(a, b), x => x.Category == "Cloud CLI" && x.Message.Contains("aws"));
        Assert.Equal(Severity.Medium, d.Severity);
    }

    [Fact]
    public void Helm_major_differs_is_medium()
    {
        var a = new IaCPayload { CloudClis = { ["helm"] = "2.17.0" } };
        var b = new IaCPayload { CloudClis = { ["helm"] = "3.15.0" } };
        var d = Assert.Single(Compare(a, b), x => x.Category == "Cloud CLI" && x.Message.Contains("helm"));
        Assert.Equal(Severity.Medium, d.Severity);
    }

    [Fact]
    public void Kubectl_same_major_differs_is_low()
    {
        var a = new IaCPayload { CloudClis = { ["kubectl"] = "1.29.0" } };
        var b = new IaCPayload { CloudClis = { ["kubectl"] = "1.30.0" } };
        var d = Assert.Single(Compare(a, b), x => x.Category == "Cloud CLI" && x.Message.Contains("kubectl"));
        Assert.Equal(Severity.Low, d.Severity);
    }

    // ---- project: required_version constraint -------------------------------

    [Fact]
    public void Machine_violating_required_version_pessimistic_is_high()
    {
        // ~> 1.9 means >=1.9.0 <2.0.0 ; 2.0.1 violates.
        var a = Tf("2.0.1", required: "~> 1.9");
        var b = Tf("1.9.5", required: "~> 1.9");
        var diffs = Compare(a, b);
        var bad = Assert.Single(diffs, x => x.Category == "Project requirement" && x.Message.Contains("m1"));
        Assert.Equal(Severity.High, bad.Severity);
        Assert.DoesNotContain(diffs, x => x.Category == "Project requirement" && x.Message.Contains("m2") && x.Severity == Severity.High);
    }

    [Fact]
    public void Required_version_satisfied_emits_nothing()
    {
        var a = Tf("1.9.4", required: ">= 1.8.0, < 2.0.0");
        var b = Tf("1.9.4", required: ">= 1.8.0, < 2.0.0");
        Assert.DoesNotContain(Compare(a, b), x => x.Category == "Project requirement");
    }

    [Fact]
    public void Required_version_but_terraform_absent_is_high()
    {
        var a = Tf(null, required: "~> 1.9");
        var b = Tf(null, required: "~> 1.9");
        var diffs = Compare(a, b).Where(x => x.Category == "Project requirement").ToList();
        Assert.Equal(2, diffs.Count);
        Assert.All(diffs, d => Assert.Equal(Severity.High, d.Severity));
    }

    [Fact]
    public void Unparseable_required_version_downgrades_to_low()
    {
        var a = Tf("1.9.4", required: "garbage-constraint");
        var b = Tf("1.9.4", required: "garbage-constraint");
        var diffs = Compare(a, b).Where(x => x.Category == "Project requirement").ToList();
        Assert.NotEmpty(diffs);
        Assert.All(diffs, d => Assert.Equal(Severity.Low, d.Severity));
    }

    [Fact]
    public void Pessimistic_three_part_constraint_floats_patch_only()
    {
        // ~> 1.9.2 means >=1.9.2 <1.10.0 ; 1.10.0 violates, 1.9.9 satisfies.
        Assert.Contains(Compare(Tf("1.10.0", required: "~> 1.9.2"), Tf("1.9.9", required: "~> 1.9.2")),
            x => x.Category == "Project requirement" && x.Message.Contains("m1") && x.Severity == Severity.High);
        Assert.DoesNotContain(Compare(Tf("1.9.9", required: "~> 1.9.2"), Tf("1.9.9", required: "~> 1.9.2")),
            x => x.Category == "Project requirement");
    }

    // ---- project: .terraform-version pin ------------------------------------

    [Fact]
    public void Concrete_pin_mismatch_at_precision_is_medium()
    {
        var a = Tf("1.8.5", pin: "1.9.5");
        var b = Tf("1.9.5", pin: "1.9.5");
        var diffs = Compare(a, b).Where(x => x.Category == "Project requirement").ToList();
        var bad = Assert.Single(diffs, x => x.Message.Contains("m1"));
        Assert.Equal(Severity.Medium, bad.Severity);
    }

    [Fact]
    public void Concrete_pin_but_terraform_absent_is_high()
    {
        var a = Tf(null, pin: "1.9.5");
        var b = Tf(null, pin: "1.9.5");
        var diffs = Compare(a, b).Where(x => x.Category == "Project requirement").ToList();
        Assert.Equal(2, diffs.Count);
        Assert.All(diffs, d => Assert.Equal(Severity.High, d.Severity));
    }

    [Fact]
    public void Non_concrete_pin_is_recorded_but_not_checked()
    {
        var a = Tf("1.8.5", pin: "latest");
        var b = Tf("9.9.9", pin: "latest");
        Assert.DoesNotContain(Compare(a, b), x => x.Category == "Project requirement");
    }

    [Fact]
    public void Pin_at_lower_precision_matches_higher_version()
    {
        // pin "1.9" should be satisfied by 1.9.5 at pin precision.
        var a = Tf("1.9.5", pin: "1.9");
        var b = Tf("1.9.5", pin: "1.9");
        Assert.DoesNotContain(Compare(a, b), x => x.Category == "Project requirement");
    }

    // ---- project: provider locks -------------------------------------------

    [Fact]
    public void Provider_lock_version_differs_is_high()
    {
        var a = new IaCPayload { Terraform = new TerraformInfo { ProviderLocks = { ["hashicorp/aws"] = "5.40.0" } } };
        var b = new IaCPayload { Terraform = new TerraformInfo { ProviderLocks = { ["hashicorp/aws"] = "5.50.0" } } };
        var d = Assert.Single(Compare(a, b), x => x.Category == "Provider lock");
        Assert.Equal(Severity.High, d.Severity);
    }

    [Fact]
    public void Provider_locked_one_side_only_is_medium()
    {
        var a = new IaCPayload { Terraform = new TerraformInfo { ProviderLocks = { ["hashicorp/aws"] = "5.40.0" } } };
        var b = new IaCPayload { Terraform = new TerraformInfo() };
        var d = Assert.Single(Compare(a, b), x => x.Category == "Provider lock");
        Assert.Equal(Severity.Medium, d.Severity);
    }

    // ---- informational: installed versions / manager -----------------------

    [Fact]
    public void Manager_differs_but_active_version_matches_is_low()
    {
        var a = new IaCPayload { Terraform = new TerraformInfo { Version = "1.9.5", ActiveManager = "tfenv" } };
        var b = new IaCPayload { Terraform = new TerraformInfo { Version = "1.9.5", ActiveManager = "homebrew" } };
        var d = Assert.Single(Compare(a, b), x => x.Category == "Terraform" && x.Message.Contains("manager differs"));
        Assert.Equal(Severity.Low, d.Severity);
    }

    [Fact]
    public void Identical_payloads_produce_no_diffs()
    {
        var a = Tf("1.9.5", required: ">= 1.8.0", pin: "1.9.5");
        a.Terraform!.ProviderLocks["hashicorp/aws"] = "5.40.0";
        a.CloudClis["kubectl"] = "1.30.0";
        var b = Tf("1.9.5", required: ">= 1.8.0", pin: "1.9.5");
        b.Terraform!.ProviderLocks["hashicorp/aws"] = "5.40.0";
        b.CloudClis["kubectl"] = "1.30.0";

        Assert.Empty(Compare(a, b));
    }
}
