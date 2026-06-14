using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;

namespace SharbatlyQMS.Web.Services.Sap;

/// <summary>
/// Production-ready <see cref="ISapClient"/> that calls the configured
/// Container Search URL when set, and falls back to the in-memory
/// <see cref="StubSapClient"/> when the URL is blank (demo / development
/// mode). The OData v4 CDS view <c>ZQC_Data</c> exposes one row per
/// PO line × container × BOL with all the shipment/material/vendor
/// fields the Arrival Search grid needs; column names are mapped 1:1
/// in <see cref="MapRow"/>.
///
/// Filters from <see cref="SapSearchQuery"/> are translated to OData
/// <c>$filter</c> clauses combined with <c>and</c>:
///   * Container -> Container eq '...'
///   * BOL       -> BOL eq '...'
///   * PO        -> PO_Number eq '...'
///   * Material  -> Material eq '...'
/// The same ABAP query the user pasted in chat uses exact-equality with
/// "OR @field IS INITIAL" -- we get the same semantics by only emitting
/// the filter clause when the value is non-empty.
/// </summary>
public class HybridSapClient : ISapClient
{
    private readonly ISettingsService _settings;
    private readonly ISapODataClient _odata;
    private readonly StubSapClient _stub;
    private readonly string _cs;
    private readonly ILogger<HybridSapClient> _log;

    public HybridSapClient(ISettingsService settings, ISapODataClient odata,
        StubSapClient stub, IConfiguration config, ILogger<HybridSapClient> log)
    {
        _settings = settings; _odata = odata; _stub = stub; _log = log;
        _cs = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
    }

    public async Task<SapHealth> GetHealthAsync(CancellationToken ct = default)
    {
        var sap = await _settings.GetSapConfigAsync();
        if (string.IsNullOrWhiteSpace(sap.ContainerSearchUrl))
            return await _stub.GetHealthAsync(ct);

        var (user, password) = sap.ResolveCredentials("ContainerSearch");
        var (ok, msg) = await _odata.TestEndpointAsync(sap.ContainerSearchUrl, user, password, ct);
        return new SapHealth { IsReachable = ok, Source = "odata", Message = msg };
    }

    public async Task<int> FetchSinceAsync(
        DateOnly sinceDocDate,
        Func<IReadOnlyList<SapShipmentRow>, CancellationToken, Task> onPage,
        CancellationToken ct = default)
    {
        var sap = await _settings.GetSapConfigAsync();
        if (string.IsNullOrWhiteSpace(sap.ContainerSearchUrl))
            return await _stub.FetchSinceAsync(sinceDocDate, onPage, ct);

        // OData $filter: bulk fetch only PO lines that actually have a
        // container number. The CDS view LEFT JOINs ekes (purchase order
        // confirmations), so a PO header with no EKES record produces a
        // row with Container = '' -- those carry no QC value and would
        // flood the cache. Doc_Date filters the start cutoff (v4 form for
        // Edm.Date; v2 `datetime'...'` is rejected by SRVD_A2X with
        // /IWCOR/CX_OD_EXPR_PARSER_ERROR).
        var filter = $"Doc_Date ge {sinceDocDate:yyyy-MM-dd} and Container ne ''";
        var url = sap.ContainerSearchUrl + (sap.ContainerSearchUrl.Contains('?') ? "&" : "?")
                  + "$filter=" + Uri.EscapeDataString(filter);

        var (user, password) = sap.ResolveCredentials("ContainerSearch");
        int total = 0;
        var (ok, _, message) = await _odata.FetchAllAsync(url, pageSize: 200, async batch =>
        {
            var rows = batch.Select(MapRow).ToList();
            await EnrichWithMaterialMasterAsync(rows, ct);
            total += rows.Count;
            if (rows.Count > 0) await onPage(rows, ct);
        }, user, password, ct);

        if (!ok)
            throw new InvalidOperationException("SAP container fetch-since failed: " + message);

        _log.LogInformation("SAP container fetch-since(>= {Date}) returned {Count} row(s)", sinceDocDate, total);
        return total;
    }

