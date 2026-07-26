namespace SharbatlyQMS.Web.ViewModels;

public class DocumentListVm
{
    public string OwnerType { get; set; } = "";        // Arrival | QualityOrder | Sample
    public long   OwnerId   { get; set; }
    public bool   Editable  { get; set; }
    /// <summary>When true, upload/delete submit via AJAX and re-render only the grid, so the surrounding tab stays active (no full-page reload).</summary>
    public bool   Ajax      { get; set; }
    public string[] Categories { get; set; } = Array.Empty<string>();
}

public class DocumentInfo
{
    public long     DocumentId    { get; set; }
    public string   OwnerType     { get; set; } = "";
    public long     OwnerId       { get; set; }
    public string   Category      { get; set; } = "";
    public string   OriginalName  { get; set; } = "";
    public string   ContentType   { get; set; } = "";
    public long     FileSizeBytes { get; set; }
    /// <summary>Path relative to the documents root (e.g. Arrival\17\{guid}.pdf). Never a URL — documents live outside wwwroot.</summary>
    public string   StoragePath   { get; set; } = "";
    public DateTime UploadedAt    { get; set; }
    public string   UploadedBy    { get; set; } = "";
}

/// <summary>
/// Display helpers for the document list. Kept here rather than in the view so
/// the grid partial stays markup; neither helper existed anywhere in the app.
/// </summary>
public static class DocumentDisplay
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB" };

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double v = bytes;
        var unit = 0;
        while (v >= 1024 && unit < Units.Length - 1) { v /= 1024; unit++; }
        // One decimal reads well at these sizes ("1.2 MB"); whole numbers drop it.
        return v >= 100 || v == Math.Truncate(v)
            ? $"{Math.Round(v)} {Units[unit]}"
            : $"{v:0.#} {Units[unit]}";
    }

    /// <summary>Bootstrap Icons class for a file extension (including the dot).</summary>
    public static string IconClass(string? fileName) =>
        Path.GetExtension(fileName ?? "").ToLowerInvariant() switch
        {
            ".pdf"                   => "bi-filetype-pdf",
            ".doc" or ".docx"        => "bi-filetype-docx",
            ".xls" or ".xlsx"        => "bi-filetype-xlsx",
            ".csv"                   => "bi-filetype-csv",
            ".txt"                   => "bi-filetype-txt",
            ".msg" or ".eml"         => "bi-envelope",
            ".zip"                   => "bi-file-earmark-zip",
            _                        => "bi-file-earmark"
        };
}
