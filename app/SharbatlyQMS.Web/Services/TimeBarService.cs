using Dapper;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.ViewModels;

namespace SharbatlyQMS.Web.Services;

public interface ITimeBarService
{
    /// <summary>
    /// One page of containers with their inspection clocks, plus the totals for
    /// the whole filtered set and the dropdown sources — in a single round trip.
    /// </summary>
    Task<TimeBarPage> ListAsync(TimeBarFilter filter, PlantScope scope,
        TimeBarConfig cfg, CancellationToken ct = default);

    Task<TimeBarFilterOptions> GetFilterOptionsAsync(PlantScope scope, CancellationToken ct = default);
}

/// <summary>
/// The inspection clock: how long each container waited between arriving and
/// having its quality order finished.
///
/// This is deliberately NOT the arrival time bar the QC report prints. That one
/// measures discharge (or arrival) to QC finish for a container that HAS a
/// quality order. This one starts from the SAP cache, so a container nobody has
/// touched — no arrival, no order, clock running — is a row on the page. Those
/// are the rows the page exists for: the claim window is short, and a container
/// nobody is looking at is the one that runs out of time.
///
/// PERFORMANCE NOTE FOR THE NEXT PERSON: no id list ever crosses the wire here.
/// Every filter is a scalar and the only expanded IN is the plant scope, which
/// is bounded by the plant directory. Do not "optimise" this by selecting a
/// page of keys in C# and re-querying with WHERE (container, bol, po) IN @keys
/// — Dapper expands that to one parameter per item, SQL Server caps a command
/// at 2100, and this application has already been broken twice that way. If a
/// second round trip is ever genuinely needed, spill the page into a #page temp
/// table and JOIN it, as ContainerCacheService.ListPendingAsync does.
/// </summary>
public class TimeBarService : ITimeBarService
{
    private readonly string _cs;
    public TimeBarService(IConfiguration config) =>
        _cs = config.GetConnectionString("Default")
              ?? throw new InvalidOperationException("ConnectionStrings:Default missing");

    private SqlConnection Open() => new(_cs);

    // ---- shared SQL fragments -------------------------------------------
    // Defined once and interpolated everywhere, so the count, the page, the
    // sort and the over-threshold filter can never drift apart.

    /// <summary>
    /// The clock's start date. @Basis picks which source leads; the rest of the
    /// chain is the fallback when it is missing.
    ///
    /// GoodsReceipt leads by default because cc.arrival_date is SAP's
    /// Receive_Date, which is what every other screen in this application calls
    /// the arrival date. A Time Bar that disagreed with the container list about
    /// when a container arrived would be argued with rather than acted on.
    /// </summary>
    private const string StartExpr = @"
        CASE WHEN @Basis = 'PortArrival'
             THEN COALESCE(cc.port_arrival_date, ss.arrival_date, cc.arrival_date, cc.receive_date)
             ELSE COALESCE(cc.arrival_date, cc.receive_date, ss.arrival_date, cc.port_arrival_date)
        END";

    /// <summary>
    /// The clock's end, IN LOCAL TERMS. closed_at is UTC datetime2 while the
    /// arrival columns are SQL DATE holding a local business date; subtracting
    /// one from the other under-counts by a day whenever a quality order is
    /// finished before 03:00 Riyadh time. On a page whose optimum is one day
    /// that is the difference between green and amber, so the UTC value is
    /// shifted into the local frame before any DATEDIFF touches it.
    /// </summary>
    private const string EndExpr = @"
        CASE WHEN qo.closed_at IS NOT NULL
             THEN CAST(DATEADD(MINUTE, @TzOffsetMinutes, qo.closed_at) AS DATE)
             ELSE CAST(@NowLocal AS DATE) END";

    private const string ElapsedExpr = @"
        CASE WHEN " + StartExpr + @" IS NULL THEN NULL
             WHEN DATEDIFF(DAY, " + StartExpr + ", " + EndExpr + @") < 0 THEN 0
             ELSE DATEDIFF(DAY, " + StartExpr + ", " + EndExpr + @") END";

