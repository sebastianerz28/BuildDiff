using System.Text.Json.Serialization;

namespace BuildDiff;

public sealed class Snapshot
{
    public string Machine { get; set; } = "";
    public string CapturedAt { get; set; } = "";
    public string SchemaVersion { get; set; } = "1";
    public OsInfo Os { get; set; } = new();
    public MsBuildInfo? MsBuild { get; set; }
    public List<VisualStudioInstall> VisualStudio { get; set; } = new();
    public List<string> Toolsets { get; set; } = new();
    public List<string> WindowsSdks { get; set; } = new();
    public DotnetInfo Dotnet { get; set; } = new();
    public EnvInfo Env { get; set; } = new();
    [JsonPropertyName("nuget")] public NuGetInfo NuGet { get; set; } = new();
    public Dictionary<string, ResolvedTool?> ResolvedTools { get; set; } = new();
}

public sealed class EnvInfo
{
    public List<string> Path { get; set; } = new();
    public Dictionary<string, string?> BuildRelevant { get; set; } = new();
    public Dictionary<string, string?> Other { get; set; } = new();
}

public sealed class NuGetInfo
{
    public List<NuGetSource> Sources { get; set; } = new();
    public string? GlobalPackages { get; set; }
    public List<string> Configs { get; set; } = new();
}

public sealed class NuGetSource
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class ResolvedTool
{
    public string? Path { get; set; }
    public string? Version { get; set; }
}

public sealed class OsInfo
{
    public string Version { get; set; } = "";
    public string Arch { get; set; } = "";
}

public sealed class MsBuildInfo
{
    public string? Version { get; set; }
    public string? Path { get; set; }
}

public sealed class VisualStudioInstall
{
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("installationVersion")] public string? InstallationVersion { get; set; }
    [JsonPropertyName("installationPath")] public string? InstallationPath { get; set; }
    [JsonPropertyName("productId")] public string? ProductId { get; set; }
    [JsonPropertyName("channelId")] public string? ChannelId { get; set; }
    public List<string> Components { get; set; } = new();
}

public sealed class DotnetInfo
{
    public List<string> Sdks { get; set; } = new();
    public List<string> Runtimes { get; set; } = new();
}
