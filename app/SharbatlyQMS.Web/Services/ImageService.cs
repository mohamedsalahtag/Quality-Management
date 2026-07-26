using System.Security.Cryptography;
using Dapper;
using Microsoft.Data.SqlClient;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using ImageInfo = SharbatlyQMS.Web.ViewModels.ImageInfo;
using SharpImage = SixLabors.ImageSharp.Image;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Image upload + thumbnail pipeline. Storage layout (inspired by C:\ImageProcessing):
///   wwwroot/uploads/{ownerType}/{ownerId}/{guid}{ext}        original
///   wwwroot/uploads/{ownerType}/{ownerId}/thumbs/{guid}{ext} thumbnail
/// One DB row in qms_image_asset per physical file, plus one row in
/// qms_image_link per (owner, category) attachment so the same image can
/// in principle appear in multiple places without re-uploading.
/// </summary>
public class ImageService : IImageService
{
    public static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp"
    };

    // Whitelisted owner types. ownerType is used to build the on-disk storage
    // path, so it must never contain caller-controlled path segments (e.g.
    // "..\.."). Anything not in this set is rejected before touching the disk.
    public static readonly HashSet<string> AllowedOwnerTypes = new(StringComparer.Ordinal)
    {
        "Arrival", "ArrivalChecklist", "Sample", "QualityOrderMaterial"
    };

    public static bool IsValidOwnerType(string? ownerType) =>
        !string.IsNullOrEmpty(ownerType) && AllowedOwnerTypes.Contains(ownerType);

    private const long MaxFileSize = 10L * 1024 * 1024; // 10 MB

    private readonly string _cs;
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _config;
    private readonly ISettingsService _settings;
    private readonly ILogger<ImageService> _log;

    public ImageService(IConfiguration config, IWebHostEnvironment env,
        ISettingsService settings, ILogger<ImageService> log)
    {
        _cs = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
        _env = env; _config = config; _settings = settings; _log = log;
    }

    private SqlConnection Open() => new(_cs);

    public async Task<IReadOnlyList<ImageInfo>> ListAsync(string ownerType, long ownerId)
    {
        using var c = Open();
        var thumb = await _settings.GetThumbnailConfigAsync();
        var rows = await c.QueryAsync<ImageInfo>(@"
            SELECT  l.image_link_id     ImageLinkId,
                    a.image_id          ImageId,
                    a.storage_url       StorageUrl,
                    a.thumbnail_url     ThumbnailUrl,
                    l.image_category    Category,
                    l.caption           Caption,
                    a.original_file_name OriginalName,
                    a.uploaded_at       UploadedAt,
                    a.uploaded_by       UploadedBy
            FROM    qms_image_link l
            JOIN    qms_image_asset a ON a.image_id = l.image_id
            WHERE   l.owner_type = @ownerType AND l.owner_id = @ownerId
              AND   a.is_deleted = 0
            ORDER BY l.display_order, a.uploaded_at",
            new { ownerType, ownerId });
        var list = rows.ToList();
        return list;
    }

    public async Task<IReadOnlyDictionary<long, int>> CountByOwnersAsync(string ownerType, IEnumerable<long> ownerIds)
    {
        var ids = ownerIds.Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<long, int>();
        using var c = Open();
        var rows = await c.QueryAsync<(long OwnerId, int Cnt)>(@"
            SELECT  l.owner_id, COUNT(*) AS cnt
            FROM    qms_image_link l
            JOIN    qms_image_asset a ON a.image_id = l.image_id
            WHERE   l.owner_type = @ownerType AND l.owner_id IN @ids AND a.is_deleted = 0
            GROUP BY l.owner_id",
            new { ownerType, ids });
        var dict = rows.ToDictionary(r => r.OwnerId, r => r.Cnt);
        // Ensure every requested id has an entry (0 if no images) so callers don't need TryGetValue.
        foreach (var id in ids) if (!dict.ContainsKey(id)) dict[id] = 0;
        return dict;
    }

    public async Task<int> UploadAsync(string ownerType, long ownerId, string category,
        IReadOnlyList<IFormFile> files, string uploadedBy)
    {
        if (files == null || files.Count == 0) return 0;
        // Reject unknown owner types before building any filesystem path — this
        // is the guard that prevents a crafted ownerType (e.g. "..\..") from
        // escaping wwwroot/uploads.
        if (!IsValidOwnerType(ownerType))
            throw new ArgumentException($"Unknown image owner type '{ownerType}'.", nameof(ownerType));
        // V31 (2026-06-20): qms_image_link.image_category is VARCHAR(40) NOT NULL.
        // ASP.NET model binding silently converts an empty "category=" form field
        // to null, which then violated the NOT NULL constraint on every upload --
        // assets landed in qms_image_asset but no link row got created (orphans).
        // Coalesce to empty string here so every code path is safe.
        category ??= "";

        var thumbCfg = await _settings.GetThumbnailConfigAsync();
        var ownerDir = Path.Combine(UploadStorage.Root(_env, _config), ownerType, ownerId.ToString());
        var thumbDir = Path.Combine(ownerDir, "thumbs");
        Directory.CreateDirectory(ownerDir);
        Directory.CreateDirectory(thumbDir);

        // Step 1: do the disk + CPU work (file write, SHA, thumbnail) in
        // parallel across files. The DB inserts in step 2 run serially over
        // one connection so we still get connection pooling without races.
        var thumbW = thumbCfg.ScreenWidth  * 2;
        var thumbH = thumbCfg.ScreenHeight * 2;
        var prepared = await Task.WhenAll(files.Select(f =>
            PrepareFileAsync(f, ownerType, ownerId, ownerDir, thumbDir, thumbW, thumbH)));

        int saved = 0;
        using var c = Open();
        await c.OpenAsync();
        foreach (var p in prepared)
        {
            if (p == null) continue;

            // Asset + link inserts are wrapped in a transaction so a failure on
            // the link insert can't leave an orphan asset row (the exact class of
            // bug V33 had to backfill).
            using var tx = c.BeginTransaction();
            var imageId = await c.ExecuteScalarAsync<long>(@"
                INSERT INTO qms_image_asset
                    (storage_provider, original_file_name, content_type, file_size_bytes,
                     storage_url, thumbnail_url, checksum_sha256, width_px, height_px,
                     uploaded_at, uploaded_by)
                VALUES
                    ('Local', @OriginalFileName, @ContentType, @FileSize,
                     @StorageUrl, @ThumbnailUrl, @Sha, @Width, @Height,
                     SYSUTCDATETIME(), @uploadedBy);
                SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
                new
                {
                    p.OriginalFileName,
                    p.ContentType,
                    p.FileSize,
                    p.StorageUrl,
                    p.ThumbnailUrl,
                    p.Sha,
                    p.Width,
                    p.Height,
                    uploadedBy
                }, tx);

            await c.ExecuteAsync(@"
                INSERT INTO qms_image_link
                    (image_id, owner_type, owner_id, image_category, display_order,
                     caption, include_in_report, created_at, created_by)
                VALUES
                    (@imageId, @ownerType, @ownerId, @category, 0,
                     NULL, 1, SYSUTCDATETIME(), @uploadedBy);",
                new { imageId, ownerType, ownerId, category, uploadedBy }, tx);
            tx.Commit();
            saved++;
        }
        return saved;
    }

    private sealed record PreparedUpload(
        string OriginalFileName, string ContentType, long FileSize,
        string StorageUrl, string? ThumbnailUrl, string Sha, int Width, int Height);

    private async Task<PreparedUpload?> PrepareFileAsync(
        IFormFile file, string ownerType, long ownerId,
        string ownerDir, string thumbDir, int thumbW, int thumbH)
    {
        if (file.Length == 0) return null;
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
        {
            _log.LogInformation("Skipping {File}: unsupported type", file.FileName);
            return null;
        }
        if (file.Length > MaxFileSize)
        {
            _log.LogInformation("Skipping {File}: too large ({Size} bytes)", file.FileName, file.Length);
            return null;
        }

        var safeName = $"{Guid.NewGuid():N}{ext}";
        var fullPath = Path.Combine(ownerDir, safeName);
        await using (var stream = File.Create(fullPath))
            await file.CopyToAsync(stream);

        int width = 0, height = 0;
        string? thumbUrl = null;
        try
        {
            using var img = await SharpImage.LoadAsync(fullPath);
            width  = img.Width;
            height = img.Height;

            // Thumbnail from a CLONE so the full-resolution image survives for the
            // space-saving re-encode below.
            var thumbPath = Path.Combine(thumbDir, safeName);
            using (var thumb = img.Clone(x => x.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Size = new SixLabors.ImageSharp.Size(thumbW, thumbH)
            })))
                thumb.Save(thumbPath);
            thumbUrl = $"/uploads/{ownerType}/{ownerId}/thumbs/{safeName}";

            // Space saver (2026-07-26): phone/camera photos arrive up to 5 MB.
            // Cap the STORED original at MaxStoredDimension px and re-encode at a
            // high but efficient quality — visually lossless for inspection use,
            // and still far higher-res than any report or screen needs (the QO
            // report only samples ~480px per cell), yet it cuts storage ~80-90%.
            // Animated GIFs are left untouched to preserve animation.
            if (ext != ".gif")
            {
                const int MaxStoredDimension = 2500;
                if (Math.Max(width, height) > MaxStoredDimension)
                    img.Mutate(x => x.Resize(new ResizeOptions
                    {
                        Mode = ResizeMode.Max,
                        Size = new SixLabors.ImageSharp.Size(MaxStoredDimension, MaxStoredDimension)
                    }));
                switch (ext)
                {
                    case ".jpg":
                    case ".jpeg":
                        img.Save(fullPath, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 85 });
                        break;
                    case ".webp":
                        img.Save(fullPath, new SixLabors.ImageSharp.Formats.Webp.WebpEncoder { Quality = 85 });
                        break;
                    case ".png":
                        img.Save(fullPath, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
                        break;
                    // .bmp: leave the original bytes (rare; keeps the extension valid).
                }
                width  = img.Width;
                height = img.Height;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Image processing (thumbnail/compress) failed for {File}", safeName);
        }

        // SHA + size are taken from the FINAL stored file (after compression).
        string sha;
        await using (var fs = File.OpenRead(fullPath))
            sha = ToHex(SHA256.HashData(fs));
        long storedSize = new FileInfo(fullPath).Length;

        return new PreparedUpload(
            OriginalFileName: file.FileName,
            ContentType: file.ContentType,
            FileSize: storedSize,
            StorageUrl: $"/uploads/{ownerType}/{ownerId}/{safeName}",
            ThumbnailUrl: thumbUrl,
            Sha: sha,
            Width: width,
            Height: height);
    }

    public async Task SoftDeleteLinkAsync(long imageLinkId, string deletedBy)
    {
        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();
        // Capture the asset id before removing the link, then delete the link,
        // then mark the asset deleted ONLY if no other link still references it —
        // an image may be attached from more than one owner, and deleting one
        // attachment must not hide it everywhere.
        var imageId = await c.ExecuteScalarAsync<long?>(
            "SELECT image_id FROM qms_image_link WHERE image_link_id = @imageLinkId",
            new { imageLinkId }, tx);
        await c.ExecuteAsync(
            "DELETE FROM qms_image_link WHERE image_link_id = @imageLinkId;",
            new { imageLinkId }, tx);
        if (imageId != null)
            await c.ExecuteAsync(@"
                UPDATE qms_image_asset SET is_deleted = 1
                WHERE image_id = @imageId
                  AND NOT EXISTS (SELECT 1 FROM qms_image_link WHERE image_id = @imageId);",
                new { imageId }, tx);
        tx.Commit();
    }

    public async Task<(string ownerType, long ownerId)?> GetLinkOwnerAsync(long imageLinkId)
    {
        using var c = Open();
        var row = await c.QuerySingleOrDefaultAsync<(string, long)?>(
            "SELECT owner_type, owner_id FROM qms_image_link WHERE image_link_id = @imageLinkId",
            new { imageLinkId });
        return row;
    }

    private static string ToHex(byte[] bytes)
    {
        var sb = new System.Text.StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
