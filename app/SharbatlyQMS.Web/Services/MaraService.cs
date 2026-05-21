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

    public async Task<IReadOnlyList<MaraGroup>> ListMaterialGroupsAsync()
    {
        // MARA groups change only when the material-master sync runs (rare).
        // Cache the projection for 15 minutes so the Defect Catalog page,
        // which calls this on every load, doesn't aggregate 33k rows every
        // time. Backed by IX_qms_sap_material_cache_group INCLUDE (desc) on
        // first miss, so even a cold load is index-only.
        if (_cache.TryGetValue<IReadOnlyList<MaraGroup>>(GroupsCacheKey, out var hit) && hit != null)
            return hit;

        const string sql = @"
            SELECT material_group AS Code, MAX(material_group_desc) AS Name
            FROM   qms_sap_material_cache
            WHERE  material_group IS NOT NULL AND LEN(material_group) > 0
            GROUP BY material_group
            ORDER BY material_group;";
        try
        {
            using var c = new SqlConnection(_cs);
            var list = (await c.QueryAsync<MaraGroup>(sql)).ToList();
            _cache.Set(GroupsCacheKey, (IReadOnlyList<MaraGroup>)list,
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
            var rows = await c.QueryAsync<MaraMaterial>(@"
                SELECT
                    material_no           AS MaterialNo,
                    material_desc         AS MaterialDesc,
                    origin_name           AS Origin,
                    variety_name          AS Variety,
                    class_name            AS MaterialClass,
                    -- MARA gives both Size_Name (descriptive) and Material_Weight_Name; prefer size if present.
                    COALESCE(NULLIF(size_name,''), NULLIF(material_weight_name,'')) AS MaterialSize,
                    material_group        AS MaterialGroup,
                    material_group_desc   AS MaterialGroupDesc,
                    COALESCE(NULLIF(major_category_desc,''), NULLIF(major_category,'')) AS MajorCategory,
                    sub_major_category    AS SubMajorCategory,
                    NULL                  AS Brand,       -- not in this CDS view; populated only if a future sync adds it
                    NULL                  AS PackType,    -- ditto
                    NULL                  AS PackCode,    -- ditto
                    weight                AS NetWeight
                FROM   qms_sap_material_cache
                WHERE  material_no IN @keys",
                new { keys });
            return rows.ToDictionary(r => r.MaterialNo, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "MARA lookup failed for {Count} key(s)", keys.Length);
            return new Dictionary<string, MaraMaterial>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