    public async Task<IReadOnlyList<SapShipmentRow>> SearchAsync(SapSearchQuery q, CancellationToken ct = default)
    {
        var sap = await _settings.GetSapConfigAsync();
        if (string.IsNullOrWhiteSpace(sap.ContainerSearchUrl))
            return await _stub.SearchAsync(q, ct);

        // Build OData v4 $filter clause. Empty values mean "no filter on that
        // axis" (matches the ABAP "( field = @value OR @value IS INITIAL )" idiom).
        // Inputs are whitelisted to SAP-natural-key shapes (alphanumeric, '-',
        // '_', up to 35 chars) BEFORE interpolation; values outside that shape
        // are rejected so they cannot extend the filter. Esc() still doubles
        // single quotes so a legal value containing one is harmless.
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(q.ContainerNo) && IsSafeKey(q.ContainerNo))
            filters.Add($"Container eq '{Esc(q.ContainerNo)}'");
        if (!string.IsNullOrWhiteSpace(q.BolNo) && IsSafeKey(q.BolNo))
            filters.Add($"BOL eq '{Esc(q.BolNo)}'");
        if (!string.IsNullOrWhiteSpace(q.Ebeln) && IsSafeKey(q.Ebeln))
            filters.Add($"PO_Number eq '{Esc(q.Ebeln)}'");
        if (!string.IsNullOrWhiteSpace(q.MaterialNo) && IsSafeKey(q.MaterialNo))
            filters.Add($"Material eq '{Esc(q.MaterialNo)}'");

        var url = sap.ContainerSearchUrl;
        if (filters.Count > 0)
        {
            var filterClause = string.Join(" and ", filters);
            url += (url.Contains('?') ? "&" : "?") + "$filter=" + Uri.EscapeDataString(filterClause);
        }

        var (user, password) = sap.ResolveCredentials("ContainerSearch");
        var rows = new List<SapShipmentRow>();
        var (ok, _, message) = await _odata.FetchAllAsync(url, pageSize: 200, batch =>
        {
            foreach (var d in batch) rows.Add(MapRow(d));
            return Task.CompletedTask;
        }, user, password, ct);

        if (!ok)
            throw new InvalidOperationException("SAP container search failed: " + message);

        // Enrich with material-master fields that aren't in the ZQC_Data CDS view
        // (Origin, Variety, Class, NetWeight, MaterialSize, Brand, PackType,
        // MajorCategory, MaterialGroupDesc) by joining qms_sap_material_cache
        // in-memory. The cache is populated by the Material Master sync.
        await EnrichWithMaterialMasterAsync(rows, ct);

