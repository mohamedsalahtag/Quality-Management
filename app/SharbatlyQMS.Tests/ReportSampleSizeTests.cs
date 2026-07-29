using System.Reflection;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services.Pdf;
using Xunit;

namespace SharbatlyQMS.Tests;

/// <summary>
/// The QO report's material card printed "—" for Sample Size on every report,
/// because it read qms_quality_order_material.sample_size — a column only the
/// "Override size" modal writes. MARA supplies no size for these materials, so
/// operators type it into each SAMPLE instead, which leaves the material column
/// NULL (248 of 248 materials in production, against 695 sized samples).
///
/// SampleSizeText resolves material size -> MARA-parsed size -> the sizes the
/// samples were actually inspected at, so the number every defect percentage is
/// divided by always appears somewhere. It is private static, so these tests
/// reach it by reflection rather than widening the API.
/// </summary>
public class ReportSampleSizeTests
{
    private static readonly MethodInfo SampleSizeText =
        typeof(QualityReportPdf).GetMethod("SampleSizeText",
            BindingFlags.NonPublic | BindingFlags.Static)!;

    private static string Text(QualityOrderMaterial? m, short?[] sampleSizes, string unit = "Pieces") =>
        (string)SampleSizeText.Invoke(null, new object?[] { m, sampleSizes, unit })!;

    [Fact]
    public void Material_level_override_wins_and_carries_the_unit()
    {
        var m = new QualityOrderMaterial { SampleSize = 150 };
        Assert.Equal("150 Pieces", Text(m, new short?[] { 125, 125 }));
    }

    [Fact]
    public void Falls_back_to_the_MARA_size_text_when_no_override()
    {
        // EffectiveSampleSize parses MaterialSize when SampleSize is null.
        var m = new QualityOrderMaterial { MaterialSize = "30" };
        Assert.Equal("30 Pieces", Text(m, new short?[] { }));
    }

    [Fact]
    public void Falls_back_to_the_samples_own_size_when_the_material_has_none()
    {
        // The production shape: nothing on the material, every sample sized 125.
        var m = new QualityOrderMaterial();
        Assert.Equal("125 Pieces", Text(m, new short?[] { 125, 125, 125 }));
    }

    [Fact]
    public void Lists_every_distinct_sample_size_rather_than_picking_one()
    {
        var m = new QualityOrderMaterial();
        Assert.Equal("113 / 125 Pieces", Text(m, new short?[] { 125, 113, 125 }));
    }

    [Fact]
    public void Uses_the_material_groups_configured_unit()
    {
        var m = new QualityOrderMaterial();
        Assert.Equal("40 Cartons", Text(m, new short?[] { 40 }, "Cartons"));
    }

    [Theory]
    [InlineData((short)0)]
    [InlineData(null)]
    public void Zero_and_null_sample_sizes_are_not_treated_as_a_value(short? size)
    {
        // A zero divisor means "no size recorded" — printing "0 Pieces" would
        // read as a real measurement next to percentages that are all 0%.
        var m = new QualityOrderMaterial();
        Assert.Equal("—", Text(m, new[] { size }));
    }

    [Fact]
    public void No_material_and_no_samples_renders_the_dash_not_an_empty_cell()
    {
        Assert.Equal("—", Text(null, new short?[] { }));
    }

    [Fact]
    public void A_materials_own_zero_does_not_block_the_sample_fallback()
    {
        var m = new QualityOrderMaterial { SampleSize = 0 };
        Assert.Equal("125 Pieces", Text(m, new short?[] { 125 }));
    }
}
