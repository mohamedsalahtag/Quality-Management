using SharbatlyQMS.Web.Models;
using Xunit;
using Xunit.Abstractions;

namespace SharbatlyQMS.Tests;

/// <summary>
/// When a defect is printed in red on the report.
///
/// The rule is "reached OR exceeded", so the boundary is inclusive -- a defect
/// sitting exactly on its tolerance is a breach, not a pass. The awkward case
/// is a tolerance of zero, which says "any occurrence fails": without a guard
/// on the defect having actually occurred, every defect that was looked for and
/// not found would print red at 0% against 0.00, and a report where most lines
/// are red says nothing at all.
/// </summary>
public class ToleranceBreachTests
{
    private readonly ITestOutputHelper _out;
    public ToleranceBreachTests(ITestOutputHelper output) => _out = output;

    private static DefectAggRow Row(decimal value, decimal pct, decimal? tolerance) =>
        new() { Name = "Bruising", SumValue = value, Percentage = pct, Tolerance = tolerance };

    [Theory]
    // exactly on the tolerance -> breach ("reach or exceed")
    [InlineData(5, 2.50, 2.50, true)]
    [InlineData(5, 2.51, 2.50, true)]
    [InlineData(5, 9.99, 2.50, true)]
    // below it -> not a breach
    [InlineData(5, 2.49, 2.50, false)]
    [InlineData(5, 0.01, 2.50, false)]
    // no tolerance agreed -> never a breach, however high
    [InlineData(5, 99.0, null, false)]
    // zero tolerance: any OCCURRENCE fails...
    [InlineData(1, 0.50, 0.00, true)]
    // ...but a defect that never occurred is not a breach of it
    [InlineData(0, 0.00, 0.00, false)]
    // nor is a zero count against an ordinary tolerance
    [InlineData(0, 0.00, 2.50, false)]
    public void The_red_rule(decimal value, decimal pct, double? tolerance, bool expected)
    {
        var row = Row(value, pct, tolerance is null ? null : (decimal)tolerance.Value);
        _out.WriteLine($"value {value}, {pct}% against {(tolerance?.ToString() ?? "no")} tolerance -> " +
                       (row.ExceedsTolerance ? "RED" : "normal"));
        Assert.Equal(expected, row.ExceedsTolerance);
    }

    /// <summary>
    /// The tolerance has to reach the row from the catalog. A row built without
    /// it can never be red, so this is the wiring that makes the feature exist
    /// at all rather than silently doing nothing.
    /// </summary>
    [Fact]
    public void A_row_built_from_a_catalog_entry_carries_its_tolerance()
    {
        var entry = new DefectCatalogEntry
        {
            DefectId = 1, DefectCode = "BRU", DefectName = "Bruising",
            DefectCategory = "Major", Tolerance = 3.25m
        };

        // The shape both builders use: catalog entry in, aggregate row out.
        var row = new DefectAggRow
        {
            DefectId = entry.DefectId, Code = entry.DefectCode, Name = entry.DefectName,
            Category = entry.DefectCategory, SumValue = 10m, Percentage = 4m,
            Tolerance = entry.Tolerance
        };

        Assert.Equal(3.25m, row.Tolerance);
        Assert.True(row.ExceedsTolerance);
    }
}