        _log.LogInformation("SAP container search returned {Count} row(s) for query {@Query}", rows.Count, q);
        return rows;
    }

    private async Task EnrichWithMaterialMasterAsync(List<SapShipmentRow> rows, CancellationToken ct)
    {
        var matnrs = rows.Select(r => r.MaterialNo)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (matnrs.Length == 0) return;

        // V07: read straight from the typed columns. No JSON parsing.
        Dictionary<string, MaraRow> cached;
        try
        {
            using var c = new SqlConnection(_cs);
            var rs = await c.QueryAsync<MaraRow>(@"
                SELECT  material_no           AS MaterialNo,
                        origin_name           AS Origin,
                        variety_name          AS Variety,
                        class_name            AS MaterialClass,
                        material_group_desc   AS MaterialGroupDesc,
                        COALESCE(NULLIF(major_category_desc,''), NULLIF(major_category,'')) AS MajorCategory,
                        COALESCE(NULLIF(size_name,''), NULLIF(material_weight_name,'')) AS MaterialSize,
                        weight                AS NetWeight
                FROM    qms_sap_material_cache
                WHERE   material_no IN @matnrs",
                new { matnrs });
            cached = rs.ToDictionary(r => r.MaterialNo, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Material-master enrichment skipped: cache query failed");
            return;
        }

        if (cached.Count == 0)
        {
            _log.LogInformation("No matching rows in qms_sap_material_cache for {Count} material(s). " +
                "Run Material Master Sync to populate.", matnrs.Length);
            return;
        }

        int enriched = 0;
        foreach (var r in rows)
        {
            if (!cached.TryGetValue(r.MaterialNo, out var m)) continue;
            if (string.IsNullOrWhiteSpace(r.Origin)        && !string.IsNullOrWhiteSpace(m.Origin))        r.Origin        = m.Origin!;
            if (string.IsNullOrWhiteSpace(r.Variety)       && !string.IsNullOrWhiteSpace(m.Variety))       r.Variety       = m.Variety!;
            if (string.IsNullOrWhiteSpace(r.MaterialClass) && !string.IsNullOrWhiteSpace(m.MaterialClass)) r.MaterialClass = m.MaterialClass!;
            if (string.IsNullOrWhiteSpace(r.MajorCategory) && !string.IsNullOrWhiteSpace(m.MajorCategory)) r.MajorCategory = m.MajorCategory!;
            if ((string.IsNullOrWhiteSpace(r.MaterialGroupDesc) ||
                 string.Equals(r.MaterialGroupDesc, r.MaterialGroup, StringComparison.Ordinal)) &&
                !string.IsNullOrWhiteSpace(m.MaterialGroupDesc))
                r.MaterialGroupDesc = m.MaterialGroupDesc!;
            if (string.IsNullOrWhiteSpace(r.MaterialSize) && !string.IsNullOrWhiteSpace(m.MaterialSize))   r.MaterialSize  = m.MaterialSize!;
            if (r.NetWeight == 0m && m.NetWeight.HasValue) r.NetWeight = m.NetWeight.Value;
            enriched++;
        }
        _log.LogInformation("Material-master enrichment hit cache for {Hit}/{Total} row(s)", enriched, rows.Count);
    }

    private sealed class MaraRow
    {
        public string MaterialNo        { get; set; } = "";
        public string? Origin           { get; set; }
        public string? Variety          { get; set; }
        public string? MaterialClass    { get; set; }
        public string? MaterialGroupDesc{ get; set; }
        public string? MajorCategory    { get; set; }
        public string? MaterialSize     { get; set; }
        public decimal? NetWeight       { get; set; }
    }

    // ---- field mapping ----
    // Source: ZQC_Data CDS view (see user's chat). Property names match the
    // CDS column aliases exactly. Material-level details (Origin/Variety/
    // Class/Brand/PackType/NetWeight/MaterialSize/MajorCategory) are NOT in
    // this view; they should be enriched from qms_sap_material_cache after
    // material master sync runs.
    private static SapShipmentRow MapRow(IReadOnlyDictionary<string, string?> d) => new()
    {
        ContainerNo       = Get(d, "Container") ?? "",
        BolNo             = Get(d, "BOL") ?? "",
        Ebeln             = Get(d, "PO_Number") ?? "",
        Ebelp             = Get(d, "Line_No") ?? "",
        PoType            = Get(d, "PO_Type") ?? "",
        DocDate           = ParseDate(Get(d, "Doc_Date")),
        Bukrs             = Get(d, "Company") ?? "",
        VendorNo          = Get(d, "Supplier") ?? "",
        VendorName        = Get(d, "Supplier_Name") ?? "",
        Carrier           = Get(d, "Carrier") ?? "",
        MaterialNo        = Get(d, "Material") ?? "",
        MaterialDesc      = Get(d, "Material_Name") ?? "",
        MaterialGroup     = Get(d, "Material_Group") ?? "",
        MaterialGroupDesc = Get(d, "Material_Group") ?? "",
        Plant             = Get(d, "Plant") ?? "",
        StorageLocation   = Get(d, "StorageLocation") ?? "",
        BatchNo           = Get(d, "Batch"),
        Quantity          = ParseDecimal(Get(d, "Qty")) ?? 0m,
        Uom               = Get(d, "OrderUnit") ?? "",
        LoadingDate       = ParseDate(Get(d, "LoadingDate")),
        SailingDate       = ParseDate(Get(d, "Sailing_Date")),
        ExaminationDate   = ParseDate(Get(d, "Examination_Date")),
        ArrivalDate       = ParseDate(Get(d, "Arrival_Date")),
        ReceiveDate       = ParseDate(Get(d, "Receive_Date")),
        TransitDays       = ParseShort(Get(d, "Transit_Days")),
        LoadingPort       = Get(d, "Loading_Port") ?? "",
        LoadingCountry    = Get(d, "Loading_Country") ?? "",
        ArrivalPlace      = Get(d, "Arrival_Place") ?? "",
        VesselName        = Get(d, "Vessel_Name") ?? "",
        VoyageNumber      = Get(d, "Voyage_Number") ?? "",
        SealNo            = Get(d, "Seal_Number") ?? ""
    };

    private static string? Get(IReadOnlyDictionary<string, string?> d, string key)
        => d.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    private static decimal? ParseDecimal(string? s)
        => decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static short? ParseShort(string? s)
        => short.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static DateOnly? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        // SAP date strings come back as "/Date(1234567890000)/" (v2) or
        // "2026-05-06" (v4 ISO date). Try both.
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            return DateOnly.FromDateTime(dt);
        if (s.StartsWith("/Date(", StringComparison.Ordinal))
        {
            var inner = s.Substring(6, s.IndexOf(')') - 6);
            if (long.TryParse(inner.Split('+', '-')[0], out var ms))
                return DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime);
        }
        return null;
    }

    // OData literal-string escape: single quotes are doubled.
    private static string Esc(string s) => s.Replace("'", "''");

    /// <summary>
    /// SAP natural-key shape guard. Container / BOL / PO / Material are short
    /// codes drawn from [A-Z0-9_-]; values outside that shape are silently
    /// dropped from the filter so a free-text query cannot extend the OData
    /// $filter expression. 35 is the largest SAP natural-key field width we
    /// hit in ZQC_Data.
    /// </summary>
    private static bool IsSafeKey(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length > 35) return false;
        foreach (var ch in s)
        {
            if (!(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_'))
                return false;
        }
        return true;
    }
}
