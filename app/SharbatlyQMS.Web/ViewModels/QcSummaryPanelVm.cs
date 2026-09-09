using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.ViewModels;

/// <summary>Shipment/arrival header shown at the top of the "Generate Summary"
/// popup — the same Shipment Details block that opens the QC report PDF.</summary>
public class ShipmentDetailsVm
{
    public Arrival           Arrival   { get; set; } = new();
    public ShipmentSnapshot? Shipment  { get; set; }
    public ArrivalChecklist? Checklist { get; set; }

    /// <summary>The date the inspection actually started: when the Quality
    /// Order was opened (falling back to when it was created), already
    /// converted to local time. NOT the arrival checklist's own inspection
    /// date, which can be days earlier — the QC report has used the QO date
    /// since 2026-08-20 and this keeps the screens saying the same thing.
    /// Null when the block is rendered without a Quality Order.</summary>
    public DateTime? InspectionDate { get; set; }

    /// <summary>Arrival custom fields (e.g. "Soft Green") for the material
    /// groups present in this order.</summary>
    public IReadOnlyList<ArrivalCustomField> CustomFields { get; set; } = Array.Empty<ArrivalCustomField>();

    /// <summary>materialGroup → total quantity (Σ arrival_item.quantity across
    /// the group's materials), the denominator for a numeric custom field's
    /// percentage. Same denominator the QC report PDF uses.</summary>
    public IReadOnlyDictionary<string, decimal> GroupQuantityTotals { get; set; } =
        new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The percentage a numeric custom field represents of its material
    /// group's total quantity, or null when it can't be computed.</summary>
    public decimal? PercentOf(ArrivalCustomField cf) =>
        string.Equals(cf.ValueKind, "Numeric", StringComparison.OrdinalIgnoreCase)
            && cf.NumericValue.HasValue
            && GroupQuantityTotals.TryGetValue(cf.MaterialGroup, out var denom) && denom > 0
                ? cf.NumericValue.Value / denom * 100m
                : (decimal?)null;
}

/// <summary>Model for the summary popup partial: the shipment header plus the
/// existing grouped material summaries.</summary>
public class QcSummaryPanelVm
{
    public ShipmentDetailsVm?                   Shipment  { get; set; }
    public IReadOnlyList<MaterialGroupSummary>  Summaries { get; set; } = Array.Empty<MaterialGroupSummary>();
}
