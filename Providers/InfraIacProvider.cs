using System.Text.Json;
using System.Text.RegularExpressions;

namespace BuildDiff;

public sealed class TerraformInfo
{
    public string? Version { get; set; }
    public string? Path { get; set; }
    public string? ActiveManager { get; set; }
    public List<string> InstalledVersions { get; set; } = new();

    // Project mode (populated only when ctx.ProjectRoot is set).
    public string? RequiredVersion { get; set; }
    public string? VersionPin { get; set; }
    public Dictionary<string, string?> ProviderLocks { get; set; } = new();
}

public sealed class OpenTofuInfo
{
    public string? Version { get; set; }
    public string? Path { get; set; }
}

public sealed class PulumiInfo
{
    public string? Version { get; set; }
    public string? Path { get; set; }
}

public sealed class IaCPayload
{
    public TerraformInfo? Terraform { get; set; }
    public OpenTofuInfo? Opentofu { get; set; }
    public PulumiInfo? Pulumi { get; set; }
    public Dictionary<string, string?> CloudClis { get; set; } = new();
}

/// <summary>
/// Infrastructure / IaC CLIs: Terraform/OpenTofu, Pulumi, and the cloud
/// deploy tools (kubectl/helm/aws/gcloud/az). Cross-platform. Calibrated so
/// that only terraform/tofu version skew is Critical — the rest is build- or
/// deploy-time noise. Reads only public version output and public project
/// files (.tf, .terraform-version, .terraform.lock.hcl); never credentials.
/// </summary>
public sealed class InfraIacProvider : IEnvironmentProvider
{
    private const int Timeout = 10_000;

    public string Id => "infra-iac";
    public string DisplayName => "Terraform/OpenTofu, Pulumi, kubectl/helm/aws/gcloud/az";
    public bool AppliesTo(OsPlatform os) => true;

    public object? Capture(CaptureContext ctx)
    {
        var payload = new IaCPayload();

        payload.Terraform = CaptureTerraform();
        payload.Opentofu = CaptureOpenTofu();
        payload.Pulumi = CapturePulumi();
        payload.CloudClis = CaptureCloudClis();

        if (ctx.ProjectRoot is not null)
            CaptureProject(ctx.ProjectRoot, ref payload);

        bool anyCloud = payload.CloudClis.Values.Any(v => !string.IsNullOrEmpty(v));
        bool anyProject = payload.Terraform is { } t &&
            (t.RequiredVersion is not null || t.VersionPin is not null || t.ProviderLocks.Count > 0);

        if (payload.Terraform?.Version is null && payload.Opentofu is null &&
            payload.Pulumi is null && !anyCloud && !anyProject)
            return null;

        // Drop empty terraform section if it carries nothing at all.
        if (payload.Terraform is { Version: null, RequiredVersion: null, VersionPin: null } te &&
            te.ProviderLocks.Count == 0 && te.InstalledVersions.Count == 0)
            payload.Terraform = null;

        return payload;
    }

    // ---- capture: machine tools --------------------------------------------

    private static TerraformInfo? CaptureTerraform()
    {
        var path = Proc.Which("terraform");
        var installed = ScanInstalledTerraform();
        if (path is null && installed.Count == 0) return null;

        var info = new TerraformInfo { Path = path, InstalledVersions = installed };
        if (path is not null)
        {
            info.Version = Extract(Proc.Run(path, "version", timeoutMs: Timeout)?.Combined, @"Terraform v([0-9][0-9.]+)");
            info.ActiveManager = DetectManager(path);
        }
        return info;
    }

    private static OpenTofuInfo? CaptureOpenTofu()
    {
        var path = Proc.Which("tofu");
        if (path is null) return null;
        return new OpenTofuInfo
        {
            Path = path,
            Version = Extract(Proc.Run(path, "version", timeoutMs: Timeout)?.Combined, @"OpenTofu v([0-9.]+)"),
        };
    }

    private static PulumiInfo? CapturePulumi()
    {
        var path = Proc.Which("pulumi");
        if (path is null) return null;
        return new PulumiInfo
        {
            Path = path,
            Version = Extract(Proc.Run(path, "version", timeoutMs: Timeout)?.Combined, @"v?([0-9][0-9.]+)"),
        };
    }

