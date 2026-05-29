namespace SharbatlyQMS.Web.ViewModels;

public class ImageGalleryVm
{
    public string OwnerType { get; set; } = "";        // Arrival | QualityOrder | Sample
    public long   OwnerId   { get; set; }
    public bool   Editable  { get; set; }
    /// <summary>When true, upload/delete submit via AJAX and re-render only the grid, so the surrounding tab stays active (no full-page reload).</summary>
    public bool   Ajax     { get; set; }
    public string[] Categories { get; set; } = Array.Empty<string>();
    public IReadOnlyList<ImageInfo> Images { get; set; } = Array.Empty<ImageInfo>();
    public int    ScreenWidth  { get; set; } = 160;
    public int    ScreenHeight { get; set; } = 120;
}

public class ImageInfo
{
    public long   ImageLinkId   { get; set; }
    public long   ImageId       { get; set; }
    public string StorageUrl    { get; set; } = "";
    public string? ThumbnailUrl { get; set; }
    public string Category      { get; set; } = "";
    public string? Caption      { get; set; }
    public string OriginalName  { get; set; } = "";
    public DateTime UploadedAt  { get; set; }
    public string  UploadedBy   { get; set; } = "";
}
