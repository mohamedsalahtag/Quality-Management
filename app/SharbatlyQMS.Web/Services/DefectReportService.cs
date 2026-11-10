using Dapper;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Reports -> Defect reports: the defect pivots (per week, per defect &amp; class,
/// per branch &amp; defect, per supplier, per sub-major, per major).
///
/// The page receives one compact dataset per receive-date window and pivots it
/// in the browser, so every filter and tab is instant. The grain is one
/// MATERIAL LINE of a finished, live quality order: its filter dimensions, its
/// sample count and inspected units, plus one total per defect that occurred.
/// That is enough to compute both figures exactly:
///
///   QC report %    = defective units / inspected units. A category total adds
///                    its defects up -- the Total line on the QC report.
///   Excel average  = the plain average of every sample's Defect %, the figure
///                    the team's Excel pivots showed. Every sample stores a row
///                    for every defect of its fruit (qms_sample_defect), so the
///                    row count behind an average is samples x defects, which
///                    is why the per-group defect sets travel with the data.
///
/// Finished inspections only (Closed, not superseded: a reinspection replaces
/// the order it was raised on), dated by the branch receive date.
/// </summary>
public interface IDefectReportService
{
    Task<DefectReportDataset> GetDatasetAsync(DateTime from, DateTime to, PlantScope scope, CancellationToken ct = default);
    Task<IReadOnlyList<DefectDrillRow>> DrillAsync(DefectDrillRequest req, PlantScope scope, CancellationToken ct = default);
    Task<IReadOnlyList<BananaRipeRow>> GetBananaRipenessAsync(DateTime from, DateTime to, PlantScope scope, CancellationToken ct = default);
}

public class DefectReportService : IDefectReportService
{
    /// <summary>A year: the page stays a browser-side pivot, and a wider window
    /// is a job for the Data hub export.</summary>
    public const int MaxDays = 366;

    private readonly string _cs;
    private readonly ICodeDescriptionDirectory _codes;

    public DefectReportService(IConfiguration config, ICodeDescriptionDirectory codes)
    {
        _cs = config.GetConnectionString("Default")
              ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
        _codes = codes;
    }

    private sealed class UnitRow
    {
        public long QoMaterialId { get; set; }
        public long QualityOrderId { get; set; }
        public string QualityOrderNo { get; set; } = "";
        public string Plant { get; set; } = "";
        public string Supplier { get; set; } = "";
        public string Major { get; set; } = "";
        public string Sub { get; set; } = "";
        public string Class { get; set; } = "";
        public string Variety { get; set; } = "";
        public DateTime ReceiveDate { get; set; }
        public string ContainerNo { get; set; } = "";
        public string MaterialGroup { get; set; } = "";
        public int Samples { get; set; }
        public decimal Inspected { get; set; }
        /// <summary>Carton net weight from SAP's material master (kg).</summary>
        public decimal? PackWeight { get; set; }
        /// <summary>PO quantity of the line (arrival item), counted once per
        /// order and arrival line.</summary>
        public decimal Cartons { get; set; }
        public string Uom { get; set; } = "";
        public string PackWeightLabel => PackWeightText(PackWeight);
    }

    /// <summary>"15 kg", "0.25 kg"; the same text the filter offers.</summary>
    public static string PackWeightText(decimal? w) =>
        w is > 0 ? $"{w.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} kg" : "(no weight)";
    private sealed record FactRow(long QoMaterialId, int DefectId, decimal Units, decimal PctSum);
    private sealed record GroupDefectRow(string MaterialGroup, int DefectId);
    private sealed record DefectRow(int DefectId, string Name, string Category, string MaterialGroup, int SortOrder);
    private sealed record CategoryRow(string Name, int SortOrder, string? Color);

    // One batch: the window's material lines into a temp table, then the four
    // result sets that hang off it. The receive date is the same expression as
    // the Data hub's ReceiveDate (cache first, snapshot second).
    private const string Sql = @"
        SET NOCOUNT ON;
        SELECT m.qo_material_id                                          AS QoMaterialId,
               qo.quality_order_id                                       AS QualityOrderId,
               qo.quality_order_no                                       AS QualityOrderNo,
               ISNULL(a.plant, '')                                       AS Plant,
               ISNULL(a.vendor_name, '')                                 AS Supplier,
               ISNULL(COALESCE(NULLIF(m.major_category, N''), mc.major_category_desc), '') AS Major,
               ISNULL(mc.sub_major_category, '')                         AS Sub,
               ISNULL(COALESCE(NULLIF(m.material_class, N''), mc.class_name), '') AS Class,
               ISNULL(COALESCE(NULLIF(m.variety, N''), mc.variety_name), '') AS Variety,
               COALESCE(cc.receive_date, ss.receive_date, ss.arrival_date) AS ReceiveDate,
               ISNULL(a.container_no, '')                                AS ContainerNo,
               ISNULL(m.material_group, '')                              AS MaterialGroup,
               NULLIF(ISNULL(NULLIF(m.net_weight, 0), mc.weight), 0)    AS PackWeight,
               -- PO quantity once per order and arrival line, so two material
               -- lines raised from one arrival line do not count it twice.
               CASE WHEN ROW_NUMBER() OVER (PARTITION BY qo.quality_order_id, m.arrival_item_id
                                            ORDER BY m.qo_material_id) = 1
                    THEN CAST(ISNULL(ai.quantity, 0) AS DECIMAL(18,3)) ELSE 0 END AS Cartons,
               ISNULL(ai.uom, '')                                        AS Uom
        INTO   #u
        FROM   qms_quality_order qo
        JOIN   qms_quality_order_material m ON m.quality_order_id = qo.quality_order_id
        LEFT JOIN qms_arrival_item ai       ON ai.arrival_item_id = m.arrival_item_id
        JOIN   qms_arrival a                ON a.arrival_id = qo.arrival_id
        LEFT JOIN qms_shipment_snapshot ss  ON ss.arrival_id = a.arrival_id
        LEFT JOIN qms_sap_material_cache mc ON mc.material_no = m.material_no
        OUTER APPLY (SELECT TOP 1 c2.receive_date FROM qms_sap_container_cache c2
                     WHERE c2.container_no = a.container_no AND c2.bol_no = a.bol_no AND c2.ebeln = a.ebeln
                     ORDER BY c2.doc_date DESC, c2.sto) cc
        WHERE  qo.status_code = 'Closed' AND qo.superseded_at IS NULL
          AND  COALESCE(cc.receive_date, ss.receive_date, ss.arrival_date) >= @from
          AND  COALESCE(cc.receive_date, ss.receive_date, ss.arrival_date) <  @toEx
          AND  (@sUnrestricted = 1 OR a.plant IN @sPlants);

        SELECT u.*, x.Samples, x.Inspected
        FROM   #u u
        CROSS APPLY (SELECT COUNT(*) AS Samples,
                            SUM(CAST(ISNULL(s.sample_size, 0) AS DECIMAL(18,2))) AS Inspected
                     FROM   qms_sample s
                     WHERE  s.qo_material_id = u.QoMaterialId AND s.is_deleted = 0) x
        WHERE  x.Samples > 0;

        SELECT s.qo_material_id                                   AS QoMaterialId,
               sd.defect_id                                       AS DefectId,
               SUM(CAST(sd.defect_value AS DECIMAL(18,2)))        AS Units,
               SUM(CAST(ISNULL(sd.defect_percentage, 0) AS DECIMAL(18,4))) AS PctSum
        FROM   #u u
        JOIN   qms_sample s         ON s.qo_material_id = u.QoMaterialId AND s.is_deleted = 0
        JOIN   qms_sample_defect sd ON sd.sample_id = s.sample_id
        GROUP  BY s.qo_material_id, sd.defect_id
        HAVING SUM(sd.defect_value) > 0;

        SELECT DISTINCT u.MaterialGroup, sd.defect_id AS DefectId
        FROM   #u u
        JOIN   qms_sample s         ON s.qo_material_id = u.QoMaterialId AND s.is_deleted = 0
        JOIN   qms_sample_defect sd ON sd.sample_id = s.sample_id;

        SELECT category_name AS Name, sort_order AS SortOrder, color_hex AS Color
        FROM   qms_defect_category
        ORDER  BY sort_order, category_name;

        DROP TABLE #u;";

    private sealed record Raw(List<UnitRow> Units, List<FactRow> Facts, List<GroupDefectRow> GroupDefects,
                              List<DefectRow> Defects, List<CategoryRow> Categories);

    private async Task<Raw> LoadAsync(DateTime from, DateTime to, PlantScope scope, CancellationToken ct)
    {
        from = from.Date; to = to.Date;
        if (to < from) (from, to) = (to, from);
        if ((to - from).TotalDays > MaxDays) from = to.AddDays(-MaxDays);

        using var c = new SqlConnection(_cs);
        await c.OpenAsync(ct);
        using var grid = await c.QueryMultipleAsync(new CommandDefinition(Sql, new
        {
            from, toEx = to.AddDays(1),
            sUnrestricted = scope.Unrestricted, sPlants = scope.QueryPlants
        }, commandTimeout: 180, cancellationToken: ct));
        var units  = (await grid.ReadAsync<UnitRow>()).ToList();
        var facts  = (await grid.ReadAsync<FactRow>()).ToList();
        var gd     = (await grid.ReadAsync<GroupDefectRow>()).ToList();
        var cats   = (await grid.ReadAsync<CategoryRow>()).ToList();

        var ids = gd.Select(x => x.DefectId).Concat(facts.Select(f => f.DefectId)).Distinct().ToArray();
        var defects = ids.Length == 0 ? new List<DefectRow>() : (await c.QueryAsync<DefectRow>(@"
            SELECT defect_id AS DefectId, defect_name AS Name, defect_category AS Category,
                   material_group AS MaterialGroup, sort_order AS SortOrder
            FROM   qms_defect_catalog WHERE defect_id IN @ids", new { ids })).ToList();
        return new Raw(units, facts, gd, defects, cats);
    }

    public async Task<DefectReportDataset> GetDatasetAsync(DateTime from, DateTime to, PlantScope scope, CancellationToken ct = default)
    {
        var raw = await LoadAsync(from, to, scope, ct);

        static (List<string> Values, Dictionary<string, int> Index) Dict(IEnumerable<string> src)
        {
            var values = src.Distinct().OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
            return (values, values.Select((v, i) => (v, i)).ToDictionary(x => x.v, x => x.i));
        }
        var plants = Dict(raw.Units.Select(u => u.Plant));
        var supps  = Dict(raw.Units.Select(u => u.Supplier));
        var majors = Dict(raw.Units.Select(u => u.Major));
        var subs   = Dict(raw.Units.Select(u => u.Sub));
        var classes= Dict(raw.Units.Select(u => u.Class));
        var groups = Dict(raw.Units.Select(u => u.MaterialGroup));
        var weights= raw.Units.Select(u => u.PackWeight).Distinct()
                        .OrderBy(w => w ?? decimal.MaxValue).Select(PackWeightText).Distinct().ToList();
        var weightIndex = weights.Select((v, i) => (v, i)).ToDictionary(x => x.v, x => x.i);
        var uoms   = Dict(raw.Units.Select(u => u.Uom));
        var varieties = Dict(raw.Units.Select(u => u.Variety));

        var unitIndex = new Dictionary<long, int>();
        var units = new List<object[]>(raw.Units.Count);
        foreach (var u in raw.Units)
        {
            unitIndex[u.QoMaterialId] = units.Count;
            units.Add(new object[]
            {
                u.QualityOrderId, u.QualityOrderNo, plants.Index[u.Plant], supps.Index[u.Supplier],
                majors.Index[u.Major], subs.Index[u.Sub], classes.Index[u.Class],
                u.ReceiveDate.ToString("yyyy-MM-dd"), u.ContainerNo, groups.Index[u.MaterialGroup],
                u.Samples, u.Inspected, weightIndex[u.PackWeightLabel], u.Cartons, uoms.Index[u.Uom],
                varieties.Index[u.Variety]
            });
        }

        var defects = raw.Defects.OrderBy(d => d.DefectId).ToList();
        var defectIndex = defects.Select((d, i) => (d.DefectId, i)).ToDictionary(x => x.DefectId, x => x.i);

        var groupDefects = groups.Values.ToDictionary(g => g, _ => new List<int>());
        foreach (var r in raw.GroupDefects)
            if (groupDefects.TryGetValue(r.MaterialGroup, out var list) && defectIndex.TryGetValue(r.DefectId, out var di))
                list.Add(di);

        var facts = new List<object[]>(raw.Facts.Count);
        foreach (var f in raw.Facts)
            if (unitIndex.TryGetValue(f.QoMaterialId, out var ui) && defectIndex.TryGetValue(f.DefectId, out var di))
                facts.Add(new object[] { ui, di, f.Units, Math.Round(f.PctSum, 4) });

        return new DefectReportDataset
        {
            From = from.Date.ToString("yyyy-MM-dd"),
            To   = to.Date.ToString("yyyy-MM-dd"),
            Plants = plants.Values, Suppliers = supps.Values, Majors = majors.Values,
            Subs = subs.Values, Classes = classes.Values, Groups = groups.Values,
            PackWeights = weights, Uoms = uoms.Values, Varieties = varieties.Values,
            // From the code directory, so a name changed on the Code Descriptions
            // screen shows here too, as it does on every other page.
            PlantNames = _codes.Plants.Select(p => p.Code).Concat(plants.Values).Distinct()
                .ToDictionary(c => c, c => _codes.PlantDisplay(c)),
            Units = units,
            Defects = defects.Select(d => new object[] { d.DefectId, d.Name, d.Category, d.MaterialGroup, d.SortOrder }).ToList(),
            GroupDefects = groupDefects.ToDictionary(kv => kv.Key, kv => kv.Value.OrderBy(x => x).ToList()),
            Facts = facts,
            Categories = raw.Categories.Select(c => new DefectReportCategory(c.Name, c.SortOrder, c.Color)).ToList(),
            Generated = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
        };
    }

    // ------------------------------------------------------------------
    // Drill-down: the orders behind one number. Same rules as the page's
    // own drill panel (wwwroot/js/defect-reports.js, openDrill), so the
    // workbook lists exactly the rows the panel showed.
    // ------------------------------------------------------------------
    public async Task<IReadOnlyList<DefectDrillRow>> DrillAsync(DefectDrillRequest req, PlantScope scope, CancellationToken ct = default)
    {
        var raw = await LoadAsync(req.From, req.To, scope, ct);
        var defectById = raw.Defects.ToDictionary(d => d.DefectId);
        var groupSets = raw.GroupDefects.GroupBy(x => x.MaterialGroup)
            .ToDictionary(g => g.Key, g => g.Select(x => defectById.TryGetValue(x.DefectId, out var d) ? d : null)
                                            .Where(d => d != null).Select(d => d!).ToList());
        var factsBy = raw.Facts.ToLookup(f => f.QoMaterialId);
        bool CatOk(string c) => req.Cat.Count == 0 || req.Cat.Contains(c);
        bool Matches(DefectRow d) =>
            req.DefectKey == "T"
            || (req.DefectKey.StartsWith("C:") && d.Category == req.DefectKey[2..])
            || (req.DefectKey.StartsWith("N:") && d.Name == req.DefectKey[2..]);

        var perQo = new Dictionary<long, DefectDrillRow>();
        foreach (var u in raw.Units)
        {
            if (!PassesFilters(u, req) || !InRow(u, req)) continue;
            if (!groupSets.TryGetValue(u.MaterialGroup, out var set)) continue;
            var rows = set.Count(d => CatOk(d.Category) && Matches(d));
            if (rows == 0) continue;
            decimal units = 0, pct = 0;
            foreach (var f in factsBy[u.QoMaterialId])
                if (defectById.TryGetValue(f.DefectId, out var d) && CatOk(d.Category) && Matches(d))
                { units += f.Units; pct += f.PctSum; }

            if (!perQo.TryGetValue(u.QualityOrderId, out var o))
            {
                o = new DefectDrillRow
                {
                    QualityOrderId = u.QualityOrderId, QualityOrderNo = u.QualityOrderNo, ReceiveDate = u.ReceiveDate,
                    Plant = u.Plant, Supplier = u.Supplier, ContainerNo = u.ContainerNo
                };
                perQo[u.QualityOrderId] = o;
            }
            var item = string.IsNullOrEmpty(u.Class) ? u.Sub : $"{u.Sub} · {u.Class}";
            if (!o.Materials.Contains(item)) o.Materials.Add(item);
            o.Samples += u.Samples; o.Inspected += u.Inspected; o.Defective += units; o.Cartons += u.Cartons;
            o.PctSum += pct; o.SampleDefectRows += u.Samples * rows;
        }
        var excel = string.Equals(req.Metric, "xls", StringComparison.OrdinalIgnoreCase);
        foreach (var o in perQo.Values)
            o.Percent = excel ? (o.SampleDefectRows > 0 ? o.PctSum / o.SampleDefectRows : null)
                              : (o.Inspected > 0 ? 100m * o.Defective / o.Inspected : null);
        return perQo.Values.OrderByDescending(o => o.Percent ?? -1).ThenByDescending(o => o.ReceiveDate).ToList();
    }

    // ------------------------------------------------------------------
    // Banana ripeness: one row per banana container, from the two counts the
    // receiving inspector enters on the arrival (Arrival fields "Yellow Ripe"
    // and "Soft Green/Destroyed", material group BANANA). Unlike the defect
    // pivots this is an ARRIVAL measurement, so it does not wait for the
    // inspection to be finished; containers with neither count are left out.
    // Matched by field name, not id, because the two databases number them
    // differently.
    // ------------------------------------------------------------------
    private const string BananaSql = @"
        SELECT a.arrival_id                    AS ArrivalId,
               a.arrival_no                    AS ArrivalNo,
               qo.quality_order_id             AS QualityOrderId,
               qo.quality_order_no             AS QualityOrderNo,
               COALESCE(cc.receive_date, ss.receive_date, ss.arrival_date) AS ReceiveDate,
               ISNULL(ss.vessel_name, '')      AS Vessel,
               ISNULL(a.container_no, '')      AS ContainerNo,
               ISNULL(a.bol_no, '')            AS BolNo,
               ISNULL(a.ebeln, '')             AS Po,
               ISNULL(a.plant, '')             AS Plant,
               ss.loading_country              AS LoadingCountry,
               ISNULL(a.vendor_name, '')       AS Supplier,
               ss.sailing_date                 AS Loading,
               ss.discharge_date               AS Discharge,
               ss.pullout_date                 AS PullOut,
               ss.transit_days                 AS SapTransitDays,
               b.Qty                           AS ReceivedQty,
               b.PackWeight                    AS PackWeight,
               yr.numeric_value                AS YellowRipe,
               gs.numeric_value                AS GreenSoft
        FROM   qms_arrival a
        CROSS APPLY (SELECT SUM(CAST(ISNULL(ai.quantity, 0) AS DECIMAL(18,3))) AS Qty,
                            MAX(NULLIF(mc.weight, 0)) AS PackWeight
                     FROM   qms_arrival_item ai
                     LEFT JOIN qms_sap_material_cache mc ON mc.material_no = ai.material_no
                     WHERE  ai.arrival_id = a.arrival_id AND ai.material_group = 'BANANA'
                     HAVING COUNT(*) > 0) b
        LEFT JOIN qms_shipment_snapshot ss ON ss.arrival_id = a.arrival_id
        OUTER APPLY (SELECT TOP 1 c2.receive_date FROM qms_sap_container_cache c2
                     WHERE c2.container_no = a.container_no AND c2.bol_no = a.bol_no AND c2.ebeln = a.ebeln
                     ORDER BY c2.doc_date DESC, c2.sto) cc
        OUTER APPLY (SELECT TOP 1 v.numeric_value FROM qms_arrival_field_value v
                     JOIN qms_arrival_field f ON f.field_id = v.field_id
                     WHERE v.arrival_id = a.arrival_id AND f.material_group = 'BANANA'
                       AND f.field_name LIKE N'%Yellow Ripe%') yr
        OUTER APPLY (SELECT TOP 1 v.numeric_value FROM qms_arrival_field_value v
                     JOIN qms_arrival_field f ON f.field_id = v.field_id
                     WHERE v.arrival_id = a.arrival_id AND f.material_group = 'BANANA'
                       AND (f.field_name LIKE N'%Soft Green%' OR f.field_name LIKE N'%Green Soft%')) gs
        OUTER APPLY (SELECT TOP 1 q.quality_order_id, q.quality_order_no FROM qms_quality_order q
                     WHERE q.arrival_id = a.arrival_id AND q.status_code <> 'Cancelled' AND q.superseded_at IS NULL) qo
        WHERE  COALESCE(cc.receive_date, ss.receive_date, ss.arrival_date) >= @from
          AND  COALESCE(cc.receive_date, ss.receive_date, ss.arrival_date) <  @toEx
          AND  (yr.numeric_value IS NOT NULL OR gs.numeric_value IS NOT NULL)
          AND  (@sUnrestricted = 1 OR a.plant IN @sPlants)
        ORDER  BY ReceiveDate, a.container_no;";

    private sealed class BananaRaw
    {
        public long ArrivalId { get; set; }
        public string? ArrivalNo { get; set; }
        public long? QualityOrderId { get; set; }
        public string? QualityOrderNo { get; set; }
        public DateTime ReceiveDate { get; set; }
        public string Vessel { get; set; } = "";
        public string ContainerNo { get; set; } = "";
        public string BolNo { get; set; } = "";
        public string Po { get; set; } = "";
        public string Plant { get; set; } = "";
        public string? LoadingCountry { get; set; }
        public string Supplier { get; set; } = "";
        public DateTime? Loading { get; set; }
        public DateTime? Discharge { get; set; }
        public DateTime? PullOut { get; set; }
        public short? SapTransitDays { get; set; }
        public decimal ReceivedQty { get; set; }
        public decimal? PackWeight { get; set; }
        public decimal? YellowRipe { get; set; }
        public decimal? GreenSoft { get; set; }
    }

    public async Task<IReadOnlyList<BananaRipeRow>> GetBananaRipenessAsync(DateTime from, DateTime to, PlantScope scope, CancellationToken ct = default)
    {
        from = from.Date; to = to.Date;
        if (to < from) (from, to) = (to, from);
        if ((to - from).TotalDays > MaxDays) from = to.AddDays(-MaxDays);

        using var c = new SqlConnection(_cs);
        var raw = await c.QueryAsync<BananaRaw>(new CommandDefinition(BananaSql, new
        {
            from, toEx = to.AddDays(1),
            sUnrestricted = scope.Unrestricted, sPlants = scope.QueryPlants
        }, commandTimeout: 180, cancellationToken: ct));

        return raw.Select(r =>
        {
            var transit = ShipmentDates.TransitDays(r.Loading, r.Discharge, r.SapTransitDays);
            var inPort = BananaDaysInPort(r.Discharge, r.PullOut);
            var ripe = (r.YellowRipe ?? 0) + (r.GreenSoft ?? 0);
            return new BananaRipeRow
            {
                ArrivalId = r.ArrivalId, ArrivalNo = r.ArrivalNo ?? "",
                QualityOrderId = r.QualityOrderId, QualityOrderNo = r.QualityOrderNo,
                Received = r.ReceiveDate.ToString("yyyy-MM-dd"),
                Vessel = r.Vessel, ContainerNo = r.ContainerNo, BolNo = r.BolNo, Po = r.Po, Plant = r.Plant,
                Origin = Countries.Display(r.LoadingCountry), Supplier = r.Supplier,
                Weight = r.PackWeight is > 0 ? PackWeightText(r.PackWeight) : "",
                ReceivedQty = r.ReceivedQty,
                TransitDays = transit,
                DaysInPort = inPort,
                Age = transit.HasValue && inPort.HasValue ? transit.Value + inPort.Value : null,
                YellowRipe = r.YellowRipe, GreenSoft = r.GreenSoft,
                RipePercent = r.ReceivedQty > 0 ? Math.Round(100m * ripe / r.ReceivedQty, 2) : null
            };
        }).ToList();
    }

    /// <summary>
    /// Days the container stood in port: vessel discharge to pull-out, counting
    /// both days, as the QC team's banana sheet does (discharged and pulled out
    /// the next day = 2). Null when either date is missing or they are reversed.
    /// </summary>
    public static int? BananaDaysInPort(DateTime? discharge, DateTime? pullOut)
    {
        if (!discharge.HasValue || !pullOut.HasValue) return null;
        var days = (pullOut.Value.Date - discharge.Value.Date).Days;
        return days < 0 ? null : days + 1;
    }

    private static bool PassesFilters(UnitRow u, DefectDrillRequest r) =>
        (r.Plant.Count == 0       || r.Plant.Contains(u.Plant))
        && (r.Supplier.Count == 0 || r.Supplier.Contains(u.Supplier))
        && (r.Major.Count == 0    || r.Major.Contains(u.Major))
        && (r.Sub.Count == 0      || r.Sub.Contains(u.Sub))
        && (r.Class.Count == 0    || r.Class.Contains(u.Class))
        && (r.PackWeight.Count == 0 || r.PackWeight.Contains(u.PackWeightLabel));

    /// <summary>The row a number sits on, per tab -- same keys the page builds.</summary>
    private static bool InRow(UnitRow u, DefectDrillRequest r)
    {
        if (r.RowKey == "__all") return true;
        return r.Tab switch
        {
            // Week rows may carry a sub-major prefix: "<sub><week>".
            "week"   => r.RowKey.Contains('')
                          ? $"{u.Sub}{WeekStart(u.ReceiveDate):yyyy-MM-dd}" == r.RowKey
                            || $"{u.Sub}__all" == r.RowKey
                          : WeekStart(u.ReceiveDate).ToString("yyyy-MM-dd") == r.RowKey,
            "class"  => r.RowKey.Contains('\u0002')
                          ? $"{u.Sub}\u0002{(string.IsNullOrEmpty(u.Class) ? "(no class)" : u.Class)}" == r.RowKey
                          : u.Sub == r.RowKey,
            // Sub-major first, its branches underneath.
            "branch" => r.RowKey.Contains('\u0002') ? $"{u.Sub}\u0002{u.Plant}" == r.RowKey : u.Sub == r.RowKey,
            // Supplier comparison: "<sub-major><supplier or __all>", or a supplier alone.
            "supplier" => r.RowKey.Contains('\u0003')
                          ? $"{u.Sub}\u0003{u.Supplier}" == r.RowKey || $"{u.Sub}\u0003__all" == r.RowKey
                          : u.Supplier == r.RowKey,
            "sub"    => r.RowKey.Contains('\u0002')
                          ? $"{SubLabel(u)}\u0002{(string.IsNullOrEmpty(u.Variety) ? "(no variety)" : u.Variety)}" == r.RowKey
                          : SubLabel(u) == r.RowKey,
            _        => (string.IsNullOrEmpty(u.Major) ? "(none)" : u.Major) == r.RowKey
        };
    }

    private static string SubLabel(UnitRow u) => string.IsNullOrEmpty(u.Sub) ? "(none)" : u.Sub;

    /// <summary>ISO 8601 weeks: Monday to Sunday, numbered by ISOWeek.</summary>
    public static DateTime WeekStart(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));
}

public class DefectReportDataset
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public List<string> Plants { get; set; } = new();
    public List<string> Suppliers { get; set; } = new();
    public List<string> Majors { get; set; } = new();
    public List<string> Subs { get; set; } = new();
    public List<string> Classes { get; set; } = new();
    public List<string> Groups { get; set; } = new();
    /// <summary>Pack weight labels ("15 kg"), lightest first.</summary>
    public List<string> PackWeights { get; set; } = new();
    public List<string> Uoms { get; set; } = new();
    public List<string> Varieties { get; set; } = new();
    /// <summary>Plant code -> display name.</summary>
    public Dictionary<string, string> PlantNames { get; set; } = new();
    /// <summary>[qoId, qoNo, plant#, supplier#, major#, sub#, class#, receive yyyy-MM-dd, container, group#,
    /// samples, inspected units, pack weight#, cartons (PO qty), uom#, variety#]</summary>
    public List<object[]> Units { get; set; } = new();
    /// <summary>[defectId, name, category, material group, sort order]</summary>
    public List<object[]> Defects { get; set; } = new();
    /// <summary>Material group -> indexes into <see cref="Defects"/> recorded for it in the window.</summary>
    public Dictionary<string, List<int>> GroupDefects { get; set; } = new();
    /// <summary>[unit#, defect#, defective units, sum of the samples' Defect %]</summary>
    public List<object[]> Facts { get; set; } = new();
    public List<DefectReportCategory> Categories { get; set; } = new();
    public string Generated { get; set; } = "";
}

public record DefectReportCategory(string Nm, int O, string? Col);

public class DefectDrillRequest
{
    public DateTime From { get; set; }
    public DateTime To { get; set; }
    // Every filter takes several values; empty means no filter.
    public List<string> Plant { get; set; } = new();
    public List<string> Supplier { get; set; } = new();
    public List<string> Major { get; set; } = new();
    public List<string> Sub { get; set; } = new();
    public List<string> Class { get; set; } = new();
    public List<string> PackWeight { get; set; } = new();
    public List<string> Cat { get; set; } = new();
    public string Tab { get; set; } = "major";
    public string RowKey { get; set; } = "__all";
    public string DefectKey { get; set; } = "T";
    public string Metric { get; set; } = "qc";
}

/// <summary>One banana container on the Banana ripeness tab.</summary>
public class BananaRipeRow
{
    public long ArrivalId { get; set; }
    public string ArrivalNo { get; set; } = "";
    public long? QualityOrderId { get; set; }
    public string? QualityOrderNo { get; set; }
    /// <summary>Branch receive date, yyyy-MM-dd.</summary>
    public string Received { get; set; } = "";
    public string Vessel { get; set; } = "";
    public string ContainerNo { get; set; } = "";
    public string BolNo { get; set; } = "";
    public string Po { get; set; } = "";
    public string Plant { get; set; } = "";
    public string Origin { get; set; } = "";
    public string Supplier { get; set; } = "";
    /// <summary>Carton weight, "13.5 kg".</summary>
    public string Weight { get; set; } = "";
    /// <summary>Quantity of the banana lines (cartons).</summary>
    public decimal ReceivedQty { get; set; }
    public int? TransitDays { get; set; }
    public int? DaysInPort { get; set; }
    /// <summary>Transit days + days in port.</summary>
    public int? Age { get; set; }
    public decimal? YellowRipe { get; set; }
    public decimal? GreenSoft { get; set; }
    /// <summary>(Yellow ripe + green soft) ÷ received quantity × 100.</summary>
    public decimal? RipePercent { get; set; }
}

public class DefectDrillRow
{
    public long QualityOrderId { get; set; }
    public string QualityOrderNo { get; set; } = "";
    public DateTime ReceiveDate { get; set; }
    public string Plant { get; set; } = "";
    public string Supplier { get; set; } = "";
    public string ContainerNo { get; set; } = "";
    public List<string> Materials { get; } = new();
    public int Samples { get; set; }
    public decimal Cartons { get; set; }
    public decimal Inspected { get; set; }
    public decimal Defective { get; set; }
    public decimal PctSum { get; set; }
    public int SampleDefectRows { get; set; }
    public decimal? Percent { get; set; }
}
