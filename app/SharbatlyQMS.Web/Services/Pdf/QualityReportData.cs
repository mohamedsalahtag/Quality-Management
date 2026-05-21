using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services.Pdf;

/// <summary>
/// Bundle of everything a Quality Control Report needs to render. Built once
/// per PDF generation so the QuestPDF document creator can stay pure.
/// </summary>
public class QualityReportData
{
    public QualityOrder         QualityOrder    { get; set; } = new();
    public Arrival              Arrival         { get; set; } = new();
    public ShipmentSnapshot?    Shipment        { get; set; }
    public ArrivalChecklist?    Checklist       { get; set; }
    public List<QualityOrderMaterial> Materials { get; set; } = new();
    public List<SampleBundle>   Samples         { get; set; } = new();

    // Settings
    public string SiteName     { get; set; } = "Sharbatly Quality Management";
    public string CompanyName  { get; set; } = "Mohamed Abdullah Sharbatly CO. LTD";
    public string CompanyFooter{ get; set; } = "Sharbatly Fruit - Al Safa District 11, N 25 E Street Abdullah Sharbatly ST.(2053) - 21491 - Jeddah - Email: info@sharbatlyfruit.com";
    public int    ThumbnailW   { get; set; } = 120;
    public int    ThumbnailH   { get; set; } = 90;
    public bool   ThumbCover   { get; set; } = true;

    /// <summary>
    /// Absolute file-system path to the company logo configured under
    /// Site Configuration → Branding. Populated by ReportsController; the
    /// renderer falls back to the "QMS" placeholder badge when null.
    /// </summary>
    public string? LogoAbsolutePath { get; set; }

    public string GeneratedBy  { get; set; } = "system";
    public DateTime GeneratedAt{ get; set; } = DateTime.UtcNow;

    // Images grouped by owner type/id; the renderer pulls absolute paths from these.
    public List<ImageRef> ArrivalImages { get; set; } = new();

    /// <summary>
    /// Photos uploaded per QO material line, keyed by QoMaterialId. The
    /// renderer groups the images appendix by material so the reader can see
    /// which photos belong to which product. Populated by
    /// ReportsController.BuildDataAsync from owner type "QualityOrderMaterial".
    /// </summary>
    public Dictionary<long, List<ImageRef>> MaterialImages { get; set; } = new();
}

public class SampleBundle
{
    public Sample                       Sample      { get; set; } = new();
    public QualityOrderMaterial?        Material    { get; set; }
    public List<SampleReading>          Readings    { get; set; } = new();
    public List<SampleDefect>           Defects     { get; set; } = new();
    public Dictionary<string, string>   SectionMap  { get; set; } = new();   // defect code -> Major/Minor
    public List<ImageRef>               Images      { get; set; } = new();

    public decimal MajorDefectTotal => Defects.Where(d => SectionMap.GetValueOrDefault(d.DefectCode) == "Major").Sum(d => d.DefectPercentage ?? 0);
    public decimal MinorDefectTotal => Defects.Where(d => SectionMap.GetValueOrDefault(d.DefectCode) != "Major").Sum(d => d.DefectPercentage ?? 0);

    public decimal? ReadingNum(string code) => Readings.FirstOrDefault(r => r.ReadingTypeCode == code)?.NumericValue;
    public string?  ReadingTxt(string code) => Readings.FirstOrDefault(r => r.ReadingTypeCode == code)?.TextValue;
}

public class ImageRef
{
    /// <summary>File-system path used by callers that embed a known file
    /// (Quality Order report path). The Arrival report uses
    /// <see cref="InlineBytes"/> instead.</summary>
    public string AbsolutePath { get; set; } = "";

    /// <summary>Pre-resized, JPEG-compressed image bytes ready for QuestPDF
    /// to embed inline. Lets the report carry the exact pixel size /
    /// quality the caller wants, without the PDF referencing the original
    /// file or any URL.</summary>
    public byte[]? InlineBytes { get; set; }

    public string OriginalName { get; set; } = "";
    public string Category     { get; set; } = "";
    public int    OrderIndex   { get; set; }
}
