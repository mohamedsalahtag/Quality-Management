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
/// <summary>A pivot dimension. <paramref name="Category"/> groups it in the
/// analyzer's field list (the UI can also sort A-Z within/across groups).</summary>
public sealed record PivotDimension(string Key, string Display, string SqlExpression, string Category = "General");

public static class PivotCategories
{
    public const string SupplierPo = "Supplier & PO";
    public const string Material    = "Material";
    public const string QualityOrder= "Quality Order";
    public const string Sample      = "Sample";
    public const string Defect      = "Defect";
    public const string Dates       = "Dates & Time";
}

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

    /// <summary>V36 -- the only aggregations valid for a text dimension used
    /// as a measure (Excel-style "count of field" values). MIN/MAX of text
    /// cannot flow through the decimal result pipeline, so they are excluded.</summary>
    public static readonly string[] FieldAggs = { Count, CountDistinct };

    public static bool IsValid(string a) => Array.IndexOf(All, a) >= 0;
}

public static class PivotRegistry
{
    public static readonly PivotReport FlatDefects = new(
        Key:      "flat_defects",
        ViewName: "dbo.vw_qms_flat_defects",
        Dimensions: new []
        {
            // ---- Supplier / PO context ----
            new PivotDimension("Plant",            "Plant",            "Plant",            PivotCategories.SupplierPo),
            new PivotDimension("StorageLocation",  "Storage Loc.",     "StorageLocation",  PivotCategories.SupplierPo),
            new PivotDimension("VendorName",       "Supplier",         "VendorName",       PivotCategories.SupplierPo),
            new PivotDimension("VendorNo",         "Vendor No",        "VendorNo",         PivotCategories.SupplierPo),
            new PivotDimension("ContainerNo",      "Container",        "ContainerNo",      PivotCategories.SupplierPo),
            new PivotDimension("BolNo",            "BOL",              "BolNo",            PivotCategories.SupplierPo),
            new PivotDimension("Ebeln",            "PO (Ebeln)",       "Ebeln",            PivotCategories.SupplierPo),
            new PivotDimension("Sto",              "STO",              "Sto",              PivotCategories.SupplierPo),
            // ---- Material (MARA-merged) ----
            new PivotDimension("MaterialNo",       "Material No",      "MaterialNo",       PivotCategories.Material),
            new PivotDimension("MaterialDesc",     "Material Desc.",   "MaterialDesc",     PivotCategories.Material),
            new PivotDimension("MaterialGroup",    "Material group",   "MaterialGroup",    PivotCategories.Material),
            new PivotDimension("MaterialGroupDesc","Material group desc.", "MaterialGroupDesc", PivotCategories.Material),
            new PivotDimension("MajorCategory",    "Major category",   "MajorCategory",    PivotCategories.Material),
            new PivotDimension("SubMajorCategory", "Sub-major cat.",   "SubMajorCategory", PivotCategories.Material),
            new PivotDimension("Variety",          "Variety",          "Variety",          PivotCategories.Material),
            new PivotDimension("MaterialClass",    "Class",            "MaterialClass",    PivotCategories.Material),
            new PivotDimension("Origin",           "Origin",           "Origin",           PivotCategories.Material),
            new PivotDimension("Brand",            "Brand",            "Brand",            PivotCategories.Material),
            new PivotDimension("PackType",         "Pack Type",        "PackType",         PivotCategories.Material),
            new PivotDimension("MaterialSize",     "Material Size",    "MaterialSize",     PivotCategories.Material),
            // ---- Quality order ----
            new PivotDimension("QualityOrderNo",   "QO Number",        "QualityOrderNo",   PivotCategories.QualityOrder),
            new PivotDimension("QoStatus",         "QO status",        "QoStatus",         PivotCategories.QualityOrder),
            // ---- Sample ----
            new PivotDimension("SampleScope",      "Sample scope",     "SampleScope",      PivotCategories.Sample),
            new PivotDimension("Grower",           "Grower",           "Grower",           PivotCategories.Sample),
            new PivotDimension("PalletNo",         "Pallet No",        "PalletNo",         PivotCategories.Sample),
            new PivotDimension("GrowerPallet",     "Grower Pallet",    "GrowerPallet",     PivotCategories.Sample),
            new PivotDimension("PackCode",         "Pack Code",        "PackCode",         PivotCategories.Sample),
            new PivotDimension("DateCode",         "Date Code",        "DateCode",         PivotCategories.Sample),
            new PivotDimension("LabelValue",       "Label",            "LabelValue",       PivotCategories.Sample),
            new PivotDimension("LotNo",            "Lot No",           "LotNo",            PivotCategories.Sample),
            new PivotDimension("PackagingMaterial","Packaging Material","PackagingMaterial", PivotCategories.Sample),
            new PivotDimension("SampleCreatedBy",  "Inspected By",     "SampleCreatedBy",  PivotCategories.Sample),
            // ---- Defect ----
            new PivotDimension("DefectCode",       "Defect code",      "DefectCode",       PivotCategories.Defect),
            new PivotDimension("DefectName",       "Defect",           "DefectName",       PivotCategories.Defect),
            new PivotDimension("DefectCategory",   "Defect category",  "DefectCategory",   PivotCategories.Defect),
            new PivotDimension("SeverityCode",     "Severity",         "SeverityCode",     PivotCategories.Defect),
            // ---- Dates & time buckets ----
            new PivotDimension("PoYear",           "PO year",          "CAST(YEAR(PoDate) AS VARCHAR(4))", PivotCategories.Dates),
            new PivotDimension("PoMonth",          "PO month",         "FORMAT(PoDate, 'yyyy-MM')", PivotCategories.Dates),
            new PivotDimension("PoQuarter",        "PO quarter",       "CAST(YEAR(PoDate) AS VARCHAR(4)) + '-Q' + CAST(DATEPART(QUARTER, PoDate) AS VARCHAR(1))", PivotCategories.Dates),
            new PivotDimension("ArrivalMonth",     "Arrival month",    "FORMAT(ArrivalDate, 'yyyy-MM')", PivotCategories.Dates),
            new PivotDimension("DischargeDate",    "Discharge date",   "FORMAT(DischargeDate, 'yyyy-MM-dd')", PivotCategories.Dates),
            new PivotDimension("DischargeMonth",   "Discharge month",  "FORMAT(DischargeDate, 'yyyy-MM')", PivotCategories.Dates),
            new PivotDimension("ReceiveMonth",     "Receive month",    "FORMAT(ReceiveDate, 'yyyy-MM')", PivotCategories.Dates),
            new PivotDimension("ShippingMonth",    "Shipping month",   "FORMAT(ShippingDate, 'yyyy-MM')", PivotCategories.Dates),
            new PivotDimension("QoClosedMonth",    "QO finished month","FORMAT(QoClosedAt, 'yyyy-MM')", PivotCategories.Dates),
        },
        Measures: new []
        {
            new PivotMeasure("DefectValue",    "Defect count",    "DefectValue",
                new[] { PivotAggregations.Sum, PivotAggregations.Avg, PivotAggregations.Min, PivotAggregations.Max, PivotAggregations.Count }),
            new PivotMeasure("DefectPercentage","Defect %",       "DefectPercentage",
                new[] { PivotAggregations.Avg, PivotAggregations.Min, PivotAggregations.Max }),
            new PivotMeasure("DefectRate",     "Defect % (calc)", "DefectRate",
                new[] { PivotAggregations.Avg, PivotAggregations.Min, PivotAggregations.Max }),
            // SUM removed (2026-07-02): the view grain is one row per sample×defect,
            // so a sample with N defects would contribute its sample_size N times,
            // inflating SUM(SampleSize). Avg/Min/Max remain (still defect-weighted,
            // but not additively wrong).
            new PivotMeasure("SampleSize",     "Sample size",     "SampleSize",
                new[] { PivotAggregations.Avg, PivotAggregations.Min, PivotAggregations.Max }),
            // V37 (2026-07-07): PO line quantity from qms_arrival_item. Same
            // grain caveat as SampleSize — it repeats per defect row — so no SUM.
            new PivotMeasure("PoQuantity",     "PO quantity",     "PoQuantity",
                new[] { PivotAggregations.Avg, PivotAggregations.Min, PivotAggregations.Max }),
            new PivotMeasure("TransitDays",    "Transit days",    "TransitDays",
                new[] { PivotAggregations.Avg, PivotAggregations.Min, PivotAggregations.Max }),
            // Time Bar (days) = discharge/arrival date -> QO finish (closed) date.
            // Same grain caveat as SampleSize (repeats per defect row), so no SUM.
            new PivotMeasure("TimeBarDischarge","Time Bar (discharge)", "TimeBarDischarge",
                new[] { PivotAggregations.Avg, PivotAggregations.Min, PivotAggregations.Max }),
            new PivotMeasure("TimeBarArrival", "Time Bar (arrival)",   "TimeBarArrival",
                new[] { PivotAggregations.Avg, PivotAggregations.Min, PivotAggregations.Max }),
            new PivotMeasure("NetWeight",      "Net weight",      "NetWeight",
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
