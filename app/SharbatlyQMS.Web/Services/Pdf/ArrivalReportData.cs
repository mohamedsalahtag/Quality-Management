using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services.Pdf;

/// <summary>
/// Bundle of everything an Arrival-Checklist PDF needs. Built once per
/// generation so the QuestPDF document stays pure.
/// </summary>
public class ArrivalReportData
{
    public Arrival           Arrival   { get; set; } = new();
    public ArrivalChecklist  Checklist { get; set; } = new();
    public ShipmentSnapshot? Shipment  { get; set; }
    public BrandingConfig    Branding  { get; set; } = new();
    /// <summary>Absolute file path of the company logo (or null when not uploaded).</summary>
    public string?           LogoAbsolutePath { get; set; }

    /// <summary>Images attached to the arrival (any category). Rendered as
    /// an appendix at the end of the PDF when non-empty.</summary>
    public List<ImageRef> Images { get; set; } = new();

    /// <summary>PDF thumbnail dimensions from <c>ThumbnailConfig</c>; used by
    /// the appendix grid. Pixel values, converted to points at render time.</summary>
    public int    ThumbnailW { get; set; } = 120;
    public int    ThumbnailH { get; set; } = 90;
    public bool   ThumbCover { get; set; } = true;

    public string   GeneratedBy { get; set; } = "system";
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}
