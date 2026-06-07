using System.Text.Json;
using System.Text.RegularExpressions;

namespace BuildDiff;

public sealed class JvmPayload
{
    public string? JavaVersion { get; set; }
    public string? JavaVendor { get; set; }
    public string? JavaHome { get; set; }
    public string? JavacVersion { get; set; }
    public string? GradleVersion { get; set; }
    public string? MavenVersion { get; set; }
    public string? KotlinVersion { get; set; }
    public List<string> InstalledJdks { get; set; } = new();
}

/// <summary>JVM stack: JDK(s), Gradle, Maven, Kotlin. Cross-platform.</summary>
public sealed class JvmProvider : IEnvironmentProvider
{
    public string Id => "jvm";
    public string DisplayName => "Java/JDK, Gradle, Maven, Kotlin";
    public bool AppliesTo(OsPlatform os) => true;

    public object? Capture(CaptureContext ctx)
    {
        var java = Proc.Which("java.exe") ?? Proc.Which("java");
        var p = new JvmPayload { JavaHome = Environment.GetEnvironmentVariable("JAVA_HOME") };

        if (java is not null)
        {
            var r = Proc.Run(java, "-version", timeoutMs: 15_000);
            if (r is not null)
            {
                var m = Regex.Match(r.Combined, "version \"([^\"]+)\"");
                if (m.Success) p.JavaVersion = m.Groups[1].Value;
                p.JavaVendor = DetectVendor(r.Combined);
            }
        }

        var javac = Proc.Which("javac.exe") ?? Proc.Which("javac");
        if (javac is not null)
            p.JavacVersion = Extract(Proc.Run(javac, "-version", timeoutMs: 15_000)?.Combined, @"javac ([0-9][0-9._]*)");

        p.GradleVersion = Extract(Tool("gradle", "-v")?.Combined, @"Gradle ([0-9][0-9.]*)");
        p.MavenVersion = Extract(Tool("mvn", "-v")?.Combined, @"Apache Maven ([0-9][0-9.]*)");
        p.KotlinVersion = Extract(Tool("kotlinc", "-version")?.Combined, @"kotlinc-jvm ([0-9][0-9.]*)");

        if (OperatingSystem.IsMacOS())
        {
            var jh = Proc.Run("/usr/libexec/java_home", "-V", timeoutMs: 10_000);
            if (jh is not null)
                foreach (var line in jh.Combined.Split('\n'))
                {
                    var m = Regex.Match(line.Trim(), @"^([0-9][0-9._]*)\s+\(");
                    if (m.Success) p.InstalledJdks.Add(m.Groups[1].Value);
                }
        }

        bool empty = p.JavaVersion is null && p.GradleVersion is null && p.MavenVersion is null && p.KotlinVersion is null;
        return empty ? null : p;
    }

    public IEnumerable<Diff> Compare(JsonElement? a, JsonElement? b, CompareContext ctx)
    {
        var pa = Json.To<JvmPayload>(a) ?? new JvmPayload();
        var pb = Json.To<JvmPayload>(b) ?? new JvmPayload();

        bool aHas = !string.IsNullOrEmpty(pa.JavaVersion), bHas = !string.IsNullOrEmpty(pb.JavaVersion);
        if (aHas && !bHas) yield return new Diff(Severity.Critical, "Java", $"java present on {ctx.A} ({pa.JavaVersion}) but NOT FOUND on {ctx.B}", null, Id);
        else if (bHas && !aHas) yield return new Diff(Severity.Critical, "Java", $"java present on {ctx.B} ({pb.JavaVersion}) but NOT FOUND on {ctx.A}", null, Id);
        else if (aHas && bHas && !string.Equals(pa.JavaVersion, pb.JavaVersion, StringComparison.Ordinal))
        {
            var sameMajor = string.Equals(JavaMajor(pa.JavaVersion), JavaMajor(pb.JavaVersion), StringComparison.Ordinal);
            yield return new Diff(sameMajor ? Severity.High : Severity.Critical, "Java",
                $"java version differs: {ctx.A}={pa.JavaVersion}, {ctx.B}={pb.JavaVersion}",
                sameMajor ? null : "Different JDK MAJOR versions emit/expect different bytecode levels — a classic build/runtime break.", Id);
        }

        foreach (var d in DiffHelp.Scalar(Id, "Java", "java vendor", pa.JavaVendor, pb.JavaVendor, ctx, Severity.Low, Severity.Low)) yield return d;
        foreach (var d in DiffHelp.Scalar(Id, "Java", "JAVA_HOME", pa.JavaHome, pb.JavaHome, ctx, Severity.Medium, Severity.Medium)) yield return d;
        foreach (var d in DiffHelp.Scalar(Id, "Gradle", "gradle", pa.GradleVersion, pb.GradleVersion, ctx, Severity.High, Severity.High,
            "Gradle is version-sensitive; prefer the project's gradle wrapper.")) yield return d;
        foreach (var d in DiffHelp.Scalar(Id, "Maven", "maven", pa.MavenVersion, pb.MavenVersion, ctx, Severity.High, Severity.Medium)) yield return d;
        foreach (var d in DiffHelp.Scalar(Id, "Kotlin", "kotlin", pa.KotlinVersion, pb.KotlinVersion, ctx, Severity.High, Severity.High)) yield return d;
    }

    private static string? DetectVendor(string text)
    {
        foreach (var (kw, name) in new[]
        {
            ("Temurin", "Eclipse Temurin"), ("Adoptium", "Eclipse Temurin"), ("Zulu", "Azul Zulu"),
            ("Corretto", "Amazon Corretto"), ("GraalVM", "GraalVM"), ("Oracle", "Oracle"),
            ("OpenJDK", "OpenJDK"), ("Microsoft", "Microsoft Build of OpenJDK"),
        })
            if (text.Contains(kw, StringComparison.OrdinalIgnoreCase)) return name;
        return null;
    }

    private static string? JavaMajor(string? v)
    {
        if (string.IsNullOrEmpty(v)) return null;
        var parts = v.Split('.', '_');
        return v.StartsWith("1.") && parts.Length > 1 ? parts[1] : parts[0];
    }

    private static Proc.Result? Tool(string name, string args)
    {
        var path = Proc.Which(name) ?? Proc.Which(name + ".cmd") ?? Proc.Which(name + ".bat");
        return path is null ? null : Proc.Run(path, args, timeoutMs: 20_000);
    }

    private static string? Extract(string? text, string pattern)
    {
        if (text is null) return null;
        var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }
}
