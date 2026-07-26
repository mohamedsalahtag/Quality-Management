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

    /// <summary>Server-side extension to MIME map. Never trust the stored content_type — it is client-supplied at upload.</summary>
    string ContentTypeFor(string fileName);
}
