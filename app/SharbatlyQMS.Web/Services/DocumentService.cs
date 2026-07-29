using System.Security.Cryptography;
using Dapper;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.ViewModels;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Document (non-image) attachment pipeline. Storage layout:
///   {DocumentRoot}/{ownerType}/{ownerId}/{guid}{ext}
/// where DocumentRoot defaults to App_Data/documents under the CONTENT root.
///
/// Content root, not web root: that is what keeps documents off static-file
/// middleware. Images live in wwwroot/uploads and are therefore readable by
/// anyone with the URL; a supplier invoice must not be. Every read goes through
/// DocumentsController.Download, which re-checks auth and plant scope.
///
/// One row in qms_document per file — no asset/link split, because a document
/// (unlike a photo) is never shared across owners.
/// </summary>
public class DocumentService : IDocumentService
{
    public static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf",
        ".doc", ".docx",
        ".xls", ".xlsx",
        ".csv", ".txt",
        ".msg", ".eml",
        ".zip"
    };

    // Whitelisted owner types. ownerType is used to build the on-disk storage
    // path, so it must never contain caller-controlled path segments (e.g.
    // "..\.."). Anything not in this set is rejected before touching the disk.
    public static readonly HashSet<string> AllowedOwnerTypes = new(StringComparer.Ordinal)
    {
        "Arrival", "QualityOrder", "Sample"
    };

    public static bool IsValidOwnerType(string? ownerType) =>
        !string.IsNullOrEmpty(ownerType) && AllowedOwnerTypes.Contains(ownerType);

    private const string DocumentSelect = @"
        SELECT  document_id        DocumentId,
                owner_type         OwnerType,
                owner_id           OwnerId,
                category           Category,
                original_file_name OriginalName,
                content_type       ContentType,
                file_size_bytes    FileSizeBytes,
                storage_path       StoragePath,
                uploaded_at        UploadedAt,
                uploaded_by        UploadedBy
        FROM    qms_document ";

    private readonly string _cs;
    private readonly string _root;
    private readonly long _maxFileSize;
    private readonly ILogger<DocumentService> _log;

    public DocumentService(IConfiguration config, IWebHostEnvironment env, ILogger<DocumentService> log)
    {
        _cs = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default missing");

        // Configurable, and actually READ -- the pre-existing QMS:ImageUploadRoot /
        // ThumbnailRoot / MaxImageSizeMB keys are dead config that nothing consumes.
        var configured = config["QMS:DocumentRoot"];
        _root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(env.ContentRootPath, "App_Data", "documents")
            : (Path.IsPathRooted(configured) ? configured : Path.Combine(env.ContentRootPath, configured));
        _root = Path.GetFullPath(_root);

        var maxMb = config.GetValue<int?>("QMS:MaxDocumentSizeMB") ?? 25;
        _maxFileSize = maxMb * 1024L * 1024L;

        _log = log;
    }

    private SqlConnection Open() => new(_cs);

    public async Task<IReadOnlyList<DocumentInfo>> ListAsync(string ownerType, long ownerId)
    {
        using var c = Open();
        var rows = await c.QueryAsync<DocumentInfo>(DocumentSelect + @"
            WHERE   owner_type = @ownerType AND owner_id = @ownerId
              AND   is_deleted = 0
            ORDER BY uploaded_at DESC",
            new { ownerType, ownerId });
        return rows.ToList();
    }

    public async Task<DocumentInfo?> GetAsync(long documentId)
    {
        using var c = Open();
        return await c.QuerySingleOrDefaultAsync<DocumentInfo>(DocumentSelect + @"
            WHERE   document_id = @documentId AND is_deleted = 0",
            new { documentId });
    }

    public async Task<(int saved, IReadOnlyList<string> rejected)> UploadAsync(
        string ownerType, long ownerId, string category,
        IReadOnlyList<IFormFile> files, string uploadedBy)
    {
        var rejected = new List<string>();
        if (files == null || files.Count == 0) return (0, rejected);

        // Reject unknown owner types before building any filesystem path — this
        // is the guard that prevents a crafted ownerType (e.g. "..\..") from
        // escaping the documents root.
        if (!IsValidOwnerType(ownerType))
            throw new ArgumentException($"Unknown document owner type '{ownerType}'.", nameof(ownerType));

        // ASP.NET model binding turns an empty "category=" form field into null,
        // which violates the NOT NULL column. Same guard as ImageService.
        category ??= "";

        var ownerDir = Path.Combine(_root, ownerType, ownerId.ToString());
        Directory.CreateDirectory(ownerDir);

        int saved = 0;
        using var c = Open();
        await c.OpenAsync();

        foreach (var file in files)
        {
            if (file.Length == 0)
            {
                rejected.Add($"{file.FileName} — file is empty");
                continue;
            }
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (!AllowedExtensions.Contains(ext))
            {
                rejected.Add($"{file.FileName} — file type not allowed");
                continue;
            }
            if (file.Length > _maxFileSize)
            {
                rejected.Add($"{file.FileName} — too large ({ViewModels.DocumentDisplay.FormatBytes(file.Length)}, max {ViewModels.DocumentDisplay.FormatBytes(_maxFileSize)})");
                continue;
            }

            var safeName = $"{Guid.NewGuid():N}{ext}";
            var fullPath = Path.Combine(ownerDir, safeName);
            await using (var stream = File.Create(fullPath))
                await file.CopyToAsync(stream);

            string sha;
            await using (var fs = File.OpenRead(fullPath))
                sha = ToHex(await SHA256.HashDataAsync(fs));

            // Relative so the root stays movable via QMS:DocumentRoot without
            // rewriting every stored row.
            var relPath = Path.Combine(ownerType, ownerId.ToString(), safeName);

            try
            {
                await c.ExecuteAsync(@"
                    INSERT INTO qms_document
                        (owner_type, owner_id, category, original_file_name, content_type,
                         file_size_bytes, storage_path, checksum_sha256,
                         uploaded_at, uploaded_by)
                    VALUES
                        (@ownerType, @ownerId, @category, @OriginalFileName, @ContentType,
                         @FileSize, @relPath, @sha,
                         SYSUTCDATETIME(), @uploadedBy);",
                    new
                    {
                        ownerType,
                        ownerId,
                        category,
                        OriginalFileName = file.FileName,
                        // Stored for reference only. Downloads use ContentTypeFor(),
                        // never this value — it is whatever the client claimed.
                        ContentType = file.ContentType ?? "application/octet-stream",
                        FileSize = file.Length,
                        relPath,
                        sha,
                        uploadedBy
                    });
                saved++;
            }
            catch (Exception ex)
            {
                // Don't leave a file on disk with no row pointing at it.
                _log.LogError(ex, "Insert failed for document {File}; removing orphaned file", safeName);
                try { File.Delete(fullPath); } catch { /* best effort */ }
                rejected.Add($"{file.FileName} — could not be saved");
            }
        }

        return (saved, rejected);
    }

    public async Task SoftDeleteAsync(long documentId, string deletedBy)
    {
        using var c = Open();
        // Soft delete only: the row stays for audit/history and the file stays on
        // disk, matching how image assets are retired.
        await c.ExecuteAsync(
            "UPDATE qms_document SET is_deleted = 1 WHERE document_id = @documentId",
            new { documentId });
    }

    public int DeleteFiles(IEnumerable<string> relativeStoragePaths)
    {
        int removed = 0;
        foreach (var rel in relativeStoragePaths)
        {
            if (string.IsNullOrWhiteSpace(rel)) continue;
            try
            {
                // Same root + escape check as ResolveAbsolutePath: a tampered
                // storage_path must not let a delete reach outside the root.
                var combined = Path.GetFullPath(Path.Combine(_root, rel));
                var rootPrefix = _root.EndsWith(Path.DirectorySeparatorChar)
                    ? _root
                    : _root + Path.DirectorySeparatorChar;
                if (!combined.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogWarning("Refusing to delete document file outside the documents root: {Path}", rel);
                    continue;
                }
                if (File.Exists(combined)) { File.Delete(combined); removed++; }
            }
            catch (Exception ex)
            {
                // Non-fatal: the DB rows are already gone, so a leftover file is
                // dead weight, not a correctness problem.
                _log.LogWarning(ex, "Could not delete document file {Path}", rel);
            }
        }
        return removed;
    }

    public string? ResolveAbsolutePath(DocumentInfo doc)
    {
        if (string.IsNullOrWhiteSpace(doc.StoragePath)) return null;
        var combined = Path.GetFullPath(Path.Combine(_root, doc.StoragePath));
        // Defense in depth: storage_path is generated server-side, but a tampered
        // row must still not be able to read outside the documents root.
        var rootPrefix = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            _log.LogWarning("Document {Id} storage_path escapes the documents root", doc.DocumentId);
            return null;
        }
        return combined;
    }

    public string ContentTypeFor(string fileName) =>
        Path.GetExtension(fileName ?? "").ToLowerInvariant() switch
        {
            ".pdf"  => "application/pdf",
            ".doc"  => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls"  => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".csv"  => "text/csv",
            ".txt"  => "text/plain",
            ".msg"  => "application/vnd.ms-outlook",
            ".eml"  => "message/rfc822",
            ".zip"  => "application/zip",
            _       => "application/octet-stream"
        };

    private static string ToHex(byte[] bytes)
    {
        var sb = new System.Text.StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
