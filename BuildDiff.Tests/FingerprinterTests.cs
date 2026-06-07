using BuildDiff;
using Xunit;

namespace BuildDiff.Tests;

public class FingerprinterTests
{
    private static Snapshot Snap(string machine, string arch, params string[] sdks)
    {
        var s = new Snapshot { Machine = machine, Os = new OsInfo { Platform = "windows", Arch = arch, Version = "v" } };
        s.Providers["dotnet"] = Json.ToElement(new DotnetPayload { Sdks = sdks.ToList(), Runtimes = new() });
        return s;
    }

    private static string FP(Snapshot s) => Fingerprinter.Compute(s).Value;

    [Fact]
    public void Identical_state_hashes_identically()
        => Assert.Equal(FP(Snap("m1", "X64", "8.0.100")), FP(Snap("m2", "X64", "8.0.100")));

    [Fact]
    public void Fingerprint_is_algorithm_prefixed_hex()
    {
        var fp = Fingerprinter.Compute(Snap("m1", "X64", "8.0.100"));
        Assert.StartsWith("sha256:", fp.Value);
        Assert.Equal(1, fp.InputsVersion);
        Assert.Matches("^sha256:[0-9a-f]{64}$", fp.Value);
    }

    [Fact]
    public void Volatile_and_identity_fields_do_not_affect_the_fingerprint()
    {
        var a = Snap("box-A", "X64", "8.0.100");
        a.CapturedAt = "2025-01-01T00:00:00Z";
        a.ToolVersion = "0.2.0";
        a.MachineId = "m_aaaa";
        var b = Snap("box-B", "X64", "8.0.100");
        b.CapturedAt = "2026-06-07T12:00:00Z";
        b.ToolVersion = "9.9.9";
        b.MachineId = "m_bbbb";
        Assert.Equal(FP(a), FP(b)); // only build-relevant state feeds the hash
    }

    [Fact]
    public void Array_reordering_within_a_provider_payload_does_not_change_the_fingerprint()
        => Assert.Equal(FP(Snap("m1", "X64", "8.0.100", "10.0.100")),
                        FP(Snap("m2", "X64", "10.0.100", "8.0.100")));

    [Fact]
    public void Changing_a_real_provider_value_changes_the_fingerprint()
        => Assert.NotEqual(FP(Snap("m1", "X64", "8.0.100")), FP(Snap("m1", "X64", "10.0.100")));

    [Fact]
    public void Changing_arch_changes_the_fingerprint()
        => Assert.NotEqual(FP(Snap("m1", "X64", "8.0.100")), FP(Snap("m1", "Arm64", "8.0.100")));

    [Fact]
    public void Capture_stamps_an_envelope_and_a_fingerprint()
    {
        var snap = Capture.Collect();
        Assert.Equal(1, snap.EnvelopeVersion);
        Assert.False(string.IsNullOrEmpty(snap.ToolVersion));
        Assert.StartsWith("m_", snap.MachineId);
        Assert.NotNull(snap.Fingerprint);
        Assert.StartsWith("sha256:", snap.Fingerprint!.Value);
    }
}
