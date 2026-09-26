using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Services.Sap;

namespace SharbatlyQMS.Web.Services;

public class ContainerCacheService : IContainerCacheService
{
    public const string SyncLogEndpointKey = "ContainerCache";

    private readonly string _cs;
    private readonly Sap.ISapClient _sap;
    private readonly ILogger<ContainerCacheService> _log;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public ContainerCacheService(IConfiguration config, Sap.ISapClient sap, ILogger<ContainerCacheService> log)
    {
        _cs  = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
        _sap = sap;
        _log = log;
    }

    private SqlConnection Open() => new(_cs);

    public async Task<int> UpsertAsync(IReadOnlyList<SapShipmentRow> rows, CancellationToken ct = default)
    {
        if (rows == null || rows.Count == 0) return 0;
        using var c = Open();
        await c.OpenAsync(ct);
        int affected = 0;
        // Per-row MERGE -- batch sizes are small (page size 200) and the
        // UNIQUE index on the natural key makes the upsert index-covered.
        const string sql = @"
            MERGE qms_sap_container_cache AS T
            USING (SELECT @ContainerNo container_no, @BolNo bol_no, @Ebeln ebeln,
                          @Ebelp ebelp, @MaterialNo material_no, @Plant plant,
                          @StorageLoc storage_loc, @BatchNo batch_no) AS S
            ON  T.container_no = S.container_no AND T.bol_no       = S.bol_no
            AND T.ebeln        = S.ebeln        AND T.ebelp        = S.ebelp
            AND T.material_no  = S.material_no  AND T.plant        = S.plant
            AND T.storage_loc  = S.storage_loc
            AND ((T.batch_no IS NULL AND S.batch_no IS NULL) OR T.batch_no = S.batch_no)
            WHEN MATCHED THEN UPDATE SET
                vendor_no      = @VendorNo,
                vendor_name    = @VendorName,
                material_desc  = @MaterialDesc,
                material_group = @MaterialGroup,
                po_type        = @PoType,
                sto            = @Sto,
                doc_date       = @DocDate,
                arrival_date   = @ArrivalDate,
                receive_date   = @ReceiveDate,
                -- Only overwrite when SAP actually supplied one, so a later
                -- page that happens to omit it cannot blank a known date.
                port_arrival_date = COALESCE(@PortArrivalDate, T.port_arrival_date),
                quantity       = @Quantity,
                uom            = @Uom,
                transit_days   = @TransitDays,
                payload_json   = @PayloadJson,
                last_seen_at   = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT
                (container_no, bol_no, ebeln, ebelp, material_no, plant, storage_loc, batch_no,
                 vendor_no, vendor_name, material_desc, material_group, po_type, sto,
                 doc_date, arrival_date, receive_date, port_arrival_date, quantity, uom, transit_days, payload_json)
            VALUES
                (@ContainerNo, @BolNo, @Ebeln, @Ebelp, @MaterialNo, @Plant, @StorageLoc, @BatchNo,
                 @VendorNo, @VendorName, @MaterialDesc, @MaterialGroup, @PoType, @Sto,
                 @DocDate, @ArrivalDate, @ReceiveDate, @PortArrivalDate, @Quantity, @Uom, @TransitDays, @PayloadJson);";
        foreach (var r in rows)
        {
            ct.ThrowIfCancellationRequested();
            var json = JsonSerializer.Serialize(r, JsonOpts);
            affected += await c.ExecuteAsync(sql, new
            {
                r.ContainerNo, r.BolNo, r.Ebeln, r.Ebelp, r.MaterialNo,
                r.Plant, StorageLoc = r.StorageLocation, r.BatchNo,
                r.VendorNo, r.VendorName, r.MaterialDesc, r.MaterialGroup, r.PoType, r.Sto,
                DocDate     = r.DocDate.HasValue     ? (DateTime?)r.DocDate.Value.ToDateTime(TimeOnly.MinValue)     : null,
                ArrivalDate = r.ArrivalDate.HasValue ? (DateTime?)r.ArrivalDate.Value.ToDateTime(TimeOnly.MinValue) : null,
                ReceiveDate = r.ReceiveDate.HasValue ? (DateTime?)r.ReceiveDate.Value.ToDateTime(TimeOnly.MinValue) : null,
                PortArrivalDate = r.PortArrivalDate.HasValue ? (DateTime?)r.PortArrivalDate.Value.ToDateTime(TimeOnly.MinValue) : null,
                r.Quantity, r.Uom, r.TransitDays,
                PayloadJson = json
            });
        }
        return affected;
    }

