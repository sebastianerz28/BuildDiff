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

    // ---- snapshot envelope (inert metadata) ----------------
    /// <summary>Versions the sync/fingerprint contract independently of SchemaVersion. 0 = pre-sync (upgraded).</summary>
    public int EnvelopeVersion { get; set; } = 1;
    /// <summary>The builddiff CLI version that produced this snapshot.</summary>
    public string? ToolVersion { get; set; }
    /// <summary>Stable, pseudonymous machine identity (salted hash) — the drift correlation key.</summary>
    public string? MachineId { get; set; }
    /// <summary>Content fingerprint of build-relevant state; enables dedup + O(1) drift detection.</summary>
    public Fingerprint? Fingerprint { get; set; }

    public OsInfo Os { get; set; } = new();

    /// <summary>Populated only when captured with <c>--project</c>.</summary>
    public ProjectInfo? Project { get; set; }

    /// <summary>How this snapshot was captured, so unlike captures aren't compared as drift.</summary>
    public CaptureMeta? CaptureMeta { get; set; }

    /// <summary>providerId → that provider's serialized payload.</summary>
    public Dictionary<string, JsonElement> Providers { get; set; } = new();
}

/// <summary>Self-describing content fingerprint. Value is "sha256:&lt;hex&gt;".</summary>
public sealed class Fingerprint
{
    public string Algorithm { get; set; } = "sha256";
    public string Value { get; set; } = "";
    /// <summary>Versions the hash recipe so it can evolve without corrupting historical comparisons.</summary>
    public int InputsVersion { get; set; } = 1;
}

/// <summary>Capture scope — never stores the absolute project path.</summary>
public sealed class CaptureMeta
{
    public bool ProjectRootPresent { get; set; }
    public List<string>? Only { get; set; }
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
    public List<ProjectLockfile> Lockfiles { get; set; } = new();
}

public sealed class ProjectLockfile
{
    public string File { get; set; } = "";
    public string Ecosystem { get; set; } = "";
    /// <summary>sha256 (hex) of the file contents — never the contents themselves.</summary>
    public string Hash { get; set; } = "";
    /// <summary>Format marker if cheaply extractable (lockfileVersion, BUNDLED WITH, …).</summary>
    public string? Marker { get; set; }
}

public sealed class ProjectManifest
{
    public string File { get; set; } = "";
    public string Kind { get; set; } = "";
    /// <summary>Requirement key → declared value (e.g. "node" → "20.11.0").</summary>
    public Dictionary<string, string?> Declares { get; set; } = new();
}
