using Dapper;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services.Sap;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Brings an order's material codes in line with SAP when SAP changes them
/// after the inspection -- in practice a regrade (Extra Fancy to Fancy), which
/// SAP books as a different material number. The QC report reads the grade
/// from the material code, so until the codes move the report prints the old
/// grade.
///
/// Two sources, tried in order:
///   * SAP: the container's current lines, each matched to the order line it
///     replaces. Used whenever SAP lists the container at all.
///   * By hand: only when SAP answered and no longer lists the container
///     (taken off its PO while the receipt is re-posted). The admin picks a
///     new grade per line from the material master.
///
/// Either way a new code must be a SIBLING of the old one -- same material
/// group, variety, origin, size, carton weight and unit -- so the samples and
/// defects recorded against the line stay true. That one rule is what keeps
/// this from becoming a way to change what was inspected.
/// </summary>
public interface IMaterialRefreshService
{
    Task<MaterialRefreshPreview> PreviewAsync(long qoId, CancellationToken ct = default);

    /// <summary>Re-runs the preview and applies it. <paramref name="picks"/>
    /// (qo_material_id -> new material number) is what the admin confirmed;
    /// in SAP mode it must equal what SAP says now, in manual mode each pick
    /// must be one of the offered grades. Throws InvalidOperationException
    /// with a user-facing message when anything does not hold.</summary>
    Task<int> ApplyAsync(long qoId, IReadOnlyDictionary<long, string> picks, string reason, string user,
                         CancellationToken ct = default);
}

public sealed class MaterialRefreshPreview
{
    /// <summary>"sap", "manual", or "none" (nothing can be done; see Message).</summary>
    public string Mode { get; set; } = "none";
    public string? Message { get; set; }
    public bool ReportSent { get; set; }
    public string? ContainerNo { get; set; }
    public List<MaterialRefreshLine> Lines { get; set; } = new();
}

public sealed class MaterialRefreshLine
{
    public long QoMaterialId { get; set; }
    public string? Ebelp { get; set; }
    public decimal? Quantity { get; set; }
    public string OldNo { get; set; } = "";
    public string? OldDesc { get; set; }
    public string? OldGrade { get; set; }
    /// <summary>SAP mode: SAP's code for this line (null = unchanged).</summary>
    public string? NewNo { get; set; }
    public string? NewDesc { get; set; }
    public string? NewGrade { get; set; }
    /// <summary>Manual mode: the grades this line may move to.</summary>
    public List<MaterialOption> Options { get; set; } = new();
}

public sealed class MaterialOption
{
    public string No { get; set; } = "";
    public string? Desc { get; set; }
    public string? Grade { get; set; }
    public string? Brand { get; set; }
}

/// <summary>The material-master fields that decide whether two codes are the
/// same goods in a different grade.</summary>
public sealed class MaterialKey
{
    public string MaterialNo { get; set; } = "";
    public string? MaterialDesc { get; set; }
    public string? ClassName { get; set; }
    public string? Brand { get; set; }
    public string? MaterialGroup { get; set; }
    public string? VarietyId { get; set; }
    public string? OriginId { get; set; }
    public string? SizeId { get; set; }
    public decimal? Weight { get; set; }
    public string? BaseUnit { get; set; }
    public string? MaterialType { get; set; }

    public static bool Siblings(MaterialKey a, MaterialKey b) =>
        Same(a.MaterialGroup, b.MaterialGroup) && Same(a.VarietyId, b.VarietyId)
        && Same(a.OriginId, b.OriginId) && Same(a.SizeId, b.SizeId)
        && a.Weight == b.Weight && Same(a.BaseUnit, b.BaseUnit)
        && !string.IsNullOrWhiteSpace(a.MaterialGroup);

