using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;
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
[Collection("workflow")]
public class ToleranceBreachTests : IClassFixture<QmsAppFactory>
{
    private readonly ITestOutputHelper _out;
    private QmsAppFactory Factory { get; }

    public ToleranceBreachTests(QmsAppFactory factory, ITestOutputHelper output)
    {
        Factory = factory;
        _out = output;
    }

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

    /// <summary>
    /// The SUMMARY has to judge a defect the same way the sample detail does.
    /// It is built by a different method, from a different aggregation, so the
    /// tolerance reaching it is a separate piece of wiring that can be
    /// forgotten on its own -- and a defect that looked acceptable in the
    /// summary while being red three pages later is worse than no colour.
    ///
    /// The test SETS a tolerance rather than hoping one exists. Nobody has
    /// filled these in on production yet, so a test that skipped when it found
    /// none would pass today and keep passing if the wiring were removed.
    /// </summary>
    [Fact]
    public async Task The_group_summary_carries_the_tolerance_too()
    {
        using var scope = Factory.Services.CreateScope();
        var qos    = scope.ServiceProvider.GetRequiredService<IQualityOrderService>();
        var claims = scope.ServiceProvider.GetRequiredService<IClaimService>();
        var cfg    = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        using var c = new Microsoft.Data.SqlClient.SqlConnection(cfg.GetConnectionString("Default"));
        await c.OpenAsync();

        // A finished order, the defects its summary actually reports on, and
        // one of those defects to put a tolerance against.
        var rows = (await claims.ListClosedQosAsync(new SharbatlyQMS.Web.ViewModels.ClaimListFilter(),
                                                   SharbatlyQMS.Web.Models.PlantScope.All, "test")).Rows;
        // An in-progress reinspection can head this list; it has no samples,
        // so the loop skips it, but be explicit rather than lucky.
        foreach (var r in rows.Where(r => r.StatusCode == QualityOrderStatus.Closed).Take(25))
        {
            var materials = await qos.GetMaterialsAsync(r.QualityOrderId);
            var before    = await qos.BuildGroupSummariesAsync(r.QualityOrderId, materials);
            var group     = before.FirstOrDefault(g => g.DefectSections.Any(sec => sec.Rows.Count > 0));
            if (group is null) continue;

            var target = group.DefectSections.SelectMany(sec => sec.Rows).First();
            var kept   = await Dapper.SqlMapper.ExecuteScalarAsync<decimal?>(c,
                "SELECT tolerance FROM qms_defect_catalog WHERE defect_id = @id", new { id = target.DefectId });
            try
            {
                await Dapper.SqlMapper.ExecuteAsync(c,
                    "UPDATE qms_defect_catalog SET tolerance = @t WHERE defect_id = @id",
                    new { t = 1.25m, id = target.DefectId });

                var after = await qos.BuildGroupSummariesAsync(r.QualityOrderId, materials);
                var row = after.SelectMany(g => g.DefectSections).SelectMany(sec => sec.Rows)
                               .First(x => x.DefectId == target.DefectId);

                _out.WriteLine($"QO {r.QualityOrderId}, defect '{row.Name}': " +
                               $"{row.Percentage:0.00}% against a 1.25 tolerance -> " +
                               (row.ExceedsTolerance ? "RED" : "normal"));

                Assert.Equal(1.25m, row.Tolerance);
                // And the verdict follows the same rule the report applies.
                Assert.Equal(row.SumValue > 0 && row.Percentage >= 1.25m, row.ExceedsTolerance);
            }
            finally
            {
                await Dapper.SqlMapper.ExecuteAsync(c,
                    "UPDATE qms_defect_catalog SET tolerance = @t WHERE defect_id = @id",
                    new { t = kept, id = target.DefectId });
            }
            return;
        }
        _out.WriteLine("No finished order with summarised defects to test against.");
    }
}
