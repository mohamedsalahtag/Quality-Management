using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Small read-through cache over the three QC catalogs that the Quality
/// Order detail page and the report flow read on every page load. The
/// catalogs change only when an admin edits them via Site Configuration,
/// so an absolute 60-minute expiry is safe; admin save endpoints call
/// <see cref="Invalidate"/> for instant flushing.
/// </summary>
public interface ICatalogCache
{
    Task<IReadOnlyList<ReadingTypeEntry>>   GetActiveReadingTypesAsync();
    Task<IReadOnlyList<ReadingTypeEntry>>   GetActiveReadingTypesForGroupAsync(string? materialGroup);
    Task<IReadOnlyList<DefectCatalogEntry>> GetActiveDefectsAsync();
    Task<IReadOnlyList<DefectCatalogEntry>> GetActiveDefectsForGroupAsync(string? materialGroup);
    Task<IReadOnlyDictionary<string,string>> GetDisplaySectionMapAsync(string? materialGroup, string? majorCategory);

    /// <summary>Drops every cached catalog. Call after admin-side catalog edits.</summary>
    void Invalidate();
}
