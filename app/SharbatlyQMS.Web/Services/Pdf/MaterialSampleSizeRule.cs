namespace SharbatlyQMS.Web.Services.Pdf;

/// <summary>
/// Decides whether the QC report may print ONE Sample Size on a material card.
///
/// For a fixed pack (apples, pears — the count per carton follows the pack
/// size) a single material-level figure is accurate and useful, so it always
/// prints. Bananas are counted as individual fingers and the number varies from
/// one sampled carton to the next, so one number at material level misrepresents
/// the inspection: there the figure is dropped and the authoritative per-carton
/// size on each sample card is the only one shown.
///
/// The rule is deliberately scoped to the groups listed here rather than
/// applied to "any material whose samples disagree" — that broader version was
/// measured against production and would also have suppressed the figure on
/// 19 apple, 8 plum, 12 pear and 23 kiwi materials, which nobody asked for.
/// Add a group here if its carton count is genuinely variable.
/// </summary>
public static class MaterialSampleSizeRule
{
    private static readonly HashSet<string> VariableCountGroups =
        new(StringComparer.OrdinalIgnoreCase) { "BANANA" };

    /// <summary>True when the material card should carry a Sample Size cell.</summary>
    public static bool ShowAtMaterialLevel(string? materialGroup, IReadOnlyList<short?> sampleSizes)
    {
        if (!VariableCountGroups.Contains((materialGroup ?? "").Trim()))
            return true;

        // Even in a variable-count group, a material whose cartons happened to
        // record the same size has nothing misleading to hide.
        var distinct = sampleSizes.Where(x => x is > 0).Select(x => x!.Value).Distinct().Count();
        return distinct <= 1;
    }
}
