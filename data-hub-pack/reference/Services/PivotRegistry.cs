namespace SharbatlyQMS.Web.Services.Reports;

/// <summary>
/// V34 (2026-06-20). Whitelist of dimensions / measures / aggregations the
/// Perspective Analyzer is allowed to pivot on, per report. Every SQL
/// identifier in the dynamic GROUP BY comes from this file -- HTTP requests
/// only carry KEYS (e.g. "Plant"), never raw SQL. That is the injection
/// guardrail.
///
/// Adding a dimension: add a <see cref="PivotDimension"/> with a key that
/// is safe to expose, a user-facing display name, and a SQL expression
/// against the report's view. The expression is interpolated AS-IS into
/// the SELECT / GROUP BY so it must be a constant string here -- NEVER
/// concatenate user input into a SqlColumn.
/// </summary>
public sealed record PivotDimension(string Key, string Display, string SqlExpression);

public sealed record PivotMeasure(string Key, string Display, string SqlInner, string[] AllowedAggs);

public sealed record PivotReport(
    string                            Key,
    string                            ViewName,
    IReadOnlyList<PivotDimension>     Dimensions,
    IReadOnlyList<PivotMeasure>       Measures);

public static class PivotAggregations
{
    public const string Sum            = "SUM";
    public const string Avg            = "AVG";
    public const string Min            = "MIN";
    public const string Max            = "MAX";
    public const string Count          = "COUNT";
    public const string CountDistinct  = "COUNT_DISTINCT";

    public static readonly string[] All = { Sum, Avg, Min, Max, Count, CountDistinct };

    public static bool IsValid(string a) => Array.IndexOf(All, a) >= 0;
}

public static class PivotRegistry
{
    public static readonly PivotReport FlatDefects = new(
        Key:      "flat_defects",
        ViewName: "dbo.vw_qms_flat_defects",
        Dimensions: new []
        {
            // ---- Arrival / PO context ----
            new PivotDimension("Plant",            "Plant",            "Plant"),
            new PivotDimension("StorageLocation",  "Storage Loc.",     "StorageLocation"),
            new PivotDimension("VendorName",       "Supplier",         "VendorName"),
            new PivotDimension("VendorNo",         "Vendor No",        "VendorNo"),
            new PivotDimension("ContainerNo",      "Container",        "ContainerNo"),
            new PivotDimension("BolNo",            "BOL",              "BolNo"),
            new PivotDimension("Ebeln",            "PO (Ebeln)",       "Ebeln"),
            // ---- Material (MARA-merged) ----
            new PivotDimension("MaterialNo",       "Material No",      "MaterialNo"),
            new PivotDimension("MaterialGroup",    "Material group",   "MaterialGroup"),
            new PivotDimension("MajorCategory",    "Major category",   "MajorCategory"),
            new PivotDimension("SubMajorCategory", "Sub-major cat.",   "SubMajorCategory"),
            new PivotDimension("Variety",          "Variety",          "Variety"),
            new PivotDimension("MaterialClass",    "Class",            "MaterialClass"),
            new PivotDimension("Origin",           "Origin",           "Origin"),
            new PivotDimension("Brand",            "Brand",            "Brand"),
            new PivotDimension("PackType",         "Pack Type",        "PackType"),
            // ---- QO + Sample ----
            new PivotDimension("QualityOrderNo",   "QO Number",        "QualityOrderNo"),
            new PivotDimension("QoStatus",         "QO status",        "QoStatus"),
            new PivotDimension("SampleScope",      "Sample scope",     "SampleScope"),
            // ---- Defect ----
            new PivotDimension("DefectCode",       "Defect code",      "DefectCode"),
            new PivotDimension("DefectName",       "Defect",           "DefectName"),
            new PivotDimension("DefectCategory",   "Defect category",  "DefectCategory"),
            new PivotDimension("SeverityCode",     "Severity",         "SeverityCode"),
            // ---- Time buckets ----
            new PivotDimension("PoYear",           "PO year",          "CAST(YEAR(PoDate) AS VARCHAR(4))"),
            new PivotDimension("PoMonth",          "PO month",         "FORMAT(PoDate, 'yyyy-MM')"),
            new PivotDimension("PoQuarter",        "PO quarter",       "CAST(YEAR(PoDate) AS VARCHAR(4)) + '-Q' + CAST(DATEPART(QUARTER, PoDate) AS VARCHAR(1))"),
            new PivotDimension("ArrivalMonth",     "Arrival month",    "FORMAT(ArrivalDate, 'yyyy-MM')"),
            new PivotDimension("ReceiveMonth",     "Receive month",    "FORMAT(ReceiveDate, 'yyyy-MM')"),
            new PivotDimension("ShippingMonth",    "Shipping month",   "FORMAT(ShippingDate, 'yyyy-MM')"),
        },
        Measures: new []
        {
            new PivotMeasure("DefectValue",    "Defect count",    "DefectValue",
                new[] { PivotAggregations.Sum, PivotAggregations.Avg, PivotAggregations.Min, PivotAggregations.Max, PivotAggregations.Count }),
            new PivotMeasure("DefectPercentage","Defect %",       "DefectPercentage",
                new[] { PivotAggregations.Avg, PivotAggregations.Min, PivotAggregations.Max }),
            new PivotMeasure("DefectRate",     "Defect % (calc)", "DefectRate",
                new[] { PivotAggregations.Avg, PivotAggregations.Min, PivotAggregations.Max }),
            new PivotMeasure("SampleSize",     "Sample size",     "SampleSize",
                new[] { PivotAggregations.Sum, PivotAggregations.Avg, PivotAggregations.Min, PivotAggregations.Max }),
            new PivotMeasure("TransitDays",    "Transit days",    "TransitDays",
                new[] { PivotAggregations.Avg, PivotAggregations.Min, PivotAggregations.Max }),
            new PivotMeasure("Samples",        "Sample count",    "SampleId",
                new[] { PivotAggregations.CountDistinct }),
            new PivotMeasure("Orders",         "QO count",        "QualityOrderId",
                new[] { PivotAggregations.CountDistinct }),
            new PivotMeasure("Defects",        "Defect rows",     "*",
                new[] { PivotAggregations.Count }),
        });

    public static readonly IReadOnlyDictionary<string, PivotReport> All =
        new Dictionary<string, PivotReport>(StringComparer.OrdinalIgnoreCase)
        {
            [FlatDefects.Key] = FlatDefects,
            // containers: see PivotService -- intentionally not registered yet.
        };

    /// <summary>
    /// Available chart renderers in the UI. Kept here so the schema endpoint
    /// can return a single source of truth.
    /// </summary>
    public static readonly string[] Renderers =
    {
        "Table", "Bar", "Stacked Bar", "Line", "Area", "Heatmap"
    };
}
