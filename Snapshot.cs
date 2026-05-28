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
