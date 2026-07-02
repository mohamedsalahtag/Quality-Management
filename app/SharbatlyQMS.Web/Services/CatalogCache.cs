using Microsoft.Extensions.Caching.Memory;
using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services;

public class CatalogCache : ICatalogCache
{
    private const string KeyReadingTypes = "cat:readingTypes";
    private const string KeyDefects      = "cat:defects";
    private const string KeyHeaderFields = "cat:headerFields";
    private const string KeyCategories   = "cat:defectCategories";
    private static string KeySectionMap(string? mg, string? mc)
        => $"cat:sectionMap:{mg ?? "_"}|{mc ?? "_"}";

    private static readonly TimeSpan TtL = TimeSpan.FromMinutes(60);

    private readonly IMemoryCache _cache;
    private readonly IQualityOrderService _qos;

    // Tracks the dynamic section-map keys we've issued so Invalidate() can clear
    // them too (IMemoryCache has no prefix-removal API). Static because the cache
    // it fronts is the singleton IMemoryCache while this service is scoped.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _sectionMapKeys = new();

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
        // V19+: include Global rows (MaterialGroup == "") so a single
        // BRIX / TARA / etc. defined once shows up on every fruit's
        // sample form. Per-group rows listed first (preserving sort),
        // then globals.
        var all = await GetActiveReadingTypesAsync();
        return all
            .Where(rt => string.Equals(rt.MaterialGroup, materialGroup, StringComparison.OrdinalIgnoreCase)
                         || string.IsNullOrEmpty(rt.MaterialGroup))
            .OrderBy(rt => string.IsNullOrEmpty(rt.MaterialGroup) ? 1 : 0)
            .ThenBy(rt => rt.SortOrder)
            .ThenBy(rt => rt.ReadingName)
            .ToList();
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
        _sectionMapKeys.TryAdd(key, 0);
        return data;
    }

    public async Task<IReadOnlyList<SampleHeaderField>> GetActiveSampleHeaderFieldsAsync()
    {
        if (_cache.TryGetValue<IReadOnlyList<SampleHeaderField>>(KeyHeaderFields, out var hit) && hit != null)
            return hit;
        var data = await _qos.GetActiveSampleHeaderFieldsAsync();
        _cache.Set(KeyHeaderFields, data, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TtL });
        return data;
    }

    public async Task<IReadOnlyList<DefectCategory>> GetActiveCategoriesAsync()
    {
        if (_cache.TryGetValue<IReadOnlyList<DefectCategory>>(KeyCategories, out var hit) && hit != null)
            return hit;
        var data = await _qos.GetActiveCategoriesAsync();
        _cache.Set(KeyCategories, data, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TtL });
        return data;
    }

    public void Invalidate()
    {
        _cache.Remove(KeyReadingTypes);
        _cache.Remove(KeyDefects);
        _cache.Remove(KeyHeaderFields);
        _cache.Remove(KeyCategories);
        // Clear the dynamic per-(group, category) section-map keys too, so a
        // display-section change takes effect immediately instead of lingering
        // for up to the 60-minute TTL.
        foreach (var key in _sectionMapKeys.Keys)
            _cache.Remove(key);
        _sectionMapKeys.Clear();
    }
}