    private const string StageExpr = @"
        CASE WHEN a.arrival_id       IS NULL THEN 'Pending'
             WHEN qo.quality_order_id IS NULL THEN 'Arrival'
             ELSE 'QC' END";

    private const string StatusExpr = @"
        CASE WHEN a.arrival_id       IS NULL THEN NULL
             WHEN qo.quality_order_id IS NULL THEN a.status_code
             ELSE qo.status_code END";

    private const string EffPlantExpr = "COALESCE(cc.eff_plant, a.plant)";

    /// <summary>
    /// The spine and its joins. One row per CONTAINER, whether or not an
    /// arrival or a quality order exists.
    ///
    /// Everything is pre-aggregated in CTEs and joined on plain equality. The
    /// first version used OUTER APPLY ... TOP 1 with ISNULL() on the arrival's
    /// join columns, which is correct but unindexable: a wrapped column cannot
    /// seek, so every one of ~5,400 container keys drove a scan of the whole
    /// arrivals table. Doing the ISNULL once inside the CTE and picking the
    /// latest row with ROW_NUMBER costs one pass instead.
    /// </summary>
    private const string FromClause = @"
        WITH cc AS (
            -- The cache grain is one row per PO LINE, so it is collapsed to one
            -- row per container BEFORE anything joins to it; joining it raw and
            -- grouping afterwards multiplies every container by its line count.
            SELECT container_no, bol_no, ebeln,
                   MAX(arrival_date)      AS arrival_date,
                   MAX(receive_date)      AS receive_date,
                   MAX(port_arrival_date) AS port_arrival_date,
                   MAX(vendor_name)       AS vendor_name,
                   MAX(vendor_no)         AS vendor_no,
                   MAX(po_type)           AS po_type,
                   MAX(sto)               AS sto,
                   MAX(archived_at)       AS archived_at,
                   COALESCE(MAX(override_plant), MAX(plant)) AS eff_plant
            FROM   qms_sap_container_cache
            GROUP  BY container_no, bol_no, ebeln
        ),
        arr AS (
            -- ISNULL is load-bearing. The cache columns are NOT NULL and store
            -- '' for a BOL-less shipment while the arrival columns are nullable;
            -- without it a NULL-BOL arrival becomes a SECOND key for a container
            -- that already has a cache row, and the container is listed twice.
            SELECT ISNULL(container_no, '') AS cn,
                   ISNULL(bol_no,       '') AS bn,
                   ISNULL(ebeln,        '') AS pn,
                   arrival_id, arrival_no, status_code, plant,
                   ROW_NUMBER() OVER (
                       PARTITION BY ISNULL(container_no, ''), ISNULL(bol_no, ''), ISNULL(ebeln, '')
                       ORDER BY arrival_id DESC) AS rn
            FROM   qms_arrival
            WHERE  status_code <> 'Cancelled'
        ),
        qorder AS (
            -- Reinspections are excluded, so this page keeps following the
            -- ORIGINAL inspection. Left in, ROW_NUMBER's DESC would pick the
            -- reinspection: a container inspected on time would have its clock
            -- restart weeks later, turn from green to over-threshold, and lose
            -- the finish date it was actually judged on.
            SELECT arrival_id, quality_order_id, quality_order_no,
                   status_code, closed_at, archived_at,
                   ROW_NUMBER() OVER (PARTITION BY arrival_id ORDER BY quality_order_id DESC) AS rn
            FROM   qms_quality_order
            WHERE  status_code <> 'Cancelled' AND reinspection_of IS NULL
        ),
        k AS (
            -- Every container key, from BOTH sides. The cache alone is not
            -- enough: an arrival created through /Arrivals/Search has no cache
            -- row, and starting from the cache would silently drop it. UNION
            -- (not UNION ALL) guarantees one row per container.
            SELECT container_no, bol_no, ebeln FROM cc
            UNION
            SELECT cn, bn, pn FROM arr
        )
        SELECT
               k.container_no                       AS ContainerNo,
               k.bol_no                             AS BolNo,
               k.ebeln                              AS Ebeln,
               cc.sto                               AS Sto,
               cc.vendor_name                       AS VendorName,
               " + EffPlantExpr + @"                AS Plant,
               cc.po_type                           AS PoType,
               (" + StartExpr + @")                 AS ArrivalDate,
               CASE WHEN (" + StartExpr + @") IS NULL THEN NULL
                    WHEN @Basis = 'PortArrival' AND cc.port_arrival_date IS NOT NULL THEN 'Port'
                    WHEN @Basis <> 'PortArrival' AND cc.arrival_date IS NOT NULL THEN 'Receipt'
                    WHEN ss.arrival_date IS NOT NULL THEN 'Snapshot'
                    ELSE 'Other' END                AS ArrivalSource,
               a.arrival_id                         AS ArrivalId,
               a.arrival_no                         AS ArrivalNo,
               qo.quality_order_id                  AS QualityOrderId,
               qo.quality_order_no                  AS QualityOrderNo,
               qo.closed_at                         AS ClosedAt,
               (" + StageExpr + @")                 AS Stage,
               (" + StatusExpr + @")                AS StatusCode,
               (" + ElapsedExpr + @")               AS ElapsedDays,
               CASE WHEN qo.closed_at IS NULL THEN 1 ELSE 0 END AS IsRunning,
               CASE WHEN (" + StartExpr + @") IS NOT NULL
                     AND DATEDIFF(DAY, (" + StartExpr + @"), (" + EndExpr + @")) < 0
                    THEN 1 ELSE 0 END               AS IsBackwards,
               CASE WHEN cc.container_no IS NULL THEN 1 ELSE 0 END AS NotInCache,
               CASE WHEN cc.archived_at IS NOT NULL OR qo.archived_at IS NOT NULL
                    THEN 1 ELSE 0 END               AS IsArchived
        INTO   #tb
        FROM   k
        LEFT JOIN cc ON cc.container_no = k.container_no
                    AND cc.bol_no       = k.bol_no
                    AND cc.ebeln        = k.ebeln
        LEFT JOIN arr a ON a.cn = k.container_no
                       AND a.bn = k.bol_no
                       AND a.pn = k.ebeln
                       AND a.rn = 1
        LEFT JOIN qms_shipment_snapshot ss ON ss.arrival_id = a.arrival_id
        LEFT JOIN qorder qo ON qo.arrival_id = a.arrival_id AND qo.rn = 1";