    public async Task<PendingPage> ListPendingAsync(
        string? container = null, string? bol = null, string? po = null,
        IReadOnlyList<string>? plant = null,
        IReadOnlyList<string>? poType = null,
        IReadOnlyList<string>? storageLoc = null,
        string? supplier = null, string? material = null,
        string? matMajor = null, string? matSubMajor = null,
        DateOnly? from = null, DateOnly? to = null,
        DateOnly? arrFrom = null, DateOnly? arrTo = null,
        int page = 1, int pageSize = 100,
        Models.PlantScope? scope = null,
        bool archived = false,
        CancellationToken ct = default)
    {
        var sc = scope ?? Models.PlantScope.All;
        if (pageSize < 1)  pageSize = 100;
        if (page < 1)      page = 1;
        var offset = (page - 1) * pageSize;
        using var c = Open();
        // Same filter clause is applied to both result sets so the
        // material-lines query never returns lines for triplets the
        // first query filtered out. Container / BOL / PO use LIKE for
        // free-text contains-match; Plant / PoType / StorageLoc match
        // exactly and accept SEVERAL values each, because they come from
        // multi-select dropdowns sourced from the same column values. Supplier is free text too -- operators
        // remember a word of the name, rarely the whole thing.
        // Plant matching runs on the EFFECTIVE plant -- COALESCE(override_plant,
        // plant) -- so a manager-reassigned container shows in the target plant's
        // list (and the target plant's scope) and leaves the original's.
        // Material is a contains-match on either the code or the description of
        // ANY line in the triplet (grouping already collapses lines, so a plain
        // predicate matches the whole triplet). doc_date is a SQL DATE, so the
        // From/To range needs no timezone conversion (unlike the arrivals list).
        //
        // ArrFrom/ArrTo filter arrival_date -- SAP's Receive_Date, which is the
        // same column the grid's "Arr" cell shows as MAX(arrival_date), so the
        // filter and what the operator reads always agree. port_arrival_date is
        // the truer port arrival but is only ~a third populated (measured over
        // 1,000 live rows, M18), so a range over it would look broken. Like the
        // PO-date filter these are per-LINE predicates evaluated before the
        // GROUP BY: a triplet whose lines disagree can match on one line while
        // the cell shows a different MAX.
        //
        // @Archived flips the page between the live pending list and the
        // archive. Archiving is a flag rather than a delete because the next
        // SAP sweep would re-insert any row we removed (same reason as
        // override_plant), so every pending query has to exclude it explicitly.
        const string filterClause = @"
            has_arrival = 0
            AND ((@Archived = 0 AND archived_at IS NULL)
              OR (@Archived = 1 AND archived_at IS NOT NULL))
            -- No receive date, no place on the pending list. SAP leaves
            -- Receive_Date blank until the goods are actually received, and a
            -- container it cannot date is one it cannot say has arrived.
            --
            -- The archive is exempt: it shows what WAS archived, and those rows
            -- keep whatever date they were archived with. Without the @Archived
            -- arm, clearing a stale date here would also erase the row from the
            -- archive, which is a record rather than a worklist.
            AND (@Archived = 1 OR receive_date IS NOT NULL)
            AND (@Container  IS NULL OR container_no LIKE @Container)
            AND (@Bol        IS NULL OR bol_no       LIKE @Bol)
            AND (@Po         IS NULL OR ebeln        LIKE @Po)
            AND (@Supplier   IS NULL OR vendor_name  LIKE @Supplier OR vendor_no LIKE @Supplier)
            AND (@Material   IS NULL OR material_no  LIKE @Material OR material_desc LIKE @Material)
            -- Multi-select: an any-flag plus an IN list. Dapper renders an
            -- empty list as IN (SELECT 1 WHERE 1=0), so the flag is what turns
            -- each filter off rather than the list being empty.
            AND (@PlantAny      = 0 OR COALESCE(override_plant, plant) IN @Plants)
            AND (@PoTypeAny     = 0 OR po_type     IN @PoTypes)
            AND (@StorageLocAny = 0 OR storage_loc IN @StorageLocs)
            AND (@From       IS NULL OR doc_date    >= @From)
            AND (@To         IS NULL OR doc_date    <= @To)
            AND (@ArrFrom    IS NULL OR arrival_date >= @ArrFrom)
            AND (@ArrTo      IS NULL OR arrival_date <= @ArrTo)
            AND (@sUnrestricted = 1 OR COALESCE(override_plant, plant) IN @sPlants)
            -- Material master categories. The outer column is qualified with the
            -- TABLE NAME because the master carries a material_no of its own:
            -- unqualified, the correlation would bind to the inner table and the
            -- predicate would be trivially true for every row.
            AND (@matMajor IS NULL AND @matSubMajor IS NULL OR EXISTS (
                    SELECT 1 FROM qms_sap_material_cache mc
                    WHERE  mc.material_no = qms_sap_container_cache.material_no
                      AND (@matMajor    IS NULL OR
                           COALESCE(NULLIF(mc.major_category_desc,''), NULLIF(mc.major_category,'')) = @matMajor)
                      AND (@matSubMajor IS NULL OR mc.sub_major_category = @matSubMajor)))";

        // SQL LIKE wildcards: empty input -> NULL (match everything);
        // populated input -> '%value%' contains-match (operators usually
        // remember a partial number).
        static string? Wrap(string? s) =>
            string.IsNullOrWhiteSpace(s) ? null : $"%{s.Trim()}%";
        // Exact-match: trim and convert empty to null so the (@x IS NULL)
        // branch above kicks in.
        static string? Exact(string? s) =>
            string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        // Cleaned for the any-flags, sentinel-wrapped for the IN clauses --
        // see FilterValues for why those are two different things.
        var plants      = ViewModels.FilterValues.Many(plant);
        var poTypes     = ViewModels.FilterValues.Many(poType);
        var storageLocs = ViewModels.FilterValues.Many(storageLoc);

        var p = new
        {
            Container  = Wrap(container),
            Bol        = Wrap(bol),
            Po         = Wrap(po),
            Supplier   = Wrap(supplier),
            Material   = Wrap(material),
            matMajor    = Exact(matMajor),
            matSubMajor = Exact(matSubMajor),
            // Trimmed and de-blanked: an unticked box submits nothing, but a
            // hand-edited query string can still carry "?plant=" and an empty
            // string would match no plant and silently empty the page.
            Plants      = ViewModels.FilterValues.ForIn(plants),      PlantAny      = plants.Count      > 0,
            PoTypes     = ViewModels.FilterValues.ForIn(poTypes),     PoTypeAny     = poTypes.Count     > 0,
            StorageLocs = ViewModels.FilterValues.ForIn(storageLocs), StorageLocAny = storageLocs.Count > 0,
            // doc_date is a SQL DATE; pass DateTime (midnight) rather than
            // DateOnly — this Dapper/SqlClient pairing doesn't bind DateOnly,
            // which is why the arrivals list converts dates the same way. Both
            // bounds are inclusive (doc_date has no time component).
            From       = from?.ToDateTime(TimeOnly.MinValue),
            To         = to?.ToDateTime(TimeOnly.MinValue),
            ArrFrom    = arrFrom?.ToDateTime(TimeOnly.MinValue),
            ArrTo      = arrTo?.ToDateTime(TimeOnly.MinValue),
            sUnrestricted = sc.Unrestricted,
            sPlants       = sc.QueryPlants,
            Archived      = archived,
            offset, pageSize
        };

        // Three statements in one round trip:
        //  1. total matching triplets (drives the pager).
        //  2. the page's triplet aggregates, cut with OFFSET/FETCH and spilled
        //     into #page so the next statement can reuse the exact same window.
        //  3. material lines for ONLY the page's triplets (JOIN #page), instead
        //     of every pending line in the cache -- this is the payload win.
        // The aggregate query is fast; the old cost was rendering all ~2.4k
        // triplets and ~12k lines into one page. filterClause matches on the
        // EFFECTIVE plant (override when set) for count and page alike.
        using var grid = await c.QueryMultipleAsync($@"
            SELECT COUNT(*) FROM (
                SELECT container_no, bol_no, ebeln
                FROM   qms_sap_container_cache
                WHERE  {filterClause}
                GROUP  BY container_no, bol_no, ebeln
            ) AS t;

            SELECT
                container_no       AS ContainerNo,
                bol_no             AS BolNo,
                ebeln              AS Ebeln,
                MAX(sto)           AS Sto,
                MAX(vendor_no)     AS VendorNo,
                MAX(vendor_name)   AS VendorName,
                MAX(po_type)       AS PoType,
                COALESCE(MAX(override_plant), MAX(plant)) AS Plant,
                MAX(plant)         AS OriginalPlant,
                CASE WHEN MAX(override_plant) IS NOT NULL THEN 1 ELSE 0 END AS IsPlantOverridden,
                MAX(storage_loc)   AS StorageLocation,
                COUNT(*)           AS LineCount,
                MAX(doc_date)      AS DocDate,
                MAX(arrival_date)  AS ArrivalDate,
                MAX(receive_date)  AS ReceiveDate,
                MAX(port_arrival_date) AS PortArrivalDate,
                MAX(transit_days)  AS TransitDays,
                MIN(first_seen_at) AS FirstSeenAt,
                MAX(archived_at)   AS ArchivedAt,
                MAX(archived_by)   AS ArchivedBy
            INTO   #page
            FROM   qms_sap_container_cache
            WHERE  {filterClause}
            GROUP BY container_no, bol_no, ebeln
            -- Newest ARRIVALS first. It used to order by doc_date -- the PO
            -- date -- which the page no longer shows anywhere, so the list was
            -- sorted by an invisible column and the newest containers were not
            -- at the top. Nulls last: a container with no arrival date yet is
            -- not the oldest, it is simply unknown.
            ORDER BY CASE WHEN MAX(arrival_date) IS NULL THEN 1 ELSE 0 END,
                     MAX(arrival_date) DESC,
                     container_no, bol_no, ebeln
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;

            -- Repeated verbatim: #page holds one page in no guaranteed order,
            -- and re-reading it without an ORDER BY would hand the view its
            -- rows in whatever order the engine finds them.
            SELECT * FROM #page
            ORDER BY CASE WHEN ArrivalDate IS NULL THEN 1 ELSE 0 END,
                     ArrivalDate DESC, ContainerNo, BolNo, Ebeln;

            SELECT
                cc.container_no   AS ContainerNo,
                cc.bol_no         AS BolNo,
                cc.ebeln          AS Ebeln,
                cc.ebelp          AS Ebelp,
                cc.material_no    AS MaterialNo,
                cc.material_desc  AS MaterialDesc,
                cc.material_group AS MaterialGroup,
                cc.quantity       AS Quantity,
                cc.uom            AS Uom
            FROM   qms_sap_container_cache cc
            JOIN   #page pg
              ON   pg.ContainerNo = cc.container_no
             AND   pg.BolNo       = cc.bol_no
             AND   pg.Ebeln       = cc.ebeln
            WHERE  cc.has_arrival = 0
            ORDER BY cc.container_no, cc.bol_no, cc.ebeln, cc.ebelp;

            DROP TABLE #page;", p);

        var total    = await grid.ReadFirstAsync<int>();
        var triplets = (await grid.ReadAsync<PendingPickupRow>()).ToList();
        var lines    = (await grid.ReadAsync<PendingMaterialLine>()).ToList();

        // Attach material lines to their parent triplet via a composite key.
        // ToLookup is the cheapest one-pass index.
        var byTriplet = lines.ToLookup(
            l => $"{l.ContainerNo}|{l.BolNo}|{l.Ebeln}",
            StringComparer.OrdinalIgnoreCase);
        foreach (var t in triplets)
        {
            t.MaterialLines = byTriplet[$"{t.ContainerNo}|{t.BolNo}|{t.Ebeln}"].ToList();
        }
        return new PendingPage(triplets, total, page, pageSize);
    }

    public async Task<PendingFilterOptions> GetPendingFilterOptionsAsync(
        Models.PlantScope? scope = null, bool archived = false, CancellationToken ct = default)
    {
        var sc = scope ?? Models.PlantScope.All;
        using var c = Open();
        // Three cheap DISTINCT queries over the pending-filtered index
        // (IX_qms_sap_container_cache_pending) -- index-only seeks, no
        // table scans. Combined into one round trip with QueryMultiple.
        // The third query returns (plant, storage_loc) pairs because
        // storage-loc codes repeat across plants (e.g. "0001" appears
        // under multiple plants in SAP); the UI needs the parent plant
        // to render and filter correctly. All three honour the user's plant
        // scope so a restricted user never even sees another plant's codes.
        // Dropdowns list the EFFECTIVE plant (override when set, else SAP's) so
        // they line up with what ListPendingAsync filters and shows, and a
        // reassigned container appears under its target plant.
        // All three also honour @Archived, so the archive view's dropdowns list
        // the codes present in the ARCHIVE rather than in the live pending list.
        using var grid = await c.QueryMultipleAsync(@"
            SELECT DISTINCT COALESCE(override_plant, plant) AS Plant
            FROM   qms_sap_container_cache
            WHERE  has_arrival = 0 AND COALESCE(override_plant, plant) IS NOT NULL AND COALESCE(override_plant, plant) <> ''
              AND ((@Archived = 0 AND archived_at IS NULL) OR (@Archived = 1 AND archived_at IS NOT NULL))
              AND (@sUnrestricted = 1 OR COALESCE(override_plant, plant) IN @sPlants)
            ORDER  BY Plant;

            SELECT DISTINCT po_type
            FROM   qms_sap_container_cache
            WHERE  has_arrival = 0 AND po_type IS NOT NULL AND po_type <> ''
              AND ((@Archived = 0 AND archived_at IS NULL) OR (@Archived = 1 AND archived_at IS NOT NULL))
              AND (@sUnrestricted = 1 OR COALESCE(override_plant, plant) IN @sPlants)
            ORDER  BY po_type;

            SELECT DISTINCT COALESCE(override_plant, plant) AS Plant, storage_loc AS Code
            FROM   qms_sap_container_cache
            WHERE  has_arrival = 0
              AND  COALESCE(override_plant, plant) IS NOT NULL AND COALESCE(override_plant, plant) <> ''
              AND  storage_loc IS NOT NULL AND storage_loc <> ''
              AND ((@Archived = 0 AND archived_at IS NULL) OR (@Archived = 1 AND archived_at IS NOT NULL))
              AND (@sUnrestricted = 1 OR COALESCE(override_plant, plant) IN @sPlants)
            ORDER  BY Plant, storage_loc;",
            new { sUnrestricted = sc.Unrestricted, sPlants = sc.QueryPlants, Archived = archived });

        var plants    = (await grid.ReadAsync<string>()).ToList();
        var poTypes   = (await grid.ReadAsync<string>()).ToList();
        var storage   = (await grid.ReadAsync<PlantStorageLoc>()).ToList();
        return new PendingFilterOptions
        {
            Plants           = plants,
            PoTypes          = poTypes,
            StorageLocations = storage
        };
    }

    public async Task<IReadOnlyList<SapShipmentRow>> GetTripletRowsAsync(string containerNo, string bolNo, string ebeln, CancellationToken ct = default)
    {
        using var c = Open();
        var jsons = await c.QueryAsync<string?>(@"
            SELECT payload_json
            FROM   qms_sap_container_cache
            WHERE  container_no = @containerNo
              AND  bol_no       = @bolNo
              AND  ebeln        = @ebeln
              AND  has_arrival  = 0",
            new { containerNo, bolNo, ebeln });
        var rows = new List<SapShipmentRow>();
        foreach (var j in jsons)
        {
            if (string.IsNullOrWhiteSpace(j)) continue;
            try
            {
                var r = JsonSerializer.Deserialize<SapShipmentRow>(j, JsonOpts);
                if (r != null) rows.Add(r);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not deserialize cache payload for {Container}/{Bol}/{Po}", containerNo, bolNo, ebeln);
            }
        }
        return rows;
    }

    public async Task MarkArrivedAsync(string containerNo, string bolNo, string ebeln, long arrivalId, CancellationToken ct = default)
    {
        using var c = Open();
        await c.ExecuteAsync(@"
            UPDATE qms_sap_container_cache
            SET    has_arrival = 1,
                   arrival_id  = @arrivalId,
                   last_seen_at = SYSUTCDATETIME()
            WHERE  container_no = @containerNo
              AND  bol_no       = @bolNo
              AND  ebeln        = @ebeln",
            new { containerNo, bolNo, ebeln, arrivalId });
    }

    public async Task<int> SetPlantOverrideAsync(string containerNo, string bolNo, string ebeln,
        string targetPlant, string user, CancellationToken ct = default)
    {
        using var c = Open();
        // Reassigning to the container's own SAP plant clears the override
        // (per-row CASE, so a triplet spanning >1 SAP plant is handled row by
        // row). override_plant is deliberately not part of the UPSERT key, so a
        // later SAP sweep re-MERGEs the same rows and leaves this column intact.
        return await c.ExecuteAsync(@"
            UPDATE qms_sap_container_cache
            SET    override_plant    = CASE WHEN @targetPlant = plant THEN NULL ELSE @targetPlant END,
                   override_plant_by  = @user,
                   override_plant_at  = SYSUTCDATETIME(),
                   last_seen_at       = SYSUTCDATETIME()
            WHERE  container_no = @containerNo
              AND  bol_no       = @bolNo
              AND  ebeln        = @ebeln
              AND  has_arrival  = 0",
            new { containerNo, bolNo, ebeln, targetPlant, user });
    }

    public async Task<string?> GetEffectivePlantAsync(string containerNo, string bolNo, string ebeln, CancellationToken ct = default)
    {
        using var c = Open();
        return await c.ExecuteScalarAsync<string?>(@"
            SELECT TOP 1 COALESCE(override_plant, plant)
            FROM   qms_sap_container_cache
            WHERE  container_no = @containerNo
              AND  bol_no       = @bolNo
              AND  ebeln        = @ebeln
              AND  has_arrival  = 0",
            new { containerNo, bolNo, ebeln });
    }

    public async Task<string?> GetPlantOverrideAsync(string containerNo, string bolNo, string ebeln, CancellationToken ct = default)
    {
        using var c = Open();
        return await c.ExecuteScalarAsync<string?>(@"
            SELECT TOP 1 override_plant
            FROM   qms_sap_container_cache
            WHERE  container_no = @containerNo
              AND  bol_no       = @bolNo
              AND  ebeln        = @ebeln
              AND  has_arrival  = 0
              AND  override_plant IS NOT NULL",
            new { containerNo, bolNo, ebeln });
    }

    public async Task<int> CountArchivedTripletsAsync(Models.PlantScope? scope = null, CancellationToken ct = default)
    {
        var sc = scope ?? Models.PlantScope.All;
        using var c = Open();
        return await c.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM (
                SELECT DISTINCT container_no, bol_no, ebeln
                FROM   qms_sap_container_cache
                WHERE  has_arrival = 0 AND archived_at IS NOT NULL
                  AND (@sUnrestricted = 1 OR COALESCE(override_plant, plant) IN @sPlants)
            ) AS x",
            new { sUnrestricted = sc.Unrestricted, sPlants = sc.QueryPlants });
    }

