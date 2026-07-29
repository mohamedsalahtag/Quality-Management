using SharbatlyQMS.Web.ViewModels;

namespace SharbatlyQMS.Web.Services;

public interface IDocumentService
{
    Task<IReadOnlyList<DocumentInfo>> ListAsync(string ownerType, long ownerId);

    /// <summary>
    /// Saves each accepted file and returns how many landed plus a human-readable
    /// reason per rejected file. Rejections are RETURNED, not just logged: the
    /// image pipeline silently drops oversize/wrong-type files and the user only
    /// sees a lower count, which reads as a bug.
    /// </summary>
    Task<(int saved, IReadOnlyList<string> rejected)> UploadAsync(
        string ownerType, long ownerId, string category,
        IReadOnlyList<IFormFile> files, string uploadedBy);

    Task<DocumentInfo?> GetAsync(long documentId);

    Task SoftDeleteAsync(long documentId, string deletedBy);

    /// <summary>
    /// Absolute on-disk path for a stored document, or null if it would escape the
    /// documents root. Callers must treat null as "not found".
    /// </summary>
    string? ResolveAbsolutePath(DocumentInfo doc);

    /// <summary>
    /// Best-effort unlink of stored documents by their relative storage_path.
    /// Used by the cascade deletes (arrival / quality order), which remove the
    /// rows inside a transaction and then call this AFTER the commit — the
    /// files live outside wwwroot, so a row deleted without its file leaves
    /// bytes on disk that nothing can ever reach again.
    ///
    /// Never throws: a locked or already-missing file is logged, not fatal.
    /// Returns how many files were actually removed.
    /// </summary>
    int DeleteFiles(IEnumerable<string> relativeStoragePaths);

    /// <summary>Server-side extension to MIME map. Never trust the stored content_type — it is client-supplied at upload.</summary>
    string ContentTypeFor(string fileName);
}
