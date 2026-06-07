using BuildDiff;
using Xunit;

namespace BuildDiff.Tests;

public class RedactionTests
{
    [Theory]
    [InlineData("https://user:pat@pkgs.dev.azure.com/org/_packaging/feed/nuget/v3/index.json",
                "https://<redacted>@pkgs.dev.azure.com/org/_packaging/feed/nuget/v3/index.json")]
    [InlineData("https://token@nuget.example.com/v3/index.json",
                "https://<redacted>@nuget.example.com/v3/index.json")]
    [InlineData("https://api.nuget.org/v3/index.json",
                "https://api.nuget.org/v3/index.json")] // no credentials -> unchanged
    public void NuGet_feed_urls_have_embedded_credentials_stripped(string input, string expected)
        => Assert.Equal(expected, NuGetProvider.RedactCredentials(input));

    [Theory]
    [InlineData("https://user:secret@host/path", true)]
    [InlineData("Server=db;Password=hunter2;", true)]
    [InlineData("pwd=abc123", true)]
    [InlineData("/usr/local/bin:/opt/tools/bin", false)]
    [InlineData("-O2 -Wall", false)]
    [InlineData(null, false)]
    public void Value_secret_heuristic_flags_credential_shaped_values(string? value, bool expected)
        => Assert.Equal(expected, EnvironmentProvider.LooksSecretValue(value));
}
