using System.Diagnostics;
using System.Text;

namespace BuildDiff;

/// <summary>
/// Cross-platform subprocess + executable resolution. No reliance on
/// Windows-only tools like <c>where.exe</c>; <see cref="WhichAll"/> walks PATH
/// directly, honoring PATHEXT on Windows and the executable bit on Unix.
/// </summary>
public static class Proc
{
    public sealed record Result(int ExitCode, string Stdout, string Stderr)
    {
        public string Combined => (Stdout + "\n" + Stderr).Trim();
        public string? FirstLine => Combined
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0);
    }

    public static Result? Run(string fileName, string arguments, int timeoutMs = 30_000, string? workingDir = null)
    {
        try
        {
            // On Windows, .cmd/.bat shims (npm, pnpm, yarn, gradle...) cannot be
            // launched directly with UseShellExecute=false — route them through cmd.
            if (OperatingSystem.IsWindows() &&
                (fileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                 fileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
            {
                arguments = $"/c \"\"{fileName}\" {arguments}\"";
                fileName = "cmd.exe";
            }

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            if (workingDir is not null) psi.WorkingDirectory = workingDir;

            using var p = Process.Start(psi);
            if (p is null) return null;
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            return new Result(p.ExitCode, stdoutTask.Result, stderrTask.Result);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>First match for <paramref name="name"/> on PATH, or null.</summary>
    public static string? Which(string name) => WhichAll(name).FirstOrDefault();

    /// <summary>Every match for <paramref name="name"/> on PATH, in PATH order, de-duplicated.</summary>
    public static IReadOnlyList<string> WhichAll(string name)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        var candidates = NameCandidates(name);

        foreach (var rawDir in dirs)
        {
            string dir;
            try { dir = rawDir.Trim().Trim('"'); } catch { continue; }
            if (dir.Length == 0) continue;
            foreach (var cand in candidates)
            {
                string full;
                try { full = Path.Combine(dir, cand); } catch { continue; }
                try
                {
                    if (File.Exists(full) && IsExecutable(full) && seen.Add(full))
                        results.Add(full);
                }
                catch { }
            }
        }
        return results;
    }

    private static List<string> NameCandidates(string name)
    {
        var list = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            var pathext = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries);
            bool hasExeExt = pathext.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
            if (hasExeExt)
            {
                list.Add(name);
            }
            else
            {
                // An extensionless name is not directly runnable on Windows — only
                // PATHEXT variants are (this avoids grabbing Unix-shim files like
                // `npm` that sit next to the real `npm.cmd`).
                foreach (var ext in pathext) list.Add(name + ext.ToLowerInvariant());
                if (Path.HasExtension(name)) list.Add(name); // e.g. composer.phar, literal last
            }
        }
        else
        {
            // Allow callers to pass Windows-style names like "dotnet.exe".
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                list.Add(name[..^4]);
            list.Add(name);
        }
        return list;
    }

    private static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return true;
        try
        {
            var mode = File.GetUnixFileMode(path);
            return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
        }
        catch { return true; }
    }
}
