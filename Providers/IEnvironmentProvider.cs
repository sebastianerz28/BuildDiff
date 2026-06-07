using System.Text.Json;

namespace BuildDiff;

/// <summary>Everything a provider needs to capture this machine's state.</summary>
public sealed class CaptureContext
{
    public OsPlatform Os { get; init; }
    /// <summary>Set when the user passed <c>--project &lt;dir&gt;</c>; otherwise null.</summary>
    public string? ProjectRoot { get; init; }
}

/// <summary>Everything a provider needs to diff two snapshots.</summary>
public sealed class CompareContext
{
    public string A { get; init; } = "A";
    public string B { get; init; } = "B";
    public bool Verbose { get; init; }
}

/// <summary>
/// One ecosystem's worth of build-environment knowledge: how to detect it,
/// what to record, and which differences matter and how much.
/// </summary>
public interface IEnvironmentProvider
{
    /// <summary>Stable, kebab-case key used as the JSON section name. Never change once shipped.</summary>
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Whether this provider can meaningfully run on the given OS.</summary>
    bool AppliesTo(OsPlatform os);

    /// <summary>
    /// Collect this machine's state for the ecosystem. Return a serializable payload,
    /// or null if the ecosystem is entirely absent (so it leaves no JSON section).
    /// </summary>
    object? Capture(CaptureContext ctx);

    /// <summary>
    /// Diff the two stored payloads (either may be null if a side lacked the section).
    /// </summary>
    IEnumerable<Diff> Compare(JsonElement? a, JsonElement? b, CompareContext ctx);
}
