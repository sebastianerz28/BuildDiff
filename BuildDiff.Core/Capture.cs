namespace BuildDiff;

public sealed class CaptureOptions
{
    public string? ProjectRoot { get; init; }
    /// <summary>If set, only these provider ids run.</summary>
    public HashSet<string>? Only { get; init; }
}

/// <summary>
/// Orchestrates capture: runs every provider that applies to this OS and
/// stores each non-empty payload under its provider id. A provider that throws
/// is skipped, never fatal.
/// </summary>
public static class Capture
{
    public static Snapshot Collect(CaptureOptions? opts = null)
    {
        opts ??= new CaptureOptions();
        var os = Os.Current;

        var snap = new Snapshot
        {
            Machine = Environment.MachineName,
            CapturedAt = DateTime.UtcNow.ToString("O"),
            SchemaVersion = 2,
            Os = new OsInfo
            {
                Platform = Os.Name(os),
                Version = Os.Description,
                Arch = Os.Arch,
            },
        };

        var ctx = new CaptureContext { Os = os, ProjectRoot = opts.ProjectRoot };

        foreach (var provider in ProviderRegistry.All)
        {
            if (!provider.AppliesTo(os)) continue;
            if (opts.Only is not null && !opts.Only.Contains(provider.Id)) continue;
            try
            {
                var payload = provider.Capture(ctx);
                if (payload is not null)
                    snap.Providers[provider.Id] = Json.ToElement(payload);
            }
            catch { /* one provider failing must not abort the whole capture */ }
        }

        if (opts.ProjectRoot is not null)
        {
            try { snap.Project = ProjectScan.Scan(opts.ProjectRoot); } catch { }
        }

        // ---- snapshot envelope (inert metadata; nothing is transmitted) ----
        snap.EnvelopeVersion = 1;
        snap.ToolVersion = BuildInfo.ToolVersion;
        snap.MachineId = MachineIdentity.Get();
        snap.CaptureMeta = new CaptureMeta
        {
            ProjectRootPresent = opts.ProjectRoot is not null,
            Only = opts.Only?.OrderBy(x => x, StringComparer.Ordinal).ToList(),
        };
        snap.Fingerprint = Fingerprinter.Compute(snap);

        return snap;
    }
}
