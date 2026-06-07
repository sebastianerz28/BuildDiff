using System.Runtime.InteropServices;

namespace BuildDiff;

public enum OsPlatform { Windows, MacOS, Linux }

/// <summary>Single source of truth for which OS we are running on.</summary>
public static class Os
{
    public static OsPlatform Current { get; } =
        OperatingSystem.IsWindows() ? OsPlatform.Windows :
        OperatingSystem.IsMacOS() ? OsPlatform.MacOS :
        OsPlatform.Linux;

    public static string Name(OsPlatform os) => os switch
    {
        OsPlatform.Windows => "windows",
        OsPlatform.MacOS => "macos",
        _ => "linux",
    };

    public static string Arch => RuntimeInformation.OSArchitecture.ToString();
    public static string Description => RuntimeInformation.OSDescription;
}
