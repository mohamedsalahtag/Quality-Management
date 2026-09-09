using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Read-through lookup over the flattened qms_sap_material_cache (V07).
/// Each MARA field is its own typed column, so this service just SELECTs --
/// no per-row JSON parsing, no field-name candidate lists, and the
/// material_group filter hits an index.
/// </summary>
public class MaraService : IMaraService
{
    private const string GroupsCacheKey = "mara:groups";
    private static readonly TimeSpan GroupsCacheTtL = TimeSpan.FromMinutes(15);

    private readonly string _cs;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MaraService> _log;

    public MaraService(IConfiguration cfg, IMemoryCache cache, ILogger<MaraService> log)
    {
        _cs = cfg.GetConnectionString("Default")
              ?? throw new InvalidOperationException("Missing Default connection string.");
        _cache = cache;
        _log = log;
    }

    public async Task<MaraMaterial?> GetAsync(string materialNo)
    {
        if (string.IsNullOrWhiteSpace(materialNo)) return null;
        var map = await LookupAsync(new[] { materialNo });
        return map.TryGetValue(materialNo, out var m) ? m : null;
    }

    public async Task<IReadOnlyList<MaraGroup>> ListMaterialGroupsAsync(
        IReadOnlyCollection<string>? materialTypes = null)
    {
        // MARA groups change only when the material-master sync runs (rare).
        // Cache the projection for 15 minutes so the Defect Catalog page,
        // which calls this on every load, doesn't aggregate 33k rows every
        // time. Backed by IX_qms_sap_material_cache_group INCLUDE (desc) on
        // first miss, so even a cold load is index-only.
        //
        // materialTypes narrows to specific MARA material types (MTART, e.g.
        // ZTRD / ZCON). Each distinct filter gets its own cache entry -- the
        // set of filters in use is tiny and fixed by the calling pages.
        var types = materialTypes?.Where(t => !string.IsNullOrWhiteSpace(t))
                                  .Select(t => t.Trim().ToUpperInvariant())
                                  .Distinct()
                                  .OrderBy(t => t, StringComparer.Ordinal)
                                  .ToList();
        var key = types is { Count: > 0 } ? $"{GroupsCacheKey}:{string.Join(',', types)}" : GroupsCacheKey;

        if (_cache.TryGetValue<IReadOnlyList<MaraGroup>>(key, out var hit) && hit != null)
            return hit;

        var sql = @"
            SELECT material_group AS Code, MAX(material_group_desc) AS Name
            FROM   qms_sap_material_cache
            WHERE  material_group IS NOT NULL AND LEN(material_group) > 0"
            + (types is { Count: > 0 } ? " AND material_type IN @types" : "")
            + @"
            GROUP BY material_group
            ORDER BY material_group;";
        try
        {
            using var c = new SqlConnection(_cs);
            var list = (await c.QueryAsync<MaraGroup>(sql, new { types })).ToList();
            _cache.Set(key, (IReadOnlyList<MaraGroup>)list,
                new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = GroupsCacheTtL });
            return list;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "ListMaterialGroupsAsync failed; returning empty list");
            return Array.Empty<MaraGroup>();
        }
    }

    public async Task<IReadOnlyDictionary<string, MaraMaterial>> LookupAsync(IEnumerable<string> materialNos)
    {
        var keys = materialNos
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (keys.Length == 0)
            return new Dictionary<string, MaraMaterial>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var c = new SqlConnection(_cs);
            // Chunked for the same reason as the sample lookups: Dapper expands
            // IN @keys to one parameter per key and SQL Server caps a command at
            // 2100. The Data Hub asks for every distinct material in a date
            // window, which passes that on a wide range.
            //
            // This one mattered more than the others because the catch below
            // swallows the failure: the page kept rendering, quietly missing
            // every Variety, Class, Origin and Brand that MARA supplies. A
            // silently wrong export is worse than a page that refuses to load.
            var rows = new List<MaraMaterial>(keys.Length);
            const int chunkSize = 1000;
            for (var i = 0; i < keys.Length; i += chunkSize)
            {
                var slice = keys.Skip(i).Take(chunkSize).ToArray();
                rows.AddRange(await c.QueryAsync<MaraMaterial>(@"
                SELECT
                    material_no           AS MaterialNo,
                    material_desc         AS MaterialDesc,
                    origin_name           AS Origin,
                    variety_name          AS Variety,
                    class_name            AS MaterialClass,
                    -- MaterialSize reads the Size ID (SizeID code) rather than Size_Name
                    -- (descriptive); falls back to Material_Weight_Name when there is no size id.
                    COALESCE(NULLIF(size_id,''), NULLIF(material_weight_name,'')) AS MaterialSize,
                    material_group        AS MaterialGroup,
                    material_group_desc   AS MaterialGroupDesc,
                    COALESCE(NULLIF(major_category_desc,''), NULLIF(major_category,'')) AS MajorCategory,
                    sub_major_category    AS SubMajorCategory,
                    brand                 AS Brand,       -- V38 (2026-07-08): brand column now synced from the material-master feed
                    NULL                  AS PackType,    -- ditto
                    NULL                  AS PackCode,    -- ditto
                    weight                AS NetWeight
                FROM   qms_sap_material_cache
                WHERE  material_no IN @keys",
                    new { keys = slice }));
            }
            // GroupBy rather than ToDictionary: material_no is the cache's key,
            // but a duplicate row would now throw across a chunk boundary where
            // before it threw inside one query -- same defence, kept explicit.
            return rows.GroupBy(r => r.MaterialNo, StringComparer.OrdinalIgnoreCase)
                       .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "MARA lookup failed for {Count} key(s)", keys.Length);
            return new Dictionary<string, MaraMaterial>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
