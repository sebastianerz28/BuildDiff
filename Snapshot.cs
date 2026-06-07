using System.Text.Json;

namespace BuildDiff;

/// <summary>
/// Schema v2: a slim core plus an open map of provider-owned payloads.
/// Adding an ecosystem never changes this type — it just adds a key under
/// <see cref="Providers"/>. Old snapshots upgrade in via <see cref="SnapshotUpgrader"/>.
/// </summary>
public sealed class Snapshot
{
    public string Machine { get; set; } = "";
    public string CapturedAt { get; set; } = "";
    public int SchemaVersion { get; set; } = 2;
    public OsInfo Os { get; set; } = new();

    /// <summary>Populated only when captured with <c>--project</c>.</summary>
    public ProjectInfo? Project { get; set; }

    /// <summary>providerId → that provider's serialized payload.</summary>
    public Dictionary<string, JsonElement> Providers { get; set; } = new();
}

public sealed class OsInfo
{
    public string Platform { get; set; } = "";   // windows | macos | linux
    public string Version { get; set; } = "";
    public string Arch { get; set; } = "";
}

public sealed class ProjectInfo
{
    public string Root { get; set; } = "";
    public List<ProjectManifest> Manifests { get; set; } = new();
}

public sealed class ProjectManifest
{
    public string File { get; set; } = "";
    public string Kind { get; set; } = "";
    /// <summary>Requirement key → declared value (e.g. "node" → "20.11.0").</summary>
    public Dictionary<string, string?> Declares { get; set; } = new();
}
