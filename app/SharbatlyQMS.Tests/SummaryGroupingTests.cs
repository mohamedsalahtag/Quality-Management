using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;
using Xunit;
using Xunit.Abstractions;

namespace SharbatlyQMS.Tests;

/// <summary>
/// The QC summary groups materials by (material group, variety, grade,
/// weight). Brand is listed, not grouped on (2026-09-26); weight was added to
/// the key on 2026-09-28, so a 13.5 kg and a 6 kg carton of the same fruit get
/// separate blocks. Weight is SAP's carton net weight, filled in by ApplyMara
/// exactly as the report does before building the summary.
/// </summary>
[Collection("workflow")]
public class SummaryGroupingTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    private readonly ITestOutputHelper _out;

    public SummaryGroupingTests(QmsAppFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    [Fact]
    public void The_weight_prints_as_a_trimmed_kilogram_figure()
    {
        Assert.Equal("13.5 kg", new MaterialGroupSummary { Weight = 13.50m }.WeightText);
        Assert.Equal("18 kg",   new MaterialGroupSummary { Weight = 18.00m }.WeightText);
        Assert.Null(new MaterialGroupSummary { Weight = null }.WeightText);
    }

    /// <summary>
    /// On a real finished order whose sampled materials share material group,
    /// variety and grade but differ in SAP weight, the summary has one block
    /// per weight -- and none merges two weights.
    /// </summary>
    [Fact]
    public async Task Materials_of_different_weight_get_separate_blocks()
    {
        using var scope = _factory.Services.CreateScope();
        var cfg = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        long qoId;
        using (var c = new SqlConnection(cfg.GetConnectionString("Default")))
        {
            qoId = await c.ExecuteScalarAsync<long>(@"
                SELECT TOP 1 m.quality_order_id
                FROM   qms_quality_order_material m
                JOIN   qms_sap_material_cache mc ON mc.material_no = m.material_no
                JOIN   qms_quality_order qo ON qo.quality_order_id = m.quality_order_id
                WHERE  qo.status_code = 'Closed' AND qo.superseded_at IS NULL
                  AND  EXISTS (SELECT 1 FROM qms_sample s
                               WHERE s.qo_material_id = m.qo_material_id AND s.is_deleted = 0)
                GROUP  BY m.quality_order_id, m.material_group, m.variety, m.material_class
                HAVING COUNT(DISTINCT mc.weight) > 1
                ORDER  BY m.quality_order_id DESC");
        }
        if (qoId == 0) { _out.WriteLine("No finished order mixes weights within one group."); return; }

        var qos  = scope.ServiceProvider.GetRequiredService<IQualityOrderService>();
        var mara = scope.ServiceProvider.GetRequiredService<IMaraService>();

        // Exactly the report's path: load, enrich from MARA, then summarise.
        var materials = (await qos.GetMaterialsAsync(qoId)).ToList();
        var lookup = await mara.LookupAsync(materials.Select(m => m.MaterialNo));
        foreach (var m in materials)
            if (lookup.TryGetValue(m.MaterialNo, out var mm)) m.ApplyMara(mm);

        var summaries = await qos.BuildGroupSummariesAsync(qoId, materials, await qos.GetReportUnitsAsync());
        foreach (var s in summaries)
            _out.WriteLine($"QO {qoId}: {s.MaterialGroup} / {s.Variety} / {s.Grade} / {s.WeightText} -> {s.MaterialCount} material(s)");

        // At least one (group, variety, grade) now appears as two blocks that
        // differ only in weight.
        var split = summaries
            .GroupBy(s => (s.MaterialGroup, s.Variety, s.Grade))
            .Where(g => g.Count() > 1)
            .ToList();
        Assert.NotEmpty(split);
        Assert.All(split, g =>
            Assert.Equal(g.Count(), g.Select(s => s.Weight).Distinct().Count()));
    }
}
