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
                quantity       = @Quantity,
                uom            = @Uom,
                transit_days   = @TransitDays,
                payload_json   = @PayloadJson,
                last_seen_at   = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT
                (container_no, bol_no, ebeln, ebelp, material_no, plant, storage_loc, batch_no,
                 vendor_no, vendor_name, material_desc, material_group, po_type, sto,
                 doc_date, arrival_date, receive_date, quantity, uom, transit_days, payload_json)
            VALUES
                (@ContainerNo, @BolNo, @Ebeln, @Ebelp, @MaterialNo, @Plant, @StorageLoc, @BatchNo,
                 @VendorNo, @VendorName, @MaterialDesc, @MaterialGroup, @PoType, @Sto,
                 @DocDate, @ArrivalDate, @ReceiveDate, @Quantity, @Uom, @TransitDays, @PayloadJson);";
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
                r.Quantity, r.Uom, r.TransitDays,
                PayloadJson = json
            });
        }
        return affected;
    }

    public async Task<PendingPage> ListPendingAsync(
        string? container = null, string? bol = null, string? po = null,
        string? plant = null, string? poType = null, string? storageLoc = null,
        string? supplier = null, string? material = null,
        DateOnly? from = null, DateOnly? to = null,
        int page = 1, int pageSize = 100,
        Models.PlantScope? scope = null,
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
        // free-text contains-match; Plant / PoType / StorageLoc use
        // equality because they come from dropdowns sourced from the
        // same column values. Supplier is free text too -- operators
        // remember a word of the name, rarely the whole thing.
        // Plant matching runs on the EFFECTIVE plant -- COALESCE(override_plant,
        // plant) -- so a manager-reassigned container shows in the target plant's
        // list (and the target plant's scope) and leaves the original's.
        // Material is a contains-match on either the code or the description of
        // ANY line in the triplet (grouping already collapses lines, so a plain
        // predicate matches the whole triplet). doc_date is a SQL DATE, so the
        // From/To range needs no timezone conversion (unlike the arrivals list).
        const string filterClause = @"
            has_arrival = 0
            AND (@Container  IS NULL OR container_no LIKE @Container)
            AND (@Bol        IS NULL OR bol_no       LIKE @Bol)
            AND (@Po         IS NULL OR ebeln        LIKE @Po)
            AND (@Supplier   IS NULL OR vendor_name  LIKE @Supplier OR vendor_no LIKE @Supplier)
            AND (@Material   IS NULL OR material_no  LIKE @Material OR material_desc LIKE @Material)
            AND (@Plant      IS NULL OR COALESCE(override_plant, plant) = @Plant)
            AND (@PoType     IS NULL OR po_type      = @PoType)
            AND (@StorageLoc IS NULL OR storage_loc  = @StorageLoc)
            AND (@From       IS NULL OR doc_date    >= @From)
            AND (@To         IS NULL OR doc_date    <= @To)
            AND (@sUnrestricted = 1 OR COALESCE(override_plant, plant) IN @sPlants)";

        // SQL LIKE wildcards: empty input -> NULL (match everything);
        // populated input -> '%value%' contains-match (operators usually
        // remember a partial number).
        static string? Wrap(string? s) =>
            string.IsNullOrWhiteSpace(s) ? null : $"%{s.Trim()}%";
        // Exact-match: trim and convert empty to null so the (@x IS NULL)
        // branch above kicks in.
        static string? Exact(string? s) =>
            string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        var p = new
        {
            Container  = Wrap(container),
            Bol        = Wrap(bol),
            Po         = Wrap(po),
            Supplier   = Wrap(supplier),
            Material   = Wrap(material),
            Plant      = Exact(plant),
            PoType     = Exact(poType),
            StorageLoc = Exact(storageLoc),
            // doc_date is a SQL DATE; pass DateTime (midnight) rather than
            // DateOnly — this Dapper/SqlClient pairing doesn't bind DateOnly,
            // which is why the arrivals list converts dates the same way. Both
            // bounds are inclusive (doc_date has no time component).
            From       = from?.ToDateTime(TimeOnly.MinValue),
            To         = to?.ToDateTime(TimeOnly.MinValue),
            sUnrestricted = sc.Unrestricted,
            sPlants       = sc.QueryPlants,
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
                MAX(transit_days)  AS TransitDays,
                MIN(first_seen_at) AS FirstSeenAt
            INTO   #page
            FROM   qms_sap_container_cache
            WHERE  {filterClause}
            GROUP BY container_no, bol_no, ebeln
            ORDER BY MAX(doc_date) DESC, container_no, bol_no, ebeln
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;

            SELECT * FROM #page ORDER BY DocDate DESC, ContainerNo, BolNo, Ebeln;

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
        Models.PlantScope? scope = null, CancellationToken ct = default)
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
        using var grid = await c.QueryMultipleAsync(@"
            SELECT DISTINCT COALESCE(override_plant, plant) AS Plant
            FROM   qms_sap_container_cache
            WHERE  has_arrival = 0 AND COALESCE(override_plant, plant) IS NOT NULL AND COALESCE(override_plant, plant) <> ''
              AND (@sUnrestricted = 1 OR COALESCE(override_plant, plant) IN @sPlants)
            ORDER  BY Plant;

            SELECT DISTINCT po_type
            FROM   qms_sap_container_cache
            WHERE  has_arrival = 0 AND po_type IS NOT NULL AND po_type <> ''
              AND (@sUnrestricted = 1 OR COALESCE(override_plant, plant) IN @sPlants)
            ORDER  BY po_type;

            SELECT DISTINCT COALESCE(override_plant, plant) AS Plant, storage_loc AS Code
            FROM   qms_sap_container_cache
            WHERE  has_arrival = 0
              AND  COALESCE(override_plant, plant) IS NOT NULL AND COALESCE(override_plant, plant) <> ''
              AND  storage_loc IS NOT NULL AND storage_loc <> ''
              AND (@sUnrestricted = 1 OR COALESCE(override_plant, plant) IN @sPlants)
            ORDER  BY Plant, storage_loc;",
            new { sUnrestricted = sc.Unrestricted, sPlants = sc.QueryPlants });

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
                WHERE  has_arrival = 0
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

    public async Task<int> RefreshFromSapAsync(DateOnly hardFloorStartDate, string triggeredBy, string triggerSource, CancellationToken ct = default)
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
            message = $"Fetched {totalRows} SAP row(s) since {effectiveSince:yyyy-MM-dd}; reconciled {reconciled} pre-existing arrival(s).";
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