    private static Dictionary<string, string?> CaptureCloudClis()
    {
        var clis = new Dictionary<string, string?>();

        AddCli(clis, "kubectl", "version --client", @"v([0-9][0-9.]+)");
        AddCli(clis, "helm", "version --short", @"v([0-9][0-9.]+)");
        AddCli(clis, "aws", "--version", @"aws-cli/([0-9][0-9.]+)");
        AddCli(clis, "gcloud", "--version", @"Google Cloud SDK ([0-9][0-9.]+)");
        AddCli(clis, "az", "version", @"azure-cli.*?([0-9][0-9.]+)");

        return clis;
    }

    private static void AddCli(Dictionary<string, string?> clis, string name, string args, string pattern)
    {
        // Proc.Which handles PATHEXT (.cmd shims for gcloud/az on Windows).
        var path = Proc.Which(name);
        if (path is null) return;
        clis[name] = Extract(Proc.Run(path, args, timeoutMs: Timeout)?.Combined, pattern);
    }

    private static string DetectManager(string path)
    {
        var p = path.Replace('\\', '/');
        if (p.Contains("/.tfenv/", StringComparison.OrdinalIgnoreCase) || p.Contains("tfenv", StringComparison.OrdinalIgnoreCase)) return "tfenv";
        if (p.Contains("/.asdf/", StringComparison.OrdinalIgnoreCase)) return "asdf";
        if (p.Contains("homebrew", StringComparison.OrdinalIgnoreCase) || p.Contains("/Cellar/", StringComparison.OrdinalIgnoreCase)) return "homebrew";
        return "system";
    }

