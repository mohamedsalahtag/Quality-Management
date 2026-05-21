using Microsoft.Extensions.Caching.Memory;
using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services;

public class CatalogCache : ICatalogCache
{
    private const string KeyReadingTypes = "cat:readingTypes";
    private const string KeyDefects      = "cat:defects";
    private static string KeySectionMap(string? mg, string? mc)
        => $"cat:sectionMap:{mg ?? "_"}|{mc ?? "_"}";

    private static readonly TimeSpan TtL = TimeSpan.FromMinutes(60);

    private readonly IMemoryCache _cache;
    private readonly IQualityOrderService _qos;

    public CatalogCache(IMemoryCache cache, IQualityOrderService qos)
    {
        _cache = cache;
        _qos = qos;
    }

    public async Task<IReadOnlyList<ReadingTypeEntry>> GetActiveReadingTypesAsync()
    {
        if (_cache.TryGetValue<IReadOnlyList<ReadingTypeEntry>>(KeyReadingTypes, out var hit) && hit != null)
            return hit;
        var data = await _qos.GetActiveReadingTypesAsync();
        _cache.Set(KeyReadingTypes, data, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TtL });
        return data;
    }

    public async Task<IReadOnlyList<ReadingTypeEntry>> GetActiveReadingTypesForGroupAsync(string? materialGroup)
    {
        if (string.IsNullOrWhiteSpace(materialGroup)) return Array.Empty<ReadingTypeEntry>();
        var all = await GetActiveReadingTypesAsync();
        return all.Where(rt => string.Equals(rt.MaterialGroup, materialGroup, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public async Task<IReadOnlyList<DefectCatalogEntry>> GetActiveDefectsAsync()
    {
        if (_cache.TryGetValue<IReadOnlyList<DefectCatalogEntry>>(KeyDefects, out var hit) && hit != null)
            return hit;
        var data = await _qos.GetActiveDefectsAsync();
        _cache.Set(KeyDefects, data, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TtL });
        return data;
    }

    public async Task<IReadOnlyList<DefectCatalogEntry>> GetActiveDefectsForGroupAsync(string? materialGroup)
    {
        if (string.IsNullOrWhiteSpace(materialGroup)) return Array.Empty<DefectCatalogEntry>();
        // Reuse the full cached catalog and filter in memory -- saves a DB
        // round-trip even on a cache hit.
        var all = await GetActiveDefectsAsync();
        return all.Where(d => string.Equals(d.MaterialGroup, materialGroup, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public async Task<IReadOnlyDictionary<string,string>> GetDisplaySectionMapAsync(string? materialGroup, string? majorCategory)
    {
        var key = KeySectionMap(materialGroup, majorCategory);
        if (_cache.TryGetValue<IReadOnlyDictionary<string,string>>(key, out var hit) && hit != null)
            return hit;
        var data = await _qos.GetDisplaySectionMapAsync(materialGroup, majorCategory);
        _cache.Set(key, data, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TtL });
        return data;
    }

    public void Invalidate()
    {
        _cache.Remove(KeyReadingTypes);
        _cache.Remove(KeyDefects);
        // Section-map keys are dynamic; cheapest approach is to compact the
        // entire IMemoryCache for our prefix. There's no public API for
        // prefix removal, so we just rely on absolute expiry for those --
        // they're per (group, category) so changes flow through naturally
        // when the matching admin endpoint is saved (admin can hard-reload
        // for instant effect).
    }
}
