using System.Text.Json;
using BuildDiff;
using Spectre.Console;

return Cli.Run(args);

internal static class Cli
{
    public static int Run(string[] args)
    {
        if (args.Length == 0) { PrintUsage(); return 1; }

        return args[0].ToLowerInvariant() switch
        {
            "capture" => DoCapture(args.Skip(1).ToArray()),
            "compare" => DoCompare(args.Skip(1).ToArray()),
            "providers" or "list" => DoProviders(),
            "-h" or "--help" or "help" => Help(),
            _ => Help(unknown: args[0]),
        };
    }

    private static int DoCapture(string[] args)
    {
        string? outPath = null;
        string? project = null;
        HashSet<string>? only = null;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if ((a == "-o" || a == "--out") && i + 1 < args.Length) outPath = args[++i];
            else if (a == "--project" && i + 1 < args.Length) project = Path.GetFullPath(args[++i]);
            else if (a == "--only" && i + 1 < args.Length)
                only = new HashSet<string>(
                    args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    StringComparer.OrdinalIgnoreCase);
        }

        AnsiConsole.MarkupLine("[grey]Capturing build-relevant environment state...[/]");
        var snap = Capture.Collect(new CaptureOptions { ProjectRoot = project, Only = only });

        outPath ??= $"builddiff-{Sanitize(snap.Machine)}.json";
        File.WriteAllText(outPath, JsonSerializer.Serialize(snap, Json.Options));

        AnsiConsole.MarkupLine(
            $"[green]Wrote[/] [bold]{Markup.Escape(outPath)}[/] " +
            $"({new FileInfo(outPath).Length:N0} bytes) — " +
            $"[cyan]{snap.Providers.Count}[/] provider section(s) on [cyan]{snap.Os.Platform}[/]");
        if (snap.Project is not null)
            AnsiConsole.MarkupLine($"[grey]  + project manifests: {snap.Project.Manifests.Count} found under {Markup.Escape(snap.Project.Root)}[/]");
        return 0;
    }

    private static int DoCompare(string[] args)
    {
        bool verbose = false;
        var positional = new List<string>();
        foreach (var arg in args)
        {
            if (arg == "-v" || arg == "--verbose") verbose = true;
            else positional.Add(arg);
        }
        if (positional.Count != 2)
        {
            AnsiConsole.MarkupLine("[red]usage: builddiff compare <A.json> <B.json> [[--verbose]][/]");
            return 1;
        }

        var a = LoadSnapshot(positional[0]);
        var b = LoadSnapshot(positional[1]);
        if (a is null || b is null) return 1;

        var diffs = Compare.Run(a, b, verbose);
        Render(diffs, a, b, verbose);
        return 0;
    }

    private static int DoProviders()
    {
        var os = Os.Current;
        AnsiConsole.MarkupLine($"[bold]BuildDiff providers[/]  (this machine: [cyan]{Os.Name(os)}[/])\n");
        foreach (var p in ProviderRegistry.All)
        {
            var applies = p.AppliesTo(os);
            var dot = applies ? "[green]●[/]" : "[grey]○[/]";
            AnsiConsole.MarkupLine($"  {dot} [bold]{Markup.Escape(p.Id)}[/]  [grey]— {Markup.Escape(p.DisplayName)}[/]");
        }
        AnsiConsole.MarkupLine("\n  [grey]● active on this OS    ○ inactive here[/]");
        return 0;
    }

    private static Snapshot? LoadSnapshot(string path)
    {
        try
        {
            return SnapshotUpgrader.Load(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to load {Markup.Escape(path)}:[/] {Markup.Escape(ex.Message)}");
            return null;
        }
    }

    private static void Render(List<Diff> diffs, Snapshot a, Snapshot b, bool verbose)
    {
        var header = new Rule($"[bold]BUILD DIFF[/]: [cyan]{Markup.Escape(a.Machine)}[/] vs [cyan]{Markup.Escape(b.Machine)}[/]")
        {
            Justification = Justify.Left,
        };
        AnsiConsole.Write(header);

        if (diffs.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No build-relevant differences detected.[/]");
            return;
        }

        int lowHidden = 0;
        foreach (var d in diffs)
        {
            if (!verbose && d.Severity == Severity.Low) { lowHidden++; continue; }
            var tag = d.Severity switch
            {
                Severity.Critical => "[bold red]CRITICAL[/]",
                Severity.High => "[red]HIGH    [/]",
                Severity.Medium => "[yellow]MEDIUM  [/]",
                Severity.Low => "[grey]LOW     [/]",
                _ => "[grey]INFO    [/]",
            };
            AnsiConsole.MarkupLine($"  {tag}  [bold]{Markup.Escape(d.Category)}[/] — {Markup.Escape(d.Message)}");
            if (d.Hint is not null)
                AnsiConsole.MarkupLine($"            [grey]→ {Markup.Escape(d.Hint)}[/]");
        }

        if (lowHidden > 0)
            AnsiConsole.MarkupLine($"  [grey]... {lowHidden} LOW/info differences hidden (--verbose to show)[/]");

        var likely = Compare.LikelyCause(diffs);
        if (likely is not null)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Panel(new Markup($"[bold]{Markup.Escape(likely)}[/]"))
            {
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(Color.Red),
            });
        }
    }

    private static string Sanitize(string s)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(s.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    private static int Help(string? unknown = null)
    {
        if (unknown is not null) AnsiConsole.MarkupLine($"[red]unknown command:[/] {Markup.Escape(unknown)}\n");
        PrintUsage();
        return unknown is null ? 0 : 1;
    }

    private static void PrintUsage()
    {
        AnsiConsole.MarkupLine("[bold]builddiff[/] — why does it build here but not there?\n");
        AnsiConsole.MarkupLine("  [bold]capture[/]  [grey][[-o file.json]] [[--project DIR]] [[--only id,id]][/]   capture this machine's build-relevant state");
        AnsiConsole.MarkupLine("  [bold]compare[/]  A.json B.json [grey][[--verbose]][/]                  diff two snapshots, ranked by likelihood of breaking the build");
        AnsiConsole.MarkupLine("  [bold]providers[/]                                          list every ecosystem BuildDiff can detect");
    }
}