    public Task<int> ArchiveByArrivalDateRangeAsync(DateOnly? from, DateOnly? to, string user,
        Models.PlantScope? scope = null, CancellationToken ct = default)
        => SetArchivedByArrivalDateRangeAsync(from, to, user, scope, archive: true);

    public Task<int> RestoreByArrivalDateRangeAsync(DateOnly? from, DateOnly? to, string user,
        Models.PlantScope? scope = null, CancellationToken ct = default)
        => SetArchivedByArrivalDateRangeAsync(from, to, user, scope, archive: false);

    /// <summary>
    /// Shared body of the two range operations — they differ only in which side
    /// of the flag they select and which side they write, so one query with two
    /// parameters beats two near-identical copies drifting apart.
    /// </summary>
    private async Task<int> SetArchivedByArrivalDateRangeAsync(DateOnly? from, DateOnly? to, string user,
        Models.PlantScope? scope, bool archive)
    {
        var sc = scope ?? Models.PlantScope.All;
        using var c = Open();
        // The range is matched on MAX(arrival_date) PER TRIPLET, not per row:
        // that is the "Arr" date the grid prints, so the operator archives
        // exactly the containers the list showed them. A triplet whose lines
        // carry different arrival dates is decided by the one date they can see.
        //
        // Was doc_date (the PO date) until the PO date was removed from the page
        // entirely -- archiving by a date nobody can see is not a usable action.
        //
        // #t is materialised first so the UPDATE and the returned count come
        // from the same set -- counting rows affected would report cache LINES
        // (a triplet has several), which reads as a wildly inflated number.
        return await c.ExecuteScalarAsync<int>(@"
            SELECT container_no, bol_no, ebeln
            INTO   #t
            FROM   qms_sap_container_cache
            WHERE  has_arrival = 0
              AND ((@Archive = 1 AND archived_at IS NULL)
                OR (@Archive = 0 AND archived_at IS NOT NULL))
              AND (@sUnrestricted = 1 OR COALESCE(override_plant, plant) IN @sPlants)
            GROUP  BY container_no, bol_no, ebeln
            HAVING (@From IS NULL OR MAX(arrival_date) >= @From)
               AND (@To   IS NULL OR MAX(arrival_date) <= @To);

            UPDATE cc
            SET    archived_at = CASE WHEN @Archive = 1 THEN SYSUTCDATETIME() END,
                   archived_by = CASE WHEN @Archive = 1 THEN @user END
            FROM   qms_sap_container_cache cc
            JOIN   #t t
              ON   t.container_no = cc.container_no
             AND   t.bol_no       = cc.bol_no
             AND   t.ebeln        = cc.ebeln
            WHERE  cc.has_arrival = 0;

            SELECT COUNT(*) FROM #t;

            DROP TABLE #t;",
            new
            {
                // doc_date is a SQL DATE with no time part, so both bounds are
                // inclusive; DateOnly needs the same DateTime conversion the
                // list query does (this Dapper/SqlClient pairing won't bind it).
                From    = from?.ToDateTime(TimeOnly.MinValue),
                To      = to?.ToDateTime(TimeOnly.MinValue),
                Archive = archive,
                user,
                sUnrestricted = sc.Unrestricted,
                sPlants       = sc.QueryPlants
            });
    }