    private static List<string> ScanInstalledTerraform()
    {
        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        void ScanDir(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var d in Directory.GetDirectories(dir))
                {
                    var name = System.IO.Path.GetFileName(d).TrimStart('v', 'V');
                    if (Regex.IsMatch(name, @"^[0-9][0-9.]*$")) versions.Add(name);
                }
            }
            catch { }
        }

        var tfenvRoot = Environment.GetEnvironmentVariable("TFENV_ROOT");
        ScanDir(System.IO.Path.Combine(string.IsNullOrEmpty(tfenvRoot) ? System.IO.Path.Combine(home, ".tfenv") : tfenvRoot, "versions"));
        ScanDir(System.IO.Path.Combine(home, ".asdf", "installs", "terraform"));

        return versions.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---- capture: project mode ---------------------------------------------

    private static void CaptureProject(string root, ref IaCPayload payload)
    {
        List<string> hclFiles, lockFiles;
        try
        {
            hclFiles = GlobIac(root);
            lockFiles = GlobLocks(root);
        }
        catch { return; }

        var required = ExtractRequiredVersion(hclFiles);
        var pin = ReadVersionPin(root);
        var locks = ReadProviderLocks(lockFiles);

        if (required is null && pin is null && locks.Count == 0) return;

        payload.Terraform ??= new TerraformInfo();
        if (required is not null) payload.Terraform.RequiredVersion = required;
        if (pin is not null) payload.Terraform.VersionPin = pin;
        foreach (var kv in locks) payload.Terraform.ProviderLocks[kv.Key] = kv.Value;
    }

    /// <summary>*.tf and *.tofu across root + one level of module subdirs.</summary>
    private static List<string> GlobIac(string root)
    {
        var files = new List<string>();
        void Collect(string dir)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir))
                    if (f.EndsWith(".tf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".tofu", StringComparison.OrdinalIgnoreCase))
                        files.Add(f);
            }
            catch { }
        }

        Collect(root);
        try
        {
            foreach (var sub in Directory.GetDirectories(root)) Collect(sub);
        }
        catch { }
        return files;
    }

    /// <summary>.terraform.lock.hcl in root + one level of module subdirs.</summary>
    private static List<string> GlobLocks(string root)
    {
        var files = new List<string>();
        void Collect(string dir)
        {
            try
            {
                var f = System.IO.Path.Combine(dir, ".terraform.lock.hcl");
                if (File.Exists(f)) files.Add(f);
            }
            catch { }
        }

        Collect(root);
        try
        {
            foreach (var sub in Directory.GetDirectories(root)) Collect(sub);
        }
        catch { }
        return files;
    }

    private static string? ExtractRequiredVersion(List<string> hclFiles)
    {
        foreach (var file in hclFiles)
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }

            var m = Regex.Match(text, @"(?ms)terraform\s*\{.*?required_version\s*=\s*""([^""]+)"".*?\}");
            if (m.Success) return m.Groups[1].Value.Trim();
        }
        return null;
    }

    private static string? ReadVersionPin(string root)
    {
        try
        {
            var f = System.IO.Path.Combine(root, ".terraform-version");
            if (!File.Exists(f)) return null;
            var line = File.ReadLines(f).FirstOrDefault()?.Trim();
            if (string.IsNullOrEmpty(line)) return null;
            return line.TrimStart('v', 'V');
        }
        catch { return null; }
    }

    private static Dictionary<string, string?> ReadProviderLocks(List<string> lockFiles)
    {
        var locks = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in lockFiles)
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }

            foreach (Match m in Regex.Matches(text,
                @"(?ms)provider\s+""(registry\.terraform\.io/[^""]+)""\s*\{.*?version\s*=\s*""([^""]+)"".*?\}"))
            {
                var name = m.Groups[1].Value;
                // Store under the short ns/name (strip the registry host).
                var shortName = name.StartsWith("registry.terraform.io/", StringComparison.OrdinalIgnoreCase)
                    ? name["registry.terraform.io/".Length..] : name;
                locks[shortName] = m.Groups[2].Value;
            }
        }
        return locks;
    }

    // ---- compare ------------------------------------------------------------

    public IEnumerable<Diff> Compare(JsonElement? a, JsonElement? b, CompareContext ctx)
    {
        var pa = Json.To<IaCPayload>(a) ?? new IaCPayload();
        var pb = Json.To<IaCPayload>(b) ?? new IaCPayload();

        foreach (var d in CompareTerraform(pa.Terraform, pb.Terraform, ctx)) yield return d;
        foreach (var d in CompareOpenTofu(pa.Opentofu, pb.Opentofu, ctx)) yield return d;
        foreach (var d in ComparePulumi(pa.Pulumi, pb.Pulumi, ctx)) yield return d;
        foreach (var d in CompareCloudClis(pa.CloudClis, pb.CloudClis, ctx)) yield return d;
        foreach (var d in CompareProject(pa.Terraform, pb.Terraform, ctx)) yield return d;
    }

    private IEnumerable<Diff> CompareTerraform(TerraformInfo? a, TerraformInfo? b, CompareContext ctx)
    {
        var av = a?.Version;
        var bv = b?.Version;
        bool aHas = !string.IsNullOrEmpty(av), bHas = !string.IsNullOrEmpty(bv);

        if (aHas && !bHas)
            yield return new Diff(Severity.Critical, "Terraform", $"terraform present on {ctx.A} ({av}) but NOT FOUND on {ctx.B}",
                "Install matching terraform (e.g. via tfenv) on the missing machine to match the pin.", Id);
        else if (bHas && !aHas)
            yield return new Diff(Severity.Critical, "Terraform", $"terraform present on {ctx.B} ({bv}) but NOT FOUND on {ctx.A}",
                "Install matching terraform (e.g. via tfenv) on the missing machine to match the pin.", Id);
        else if (aHas && bHas && !string.Equals(av, bv, StringComparison.Ordinal))
        {
            bool sameMinor = SameMajorMinor(av, bv);
            yield return new Diff(sameMinor ? Severity.Low : Severity.Critical, "Terraform",
                $"terraform version differs: {ctx.A}={av}, {ctx.B}={bv}",
                sameMinor ? null : "State format & provider protocol track the minor version; pin the same minor via .terraform-version.", Id);
        }

        // Installed-versions / active-manager drift is informational when the active version matches.
        if (aHas && bHas && string.Equals(av, bv, StringComparison.Ordinal))
        {
            if (!string.Equals(a!.ActiveManager, b!.ActiveManager, StringComparison.OrdinalIgnoreCase))
                yield return new Diff(Severity.Low, "Terraform",
                    $"terraform manager differs: {ctx.A}={a.ActiveManager ?? "<unset>"}, {ctx.B}={b.ActiveManager ?? "<unset>"} (active version matches)", null, Id);

            var aSet = new HashSet<string>(a.InstalledVersions, StringComparer.OrdinalIgnoreCase);
            var bSet = new HashSet<string>(b.InstalledVersions, StringComparer.OrdinalIgnoreCase);
            if (!aSet.SetEquals(bSet) && (aSet.Count > 0 || bSet.Count > 0))
                yield return new Diff(Severity.Low, "Terraform",
                    $"installed terraform versions differ (active version matches): {ctx.A}=[{string.Join(",", a.InstalledVersions)}], {ctx.B}=[{string.Join(",", b.InstalledVersions)}]", null, Id);
        }
    }

    private IEnumerable<Diff> CompareOpenTofu(OpenTofuInfo? a, OpenTofuInfo? b, CompareContext ctx)
    {
        var av = a?.Version;
        var bv = b?.Version;
        bool aHas = !string.IsNullOrEmpty(av), bHas = !string.IsNullOrEmpty(bv);

        if (aHas && !bHas)
            yield return new Diff(Severity.Critical, "OpenTofu", $"tofu present on {ctx.A} ({av}) but NOT FOUND on {ctx.B}",
                "Install matching OpenTofu on the missing machine to match the pin.", Id);
        else if (bHas && !aHas)
            yield return new Diff(Severity.Critical, "OpenTofu", $"tofu present on {ctx.B} ({bv}) but NOT FOUND on {ctx.A}",
                "Install matching OpenTofu on the missing machine to match the pin.", Id);
        else if (aHas && bHas && !string.Equals(av, bv, StringComparison.Ordinal))
        {
            bool sameMinor = SameMajorMinor(av, bv);
            yield return new Diff(sameMinor ? Severity.Low : Severity.Critical, "OpenTofu",
                $"tofu version differs: {ctx.A}={av}, {ctx.B}={bv}",
                sameMinor ? null : "State format & provider protocol track the minor version; pin the same minor.", Id);
        }
    }

    private IEnumerable<Diff> ComparePulumi(PulumiInfo? a, PulumiInfo? b, CompareContext ctx)
    {
        var av = a?.Version;
        var bv = b?.Version;
        bool aHas = !string.IsNullOrEmpty(av), bHas = !string.IsNullOrEmpty(bv);

        if (aHas && !bHas)
            yield return new Diff(Severity.High, "Pulumi", $"pulumi present on {ctx.A} ({av}) but NOT FOUND on {ctx.B}", null, Id);
        else if (bHas && !aHas)
            yield return new Diff(Severity.High, "Pulumi", $"pulumi present on {ctx.B} ({bv}) but NOT FOUND on {ctx.A}", null, Id);
        else if (aHas && bHas && !string.Equals(av, bv, StringComparison.Ordinal))
        {
            bool sameMajor = SameMajor(av, bv);
            yield return new Diff(sameMajor ? Severity.Low : Severity.High, "Pulumi",
                $"pulumi version differs: {ctx.A}={av}, {ctx.B}={bv}", null, Id);
        }
    }

    private IEnumerable<Diff> CompareCloudClis(Dictionary<string, string?> a, Dictionary<string, string?> b, CompareContext ctx)
    {
        a ??= new();
        b ??= new();
        foreach (var name in a.Keys.Union(b.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            a.TryGetValue(name, out var av);
            b.TryGetValue(name, out var bv);
            bool aHas = !string.IsNullOrEmpty(av), bHas = !string.IsNullOrEmpty(bv);

            if (aHas && !bHas)
                yield return new Diff(Severity.Low, "Cloud CLI", $"{name} present on {ctx.A} ({av}) but NOT FOUND on {ctx.B} (usually deploy-time, not build)", null, Id);
            else if (bHas && !aHas)
                yield return new Diff(Severity.Low, "Cloud CLI", $"{name} present on {ctx.B} ({bv}) but NOT FOUND on {ctx.A} (usually deploy-time, not build)", null, Id);
            else if (aHas && bHas && !string.Equals(av, bv, StringComparison.Ordinal))
            {
                bool sameMajor = SameMajor(av, bv);
                var sev = sameMajor ? Severity.Low : MajorBreakSeverity(name);
                yield return new Diff(sev, "Cloud CLI", $"{name} version differs: {ctx.A}={av}, {ctx.B}={bv}", null, Id);
            }
        }
    }

    /// <summary>aws v1 vs v2 and helm 2 vs 3 are notable major breaks; others stay Low.</summary>
    private static Severity MajorBreakSeverity(string name)
        => name.Equals("aws", StringComparison.OrdinalIgnoreCase) || name.Equals("helm", StringComparison.OrdinalIgnoreCase)
            ? Severity.Medium : Severity.Low;

    private IEnumerable<Diff> CompareProject(TerraformInfo? a, TerraformInfo? b, CompareContext ctx)
    {
        // required_version constraint (HCL) — machine terraform.version must satisfy it.
        foreach (var d in CompareRequired(a, b, ctx)) yield return d;
        // .terraform-version pin — concrete pin checked at pin precision.
        foreach (var d in ComparePin(a, b, ctx)) yield return d;
        // .terraform.lock.hcl provider locks — compared between the two snapshots.
        foreach (var d in CompareLocks(a, b, ctx)) yield return d;
    }

    private IEnumerable<Diff> CompareRequired(TerraformInfo? a, TerraformInfo? b, CompareContext ctx)
    {
        // The constraint is a property of the project; take whichever side recorded it.
        var constraint = a?.RequiredVersion ?? b?.RequiredVersion;
        if (constraint is null) yield break;

        foreach (var (side, info) in new[] { (ctx.A, a), (ctx.B, b) })
        {
            var ver = info?.Version;
            if (string.IsNullOrEmpty(ver))
            {
                yield return new Diff(Severity.High, "Project requirement",
                    $"{side}: terraform required_version \"{constraint}\" declared but terraform NOT FOUND",
                    "Install a terraform version satisfying the required_version constraint.", Id);
                continue;
            }

            var sat = Satisfies(ver, constraint);
            if (sat == false)
                yield return new Diff(Severity.High, "Project requirement",
                    $"{side}: terraform {ver} does not satisfy required_version \"{constraint}\"",
                    "Switch to a terraform version inside the constraint (e.g. via tfenv/.terraform-version).", Id);
            else if (sat is null)
                yield return new Diff(Severity.Low, "Project requirement",
                    $"{side}: terraform required_version \"{constraint}\" could not be parsed to verify {ver}", null, Id);
        }
    }

    private IEnumerable<Diff> ComparePin(TerraformInfo? a, TerraformInfo? b, CompareContext ctx)
    {
        var pin = a?.VersionPin ?? b?.VersionPin;
        if (pin is null) yield break;

        // Non-concrete tfenv pins are recorded but not checked.
        if (IsNonConcretePin(pin)) yield break;

        foreach (var (side, info) in new[] { (ctx.A, a), (ctx.B, b) })
        {
            var ver = info?.Version;
            if (string.IsNullOrEmpty(ver))
            {
                yield return new Diff(Severity.High, "Project requirement",
                    $"{side}: .terraform-version pins {pin} but terraform NOT FOUND",
                    "Install the pinned terraform version (tfenv install).", Id);
                continue;
            }

            if (!MatchesAtPrecision(ver, pin))
                yield return new Diff(Severity.Medium, "Project requirement",
                    $"{side}: terraform {ver} does not match .terraform-version pin {pin}",
                    "Run tfenv use to align with the pinned version.", Id);
        }
    }

    private IEnumerable<Diff> CompareLocks(TerraformInfo? a, TerraformInfo? b, CompareContext ctx)
    {
        var aLocks = a?.ProviderLocks ?? new();
        var bLocks = b?.ProviderLocks ?? new();
        if (aLocks.Count == 0 && bLocks.Count == 0) yield break;

        foreach (var name in aLocks.Keys.Union(bLocks.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            bool aHas = aLocks.TryGetValue(name, out var av);
            bool bHas = bLocks.TryGetValue(name, out var bv);

            if (aHas && !bHas)
                yield return new Diff(Severity.Medium, "Provider lock", $"provider {name} locked on {ctx.A} ({av}) but absent on {ctx.B}", null, Id);
            else if (bHas && !aHas)
                yield return new Diff(Severity.Medium, "Provider lock", $"provider {name} locked on {ctx.B} ({bv}) but absent on {ctx.A}", null, Id);
            else if (!string.Equals(av, bv, StringComparison.Ordinal))
                yield return new Diff(Severity.High, "Provider lock", $"provider {name} locked version differs: {ctx.A}={av}, {ctx.B}={bv}",
                    "Run terraform init -upgrade and commit a single .terraform.lock.hcl so both machines resolve identically.", Id);
        }
    }

    // ---- version helpers ----------------------------------------------------

    private static string? Extract(string? text, string pattern)
    {
        if (text is null) return null;
        var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? Major(string? v) => Parts(v).FirstOrDefault();

    private static bool SameMajor(string? a, string? b)
        => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
           && string.Equals(Major(a), Major(b), StringComparison.Ordinal);

    private static bool SameMajorMinor(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        var pa = Parts(a);
        var pb = Parts(b);
        return pa.Length >= 2 && pb.Length >= 2 && pa[0] == pb[0] && pa[1] == pb[1];
    }

    private static string[] Parts(string? v)
        => string.IsNullOrEmpty(v) ? Array.Empty<string>() : v.Split('.', '-', '+');

    /// <summary>tfenv-only non-concrete pins: 'latest', 'min-required', 'latest:&lt;re&gt;'.</summary>
    private static bool IsNonConcretePin(string pin)
        => pin.Equals("latest", StringComparison.OrdinalIgnoreCase)
           || pin.Equals("min-required", StringComparison.OrdinalIgnoreCase)
           || pin.StartsWith("latest:", StringComparison.OrdinalIgnoreCase);

    /// <summary>True if <paramref name="version"/> matches <paramref name="pin"/> at the pin's precision (e.g. pin "1.9" matches "1.9.4").</summary>
    private static bool MatchesAtPrecision(string version, string pin)
    {
        var vp = Parts(version);
        var pp = Parts(pin);
        if (pp.Length == 0) return true;
        for (int i = 0; i < pp.Length; i++)
        {
            if (i >= vp.Length) return false;
            if (!string.Equals(vp[i], pp[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    /// <summary>
    /// Evaluate a Terraform required_version constraint against a concrete version.
    /// Returns true (satisfies), false (violates), or null (unparseable).
    /// Supports =, !=, &gt;, &gt;=, &lt;, &lt;=, ~&gt; and comma-separated AND.
    /// </summary>
    private static bool? Satisfies(string version, string constraint)
    {
        var ver = ParseVersion(version);
        if (ver is null) return null;

        foreach (var raw in constraint.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = Regex.Match(raw, @"^(=|!=|>=|<=|>|<|~>)?\s*v?([0-9][0-9.]*)$");
            if (!m.Success) return null;
            var op = m.Groups[1].Success ? m.Groups[1].Value : "=";
            var operand = ParseVersion(m.Groups[2].Value);
            if (operand is null) return null;
            int operandParts = m.Groups[2].Value.Split('.').Length;

            if (!CheckOne(ver, op, operand, operandParts, m.Groups[2].Value)) return false;
        }
        return true;
    }

    private static bool CheckOne(int[] ver, string op, int[] operand, int operandParts, string operandRaw)
    {
        int cmp = CompareVersion(ver, operand);
        switch (op)
        {
            // Terraform pads `= 1.5` to exactly 1.5.0 and requires exact equality.
            case "=": return cmp == 0;
            case "!=": return cmp != 0;
            case ">": return cmp > 0;
            case ">=": return cmp >= 0;
            case "<": return cmp < 0;
            case "<=": return cmp <= 0;
            case "~>":
                // Pessimistic: lower bound = operand; upper bound floats the last specified component.
                if (cmp < 0) return false;
                var upper = (int[])operand.Clone();
                if (operandParts <= 1) return true; // '~> 1' has no upper bound
                int floatIndex = operandParts - 2; // bump the part before the last specified one
                upper[floatIndex] += 1;
                for (int i = floatIndex + 1; i < upper.Length; i++) upper[i] = 0;
                return CompareVersion(ver, upper) < 0;
            default: return false;
        }
    }

    private static int[]? ParseVersion(string v)
    {
        var parts = v.Split('.');
        var nums = new int[3];
        for (int i = 0; i < 3; i++)
        {
            if (i < parts.Length)
            {
                if (!int.TryParse(parts[i], out var n)) return null;
                nums[i] = n;
            }
        }
        return nums;
    }

    private static int CompareVersion(int[] a, int[] b)
    {
        for (int i = 0; i < 3; i++)
        {
            int c = a[i].CompareTo(b[i]);
            if (c != 0) return c;
        }
        return 0;
    }
}
