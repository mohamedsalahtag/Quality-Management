using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharbatlyQMS.Web.Models.Reports;

/// <summary>
/// A saved Report Builder design. Persisted as the <c>config_json</c> of a
/// <c>qms_perspective</c> row whose <c>report_key</c> is
/// <see cref="Services.Reports.ReportBuilderRegistry.ReportKey"/>.
/// </summary>
public class ReportBuilderDefinition
{
    /// <summary>Drives the designer palette and the default report filter.</summary>
    public string? MaterialGroup { get; set; }

    public List<ReportColumn> Columns { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static ReportBuilderDefinition FromJson(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? new ReportBuilderDefinition()
            : JsonSerializer.Deserialize<ReportBuilderDefinition>(json, JsonOpts) ?? new ReportBuilderDefinition();
}

public class ReportColumn
{
    /// <summary>Column key. Static keys come from
    /// <see cref="Services.Reports.ReportBuilderRegistry.StaticFields"/>; dynamic
    /// keys are prefixed (<c>d:</c>, <c>r:</c>, <c>sh:</c>, <c>mh:</c>,
    /// <c>calc:</c>, <c>blank:</c>).</summary>
    public string Key { get; set; } = "";

    /// <summary>One of <see cref="ColumnKinds"/>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Display label (Excel header / header-block label). Falls back to
    /// the registry label or the key when blank.</summary>
    public string? Label { get; set; }

    /// <summary>One of <see cref="ColumnPlacements"/>. Defaults to column.</summary>
    public string Placement { get; set; } = ColumnPlacements.Column;

    /// <summary>Defect columns only: "count" (default) or "percent".</summary>
    public string? DefectValue { get; set; }

    /// <summary>Totals for a numeric column: null (none), "sum", or "avg".</summary>
    public string? Total { get; set; }

    /// <summary>Calculated columns only: the arithmetic formula.</summary>
    public string? Formula { get; set; }

    /// <summary>Total columns only: the labels of the value columns to sum, one
    /// per dragged-in field. The row value is the sum of those columns' numeric
    /// values (missing/blank members are skipped).</summary>
    public List<string>? Members { get; set; }
}

/// <summary>Body for export / preview: a design (inline or by saved id) plus
/// the browse filter.</summary>
public class ReportBuilderRequest
{
    public long?                    PerspectiveId { get; set; }
    public string?                  Name          { get; set; }
    public ReportBuilderDefinition? Definition    { get; set; }
    public FlatDefectFilter         Filter        { get; set; } = new();
}

/// <summary>Body for the live formula validator.</summary>
public class FormulaValidateRequest
{
    public string?       Formula { get; set; }
    public List<string>  Columns { get; set; } = new();
}

public static class ColumnKinds
{
    public const string Static         = "static";
    public const string Defect         = "defect";
    public const string Reading        = "reading";
    public const string SampleHeader   = "sampleHeader";
    public const string MaterialHeader = "materialHeader";
    public const string Calc           = "calc";
    public const string Total          = "total";
    public const string Empty          = "empty";
}

public static class ColumnPlacements
{
    public const string Header = "header";
    public const string Column = "column";
}

public static class DefectValueModes
{
    public const string Count   = "count";
    public const string Percent = "percent";
}

public static class ColumnTotals
{
    public const string Sum = "sum";
    public const string Avg = "avg";

    public static bool IsValid(string? t) => t == Sum || t == Avg;
}