    /// <summary>
    /// Archives every pending container that arrived before <paramref name="before"/>,
    /// and repairs any container whose lines disagree about being archived.
    ///
    /// Runs at the end of each sweep. Both statements are idempotent -- they
    /// only ever touch rows that are not already in the state they want -- so
    /// repeating them costs nothing.
    ///
    /// The second statement matters even with no floor set: archiving marks a
    /// whole container (container_no, bol_no, ebeln), but the cache key also
    /// includes the PO line, the material, the storage location and the batch.
    /// A new line on an already-archived container therefore inserts unarchived
    /// and, because the pending list filters rows before grouping them, brings
    /// the whole container back. Inheriting the state closes that.
    /// </summary>
    public async Task<int> ArchiveArrivalsBeforeAsync(DateOnly? before, CancellationToken ct = default)
    {
        using var c = Open();
        await c.OpenAsync(ct);

        // Repair first, so a container that only came back through a new line is
        // re-hidden even when no floor is configured.
        await c.ExecuteAsync(@"
            UPDATE cc
            SET    archived_at = t.archived_at,
                   archived_by = t.archived_by
            FROM   qms_sap_container_cache cc
            JOIN  (SELECT container_no, bol_no, ebeln,
                          MAX(archived_at) AS archived_at,
                          MAX(archived_by) AS archived_by
                   FROM   qms_sap_container_cache
                   WHERE  archived_at IS NOT NULL
                   GROUP  BY container_no, bol_no, ebeln) t
              ON   t.container_no = cc.container_no
             AND   t.bol_no       = cc.bol_no
             AND   t.ebeln        = cc.ebeln
            WHERE  cc.archived_at IS NULL AND cc.has_arrival = 0;");

        if (before is null) return 0;

        // Then the floor. Matched on MAX(arrival_date) per container, the same
        // date the pending grid prints, so what disappears is what the operator
        // would have selected by hand.
        return await c.ExecuteScalarAsync<int>(@"
            SELECT container_no, bol_no, ebeln
            INTO   #old
            FROM   qms_sap_container_cache
            WHERE  has_arrival = 0 AND archived_at IS NULL
            GROUP  BY container_no, bol_no, ebeln
            HAVING MAX(arrival_date) IS NOT NULL AND MAX(arrival_date) < @before;

            UPDATE cc
            SET    archived_at = SYSUTCDATETIME(),
                   archived_by = 'auto (arrived before ' + CONVERT(VARCHAR(10), @before, 23) + ')'
            FROM   qms_sap_container_cache cc
            JOIN   #old o
              ON   o.container_no = cc.container_no
             AND   o.bol_no       = cc.bol_no
             AND   o.ebeln        = cc.ebeln
            WHERE  cc.has_arrival = 0;

            SELECT COUNT(*) FROM #old;

            DROP TABLE #old;",
            new { before = before.Value.ToDateTime(TimeOnly.MinValue) });
    }

    public async Task<int> SetArchivedAsync(string containerNo, string bolNo, string ebeln,
        bool archived, string user, CancellationToken ct = default)
    {
        using var c = Open();
        // Whole triplet at once: the pending list groups by it, so leaving some
        // of its lines unarchived would make the container reappear.
        return await c.ExecuteAsync(@"
            UPDATE qms_sap_container_cache
            SET    archived_at = CASE WHEN @archived = 1 THEN SYSUTCDATETIME() END,
                   archived_by = CASE WHEN @archived = 1 THEN @user END
            WHERE  container_no = @containerNo
              AND  bol_no       = @bolNo
              AND  ebeln        = @ebeln
              AND  has_arrival  = 0",
            new { containerNo, bolNo, ebeln, archived, user });
    }

    public async Task<int> ReconcileWithArrivalsAsync(CancellationToken ct = default)
    {
        using var c = Open();
        return await c.ExecuteAsync(@"
            UPDATE cc
            SET    cc.has_arrival = 1,
                   cc.arrival_id  = a.arrival_id,
                   cc.last_seen_at = SYSUTCDATETIME()
            FROM   qms_sap_container_cache cc
            JOIN   qms_arrival a
                ON  a.container_no = cc.container_no
                AND a.bol_no       = cc.bol_no
                AND a.ebeln        = cc.ebeln
            WHERE  cc.has_arrival  = 0
              AND  a.status_code  <> 'Cancelled'");
    }

    public async Task<int> CountPendingTripletsAsync(CancellationToken ct = default)
    {
        using var c = Open();
        return await c.ExecuteScalarAsync<int>(@"
            SELECT COUNT(*) FROM (
                SELECT DISTINCT container_no, bol_no, ebeln
                FROM   qms_sap_container_cache
                WHERE  has_arrival = 0 AND archived_at IS NULL
            ) AS x");
    }

    public async Task<ContainerPullStatus> GetPullStatusAsync(CancellationToken ct = default)
    {
        using var c = Open();
        // Two reads in one round trip: latest COMPLETED run (for the
        // "Last retrieval" line) + whatever is currently in flight (for
        // the "Currently retrieving..." indicator).
        using var grid = await c.QueryMultipleAsync(@"
            SELECT TOP 1 completed_at, rows_synced, message, success, triggered_by, trigger_source
            FROM   qms_sap_sync_log
            WHERE  endpoint_key = @ep AND completed_at IS NOT NULL
            ORDER  BY completed_at DESC;

            SELECT TOP 1 started_at, trigger_source
            FROM   qms_sap_sync_log
            WHERE  endpoint_key = @ep AND completed_at IS NULL
            ORDER  BY started_at DESC;",
            new { ep = SyncLogEndpointKey });

        var done = (await grid.ReadAsync<(DateTime? completed_at, int? rows_synced, string? message, bool? success, string? triggered_by, string? trigger_source)>())
                   .FirstOrDefault();
        var inflight = (await grid.ReadAsync<(DateTime? started_at, string? trigger_source)>())
                       .FirstOrDefault();

        var status = new ContainerPullStatus
        {
            LastRunUtc        = done.completed_at,
            LastRowCount      = done.rows_synced,
            LastTriggerSource = done.trigger_source,
            LastTriggeredBy   = done.triggered_by,
            LastResult        = done.completed_at is null
                ? null
                : ((done.success ?? false)
                    ? (done.message ?? "OK")
                    : ("FAILED: " + (done.message ?? "(no detail)"))),
            IsRunning            = inflight.started_at.HasValue,
            RunningSince         = inflight.started_at,
            RunningTriggerSource = inflight.trigger_source
        };
        return status;
    }

    /// <summary>
    /// Copies SAP's CURRENT dates from the cache onto every arrival's shipment
    /// snapshot. This is the fix for the arrival page, the QO page, the claims
    /// list, the QC report and the dashboard all disagreeing with the pending
    /// list and the Time Bar page about when a container was received.
    ///
    /// SAP's Receive_Date is not fixed at first sight. It appears when the
    /// container is pulled out of the port and is then advanced to the day the
    /// branch books the goods in -- on average four days later, on more than
    /// half of all arrivals (1,180 of 2,268 with a finished QC, measured
    /// 2026-09-26). The snapshot copied it once at arrival creation and never
    /// looked again, so everything reading the snapshot showed the pull-out
    /// date while everything reading the cache showed the goods receipt.
    ///
    /// Only SAP-owned columns are touched (receive_date, its legacy twin
    /// arrival_date, port_arrival_date and SAP's transit_days); the inspector's
    /// own dates are never written here. A cache line that has lost its date is
    /// ignored rather than copied, so a snapshot is never blanked: an arrival is
    /// a record of a container that WAS received. Matching mirrors
    /// TimeBarService -- ISNULL on the arrival's nullable keys, because the
    /// cache stores '' for a BOL-less shipment.
    ///
    /// Safe to run on every sweep: it stops changing anything once SAP's dates
    /// settle, and they do -- the receipt never moved more than five days past
    /// a QC finish on any of the 2,268 measured.
    /// </summary>
    public async Task<int> RefreshArrivalSnapshotsAsync(CancellationToken ct = default)
    {
        using var c = Open();
        await c.OpenAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition(@"
            ;WITH k AS (
                SELECT container_no, bol_no, ebeln,
                       MAX(receive_date)      AS receive_date,
                       MAX(port_arrival_date) AS port_arrival_date,
                       MAX(transit_days)      AS transit_days
                FROM   qms_sap_container_cache
                GROUP  BY container_no, bol_no, ebeln
            )
            UPDATE ss
            SET    ss.receive_date      = COALESCE(k.receive_date,      ss.receive_date),
                   ss.arrival_date      = COALESCE(k.receive_date,      ss.arrival_date),
                   ss.port_arrival_date = COALESCE(k.port_arrival_date, ss.port_arrival_date),
                   ss.transit_days      = COALESCE(k.transit_days,      ss.transit_days)
            FROM   qms_shipment_snapshot ss
            JOIN   qms_arrival a ON a.arrival_id = ss.arrival_id
            JOIN   k ON k.container_no = ISNULL(a.container_no, '')
                    AND k.bol_no       = ISNULL(a.bol_no, '')
                    AND k.ebeln        = ISNULL(a.ebeln, '')
            WHERE  (k.receive_date      IS NOT NULL AND (ss.receive_date      IS NULL OR ss.receive_date      <> k.receive_date
                                                      OR ss.arrival_date      IS NULL OR ss.arrival_date      <> k.receive_date))
               OR  (k.port_arrival_date IS NOT NULL AND (ss.port_arrival_date IS NULL OR ss.port_arrival_date <> k.port_arrival_date))
               OR  (k.transit_days      IS NOT NULL AND (ss.transit_days      IS NULL OR ss.transit_days      <> k.transit_days));",
            commandTimeout: 120, cancellationToken: ct));
    }

    /// <summary>
    /// Clears the cached dates on pending rows that this sweep should have
    /// refreshed but did not, because SAP no longer reports a Receive_Date for
    /// them.
    ///
    /// The sweep fetches on `Receive_Date ge {start}`, and a blank date never
    /// satisfies a `ge` comparison. So when SAP stops dating a container -- as
    /// it does until the goods are actually received -- that container simply
    /// stops appearing in the fetch. Left alone the row keeps whatever date it
    /// was last given, which is how a container came to sit on the pending list
    /// showing an arrival date SAP had already withdrawn.
    ///
    /// Scoped deliberately tight, because "SAP did not return it" has two very
    /// different causes:
    ///   * cached date >= the window start -- the row SHOULD have come back and
    ///     did not, so SAP has withdrawn its date. That is this method's case.
    ///   * cached date &lt; the window start -- the row is simply older than the
    ///     fetch window and was never expected. Those are left alone; nulling
    ///     them would erase the history of ~18,000 rows to fix ~150.
    ///
    /// Only visible pending rows are touched. An arrival already created has
    /// its own dated snapshot, and an archived row is a record, not a worklist
    /// entry.
    ///
    /// Self-healing: if SAP dates the container again, the next sweep returns it
    /// and the MERGE fills both columns straight back in.
    /// </summary>
    private async Task<int> DropStaleReceiveDatesAsync(
        DateTime sweepStartedAtUtc, DateOnly windowStart, CancellationToken ct)
    {
        using var c = Open();
        await c.OpenAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition(@"
            UPDATE qms_sap_container_cache
            SET    receive_date = NULL,
                   arrival_date = NULL
            WHERE  has_arrival  = 0
              AND  archived_at  IS NULL
              AND  receive_date IS NOT NULL
              AND  receive_date >= @windowStart
              AND  last_seen_at <  @sweepStartedAtUtc",
            new { windowStart = windowStart.ToDateTime(TimeOnly.MinValue), sweepStartedAtUtc },
            cancellationToken: ct));
    }

    public async Task<int> RefreshFromSapAsync(DateOnly hardFloorStartDate, string triggeredBy, string triggerSource,
                                               DateOnly? archiveArrivalsBefore = null, CancellationToken ct = default)
    {
        // Always pull every row from the admin-configured start date.
        //
        // A delta cursor (Doc_Date ge max(cache.doc_date) - overlap) would
        // miss the realistic case where a PO with an OLD Doc_Date sat in
        // SAP with no container assigned and then gets a confirmation
        // (EKES) added today: the row's Doc_Date doesn't move, so a
        // delta-by-doc-date filter never catches it. Always-full-sweep
        // costs more SAP traffic but is the only correct answer with this
        // CDS view (no created_at / changed_at exposed). The
        // `Container ne ''` filter in HybridSapClient already strips the
        // confirmation-less PO lines server-side, and UPSERT (MERGE on
        // the natural key) makes re-fetched rows cheap.
        DateOnly effectiveSince = hardFloorStartDate;

        // C5: open the sync_log row eagerly on a *short-lived* connection
        // that uses the explicit started_at timestamp captured here. The
        // try/finally still owns a *separate* connection for the close
        // -- if the host process is killed between OPEN and CLOSE,
        // ContainerPollingService.SweepStaleAsync will mark the row
        // cancelled at next startup, which is the desired behavior.
        var startedAtUtc = DateTime.UtcNow;
        long syncLogId;
        using (var c = Open())
        {
            syncLogId = await c.ExecuteScalarAsync<long>(@"
                INSERT INTO qms_sap_sync_log
                    (endpoint_key, started_at, triggered_by, trigger_source)
                VALUES
                    (@EndpointKey, @startedAtUtc, @triggeredBy, @triggerSource);
                SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
                new { EndpointKey = SyncLogEndpointKey, startedAtUtc, triggeredBy, triggerSource });
        }

        int totalRows = 0;
        string? message = null;
        bool success = false;
        try
        {
            totalRows = await _sap.FetchSinceAsync(effectiveSince, async (page, c2) =>
            {
                await UpsertAsync(page, c2);
            }, ct);
            var reconciled = await ReconcileWithArrivalsAsync(ct);
            var refreshed = await RefreshArrivalSnapshotsAsync(ct);
            var undated = await DropStaleReceiveDatesAsync(startedAtUtc, effectiveSince, ct);
            var autoArchived = await ArchiveArrivalsBeforeAsync(archiveArrivalsBefore, ct);
            message = $"Fetched {totalRows} SAP row(s) since {effectiveSince:yyyy-MM-dd}; reconciled {reconciled} pre-existing arrival(s)."
                    + (refreshed > 0 ? $" Refreshed SAP dates on {refreshed} arrival(s)." : "")
                    + (undated > 0 ? $" Cleared {undated} container line(s) SAP no longer dates." : "")
                    + (autoArchived > 0 ? $" Auto-archived {autoArchived} container(s) that arrived before {archiveArrivalsBefore:yyyy-MM-dd}." : "");
            success = true;
            _log.LogInformation("Container pull OK ({Trigger}) -- {Msg}", triggerSource, message);
        }
        catch (OperationCanceledException)
        {
            message = "Cancelled";
            throw;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            _log.LogError(ex, "Container pull FAILED ({Trigger})", triggerSource);
            throw;
        }
        finally
        {
            // C5: persist success + rows_synced + message + completed_at
            // in a SINGLE UPDATE statement, on a fresh connection. Both
            // started_at (above) and the close (here) use explicit UTC
            // timestamps so a clock skew between SQL Server and the app
            // host can't produce completed_at < started_at. The retry
            // loop catches transient deadlocks against the same row
            // (very unlikely -- sync_log_id is unique) without spinning.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var c = Open();
                    await c.ExecuteAsync(@"
                        UPDATE qms_sap_sync_log
                        SET    completed_at = @completedAtUtc,
                               success      = @success,
                               rows_synced  = @rows,
                               message      = @message
                        WHERE  sync_log_id  = @id",
                        new { id = syncLogId, success = success ? 1 : 0, rows = totalRows, message, completedAtUtc = DateTime.UtcNow });
                    break;
                }
                catch (Exception ex) when (attempt < 2)
                {
                    _log.LogWarning(ex, "Container pull sync_log close retry {Attempt}/3 for row {Id}", attempt + 1, syncLogId);
                    await Task.Delay(150);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Could not update container pull sync_log row {Id} after retries", syncLogId);
                }
            }
        }
        return totalRows;
    }
}