    private static bool Same(string? x, string? y) =>
        string.Equals((x ?? "").Trim(), (y ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
}

public sealed class MaterialRefreshService : IMaterialRefreshService
{
    private readonly string _cs;
    private readonly ISapClient _sap;
    private readonly IAuditService _audit;
    private readonly ILogger<MaterialRefreshService> _log;

    public MaterialRefreshService(IConfiguration config, ISapClient sap, IAuditService audit,
                                  ILogger<MaterialRefreshService> log)
    {
        _cs = config.GetConnectionString("Default")
              ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
        _sap = sap; _audit = audit; _log = log;
    }

    private const string KeySelect = @"
        SELECT material_no MaterialNo, material_desc MaterialDesc, class_name ClassName, brand Brand,
               material_group MaterialGroup, variety_id VarietyId, origin_id OriginId, size_id SizeId,
               weight Weight, base_unit BaseUnit, material_type MaterialType
        FROM   qms_sap_material_cache";

    private sealed class OrderRow
    {
        public string StatusCode { get; set; } = "";
        public DateTime? SupersededAt { get; set; }
        public DateTime? ArchivedAt { get; set; }
        public string? ContainerNo { get; set; }
        public bool ReportSent { get; set; }
    }

    internal sealed class LineRow
    {
        public long QoMaterialId { get; set; }
        public long? ArrivalItemId { get; set; }
        public string? Ebelp { get; set; }
        public decimal? Quantity { get; set; }
        public string MaterialNo { get; set; } = "";
        public string? MaterialDesc { get; set; }
    }

    public async Task<MaterialRefreshPreview> PreviewAsync(long qoId, CancellationToken ct = default)
    {
        using var c = new SqlConnection(_cs);
        var order = await c.QuerySingleOrDefaultAsync<OrderRow>(@"
            SELECT q.status_code StatusCode, q.superseded_at SupersededAt, q.archived_at ArchivedAt,
                   a.container_no ContainerNo,
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM qms_qo_report_send s WHERE s.quality_order_id = q.quality_order_id)
                             THEN 1 ELSE 0 END AS BIT) ReportSent
            FROM   qms_quality_order q JOIN qms_arrival a ON a.arrival_id = q.arrival_id
            WHERE  q.quality_order_id = @qoId", new { qoId });
        var p = new MaterialRefreshPreview();
        if (order == null) { p.Message = "Quality order not found."; return p; }
        p.ReportSent = order.ReportSent;
        p.ContainerNo = order.ContainerNo;
        if (Blocked(order) is { } why) { p.Message = why; return p; }

        var lines = (await c.QueryAsync<LineRow>(@"
            SELECT m.qo_material_id QoMaterialId, m.arrival_item_id ArrivalItemId, ai.ebelp Ebelp,
                   ai.quantity Quantity, m.material_no MaterialNo, m.material_desc MaterialDesc
            FROM   qms_quality_order_material m
            LEFT JOIN qms_arrival_item ai ON ai.arrival_item_id = m.arrival_item_id
            WHERE  m.quality_order_id = @qoId
            ORDER BY ai.ebelp, m.qo_material_id", new { qoId })).ToList();
        if (lines.Count == 0) { p.Message = "This order has no material lines."; return p; }

        var oldKeys = await KeysAsync(c, lines.Select(l => l.MaterialNo));
        foreach (var l in lines)
        {
            oldKeys.TryGetValue(l.MaterialNo, out var k);
            p.Lines.Add(new MaterialRefreshLine
            {
                QoMaterialId = l.QoMaterialId, Ebelp = l.Ebelp, Quantity = l.Quantity,
                OldNo = l.MaterialNo, OldDesc = k?.MaterialDesc ?? l.MaterialDesc, OldGrade = k?.ClassName
            });
        }
        if (lines.Any(l => !oldKeys.ContainsKey(l.MaterialNo)))
        {
            p.Message = "A material on this order is missing from the SAP material list, so its grade cannot be compared.";
            return p;
        }

        // ---- SAP first. An unreachable SAP is NOT a reason to fall back to
        //      picking by hand: that would let an outage widen what the
        //      function allows.
        IReadOnlyList<SapShipmentRow> sapRows;
        try
        {
            sapRows = string.IsNullOrWhiteSpace(order.ContainerNo)
                ? Array.Empty<SapShipmentRow>()
                : (await _sap.SearchAsync(new SapSearchQuery { ContainerNo = order.ContainerNo.Trim() }, ct))
                      .Where(r => string.Equals(r.ContainerNo?.Trim(), order.ContainerNo.Trim(), StringComparison.OrdinalIgnoreCase))
                      .ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Material refresh: SAP search failed for QO {QoId}", qoId);
            p.Message = "SAP could not be reached. Try again in a few minutes.";
            return p;
        }

        if (sapRows.Count > 0)
        {
            var sapKeys = await KeysAsync(c, sapRows.Select(r => r.MaterialNo));
            var error = MatchSap(p.Lines, lines, oldKeys, sapRows, sapKeys);
            if (error != null) { p.Message = error; p.Lines.ForEach(x => { x.NewNo = x.NewDesc = x.NewGrade = null; }); return p; }
            p.Mode = "sap";
            if (p.Lines.All(x => x.NewNo == null))
            {
                p.Mode = "none";
                p.Message = "The materials on this order already match SAP. Nothing to refresh.";
            }
            return p;
        }

        // ---- SAP answered and no longer lists the container: offer the
        //      grades the material master has for exactly these goods.
        var candidates = await SiblingsAsync(c, oldKeys.Values);
        foreach (var line in p.Lines)
        {
            var old = oldKeys[line.OldNo];
            line.Options = candidates
                .Where(k => MaterialKey.Siblings(old, k)
                            && !string.IsNullOrWhiteSpace(k.ClassName)
                            && !string.Equals(k.ClassName, old.ClassName, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(k.MaterialType, old.MaterialType, StringComparison.OrdinalIgnoreCase))
                .OrderBy(k => k.ClassName).ThenBy(k => k.MaterialNo)
                .Select(k => new MaterialOption { No = k.MaterialNo, Desc = k.MaterialDesc, Grade = k.ClassName, Brand = k.Brand })
                .ToList();
        }
        if (p.Lines.All(x => x.Options.Count == 0))
        {
            p.Message = $"SAP does not list container {order.ContainerNo} at the moment, and the material list has no other grade of these goods.";
            return p;
        }
        p.Mode = "manual";
        p.Message = $"SAP does not list container {order.ContainerNo} at the moment (it may be off its PO while the receipt is re-posted). " +
                    "Choose the new grade for each line; only grades of exactly the same goods are offered.";
        return p;
    }

    /// <summary>
    /// Pairs every order line with the SAP line that now stands for it: the
    /// SAP line whose material is a sibling of the line's current material,
    /// tie-broken on the PO item. Refuses anything it cannot pair one-to-one,
    /// and any change of quantity -- a quantity change is a receiving
    /// correction, not a regrade. Returns an error message, or null and fills
    /// NewNo/NewDesc/NewGrade on the lines that change.
    /// </summary>
    internal static string? MatchSap(List<MaterialRefreshLine> preview, IReadOnlyList<LineRow> lines,
        IReadOnlyDictionary<string, MaterialKey> oldKeys, IReadOnlyList<SapShipmentRow> sapRows,
        IReadOnlyDictionary<string, MaterialKey> sapKeys)
    {
        if (sapRows.Count != lines.Count)
            return $"SAP lists {sapRows.Count} line(s) for this container and the order has {lines.Count}. " +
                   "Only a change of grade can be refreshed; a change of lines needs a new inspection.";
        var missing = sapRows.FirstOrDefault(r => !sapKeys.ContainsKey(r.MaterialNo));
        if (missing != null)
            return $"SAP's material {missing.MaterialNo} is not in the SAP material list yet. Run Material Master sync and try again.";

        var used = new HashSet<SapShipmentRow>();
        foreach (var line in lines)
        {
            var old = oldKeys[line.MaterialNo];
            var fits = sapRows.Where(r => !used.Contains(r) && MaterialKey.Siblings(old, sapKeys[r.MaterialNo])).ToList();
            if (fits.Count > 1)
                fits = fits.Where(r => NormItem(r.Ebelp) == NormItem(line.Ebelp)).ToList();
            if (fits.Count != 1)
                return $"Line {line.MaterialNo} has no single matching line in SAP of the same goods " +
                       "(same fruit, variety, origin, size and weight). Only a change of grade can be refreshed.";
            var sap = fits[0];
            used.Add(sap);
            if (line.Quantity.HasValue && sap.Quantity != line.Quantity.Value)
                return $"SAP's quantity for {sap.MaterialNo} is {sap.Quantity:0.###} and the order has {line.Quantity:0.###}. " +
                       "Quantity changes are not refreshed here.";
            if (string.Equals(sap.MaterialNo, line.MaterialNo, StringComparison.OrdinalIgnoreCase)) continue;
            var k = sapKeys[sap.MaterialNo];
            var pl = preview.First(x => x.QoMaterialId == line.QoMaterialId);
            pl.NewNo = k.MaterialNo; pl.NewDesc = k.MaterialDesc; pl.NewGrade = k.ClassName;
        }
        return null;
    }

    private static string NormItem(string? s) => (s ?? "").Trim().TrimStart('0');

    private static string? Blocked(OrderRow o) =>
        o.SupersededAt.HasValue ? "This order was superseded by a reinspection; refresh the reinspection instead."
        : o.ArchivedAt.HasValue ? "Archived orders are kept as a historical record and cannot be changed."
        : o.StatusCode == QualityOrderStatus.Cancelled ? "Cancelled orders cannot be changed."
        : null;

    private static async Task<Dictionary<string, MaterialKey>> KeysAsync(SqlConnection c, IEnumerable<string> nos)
    {
        var keys = nos.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (keys.Length == 0) return new(StringComparer.OrdinalIgnoreCase);
        var rows = await c.QueryAsync<MaterialKey>(KeySelect + " WHERE material_no IN @keys", new { keys });
        return rows.GroupBy(r => r.MaterialNo, StringComparer.OrdinalIgnoreCase)
                   .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<List<MaterialKey>> SiblingsAsync(SqlConnection c, IEnumerable<MaterialKey> olds)
    {
        var result = new List<MaterialKey>();
        foreach (var g in olds.GroupBy(o => (o.MaterialGroup, o.VarietyId, o.OriginId)))
            result.AddRange(await c.QueryAsync<MaterialKey>(KeySelect + @"
                WHERE material_group = @g AND variety_id = @v AND origin_id = @o",
                new { g = g.Key.MaterialGroup, v = g.Key.VarietyId, o = g.Key.OriginId }));
        return result;
    }

    public async Task<int> ApplyAsync(long qoId, IReadOnlyDictionary<long, string> picks, string reason, string user,
                                      CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5)
            throw new InvalidOperationException("Say why the materials are being changed; the reason goes into the audit log.");
        reason = reason.Trim();

        // Recomputed, never trusted from the browser: the preview the admin saw
        // only has to AGREE with what is true now.
        var p = await PreviewAsync(qoId, ct);
        var changes = new List<(MaterialRefreshLine Line, string NewNo)>();
        if (p.Mode == "sap")
        {
            foreach (var l in p.Lines.Where(x => x.NewNo != null))
            {
                if (!picks.TryGetValue(l.QoMaterialId, out var pick) || !string.Equals(pick, l.NewNo, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("SAP changed since the preview was shown. Open the refresh again.");
                changes.Add((l, l.NewNo!));
            }
            if (picks.Count != changes.Count)
                throw new InvalidOperationException("SAP changed since the preview was shown. Open the refresh again.");
        }
        else if (p.Mode == "manual")
        {
            foreach (var (id, pick) in picks)
            {
                if (string.IsNullOrWhiteSpace(pick)) continue;
                var l = p.Lines.FirstOrDefault(x => x.QoMaterialId == id)
                        ?? throw new InvalidOperationException("That line is not on this order.");
                if (!l.Options.Any(o => string.Equals(o.No, pick, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"{pick} is not another grade of {l.OldNo}.");
                changes.Add((l, l.Options.First(o => string.Equals(o.No, pick, StringComparison.OrdinalIgnoreCase)).No));
            }
        }
        else throw new InvalidOperationException(p.Message ?? "Nothing to refresh.");
        if (changes.Count == 0) throw new InvalidOperationException("Choose a new grade for at least one line.");

        using var c = new SqlConnection(_cs);
        await c.OpenAsync(ct);
        using var tx = c.BeginTransaction();
        try
        {
            var state = await c.QuerySingleAsync<OrderRow>(@"
                SELECT q.status_code StatusCode, q.superseded_at SupersededAt, q.archived_at ArchivedAt
                FROM   qms_quality_order q WITH (UPDLOCK, HOLDLOCK) WHERE q.quality_order_id = @qoId", new { qoId }, tx);
            if (Blocked(state) is { } why) throw new InvalidOperationException(why);

            var newKeys = await KeysTxAsync(c, tx, changes.Select(x => x.NewNo));
            foreach (var (line, newNo) in changes)
            {
                var k = newKeys[newNo];
                var n = await c.ExecuteAsync(@"
                    UPDATE qms_quality_order_material
                    SET    material_no = @newNo, material_desc = @desc,
                           material_class = CASE WHEN NULLIF(material_class, N'') IS NULL THEN material_class ELSE @grade END
                    WHERE  qo_material_id = @id AND quality_order_id = @qoId AND material_no = @oldNo;
                    UPDATE ai SET ai.material_no = @newNo, ai.material_desc = @desc
                    FROM   qms_arrival_item ai JOIN qms_quality_order_material m ON m.arrival_item_id = ai.arrival_item_id
                    WHERE  m.qo_material_id = @id AND ai.material_no = @oldNo;",
                    new { newNo, desc = k.MaterialDesc, grade = k.ClassName, id = line.QoMaterialId, qoId, oldNo = line.OldNo }, tx);
                if (n == 0) throw new InvalidOperationException("The order changed while you were looking. Open the refresh again.");

                await _audit.WriteAsync(c, tx, EntityTypes.QualityOrderMaterial, line.QoMaterialId, ActionCodes.MaterialRefreshed,
                    oldValues: new { material_no = line.OldNo, grade = line.OldGrade },
                    newValues: new { material_no = newNo, grade = k.ClassName, source = p.Mode == "sap" ? "SAP" : "chosen by hand (SAP had no lines)", reason },
                    actor: user);
            }
            await _audit.WriteAsync(c, tx, EntityTypes.QualityOrder, qoId, ActionCodes.MaterialRefreshed,
                oldValues: new { materials = changes.Select(x => x.Line.OldNo).ToArray() },
                newValues: new { materials = changes.Select(x => x.NewNo).ToArray(),
                                 source = p.Mode == "sap" ? "SAP" : "chosen by hand (SAP had no lines)", reason },
                actor: user);
            tx.Commit();
            return changes.Count;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private static async Task<Dictionary<string, MaterialKey>> KeysTxAsync(SqlConnection c, SqlTransaction tx, IEnumerable<string> nos)
    {
        var keys = nos.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var rows = await c.QueryAsync<MaterialKey>(KeySelect + " WHERE material_no IN @keys", new { keys }, tx);
        return rows.GroupBy(r => r.MaterialNo, StringComparer.OrdinalIgnoreCase)
                   .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }
}
