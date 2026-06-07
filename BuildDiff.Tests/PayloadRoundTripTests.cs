using System.Text.Json;
using BuildDiff;
using Xunit;

namespace BuildDiff.Tests;

/// <summary>
/// Guards AOT-safety: every payload type must be registered in AppJsonContext, otherwise
/// Json.ToElement throws (here, at test time) instead of crashing at runtime under AOT.
/// </summary>
public class PayloadRoundTripTests
{
    public static IEnumerable<object[]> Payloads() => new[]
    {
        new object[] { new VsPayload() },
        new object[] { new DotnetPayload() },
        new object[] { new NuGetPayload() },
        new object[] { new NativeRuntimePayload() },
        new object[] { new PythonPayload() },
        new object[] { new SwigInfo() },
        new object[] { new EnvPayload() },
        new object[] { new NodePayload() },
        new object[] { new SwiftPayload() },
        new object[] { new CMakeCppPayload() },
        new object[] { new GoPayload() },
        new object[] { new RustPayload() },
        new object[] { new JvmPayload() },
        new object[] { new RubyPayload() },
        new object[] { new PhpPayload() },
        new object[] { new ContainerPayload() },
        new object[] { new GenericPayload() },
        new object[] { new AndroidPayload() },
        new object[] { new IaCPayload() },
        new object[] { new BazelPayload() },
    };

    [Theory]
    [MemberData(nameof(Payloads))]
    public void Every_payload_serializes_via_the_aot_context(object payload)
    {
        var el = Json.ToElement(payload);
        Assert.Equal(JsonValueKind.Object, el.ValueKind);
    }

    [Fact]
    public void Snapshot_round_trips_through_the_context_and_upgrader()
    {
        var snap = Capture.Collect();
        var json = JsonSerializer.Serialize(snap, AppJsonContext.Default.Snapshot);

        var loaded = SnapshotUpgrader.Load(json);
        Assert.Equal(snap.Providers.Count, loaded.Providers.Count);
        Assert.Equal(snap.Fingerprint!.Value, loaded.Fingerprint!.Value); // fingerprint survives serialization
    }
}