    private static string WhereClause => $@"
        WHERE (@sUnrestricted = 1 OR {EffPlantExpr} IN @sPlants)
          -- A container counts as archived if EITHER its cache triplet was filed
          -- away OR its quality order was; hiding only one leaves half-archived
          -- containers cluttering a list that claims to be live work.
          AND (@IncludeArchived = 1 OR (cc.archived_at IS NULL AND qo.archived_at IS NULL))
          AND (@Search    IS NULL OR k.container_no LIKE @Search OR k.bol_no LIKE @Search
                                  OR k.ebeln LIKE @Search OR a.arrival_no LIKE @Search
                                  OR qo.quality_order_no LIKE @Search OR cc.vendor_name LIKE @Search)
          AND (@Container IS NULL OR k.container_no LIKE @Container)
          AND (@Bol       IS NULL OR k.bol_no       LIKE @Bol)
          AND (@Po        IS NULL OR k.ebeln        LIKE @Po)
          AND (@Supplier  IS NULL OR cc.vendor_name LIKE @Supplier OR cc.vendor_no LIKE @Supplier)
          AND (@Plant     IS NULL OR {EffPlantExpr} = @Plant)
          AND (@PoType    IS NULL OR cc.po_type = @PoType)
          -- Material master categories. The cache is aggregated to one row per
          -- container before this point, so the test has to go back to the raw
          -- lines: a container counts as, say, Apples if ANY of its lines is.
          AND (@matMajor IS NULL AND @matSubMajor IS NULL OR EXISTS (
                    SELECT 1
                    FROM   qms_sap_container_cache cl
                    JOIN   qms_sap_material_cache  mc ON mc.material_no = cl.material_no
                    WHERE  cl.container_no = k.container_no
                      AND  cl.bol_no       = k.bol_no
                      AND  cl.ebeln        = k.ebeln
                      AND (@matMajor    IS NULL OR
                           COALESCE(NULLIF(mc.major_category_desc,''), NULLIF(mc.major_category,'')) = @matMajor)
                      AND (@matSubMajor IS NULL OR mc.sub_major_category = @matSubMajor)))
          AND (@Stage     IS NULL OR ({StageExpr}) = @Stage)
          AND (@Status    IS NULL OR ({StatusExpr}) = @Status)
          -- The go-live floor. Applied to the count, the summary, the median and
          -- the page alike, because a page whose header disagreed with its own
          -- rows would be worse than no page. A container with no arrival date
          -- is dropped too: it cannot be shown to have arrived after the floor,
          -- and it has no clock to measure.
          AND (@CutOff    IS NULL OR (({StartExpr}) IS NOT NULL AND ({StartExpr}) >= @CutOff))
          AND (@From      IS NULL OR ({StartExpr}) >= @From)
          AND (@To        IS NULL OR ({StartExpr}) <= @To)
          AND (@NoCacheOnly = 0 OR cc.container_no IS NULL)
          AND (@MinDays   IS NULL OR ({ElapsedExpr}) >= @MinDays)
          AND (@OverOnly  = 0 OR (({ElapsedExpr}) IS NOT NULL AND ({ElapsedExpr}) > @WarnDays))";

