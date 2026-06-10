namespace BuildDiff;

public enum Severity { Critical, High, Medium, Low }

public sealed record Diff(
    Severity Severity,
    string Category,
    string Message,
    string? Hint = null,
    string? ProviderId = null);

/// <summary>
/// Reusable diff shapes so providers don't each re-implement set-difference,
/// scalar-mismatch and dictionary-diff.
/// </summary>
public static class DiffHelp
{
    /// <summary>A single value that may be present on one side only, or differ.</summary>
    public static IEnumerable<Diff> Scalar(string providerId, string category, string subject,
        string? a, string? b, CompareContext ctx, Severity missing, Severity mismatch, string? hint = null)
    {
        bool aHas = !string.IsNullOrEmpty(a);
        bool bHas = !string.IsNullOrEmpty(b);
        if (aHas && !bHas)
            yield return new Diff(missing, category, $"{subject} present on {ctx.A} ({a}) but NOT FOUND on {ctx.B}", hint, providerId);
        else if (bHas && !aHas)
            yield return new Diff(missing, category, $"{subject} present on {ctx.B} ({b}) but NOT FOUND on {ctx.A}", hint, providerId);
        else if (aHas && bHas && !string.Equals(a, b, StringComparison.Ordinal))
            yield return new Diff(mismatch, category, $"{subject} differs: {ctx.A}={a}, {ctx.B}={b}", hint, providerId);
    }

    /// <summary>Two sets; flags items present on exactly one side.</summary>
    public static IEnumerable<Diff> Sets(string providerId, string category,
        IEnumerable<string> a, IEnumerable<string> b, CompareContext ctx,
        Severity sev, Func<string, string?>? hint = null)
    {
        var aSet = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
        var bSet = new HashSet<string>(b, StringComparer.OrdinalIgnoreCase);
        foreach (var item in aSet.Except(bSet).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            yield return new Diff(sev, category, $"{item} present on {ctx.A}, MISSING on {ctx.B}", hint?.Invoke(item), providerId);
        foreach (var item in bSet.Except(aSet).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            yield return new Diff(sev, category, $"{item} present on {ctx.B}, MISSING on {ctx.A}", hint?.Invoke(item), providerId);
    }

    /// <summary>Two string→string maps; flags presence and value differences.</summary>
    public static IEnumerable<Diff> Dict(string providerId, string category,
        IReadOnlyDictionary<string, string?> a, IReadOnlyDictionary<string, string?> b,
        CompareContext ctx, Severity sev)
    {
        foreach (var k in a.Keys.Union(b.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            bool aHas = a.TryGetValue(k, out var av);
            bool bHas = b.TryGetValue(k, out var bv);
            if (aHas && !bHas)
                yield return new Diff(sev, category, $"{k} set on {ctx.A}, unset on {ctx.B}", null, providerId);
            else if (bHas && !aHas)
                yield return new Diff(sev, category, $"{k} set on {ctx.B}, unset on {ctx.A}", null, providerId);
            else if (!string.Equals(av, bv, StringComparison.Ordinal))
                yield return new Diff(sev, category, $"{k} differs: {ctx.A}={Trunc(av)}, {ctx.B}={Trunc(bv)}", null, providerId);
        }
    }

    /// <summary>Presence-only diff for noisy maps where value drift would overwhelm.</summary>
    public static IEnumerable<Diff> Presence(string providerId, string category,
        IEnumerable<string> aKeys, IEnumerable<string> bKeys, CompareContext ctx, Severity sev)
    {
        var aSet = new HashSet<string>(aKeys, StringComparer.OrdinalIgnoreCase);
        var bSet = new HashSet<string>(bKeys, StringComparer.OrdinalIgnoreCase);
        foreach (var k in aSet.Except(bSet).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            yield return new Diff(sev, category, $"{k} set on {ctx.A} only", null, providerId);
        foreach (var k in bSet.Except(aSet).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            yield return new Diff(sev, category, $"{k} set on {ctx.B} only", null, providerId);
    }

    public static string Trunc(string? s, int max = 80)
        => s is null ? "<unset>" : (s.Length > max ? string.Concat(s.AsSpan(0, max - 1), "…") : s);
}
