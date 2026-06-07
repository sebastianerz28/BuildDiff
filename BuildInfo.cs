using System.Reflection;

namespace BuildDiff;

/// <summary>The CLI's own version, read from the assembly (csproj &lt;Version&gt;).</summary>
public static class BuildInfo
{
    public static string ToolVersion { get; } = Resolve();

    private static string Resolve()
    {
        try
        {
            var asm = typeof(BuildInfo).Assembly;
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                var plus = info.IndexOf('+'); // strip "+<commit>" source-link suffix
                return plus >= 0 ? info[..plus] : info;
            }
            return asm.GetName().Version?.ToString() ?? "0.0.0";
        }
        catch { return "0.0.0"; }
    }
}
