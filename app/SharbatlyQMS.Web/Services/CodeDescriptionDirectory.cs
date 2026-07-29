using Dapper;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Singleton implementation of <see cref="ICodeDescriptionDirectory"/>.
///
/// Holds three immutable dictionaries behind a single <see cref="Snapshot"/>
/// reference. <see cref="RefreshAsync"/> builds a brand-new snapshot and swaps
/// the reference in one assignment, so readers never see a half-built map and
/// need no lock. A failed refresh leaves the previous snapshot in place — a DB
/// blip degrades to slightly stale names, never to a broken page.
/// </summary>
public sealed class CodeDescriptionDirectory : ICodeDescriptionDirectory
{
    private sealed class Snapshot
    {
        public required IReadOnlyDictionary<string, string> Plants          { get; init; }
        public required IReadOnlyDictionary<string, string> StorageLocations{ get; init; }  // keyed "PLANT|CODE"
        public required IReadOnlyDictionary<string, string> PoTypes         { get; init; }
        public required IReadOnlyList<CodeDescriptionEntry> PlantList       { get; init; }
    }

    private readonly string _cs;
    private readonly ILogger<CodeDescriptionDirectory> _log;

    // volatile: the swap in RefreshAsync must be visible to reader threads
    // immediately, without a lock on the (very hot) read path.
    private volatile Snapshot _snap;

    public CodeDescriptionDirectory(IConfiguration cfg, ILogger<CodeDescriptionDirectory> log)
    {
        _cs = cfg.GetConnectionString("Default")
              ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
        _log = log;
        // Start from the seed so the very first request already renders names,
        // even before the startup refresh has run (or if the DB is unreachable).
        _snap = SeedSnapshot();
    }

    private static string SlKey(string plant, string code) =>
        plant.ToUpperInvariant() + "|" + code.ToUpperInvariant();

    private static Snapshot SeedSnapshot() => new()
    {
        Plants = SapPlantDirectorySeed.Plants
            .ToDictionary(p => p.Code, p => p.Name, StringComparer.OrdinalIgnoreCase),
        StorageLocations = SapPlantDirectorySeed.StorageLocations
            .GroupBy(s => SlKey(s.PlantCode, s.Code))
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase),
        PoTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        PlantList = SapPlantDirectorySeed.Plants
            .Select(p => new CodeDescriptionEntry
            {
                Domain = CodeDomains.Plant, Code = p.Code, Description = p.Name, IsActive = true
            })
            .ToList()
    };

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        try
        {
            using var c = new SqlConnection(_cs);
            var rows = (await c.QueryAsync<CodeDescriptionEntry>(@"
                SELECT code_desc_id CodeDescId, domain Domain, parent_code ParentCode,
                       code Code, description Description, sort_order SortOrder,
                       is_active IsActive, updated_at UpdatedAt, updated_by UpdatedBy
                FROM   qms_code_description
                WHERE  is_active = 1")).ToList();

            // Seed first, DB second: a DB row for the same code wins, and any
            // code the admin hasn't touched keeps its seeded name.
            var seed = SeedSnapshot();
            var plants  = new Dictionary<string, string>(seed.Plants,           StringComparer.OrdinalIgnoreCase);
            var storage = new Dictionary<string, string>(seed.StorageLocations, StringComparer.OrdinalIgnoreCase);
            var poTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var r in rows)
            {
                if (string.IsNullOrWhiteSpace(r.Code) || string.IsNullOrWhiteSpace(r.Description)) continue;
                switch (r.Domain)
                {
                    case CodeDomains.Plant:
                        plants[r.Code] = r.Description;
                        break;
                    case CodeDomains.StorageLocation:
                        // parent_code is the owning plant; a row without one is
                        // unusable for lookup (codes repeat across plants).
                        if (!string.IsNullOrWhiteSpace(r.ParentCode))
                            storage[SlKey(r.ParentCode!, r.Code)] = r.Description;
                        break;
                    case CodeDomains.PoType:
                        poTypes[r.Code] = r.Description;
                        break;
                }
            }

            var plantList = rows
                .Where(r => r.Domain == CodeDomains.Plant)
                .OrderBy(r => r.SortOrder).ThenBy(r => r.Code, StringComparer.OrdinalIgnoreCase)
                .ToList();
            // Cold table (migration not applied yet): keep the seeded plants so
            // the user-admin plant dropdown is never empty.
            if (plantList.Count == 0) plantList = seed.PlantList.ToList();

            _snap = new Snapshot
            {
                Plants = plants, StorageLocations = storage, PoTypes = poTypes, PlantList = plantList
            };
            _log.LogInformation("Code descriptions loaded: {Plants} plants, {Storage} storage locations, {PoTypes} PO types.",
                plants.Count, storage.Count, poTypes.Count);
        }
        catch (Exception ex)
        {
            // Keep serving the previous (or seed) snapshot -- names going stale
            // is far better than a 500 on every list page.
            _log.LogWarning(ex, "Code-description refresh failed; keeping the previous lookup.");
        }
    }

    public string? PlantName(string? code) =>
        !string.IsNullOrWhiteSpace(code) && _snap.Plants.TryGetValue(code!, out var n) ? n : null;

    public string? StorageLocationName(string? plant, string? code) =>
        !string.IsNullOrWhiteSpace(plant) && !string.IsNullOrWhiteSpace(code)
            && _snap.StorageLocations.TryGetValue(SlKey(plant!, code!), out var n) ? n : null;

    public string? PoTypeName(string? code) =>
        !string.IsNullOrWhiteSpace(code) && _snap.PoTypes.TryGetValue(code!, out var n) ? n : null;

    public string PlantLabel(string? code) =>
        PlantName(code) is { } n ? $"{code} — {n}" : (code ?? "");

    public string StorageLocationLabel(string? plant, string? code) =>
        StorageLocationName(plant, code) is { } n ? $"{code} — {n}" : (code ?? "");

    public string PoTypeLabel(string? code) =>
        PoTypeName(code) is { } n ? $"{code} — {n}" : (code ?? "");

    public string PlantDisplay(string? code) => PlantName(code) ?? code ?? "";
    public string StorageLocationDisplay(string? plant, string? code) => StorageLocationName(plant, code) ?? code ?? "";
    public string PoTypeDisplay(string? code) => PoTypeName(code) ?? code ?? "";

    public IReadOnlyList<CodeDescriptionEntry> Plants => _snap.PlantList;
}

/// <summary>
/// Loads the code descriptions once at startup so the first page render
/// already has DB names. Mirrors the existing AD cache primer; failures are
/// swallowed by RefreshAsync, so a cold DB never blocks boot.
/// </summary>
public sealed class CodeDescriptionPrimingService : IHostedService
{
    private readonly ICodeDescriptionDirectory _dir;
    public CodeDescriptionPrimingService(ICodeDescriptionDirectory dir) => _dir = dir;

    public Task StartAsync(CancellationToken ct) => _dir.RefreshAsync(ct);
    public Task StopAsync(CancellationToken ct)  => Task.CompletedTask;
}