    /// <summary>
    /// One round trip: the filtered set is materialised into a temp table once,
    /// then counted, summarised and paged from there.
    ///
    /// It used to run the whole join tree four times over -- once each for the
    /// total, the summary, the median and the page -- recomputing the same
    /// clock expressions on every pass. On ~5,400 containers that made the page
    /// take seconds to open.
    ///
    /// No id list ever crosses the wire: every filter is a scalar, so the
    /// 2100-parameter limit that has broken this application twice cannot apply.
    /// </summary>
    public async Task<TimeBarPage> ListAsync(TimeBarFilter f, PlantScope scope,
        TimeBarConfig cfg, CancellationToken ct = default)
    {
        f     ??= new TimeBarFilter();
        scope ??= PlantScope.All;
        cfg   ??= new TimeBarConfig();

        var pageSize = f.PageSize is 50 or 100 ? f.PageSize : 100;
        var page     = f.Page < 1 ? 1 : f.Page;
        var offset   = (page - 1) * pageSize;

        var now = DateTime.Now;
        var p = new
        {
            Basis           = cfg.ArrivalBasis,
            WarnDays        = cfg.WarnDays,
            CutOff          = cfg.StartDate?.ToDateTime(TimeOnly.MinValue),
            TzOffsetMinutes = (int)TimeZoneInfo.Local.GetUtcOffset(now).TotalMinutes,
            NowLocal        = now,
            Search    = Wrap(f.Search),
            Container = Wrap(f.Container),
            Bol       = Wrap(f.Bol),
            Po        = Wrap(f.Po),
            Supplier  = Wrap(f.Supplier),
            Plant     = Exact(f.Plant),
            PoType    = Exact(f.PoType),
            matMajor    = Exact(f.MatMajor),
            matSubMajor = Exact(f.MatSubMajor),
            Stage     = Exact(f.Stage),
            Status    = Exact(f.Status),
            From      = f.From?.ToDateTime(TimeOnly.MinValue),
            To        = f.To?.ToDateTime(TimeOnly.MinValue),
            OverOnly  = f.OverOnly,
            NoCacheOnly = f.NoCacheOnly,
            IncludeArchived = f.IncludeArchived,
            MinDays   = f.MinDays,
            sUnrestricted = scope.Unrestricted,
            sPlants       = scope.QueryPlants,
            offset, pageSize
        };

        using var c = Open();
        using var grid = await c.QueryMultipleAsync(new CommandDefinition($@"
            -- 0) build the filtered set ONCE
            {FromClause} {WhereClause};

            -- 1) total matching containers (drives the pager)
            SELECT COUNT(*) FROM #tb;

            -- 2) the headline totals, over the WHOLE filtered set rather than
            --    the visible page -- a summary of one page would be misleading.
            SELECT COUNT(*)                                                  AS Containers,
                   SUM(CASE WHEN ArrivalId IS NULL THEN 1 ELSE 0 END)        AS Pending,
                   SUM(CASE WHEN ClosedAt  IS NULL THEN 1 ELSE 0 END)        AS Running,
                   SUM(CASE WHEN ElapsedDays > @WarnDays THEN 1 ELSE 0 END)  AS OverThreshold
            FROM   #tb;

