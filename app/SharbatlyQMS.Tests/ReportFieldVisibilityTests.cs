using System.Reflection;
using SharbatlyQMS.Web.Services.Pdf;
using Xunit;

namespace SharbatlyQMS.Tests;

/// <summary>
/// The QO report suppresses fields at two levels, and the difference matters:
///
///   IsHiddenReportField   — hidden EVERYWHERE (packaging / packing material).
///   IsHiddenSummaryField  — the above PLUS identifiers that are meaningless
///                           rolled up across a material group (PUC, Grower,
///                           Lot No, Date Code). Those still print on each
///                           per-sample card; only the page-1 group summary
///                           drops them (2026-07-29).
///
/// Both are private, so these tests reach them by reflection to lock the
/// behaviour down without widening the API. Pallet No is deliberately in
/// NEITHER set — the user asked for exactly the four identifiers above.
/// </summary>
public class ReportFieldVisibilityTests
{
    private static readonly MethodInfo IsHiddenEverywhere =
        typeof(QualityReportPdf).GetMethod("IsHiddenReportField",
            BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo IsHiddenInSummary =
        typeof(QualityReportPdf).GetMethod("IsHiddenSummaryField",
            BindingFlags.NonPublic | BindingFlags.Static)!;

    private static bool HiddenEverywhere(string name) =>
        (bool)IsHiddenEverywhere.Invoke(null, new object?[] { name })!;

    private static bool HiddenInSummary(string name) =>
        (bool)IsHiddenInSummary.Invoke(null, new object?[] { name })!;

    [Theory]
    [InlineData("Packaging Material")]
    [InlineData("PACKAGING_MATERIAL")]
    [InlineData("packing material")]
    public void Fields_hidden_everywhere_are_suppressed_in_both_places(string name)
    {
        Assert.True(HiddenEverywhere(name));
        Assert.True(HiddenInSummary(name));   // the summary set is a superset
    }

    // Identifiers: dropped from the group roll-up, kept on each sample card.
    [Theory]
    [InlineData("Grower")]
    [InlineData("GROWER")]
    [InlineData("Date Code")]
    [InlineData("DATE_CODE")]
    [InlineData("Lot No")]
    [InlineData("Lot No.")]
    [InlineData("Lot Number")]
    [InlineData("PUC")]
    public void Identifier_fields_are_hidden_from_the_summary_only(string name)
    {
        Assert.True(HiddenInSummary(name));
        Assert.False(HiddenEverywhere(name));
    }

    [Theory]
    [InlineData("Brix")]
    [InlineData("Firmness")]
    [InlineData("Colour")]
    [InlineData("Pack Code")]      // deliberately still shown
    [InlineData("Label")]
    [InlineData("Net Weight")]
    [InlineData("Pallet No")]      // in neither hidden set
    [InlineData("PALLET_NO")]
    [InlineData("")]
    public void Other_fields_are_shown_everywhere(string name)
    {
        Assert.False(HiddenEverywhere(name));
        Assert.False(HiddenInSummary(name));
    }
}
