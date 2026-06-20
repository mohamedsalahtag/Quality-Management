namespace SharbatlyQMS.Web.Models.Reports;

/// <summary>
/// V34 (2026-06-20). POST /Reports/Pivot body. The <c>Rows / Cols / Measure /
/// Agg</c> values are dimension / measure / aggregation KEYS from the server's
/// <see cref="SharbatlyQMS.Web.Services.Reports.PivotRegistry"/>, never raw
/// SQL. <see cref="SharbatlyQMS.Web.Services.Reports.PivotService"/> validates
/// each one and rejects anything not in the registry, which is the dynamic-SQL
/// guardrail.
/// </summary>
public class PivotRequest
{
    public string?                ReportKey        { get; set; }
    public string[]               Rows             { get; set; } = Array.Empty<string>();
    public string[]               Cols             { get; set; } = Array.Empty<string>();

    /// <summary>Deprecated scalar fields kept so saved perspectives from
    /// v34.0..v34.2 still load. When <see cref="Measures"/> is null/empty the
    /// service synthesises a single-element list from these.</summary>
    public string?                Measure          { get; set; }
    public string?                Agg              { get; set; }

    /// <summary>
    /// V34.3 (2026-06-20). Multi-measure support. Each entry adds one
    /// aggregated column to the result. Measure keys + aggs are validated
    /// against <see cref="SharbatlyQMS.Web.Services.Reports.PivotRegistry"/>.
    /// </summary>
    public List<PivotMeasureRequest>? Measures     { get; set; }

    public int?                   TopN             { get; set; }   // optional cap by row total
    public FlatDefectFilter?      Filter           { get; set; }   // pushed down to SQL WHERE

    /// <summary>
    /// V34.1 (2026-06-20). When true, <see cref="Filter"/> is ignored so the
    /// analyzer pivots over the whole dataset (no PO-date window, no plant /
    /// vendor / etc. constraints). The drills below still apply.
    /// </summary>
    public bool                   IgnorePageFilter { get; set; }

    /// <summary>
    /// V34.1 (2026-06-20). Analyzer-only WHERE constraints. Each drill is an
    /// AND on top of whatever scope is active. Dim keys validated against
    /// <see cref="SharbatlyQMS.Web.Services.Reports.PivotRegistry"/>; values
    /// bind as parameters.
    /// </summary>
    public List<PivotDrillFilter>? Drills          { get; set; }
}

public sealed class PivotDrillFilter
{
    public string?   DimensionKey { get; set; }
    public string[]? Values       { get; set; }   // "(null)" sentinel maps to IS NULL
}

/// <summary>
/// V34.3 (2026-06-20). One element per measure column in the result.
/// Mirrors Excel's "Σ Values" zone behaviour.
/// </summary>
public sealed class PivotMeasureRequest
{
    public string? Key    { get; set; }   // registry measure key, e.g. "DefectValue"
    public string? Agg    { get; set; }   // SUM | AVG | MIN | MAX | COUNT | COUNT_DISTINCT
    public string? Format { get; set; }   // optional override: auto | int | dec1 | dec2 | percent
    public string? Label  { get; set; }   // optional user-facing override
}
