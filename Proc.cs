using System.Diagnostics;
using System.Text;

namespace BuildDiff;

public static class Proc
{
    public sealed record Result(int ExitCode, string Stdout, string Stderr);

    public static Result? Run(string fileName, string arguments, int timeoutMs = 30_000)
    {
        try
        {
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

    public static string? Which(string name)
    {
        var r = Run("where.exe", name, timeoutMs: 5_000);
        if (r is null || r.ExitCode != 0) return null;
        var first = r.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0);
        return string.IsNullOrWhiteSpace(first) ? null : first;
    }
}
