// =====================================================================
// data-hub-pack — example: fruit-quality defects (Sharbatly QMS)
//
// CANONICAL instantiation. The four pieces a host provides — filter
// shape, view, registry entry, filter adapter — gathered in one file so
// the seam is visible at a glance. The live system splits these across
// several files in the QMS repo; this example is the abridged version
// that proves the contract.
//
// What the QMS team wired:
//   - vw_qms_flat_defects     -- one row per (sample × entered defect)
//   - FlatDefectFilter        -- 18 filter slots
//   - PivotRegistry.FlatDefects -- ~30 dims + 8 measures
//   - FlatDefectFilterAdapter -- pushes every slot to SQL via DynamicParameters
// =====================================================================

using System.Data;
using System.Text;
using Dapper;

namespace SharbatlyQMS.Web.Models.Reports
{
    // ----- 1. Filter shape ----------------------------------------------
    // 18 optional slots. Each can independently narrow the analyzer's scope.
    public sealed class FlatDefectFilter
    {
        public long?     QualityOrderId  { get; set; }
        public long?     ArrivalId       { get; set; }
        public string?   Status          { get; set; }   // Initial/Open/Submitted/Closed/Cancelled
        public string?   Plant           { get; set; }
        public string?   MaterialGroup   { get; set; }
        public string?   MajorCategory   { get; set; }
        public string?   Variety         { get; set; }
        public string?   Origin          { get; set; }
        public string?   MaterialClass   { get; set; }
        public string?   VendorName      { get; set; }   // substring match
        public string?   VendorNo        { get; set; }   // exact
        public string?   StorageLocation { get; set; }
        public string?   ContainerNo     { get; set; }
        public string?   BolNo           { get; set; }
        public string?   Ebeln           { get; set; }
        public string?   SampleScope     { get; set; }
        public DateTime? PoFrom          { get; set; }
        public DateTime? PoTo            { get; set; }
    }
}

namespace SharbatlyQMS.Web.Services.Reports
{
    // ----- 2. Filter adapter -- the plug-in seam ------------------------
    // Translates the host's filter POCO into parameterised SQL WHERE
    // fragments. Note every value binds through DynamicParameters; no
    // string concat. The fragments are emitted in the same form that
    // PivotService's WHERE-builder expects: "AND <expr> = @param".
    public sealed class FlatDefectFilterAdapter : IDataHubFilterAdapter
    {
        public void AppendFilterWhere(StringBuilder sb, DynamicParameters p, object? filter)
        {
            if (filter is not FlatDefectFilter f) return;

            if (f.QualityOrderId.HasValue)
            {
                sb.Append(" AND QualityOrderId = @qoId");
                p.Add("qoId", f.QualityOrderId.Value, DbType.Int64);
            }
            if (f.ArrivalId.HasValue)
            {
                sb.Append(" AND ArrivalId = @arrivalId");
                p.Add("arrivalId", f.ArrivalId.Value, DbType.Int64);
            }
            if (!string.IsNullOrWhiteSpace(f.Status))
            {
                sb.Append(" AND QoStatus = @status");
                p.Add("status", f.Status, DbType.AnsiString);
            }
            if (!string.IsNullOrWhiteSpace(f.Plant))
            {
                sb.Append(" AND Plant = @plant");
                p.Add("plant", f.Plant, DbType.AnsiString);
            }
            if (!string.IsNullOrWhiteSpace(f.MaterialGroup))
            {
                sb.Append(" AND MaterialGroup = @matGroup");
                p.Add("matGroup", f.MaterialGroup, DbType.AnsiString);
            }
            if (!string.IsNullOrWhiteSpace(f.MajorCategory))
            {
                sb.Append(" AND MajorCategory = @majorCat");
                p.Add("majorCat", f.MajorCategory, DbType.String);
            }
            if (!string.IsNullOrWhiteSpace(f.Variety))
            {
                sb.Append(" AND Variety = @variety");
                p.Add("variety", f.Variety, DbType.String);
            }
            if (!string.IsNullOrWhiteSpace(f.Origin))
            {
                sb.Append(" AND Origin = @origin");
                p.Add("origin", f.Origin, DbType.String);
            }
            if (!string.IsNullOrWhiteSpace(f.MaterialClass))
            {
                sb.Append(" AND MaterialClass = @matClass");
                p.Add("matClass", f.MaterialClass, DbType.String);
            }
            if (!string.IsNullOrWhiteSpace(f.VendorName))
            {
                // Substring match -- the only non-equality filter in the bag.
                sb.Append(" AND VendorName LIKE '%' + @vendorName + '%'");
                p.Add("vendorName", f.VendorName, DbType.String);
            }
            if (!string.IsNullOrWhiteSpace(f.VendorNo))
            {
                sb.Append(" AND VendorNo = @vendorNo");
                p.Add("vendorNo", f.VendorNo, DbType.AnsiString);
            }
            if (!string.IsNullOrWhiteSpace(f.StorageLocation))
            {
                sb.Append(" AND StorageLocation = @storageLoc");
                p.Add("storageLoc", f.StorageLocation, DbType.AnsiString);
            }
            if (!string.IsNullOrWhiteSpace(f.ContainerNo))
            {
                sb.Append(" AND ContainerNo = @containerNo");
                p.Add("containerNo", f.ContainerNo, DbType.AnsiString);
            }
            if (!string.IsNullOrWhiteSpace(f.BolNo))
            {
                sb.Append(" AND BolNo = @bolNo");
                p.Add("bolNo", f.BolNo, DbType.AnsiString);
            }
            if (!string.IsNullOrWhiteSpace(f.Ebeln))
            {
                sb.Append(" AND Ebeln = @ebeln");
                p.Add("ebeln", f.Ebeln, DbType.AnsiString);
            }
            if (!string.IsNullOrWhiteSpace(f.SampleScope))
            {
                sb.Append(" AND SampleScope = @sampleScope");
                p.Add("sampleScope", f.SampleScope, DbType.AnsiString);
            }
            // Date range. NULL PoDate rows are INCLUDED so arrivals whose
            // cache row is missing still surface in the default window.
            if (f.PoFrom.HasValue)
            {
                sb.Append(" AND (PoDate IS NULL OR PoDate >= @poFrom)");
                p.Add("poFrom", f.PoFrom.Value.Date, DbType.Date);
            }
            if (f.PoTo.HasValue)
            {
                // Roll a whole-date "to" forward by one day so the user's "to = today"
                // includes today's POs.
                var poToExcl = f.PoTo.Value.TimeOfDay == TimeSpan.Zero
                    ? f.PoTo.Value.AddDays(1)
                    : f.PoTo.Value;
                sb.Append(" AND (PoDate IS NULL OR PoDate < @poToExcl)");
                p.Add("poToExcl", poToExcl.Date, DbType.Date);
            }
        }
    }

