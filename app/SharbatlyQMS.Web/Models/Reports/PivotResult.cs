namespace SharbatlyQMS.Web.Models.Reports;

/// <summary>
/// V34 (2026-06-20). Result of a pivot run. Tall layout (one Cell per non-empty
/// intersection) keeps the JSON small for sparse pivots; the client expands it
/// into a matrix at render time. RowKeys / ColKeys are the distinct compound
/// keys in display order so the client knows the matrix shape without an
/// extra pass.
///
/// V34.3 (2026-06-20). Multi-measure: every cell carries one decimal per
/// measure (<see cref="PivotCell.Values"/>) instead of a single scalar; totals
/// are computed per measure (`[axisIdx][measureIdx]`).
/// </summary>
public class PivotResult
{
    public string[]            RowDimensions { get; set; } = Array.Empty<string>();
    public string[]            ColDimensions { get; set; } = Array.Empty<string>();
    public PivotMeasureInfo[]  Measures      { get; set; } = Array.Empty<PivotMeasureInfo>();
    public string[][]          RowKeys       { get; set; } = Array.Empty<string[]>();
    public string[][]          ColKeys       { get; set; } = Array.Empty<string[]>();
    public PivotCell[]         Cells         { get; set; } = Array.Empty<PivotCell>();
    public decimal[][]         RowTotals     { get; set; } = Array.Empty<decimal[]>();
    public decimal[][]         ColTotals     { get; set; } = Array.Empty<decimal[]>();
    public decimal[]           GrandTotals   { get; set; } = Array.Empty<decimal>();
    /// <summary>
    /// Per-measure flag: is a roll-up total mathematically meaningful for this
    /// measure's aggregation? SUM/COUNT (additive), MIN and MAX roll up
    /// correctly; AVG and COUNT_DISTINCT do NOT (you cannot average averages or
    /// sum distinct counts across groups), so their totals are suppressed rather
    /// than shown wrong.
    /// </summary>
    public bool[]              MeasureTotalsValid { get; set; } = Array.Empty<bool>();
    public int                 RowsScanned   { get; set; }
    public bool                Truncated     { get; set; }
}

public class PivotCell
{
    public int       RowIndex { get; set; }   // index into RowKeys
    public int       ColIndex { get; set; }   // index into ColKeys
    /// <summary>One value per measure in the request order. Nullable so a
    /// cell that returns NULL from SQL (e.g. AVG over an empty set) stays
    /// distinguishable from 0.</summary>
    public decimal?[] Values   { get; set; } = Array.Empty<decimal?>();
}

public class PivotMeasureInfo
{
    public string  Key    { get; set; } = "";
    public string  Label  { get; set; } = "";
    public string  Agg    { get; set; } = "";
    public string? Format { get; set; }
}
