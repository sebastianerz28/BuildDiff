using System.Text.Json;
using System.Text.RegularExpressions;

namespace BuildDiff;

public sealed class ContainerPayload
{
    public string? DockerClient { get; set; }
    public string? DockerServer { get; set; }
    public string? ServerOsArch { get; set; }
    public bool DaemonReachable { get; set; }
    public string? BuildxVersion { get; set; }
    public string? ComposeVersion { get; set; }
    public string? PodmanVersion { get; set; }
}

/// <summary>Container tooling: Docker (client/server), Buildx, Compose, Podman. Cross-platform.</summary>
public sealed class ContainerProvider : IEnvironmentProvider
{
    public string Id => "container-docker";
    public string DisplayName => "Docker / Podman / Compose";
    public bool AppliesTo(OsPlatform os) => true;

    public object? Capture(CaptureContext ctx)
    {
        var docker = Proc.Which("docker.exe") ?? Proc.Which("docker");
        var podman = Proc.Which("podman.exe") ?? Proc.Which("podman");
        if (docker is null && podman is null) return null;

        var p = new ContainerPayload();
        if (docker is not null)
        {
            p.DockerClient = Extract(Proc.Run(docker, "--version", timeoutMs: 10_000)?.Combined, @"version ([0-9][0-9.]*)");

            // docker info needs the daemon; short timeout so a dead daemon doesn't hang us.
            var srv = Proc.Run(docker, "info --format \"{{.ServerVersion}}|{{.OSType}}/{{.Architecture}}\"", timeoutMs: 8_000);
            if (srv is not null && srv.ExitCode == 0 && srv.FirstLine is { Length: > 0 } line && !line.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                var parts = line.Split('|');
                p.DockerServer = parts.ElementAtOrDefault(0)?.Trim();
                p.ServerOsArch = parts.ElementAtOrDefault(1)?.Trim();
                p.DaemonReachable = !string.IsNullOrWhiteSpace(p.DockerServer);
            }

            p.BuildxVersion = Extract(Proc.Run(docker, "buildx version", timeoutMs: 10_000)?.Combined, @"v?([0-9][0-9.]+)");
            p.ComposeVersion = Extract(Proc.Run(docker, "compose version", timeoutMs: 10_000)?.Combined, @"v?([0-9][0-9.]+)");
        }
        if (podman is not null)
            p.PodmanVersion = Extract(Proc.Run(podman, "--version", timeoutMs: 10_000)?.Combined, @"version ([0-9][0-9.]*)");

        return p;
    }

    public IEnumerable<Diff> Compare(JsonElement? a, JsonElement? b, CompareContext ctx)
    {
        var pa = Json.To<ContainerPayload>(a) ?? new ContainerPayload();
        var pb = Json.To<ContainerPayload>(b) ?? new ContainerPayload();

        foreach (var d in DiffHelp.Scalar(Id, "Docker", "docker client", pa.DockerClient, pb.DockerClient, ctx, Severity.Medium, Severity.Low)) yield return d;
        foreach (var d in DiffHelp.Scalar(Id, "Docker", "docker server", pa.DockerServer, pb.DockerServer, ctx, Severity.Medium, Severity.Medium)) yield return d;
        foreach (var d in DiffHelp.Scalar(Id, "Docker", "server platform", pa.ServerOsArch, pb.ServerOsArch, ctx, Severity.High, Severity.High,
            "Image builds for a different OS/arch (e.g. linux/arm64 vs linux/amd64) produce incompatible artifacts.")) yield return d;
        foreach (var d in DiffHelp.Scalar(Id, "Docker", "compose", pa.ComposeVersion, pb.ComposeVersion, ctx, Severity.Low, Severity.Low)) yield return d;
        foreach (var d in DiffHelp.Scalar(Id, "Docker", "buildx", pa.BuildxVersion, pb.BuildxVersion, ctx, Severity.Low, Severity.Low)) yield return d;
        foreach (var d in DiffHelp.Scalar(Id, "Podman", "podman", pa.PodmanVersion, pb.PodmanVersion, ctx, Severity.Low, Severity.Low)) yield return d;

        if (pa.DaemonReachable != pb.DaemonReachable)
            yield return new Diff(Severity.Medium, "Docker",
                $"docker daemon reachable on {(pa.DaemonReachable ? ctx.A : ctx.B)} only", null, Id);
    }

    private static string? Extract(string? text, string pattern)
    {
        if (text is null) return null;
        var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }
}