    // ----- 3. Registry entry --------------------------------------------
    // 30+ dimensions, 8 measures, every "safe to group on" column in the
    // view. Time-bucket dims are constant SQL expressions; user input never
    // reaches a SELECT or GROUP BY.
    public static class FlatDefectsRegistration
    {
        public static readonly PivotReport FlatDefects = new(
            Key:      "flat_defects",
            ViewName: "dbo.vw_qms_flat_defects",
            Dimensions: new []
            {
                new PivotDimension("Plant",            "Plant",            "Plant"),
                new PivotDimension("StorageLocation",  "Storage Loc.",     "StorageLocation"),
                new PivotDimension("VendorName",       "Supplier",         "VendorName"),
                new PivotDimension("VendorNo",         "Vendor No",        "VendorNo"),
                new PivotDimension("ContainerNo",      "Container",        "ContainerNo"),
                new PivotDimension("BolNo",            "BOL",              "BolNo"),
                new PivotDimension("Ebeln",            "PO (Ebeln)",       "Ebeln"),
                new PivotDimension("MaterialNo",       "Material No",      "MaterialNo"),
                new PivotDimension("MaterialGroup",    "Material group",   "MaterialGroup"),
                new PivotDimension("MajorCategory",    "Major category",   "MajorCategory"),
                new PivotDimension("SubMajorCategory", "Sub-major cat.",   "SubMajorCategory"),
                new PivotDimension("Variety",          "Variety",          "Variety"),
                new PivotDimension("MaterialClass",    "Class",            "MaterialClass"),
                new PivotDimension("Origin",           "Origin",           "Origin"),
                new PivotDimension("Brand",            "Brand",            "Brand"),
                new PivotDimension("PackType",         "Pack Type",        "PackType"),
                new PivotDimension("QualityOrderNo",   "QO Number",        "QualityOrderNo"),
                new PivotDimension("QoStatus",         "QO status",        "QoStatus"),
                new PivotDimension("SampleScope",      "Sample scope",     "SampleScope"),
                new PivotDimension("DefectCode",       "Defect code",      "DefectCode"),
                new PivotDimension("DefectName",       "Defect",           "DefectName"),
                new PivotDimension("DefectCategory",   "Defect category",  "DefectCategory"),
                new PivotDimension("SeverityCode",     "Severity",         "SeverityCode"),
                // Time-bucket dims -- constant SQL expressions, safe to interpolate.
                new PivotDimension("PoYear",           "PO year",          "CAST(YEAR(PoDate) AS VARCHAR(4))"),
                new PivotDimension("PoMonth",          "PO month",         "FORMAT(PoDate, 'yyyy-MM')"),
                new PivotDimension("PoQuarter",        "PO quarter",       "CAST(YEAR(PoDate) AS VARCHAR(4)) + '-Q' + CAST(DATEPART(QUARTER, PoDate) AS VARCHAR(1))"),
                new PivotDimension("ArrivalMonth",     "Arrival month",    "FORMAT(ArrivalDate, 'yyyy-MM')"),
                new PivotDimension("ReceiveMonth",     "Receive month",    "FORMAT(ReceiveDate, 'yyyy-MM')"),
                new PivotDimension("ShippingMonth",    "Shipping month",   "FORMAT(ShippingDate, 'yyyy-MM')"),
            },
            Measures: new []
            {
                new PivotMeasure("DefectValue",      "Defect count",     "DefectValue",
                    new[] { "SUM","AVG","MIN","MAX","COUNT" }),
                new PivotMeasure("DefectPercentage", "Defect %",         "DefectPercentage",
                    new[] { "AVG","MIN","MAX" }),
                new PivotMeasure("DefectRate",       "Defect % (calc)",  "DefectRate",
                    new[] { "AVG","MIN","MAX" }),
                new PivotMeasure("SampleSize",       "Sample size",      "SampleSize",
                    new[] { "SUM","AVG","MIN","MAX" }),
                new PivotMeasure("TransitDays",      "Transit days",     "TransitDays",
                    new[] { "AVG","MIN","MAX" }),
                new PivotMeasure("Samples",          "Sample count",     "SampleId",
                    new[] { "COUNT_DISTINCT" }),
                new PivotMeasure("Orders",           "QO count",         "QualityOrderId",
                    new[] { "COUNT_DISTINCT" }),
                new PivotMeasure("Defects",          "Defect rows",      "*",
                    new[] { "COUNT" }),
            });
    }
}

