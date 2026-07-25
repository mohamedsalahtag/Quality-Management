using SharbatlyQMS.Web.Services.Reports;
using Xunit;

namespace SharbatlyQMS.Tests;

/// <summary>
/// The Report Builder calculated-column evaluator: arithmetic, precedence,
/// column references, and the safety guarantees (null propagation, divide-by-
/// zero → null, rejects anything that isn't numbers/operators/[columns]).
/// </summary>
public class ReportFormulaTests
{
    private static readonly Dictionary<string, double?> Row = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bruising"] = 3,
        ["Decay"] = 2,
        ["Sample Size"] = 20,
        ["Empty"] = null,
    };

    [Theory]
    [InlineData("1 + 2", 3)]
    [InlineData("2 + 3 * 4", 14)]          // precedence
    [InlineData("(2 + 3) * 4", 20)]        // parentheses
    [InlineData("10 / 4", 2.5)]
    [InlineData("-5 + 2", -3)]             // unary minus
    [InlineData("2 * -3", -6)]
    [InlineData("[Bruising] + [Decay]", 5)]
    [InlineData("([Bruising] + [Decay]) / [Sample Size] * 100", 25)]
    public void Evaluates_arithmetic(string formula, double expected)
        => Assert.Equal(expected, ReportFormula.Evaluate(formula, Row));

    [Fact]
    public void Divide_by_zero_yields_null()
        => Assert.Null(ReportFormula.Evaluate("[Bruising] / 0", Row));

    [Fact]
    public void Null_column_propagates_to_null()
        => Assert.Null(ReportFormula.Evaluate("[Bruising] + [Empty]", Row));

    [Fact]
    public void Case_insensitive_column_reference()
        => Assert.Equal(5, ReportFormula.Evaluate("[bruising] + [DECAY]", Row));

    // ---- validation --------------------------------------------------------

    [Fact]
    public void Validate_accepts_known_columns()
        => Assert.True(ReportFormula.Validate("[Bruising] + [Decay]", new[] { "Bruising", "Decay" }).Ok);

    [Fact]
    public void Validate_rejects_unknown_column()
    {
        var r = ReportFormula.Validate("[Bruising] + [Nope]", new[] { "Bruising" });
        Assert.False(r.Ok);
        Assert.Contains("Nope", r.Error);
    }

    [Theory]
    [InlineData("1 + ")]                    // dangling operator
    [InlineData("(1 + 2")]                  // unbalanced
    [InlineData("1 ; 2")]                   // illegal char
    [InlineData("DROP TABLE x")]            // not arithmetic at all
    [InlineData("[Bruising] [Decay]")]      // two operands, no operator
    public void Validate_rejects_malformed(string formula)
        => Assert.False(ReportFormula.Validate(formula, new[] { "Bruising", "Decay" }).Ok);

    [Fact]
    public void Malformed_formula_evaluates_to_null_rather_than_throwing()
        => Assert.Null(ReportFormula.Evaluate("1 + )", Row));
}
