namespace SharbatlyQMS.Web.ViewModels;

public class ImageGalleryVm
{
    public string OwnerType { get; set; } = "";        // Arrival | QualityOrder | Sample
    public long   OwnerId   { get; set; }
    public bool   Editable  { get; set; }

    /// <summary>Whether the delete button shows on each card. Defaults to
    /// <see cref="Editable"/> so existing callers behave as before; the arrival
    /// gallery sets it independently because a Completed arrival still accepts
    /// NEW photos but never lets an existing one be removed.</summary>
    public bool   AllowDelete
    {
        get => _allowDelete ?? Editable;
        set => _allowDelete = value;
    }
    private bool? _allowDelete;

    /// <summary>Optional line shown under the upload box, e.g. to explain that
    /// photos added after completion are recorded in the audit trail.</summary>
    public string? Notice { get; set; }
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