/* ----- 4. The view it points at ----------------------------------------

   The DDL lives in app/db/V34__add_qms_perspective.sql in the QMS repo.
   Distilled shape:

   CREATE OR ALTER VIEW dbo.vw_qms_flat_defects AS
   SELECT
       s.sample_id          AS SampleId,
       qo.quality_order_id  AS QualityOrderId,
       qo.status_code       AS QoStatus,
       a.plant              AS Plant,
       a.container_no       AS ContainerNo,
       a.vendor_name        AS VendorName,
       ai.storage_location  AS StorageLocation,
       cc.doc_date          AS PoDate,
       ss.sailing_date      AS ShippingDate,
       ss.transit_days      AS TransitDays,
       m.material_group     AS MaterialGroup,
       COALESCE(NULLIF(m.variety, ''), mc.variety_name) AS Variety,
       ...
       dc.defect_name       AS DefectName,
       dc.defect_category   AS DefectCategory,
       sd.severity_code     AS SeverityCode,
       sd.defect_value      AS DefectValue,
       CASE WHEN ISNULL(s.sample_size, 0) > 0
            THEN CAST(sd.defect_value AS DECIMAL(18,4)) * 100.0 / s.sample_size
       END                  AS DefectRate
   FROM qms_sample s
   JOIN qms_quality_order qo ON ...
   JOIN qms_sample_defect sd ON ...
   JOIN qms_defect_catalog dc ON ...
   LEFT JOIN qms_sap_material_cache mc ON ...
   OUTER APPLY (SELECT TOP 1 ... FROM qms_sap_container_cache ...) cc
   LEFT JOIN qms_shipment_snapshot ss ON ...
   WHERE s.is_deleted = 0;
   GO

   Key shape notes:
   - One row per (active sample × entered defect). The view excludes
     zero-fill catalog rows so pivots count what was actually entered.
   - MARA-merged categorical dims via COALESCE(snapshot, cache) so old
     QOs with NULL snapshot still pivot.
   - Every dim's SqlExpression in the registry above is exactly the
     column alias above.
------------------------------------------------------------------------ */
