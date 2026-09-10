using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;

namespace SharbatlyQMS.Web.Services;

/// <summary>One major category and the sub-majors filed under it.</summary>
public sealed class MaterialMajor
{
    public string Name { get; init; } = "";
    public List<string> SubMajors { get; init; } = new();
}

public interface IMaterialTaxonomyService
{
    /// <summary>
    /// The major categories from the material master, each with its own
    /// sub-majors, alphabetically. Every list screen offers the same set, so a
    /// filter learned on one page works the same on the next.
    /// </summary>
    Task<IReadOnlyList<MaterialMajor>> GetAsync(CancellationToken ct = default);
}

/// <summary>
/// The material master's category tree, for the filter panels.
///
/// Cached: it is 15 majors over 34,000 materials and changes only when the
/// material master is re-synced, but the DISTINCT that produces it scans the
/// whole cache — running that on every list page load, several times per page
/// where a page has more than one filter panel, would be an odd way to spend a
/// second.
/// </summary>
public class MaterialTaxonomyService : IMaterialTaxonomyService
{
    private const string CacheKey = "material.taxonomy.v1";
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    private readonly string _cs;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MaterialTaxonomyService> _log;

    public MaterialTaxonomyService(IConfiguration config, IMemoryCache cache,
        ILogger<MaterialTaxonomyService> log)
    {
        _cs = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
        _cache = cache;
        _log   = log;
    }

    public async Task<IReadOnlyList<MaterialMajor>> GetAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CacheKey, out IReadOnlyList<MaterialMajor>? hit) && hit is not null)
            return hit;

        try
        {
            using var c = new SqlConnection(_cs);
            // The description when the master carries one, the code otherwise:
            // the same COALESCE the material lookup and the data hub already
            // use, so the values a filter offers match the values every other
            // screen prints.
            var rows = (await c.QueryAsync<(string Major, string? SubMajor)>(new CommandDefinition(@"
                SELECT DISTINCT
                       COALESCE(NULLIF(major_category_desc,''), NULLIF(major_category,'')) AS Major,
                       NULLIF(sub_major_category,'')                                       AS SubMajor
                FROM   qms_sap_material_cache
                WHERE  COALESCE(NULLIF(major_category_desc,''), NULLIF(major_category,'')) IS NOT NULL
                ORDER  BY Major, SubMajor", cancellationToken: ct))).ToList();

            var result = rows
                .GroupBy(r => r.Major, StringComparer.OrdinalIgnoreCase)
                .Select(g => new MaterialMajor
                {
                    Name = g.Key,
                    SubMajors = g.Select(x => x.SubMajor)
                                 .Where(x => !string.IsNullOrWhiteSpace(x))
                                 .Select(x => x!.Trim())
                                 .Distinct(StringComparer.OrdinalIgnoreCase)
                                 .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                                 .ToList()
                })
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _cache.Set(CacheKey, (IReadOnlyList<MaterialMajor>)result, Ttl);
            return result;
        }
        catch (Exception ex)
        {
            // A filter panel that cannot offer its categories is a smaller
            // problem than a list page that will not open, so this degrades to
            // an empty picker rather than throwing. Logged loudly because an
            // empty picker looks like missing data rather than a failure.
            _log.LogError(ex, "Material taxonomy lookup failed; category filters will be empty.");
            return Array.Empty<MaterialMajor>();
        }
    }
}

/// <summary>
/// The SQL that filters a list by material category.
///
/// Kept in one place because four screens need the identical predicate against
/// four different item tables, and a copy that drifts would quietly filter one
/// screen differently from the next.
/// </summary>
public static class MaterialCategoryFilter
{
    /// <summary>
    /// An EXISTS predicate binding <c>@matMajor</c> and <c>@matSubMajor</c>.
    /// Both are optional: a null parameter matches everything, so the caller
    /// can always include this clause.
    /// </summary>
    /// <param name="itemTable">Table holding one row per material line.</param>
    /// <param name="itemAlias">Alias to give it inside the EXISTS.</param>
    /// <param name="joinPredicate">How that table relates to the outer row.</param>
    public static string Sql(string itemTable, string itemAlias, string joinPredicate) => $@"
              AND (@matMajor IS NULL AND @matSubMajor IS NULL OR EXISTS (
                        SELECT 1
                        FROM   {itemTable} {itemAlias}
                        JOIN   qms_sap_material_cache mc
                          ON   mc.material_no = {itemAlias}.material_no
                        WHERE  {joinPredicate}
                          AND (@matMajor    IS NULL OR
                               COALESCE(NULLIF(mc.major_category_desc,''), NULLIF(mc.major_category,'')) = @matMajor)
                          AND (@matSubMajor IS NULL OR mc.sub_major_category = @matSubMajor)))";
}