            -- 3) median elapsed among SETTLED rows. Median, not mean: one
            --    container stuck for 90 days would drag an average to a number
            --    no individual container is anywhere near.
            SELECT DISTINCT PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY CAST(ElapsedDays AS FLOAT))
                   OVER ()                                                   AS MedianSettled
            FROM   #tb
            WHERE  ClosedAt IS NOT NULL AND ArrivalDate IS NOT NULL;

            -- 4) the page. Worst first: the longest clock at the top, unknown
            --    dates last, and among equals the ones still running -- those
            --    are still growing. The container triplet closes the sort
            --    because without a total order OFFSET/FETCH can place a tied
            --    row on two pages, or on none.
            SELECT * FROM #tb
            ORDER BY CASE WHEN ElapsedDays IS NULL THEN 1 ELSE 0 END,
                     ElapsedDays DESC,
                     IsRunning DESC,
                     ContainerNo, BolNo, Ebeln
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;

            DROP TABLE #tb;",
            p, commandTimeout: 120, cancellationToken: ct));

        var total   = await grid.ReadFirstAsync<int>();
        var totals  = await grid.ReadFirstAsync<TimeBarSummary>();
        var median  = await grid.ReadFirstOrDefaultAsync<double?>();
        var rows    = (await grid.ReadAsync<TimeBarRow>()).ToList();

        totals.MedianSettled = median.HasValue ? (int)Math.Round(median.Value) : null;
        return new TimeBarPage(rows, total, page, pageSize, totals);

    }

    public async Task<TimeBarFilterOptions> GetFilterOptionsAsync(PlantScope scope, CancellationToken ct = default)
    {
        var sc = scope ?? PlantScope.All;
        using var c = Open();
        // Drawn from the data the user can actually see, so a dropdown never
        // offers a value that returns an empty page.
        using var grid = await c.QueryMultipleAsync(new CommandDefinition(@"
            SELECT DISTINCT COALESCE(override_plant, plant) AS Plant
            FROM   qms_sap_container_cache
            WHERE  COALESCE(override_plant, plant) IS NOT NULL
              AND  COALESCE(override_plant, plant) <> ''
              AND (@sUnrestricted = 1 OR COALESCE(override_plant, plant) IN @sPlants)
            ORDER  BY Plant;

            SELECT DISTINCT po_type
            FROM   qms_sap_container_cache
            WHERE  po_type IS NOT NULL AND po_type <> ''
              AND (@sUnrestricted = 1 OR COALESCE(override_plant, plant) IN @sPlants)
            ORDER  BY po_type;

            -- Arrival AND quality-order codes together: the Status column shows
            -- whichever applies, so the filter has to offer both.
            SELECT DISTINCT s FROM (
                SELECT status_code AS s FROM qms_arrival        WHERE status_code <> 'Cancelled'
                UNION
                SELECT status_code      FROM qms_quality_order  WHERE status_code <> 'Cancelled'
            ) x WHERE s IS NOT NULL ORDER BY s;",
            new { sUnrestricted = sc.Unrestricted, sPlants = sc.QueryPlants },
            cancellationToken: ct));

        return new TimeBarFilterOptions
        {
            Plants   = (await grid.ReadAsync<string>()).ToList(),
            PoTypes  = (await grid.ReadAsync<string>()).ToList(),
            Statuses = (await grid.ReadAsync<string>()).ToList()
        };
    }

    private static string? Wrap(string? s)  => string.IsNullOrWhiteSpace(s) ? null : $"%{s.Trim()}%";
    private static string? Exact(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
