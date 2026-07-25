using SharbatlyQMS.Web.Models.Reports;

namespace SharbatlyQMS.Web.Services.Reports;

/// <summary>
/// Whitelist of the static arrival / PO / shipment / material / sample fields a
/// custom Report Builder design may reference, plus how to read each one off a
/// <see cref="FlatDefectRow"/>. Same safety model as <see cref="PivotRegistry"/>:
/// an HTTP request carries a KEY (e.g. "ContainerNo"), never a column name or
/// SQL, and the exporter resolves it here. A key absent from this table is
/// rejected before any data is read.
///
/// Defect / reading / header columns are NOT listed here — they are dynamic and
/// resolved per material group at request time from the catalogues. Their column
/// keys are prefixed so the kind is unambiguous:
///   d:{defectId}  r:{readingCode}  sh:{sampleHeaderCode}  mh:{materialHeaderCode}
///   calc:{n}      blank:{n}
/// </summary>
public enum ReportFieldType { Text, Number, Date }

public sealed record StaticField(
    string Key,
    string Label,
    ReportFieldType Type,
    Func<FlatDefectRow, object?> Get);

public static class ReportBuilderRegistry
{
    /// <summary>report_key stored in qms_perspective for saved Report Builder designs.</summary>
    public const string ReportKey = "report_builder";

    /// <summary>Column-key prefixes for the dynamic (per-group) column kinds.</summary>
    public const string DefectPrefix       = "d:";
    public const string ReadingPrefix      = "r:";
    public const string SampleHeaderPrefix = "sh:";
    public const string MaterialHeaderPrefix = "mh:";
    public const string CalcPrefix         = "calc:";
    public const string EmptyPrefix        = "blank:";

    public static readonly IReadOnlyList<StaticField> StaticFields = new StaticField[]
    {
        // ---- Arrival / PO / shipment context ----
        new("ArrivalNo",        "Arrival Number",   ReportFieldType.Text,   r => r.ArrivalNo),
        new("Plant",            "Plant",            ReportFieldType.Text,   r => r.Plant),
        new("StorageLocation",  "Storage Location", ReportFieldType.Text,   r => r.StorageLocation),
        new("ContainerNo",      "Container",        ReportFieldType.Text,   r => r.ContainerNo),
        new("BolNo",            "BOL",              ReportFieldType.Text,   r => r.BolNo),
        new("Ebeln",            "PO Number",        ReportFieldType.Text,   r => r.Ebeln),
        new("Sto",              "STO",              ReportFieldType.Text,   r => r.Sto),
        new("PoDate",           "PO Date",          ReportFieldType.Date,   r => r.PoDate),
        new("LoadingDate",      "Loading Date",     ReportFieldType.Date,   r => r.LoadingDate),
        new("ShippingDate",     "Shipping Date",    ReportFieldType.Date,   r => r.ShippingDate),
        new("ArrivalDate",      "Arrival Date",     ReportFieldType.Date,   r => r.ArrivalDate),
        new("ReceiveDate",      "Receive Date",     ReportFieldType.Date,   r => r.ReceiveDate),
        new("TransitDays",      "Transit Days",     ReportFieldType.Number, r => (int?)r.TransitDays),
        new("VendorNo",         "Vendor No",        ReportFieldType.Text,   r => r.VendorNo),
        new("VendorName",       "Supplier",         ReportFieldType.Text,   r => r.VendorName),

        // ---- Quality order ----
        new("QualityOrderNo",   "QC Order Number",  ReportFieldType.Text,   r => r.QualityOrderNo),
        new("QoStatus",         "QC Status",        ReportFieldType.Text,   r => r.QoStatusDisplay),
        new("QoCreatedAt",      "QC Order Created", ReportFieldType.Date,   r => r.QoCreatedAt),

        // ---- Material (MARA-merged) ----
        new("MaterialNo",       "Material No",      ReportFieldType.Text,   r => r.MaterialNo),
        new("MaterialDesc",     "Material Desc.",   ReportFieldType.Text,   r => r.MaterialDesc),
        new("MaterialGroup",    "Material Group",   ReportFieldType.Text,   r => r.MaterialGroup),
        new("MaterialGroupDesc","Material Group Desc.", ReportFieldType.Text, r => r.MaterialGroupDesc),
        new("MajorCategory",    "Major Category",   ReportFieldType.Text,   r => r.MajorCategory),
        new("SubMajorCategory", "Sub-Major Category", ReportFieldType.Text, r => r.SubMajorCategory),
        new("Variety",          "Variety",          ReportFieldType.Text,   r => r.Variety),
        new("MaterialClass",    "Class",            ReportFieldType.Text,   r => r.MaterialClass),
        new("Origin",           "Origin",           ReportFieldType.Text,   r => r.Origin),
        new("Brand",            "Brand",            ReportFieldType.Text,   r => r.Brand),
        new("PackType",         "Pack Type",        ReportFieldType.Text,   r => r.PackType),
        new("PackCode",         "Pack Code",        ReportFieldType.Text,   r => r.PackCode),
        new("NetWeight",        "Net Weight",       ReportFieldType.Number, r => r.NetWeight),
        new("ArrivalItemQuantity","PO Quantity",    ReportFieldType.Number, r => r.ArrivalItemQuantity),
        new("ArrivalItemUom",   "UoM",              ReportFieldType.Text,   r => r.ArrivalItemUom),
        new("MaterialSize",     "Material Size",    ReportFieldType.Text,   r => r.MaterialSize),
        new("MaterialSampleSize","Material Sample Size", ReportFieldType.Number, r => (int?)r.MaterialSampleSize),

        // ---- Sample ----
        new("SampleNo",         "Sample Number",    ReportFieldType.Number, r => r.SampleNo),
        new("SampleScope",      "Sample Scope",     ReportFieldType.Text,   r => r.SampleScope),
        new("SampleSize",       "Sample Size",      ReportFieldType.Number, r => (int?)r.SampleSize),
        new("Grower",           "Grower",           ReportFieldType.Text,   r => r.Grower),
        new("PalletNo",         "Pallet Number",    ReportFieldType.Text,   r => r.PalletNo),
        new("GrowerPallet",     "Grower Pallet",    ReportFieldType.Text,   r => r.GrowerPallet),
        new("DateCode",         "Date Code",        ReportFieldType.Text,   r => r.DateCode),
        new("LabelValue",       "Label",            ReportFieldType.Text,   r => r.LabelValue),
        new("LotNo",            "Lot Number",       ReportFieldType.Text,   r => r.LotNo),
        new("SampleCreatedAt",  "Inspection Date",  ReportFieldType.Date,   r => r.SampleCreatedAt),
        new("SampleCreatedBy",  "Inspected By",     ReportFieldType.Text,   r => r.SampleCreatedBy),
    };

    private static readonly IReadOnlyDictionary<string, StaticField> _byKey =
        StaticFields.ToDictionary(f => f.Key, StringComparer.Ordinal);

    public static bool TryGetStatic(string key, out StaticField field)
        => _byKey.TryGetValue(key, out field!);

    public static bool IsStatic(string key) => _byKey.ContainsKey(key);
}
