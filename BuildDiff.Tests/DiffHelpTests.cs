using BuildDiff;
using Xunit;

namespace BuildDiff.Tests;

public class DiffHelpTests
{
    private static readonly CompareContext Ctx = new() { A = "m1", B = "m2" };

    [Fact]
    public void Scalar_present_on_one_side_uses_missing_severity()
    {
        var diffs = DiffHelp.Scalar("p", "cat", "thing", "1.0", null, Ctx, Severity.Critical, Severity.High).ToList();
        var d = Assert.Single(diffs);
        Assert.Equal(Severity.Critical, d.Severity);
        Assert.Contains("present on m1", d.Message);
        Assert.Contains("NOT FOUND on m2", d.Message);
    }

    [Fact]
    public void Scalar_value_mismatch_uses_mismatch_severity()
    {
        var d = Assert.Single(DiffHelp.Scalar("p", "cat", "thing", "1.0", "2.0", Ctx, Severity.Critical, Severity.High).ToList());
        Assert.Equal(Severity.High, d.Severity);
    }

    [Fact]
    public void Scalar_equal_values_produce_nothing()
        => Assert.Empty(DiffHelp.Scalar("p", "cat", "thing", "1.0", "1.0", Ctx, Severity.Critical, Severity.High));

    [Fact]
    public void Sets_flags_items_unique_to_each_side()
    {
        var diffs = DiffHelp.Sets("p", "cat", new[] { "a", "b" }, new[] { "b", "c" }, Ctx, Severity.Medium).ToList();
        Assert.Equal(2, diffs.Count);
        Assert.Contains(diffs, d => d.Message.Contains("a present on m1"));
        Assert.Contains(diffs, d => d.Message.Contains("c present on m2"));
    }

    [Fact]
    public void Dict_flags_presence_and_value_differences()
    {
        var a = new Dictionary<string, string?> { ["X"] = "1", ["Y"] = "same" };
        var b = new Dictionary<string, string?> { ["Y"] = "same", ["Z"] = "9" };
        var diffs = DiffHelp.Dict("p", "cat", a, b, Ctx, Severity.High).ToList();
        Assert.Contains(diffs, d => d.Message.Contains("X set on m1, unset on m2"));
        Assert.Contains(diffs, d => d.Message.Contains("Z set on m2, unset on m1"));
        Assert.DoesNotContain(diffs, d => d.Message.Contains("Y"));
    }
}
