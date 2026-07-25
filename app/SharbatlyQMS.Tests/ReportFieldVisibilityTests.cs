using System.Reflection;
using SharbatlyQMS.Web.Services.Pdf;
using Xunit;

namespace SharbatlyQMS.Tests;

/// <summary>
/// The QO report hides six fields (packing material, date code, pallet number,
/// grower, PUC, lot number) across their spelling variants. IsHiddenReportField
/// is private, so these tests reach it by reflection to lock the behaviour down
/// without opening the API.
/// </summary>
public class ReportFieldVisibilityTests
{
    private static readonly MethodInfo IsHidden =
        typeof(QualityReportPdf).GetMethod("IsHiddenReportField",
            BindingFlags.NonPublic | BindingFlags.Static)!;

    private static bool Hidden(string name) => (bool)IsHidden.Invoke(null, new object?[] { name })!;

    [Theory]
    [InlineData("Grower")]
    [InlineData("Pallet No")]
    [InlineData("Pallet No.")]
    [InlineData("PALLET_NO")]
    [InlineData("Pallet Number")]
    [InlineData("Date Code")]
    [InlineData("DATE_CODE")]
    [InlineData("Lot No")]
    [InlineData("Lot No.")]
    [InlineData("Lot Number")]
    [InlineData("PUC")]
    [InlineData("Packaging Material")]
    [InlineData("PACKAGING_MATERIAL")]
    [InlineData("packing material")]
    public void Hidden_fields_are_suppressed(string name) => Assert.True(Hidden(name));

    [Theory]
    [InlineData("Brix")]
    [InlineData("Firmness")]
    [InlineData("Colour")]
    [InlineData("Pack Code")]      // deliberately still shown
    [InlineData("Label")]
    [InlineData("Net Weight")]
    [InlineData("")]
    public void Other_fields_are_shown(string name) => Assert.False(Hidden(name));
}
