using SharbatlyQMS.Web.ViewModels;

namespace SharbatlyQMS.Web.Services;

public interface IImageService
{
    Task<IReadOnlyList<ImageInfo>> ListAsync(string ownerType, long ownerId);
    /// <summary>Single-query count of attached images per owner id, used by the QO Details page to badge each material card without N+1 lookups.</summary>
    Task<IReadOnlyDictionary<long, int>> CountByOwnersAsync(string ownerType, IEnumerable<long> ownerIds);
    Task<int> UploadAsync(string ownerType, long ownerId, string category,
        IReadOnlyList<IFormFile> files, string uploadedBy);
    Task SoftDeleteLinkAsync(long imageLinkId, string deletedBy);
    /// <summary>Resolves the (ownerType, ownerId) an image link is attached to, so callers can authorize a delete. Null if the link doesn't exist.</summary>
    Task<(string ownerType, long ownerId)?> GetLinkOwnerAsync(long imageLinkId);
}
