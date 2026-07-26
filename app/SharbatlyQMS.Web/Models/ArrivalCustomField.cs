using System.Globalization;

namespace SharbatlyQMS.Web.Models;

/// <summary>
/// V36 (2026-07-07). Admin-defined extra field on an Arrival, linked to
/// exactly one material group. The field is only rendered on arrivals whose
/// line items contain that material group, and is printed in the Arrival
/// Checklist PDF identity block after Seal Number.
///
/// One class serves both the Parameters catalog screen (definition +
/// IsInUse) and the arrival Details page / PDF (definition + this arrival's
/// value) — Dapper simply leaves the columns a query doesn't select at
/// their defaults.
/// </summary>
public class ArrivalCustomField
{
    // -- definition (qms_arrival_field) ---------------------------------
    public int     FieldId       { get; set; }
    public string  FieldName     { get; set; } = "";
    /// <summary>'Text' | 'Numeric' | 'Date' | 'YesNo'</summary>
    public string  ValueKind     { get; set; } = "Text";
    public string  MaterialGroup { get; set; } = "";
    public int     SortOrder     { get; set; } = 500;
    public bool    IsActive      { get; set; } = true;
    /// <summary>Catalog screen only: at least one arrival stores a value.</summary>
    public bool    IsInUse       { get; set; }

    // -- per-arrival value (qms_arrival_field_value) ---------------------
    public string?   TextValue    { get; set; }
    public decimal?  NumericValue { get; set; }
    public DateTime? DateValue    { get; set; }

    /// <summary>The stored value rendered for display (Details page + PDF).</summary>
    public string DisplayValue => ValueKind switch
    {
        "Numeric" => NumericValue?.ToString("0.####", CultureInfo.InvariantCulture) ?? "",
        "Date"    => DateValue?.ToString("yyyy-MM-dd") ?? "",
        _         => TextValue ?? "",
    };

    /// <summary>The value as it should populate an editable input.</summary>
    public string InputValue => DisplayValue;

    public static readonly string[] ValueKinds = { "Text", "Numeric", "Date", "YesNo" };
}
