using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace SharbatlyQMS.Web.Services.Sap;

/// <summary>
/// Endpoint identifiers for syncable SAP master data. PO / Shipment / Container
/// search endpoints are intentionally absent: those are search-only (live OData
/// at arrival creation time, snapshotted on save -- they never get bulk-synced).
/// </summary>
public static class SyncableEndpoints
{
    public const string MaterialMaster = "MaterialMaster";
    public const string VendorMaster   = "VendorMaster";

    public static readonly string[] All = { MaterialMaster, VendorMaster };
    public static bool IsValid(string? key) => key != null && Array.IndexOf(All, key) >= 0;
}

public interface ISapSyncService
{
    /// <summary>
    /// Runs the named master-data sync, persisting rows into the corresponding
    /// cache table and writing a qms_sap_sync_log entry. Returns success flag,
    /// row count, and message (used by the UI's "Sync now" button).
    /// </summary>
    Task<(bool ok, int rows, string message)> SyncAsync(string endpointKey,
        string triggeredBy, string source, CancellationToken ct = default);
}

public class SapSyncService : ISapSyncService
{
    private readonly ISapODataClient _client;
    private readonly ISettingsService _settings;
    private readonly string _cs;
    private readonly ILogger<SapSyncService> _log;

    public SapSyncService(ISapODataClient client, ISettingsService settings,
        IConfiguration config, ILogger<SapSyncService> log)
    {
        _client = client; _settings = settings; _log = log;
        _cs = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
    }

    public async Task<(bool ok, int rows, string message)> SyncAsync(string endpointKey,
        string triggeredBy, string source, CancellationToken ct = default)
    {
        if (!SyncableEndpoints.IsValid(endpointKey))
            return (false, 0, $"Endpoint '{endpointKey}' is not syncable.");

        var sap = await _settings.GetSapConfigAsync();
        var url = endpointKey switch
        {
            SyncableEndpoints.MaterialMaster => sap.MaterialMasterUrl,
            SyncableEndpoints.VendorMaster   => sap.VendorMasterUrl,
            _ => ""
        };
        if (string.IsNullOrWhiteSpace(url))
            return (false, 0, $"OData URL for {endpointKey} is empty. Configure it on the SAP tab first.");

        // Resolve per-endpoint credentials (falls back to global Sap.User if blank).
        var (user, password) = sap.ResolveCredentials(endpointKey);

        var startedAt = DateTime.UtcNow;
        var logId = await OpenSyncLogAsync(endpointKey, triggeredBy, source, startedAt);

        try
        {
            var (ok, rows, message) = endpointKey switch
            {
                SyncableEndpoints.MaterialMaster => await PullMaterialsAsync(url, user, password, ct),
                SyncableEndpoints.VendorMaster   => await PullVendorsAsync(url, user, password, ct),
                _ => (false, 0, "Unknown endpoint")
            };

            // Prune rows that pre-date this run -- they were imported by a previous
            // sync but SAP no longer returned them (or, more commonly here, they
            // were keyed wrong by an older version of the picker logic). MERGE
            // touches synced_at on every match/insert, so anything still < startedAt
            // is genuinely stale and safe to delete.
            if (ok && rows > 0)
            {
                var pruneTable = endpointKey switch
                {
                    SyncableEndpoints.MaterialMaster => "qms_sap_material_cache",
                    SyncableEndpoints.VendorMaster   => "qms_sap_vendor_cache",
                    _ => null
                };
                if (pruneTable != null)
                {
                    using var pc = new SqlConnection(_cs);
                    var pruned = await pc.ExecuteAsync(
                        $"DELETE FROM {pruneTable} WHERE synced_at < @startedAt",
                        new { startedAt }, commandTimeout: 120);
                    if (pruned > 0) message = $"{message} Pruned {pruned} stale row(s).";
                }
            }

            await CloseSyncLogAsync(logId, ok, rows, message);
            await UpdateLastRunAsync(endpointKey, ok, rows, message);
            return (ok, rows, message);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Sync failed for {Endpoint}", endpointKey);
            await CloseSyncLogAsync(logId, false, 0, ex.Message);
            await UpdateLastRunAsync(endpointKey, false, 0, ex.Message);
            return (false, 0, ex.Message);
        }
    }

    // ---- Material Master --------------------------------------------------
    // Field priority matches ProductionControl.Services.SapODataService -- the
    // shared SAP CDS view (ZSHR_MARA_CDS) exposes the code as MATERAIL (typo
    // intentional) so we MUST pick that first.
    //
    // Throughput: each batch is staged via SqlBulkCopy (single round trip for
    // 1000 rows) and then merged with one MERGE statement that joins the
    // staging table. This replaces the previous per-row MERGE pattern which
    // was sending ~28,000 individual SQL statements over the wire and was
    // the dominant cost of a full master refresh.
    //
    // V07 (May 2026): each MARA field is now persisted into its own typed
    // column on qms_sap_material_cache so readers don't have to parse the
    // JSON blob.
    // V08 (May 2026): payload_json and source_id columns dropped; this
    // writer no longer touches them.
    private async Task<(bool ok, int rows, string message)> PullMaterialsAsync(
        string url, string user, string password, CancellationToken ct)
    {
        return await _client.FetchAllAsync(url, 1000, async batch =>
        {
            var deduped = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in batch)
            {
                var matnr = SapODataClient.Pick(row,
                    "MATERAIL", "Matnr", "MATNR", "MaterialNo");
                if (string.IsNullOrWhiteSpace(matnr)) continue;
                matnr = matnr.Trim();
                if (matnr.Length > 40) matnr = matnr.Substring(0, 40);
                deduped[matnr] = row;
            }
            if (deduped.Count == 0) return;

            await BulkMergeMaterialsAsync(deduped, ct);
        }, user, password, ct);
    }

    // ---- Vendor Master ----------------------------------------------------
    private async Task<(bool ok, int rows, string message)> PullVendorsAsync(
        string url, string user, string password, CancellationToken ct)
    {
        return await _client.FetchAllAsync(url, 1000, async batch =>
        {
            var deduped = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in batch)
            {
                var lifnr = SapODataClient.Pick(row, "Lifnr", "LIFNR", "VENDOR", "VendorNo", "Vendor");
                if (string.IsNullOrWhiteSpace(lifnr)) continue;
                lifnr = lifnr.Trim();
                if (lifnr.Length > 20) lifnr = lifnr.Substring(0, 20);
                deduped[lifnr] = row;
            }
            if (deduped.Count == 0) return;

            await BulkMergeAsync(
                tableName:    "qms_sap_vendor_cache",
                keyColumn:    "vendor_no",
                keyLength:    20,
                rows:         deduped,
                ct:           ct);
        }, user, password, ct);
    }

    /// <summary>
    /// Stages the batch via SqlBulkCopy into a session-scoped temp table then
    /// MERGEs it into the cache in a single set-based statement. One round
    /// trip for the bulk insert, one for the merge -- regardless of batch
    /// size. Caller has already deduped on the natural key.
    ///
    /// Used by the vendor sync (and any future endpoint that wants a simple
    /// JSON-blob cache). The material sync uses
    /// <see cref="BulkMergeMaterialsAsync"/> instead so each MARA field
    /// lands in its own typed column.
    /// </summary>
    private async Task BulkMergeAsync(
        string tableName, string keyColumn, int keyLength,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> rows,
        CancellationToken ct)
    {
        var dt = new DataTable();
        dt.Columns.Add(keyColumn,      typeof(string));
        dt.Columns.Add("payload_json", typeof(string));
        foreach (var (key, row) in rows)
            dt.Rows.Add(key, JsonSerializer.Serialize(row));

        using var c = new SqlConnection(_cs);
        await c.OpenAsync(ct);
        using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);

        var stage = "#sync_stage";
        await c.ExecuteAsync($@"
            CREATE TABLE {stage} (
                {keyColumn}  VARCHAR({keyLength}) NOT NULL,
                payload_json NVARCHAR(MAX)        NOT NULL);", transaction: tx);

        using (var bulk = new SqlBulkCopy((SqlConnection)c, SqlBulkCopyOptions.Default, tx)
        {
            DestinationTableName = stage,
            BulkCopyTimeout      = 120,
            BatchSize            = 1000
        })
        {
            bulk.ColumnMappings.Add(keyColumn,      keyColumn);
            bulk.ColumnMappings.Add("payload_json", "payload_json");
            await bulk.WriteToServerAsync(dt, ct);
        }

        await c.ExecuteAsync($@"
            MERGE {tableName} AS t
            USING (SELECT {keyColumn}, payload_json FROM {stage}) AS s
              ON  t.{keyColumn} = s.{keyColumn}
            WHEN MATCHED THEN
                UPDATE SET payload_json = s.payload_json, synced_at = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT ({keyColumn}, payload_json) VALUES (s.{keyColumn}, s.payload_json);
            DROP TABLE {stage};", transaction: tx, commandTimeout: 120);

        tx.Commit();
    }

    /// <summary>
    /// Materials-specific bulk merge: each MARA field lands in its own typed
    /// column on qms_sap_material_cache (V07 schema; V08 removed the
    /// payload_json + source_id columns this writer used to populate).
    /// </summary>
    private async Task BulkMergeMaterialsAsync(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> rows,
        CancellationToken ct)
    {
        // Build a DataTable matching the staging-table layout below. Order
        // here MUST match the temp-table column order.
        var dt = new DataTable();
        dt.Columns.Add("material_no",            typeof(string));
        dt.Columns.Add("material_desc",          typeof(string));
        dt.Columns.Add("major_category",         typeof(string));
        dt.Columns.Add("major_category_desc",    typeof(string));
        dt.Columns.Add("sub_major_category",     typeof(string));
        dt.Columns.Add("overhead",               typeof(string));
        dt.Columns.Add("material_group",         typeof(string));
        dt.Columns.Add("material_group_desc",    typeof(string));
        dt.Columns.Add("origin_id",              typeof(string));
        dt.Columns.Add("origin_name",            typeof(string));
        dt.Columns.Add("procurement_type",       typeof(string));
        dt.Columns.Add("procurement_type_name",  typeof(string));
        dt.Columns.Add("variety_id",             typeof(string));
        dt.Columns.Add("variety_name",           typeof(string));
        dt.Columns.Add("class_id",               typeof(string));
        dt.Columns.Add("class_name",             typeof(string));
        dt.Columns.Add("size_id",                typeof(string));
        dt.Columns.Add("size_name",              typeof(string));
        dt.Columns.Add("material_weight",        typeof(string));
        dt.Columns.Add("material_weight_name",   typeof(string));
        dt.Columns.Add("old_material_code",      typeof(string));
        dt.Columns.Add("weight",                 typeof(decimal));
        dt.Columns.Add("weight_unit",            typeof(string));
        dt.Columns.Add("material_type",          typeof(string));
        dt.Columns.Add("base_unit",              typeof(string));
        dt.Columns.Add("base_unit_name",         typeof(string));
        dt.Columns.Add("sap_created_on",         typeof(DateTime));
        dt.Columns.Add("sap_created_by",         typeof(string));

        foreach (var (key, row) in rows)
        {
            // Pick uses case-insensitive name fallbacks; nulls / empty strings
            // become DBNull below so the typed columns stay NULL instead of
            // storing literal "".
            string? Get(params string[] keys) => SapODataClient.Pick(row, keys);
            decimal? weight = TryDecimal(Get("Weight", "WEIGHT", "Brgew", "BRGEW", "NetWeight"));
            DateTime? createdOn = TryEdmDate(Get("Created_ON", "CreatedOn", "CreatedAt"));

            dt.Rows.Add(
                key,
                NullIfEmpty(Get("Material_Desc", "MATERIAL_DESC", "MaterialDesc", "Maktx", "MAKTX")),
                NullIfEmpty(Get("Major_Category", "MAJOR_CATEGORY", "MajorCategory")),
                NullIfEmpty(Get("Major_Category_Desc", "MAJOR_CATEGORY_DESC", "MajorCategoryDesc")),
                NullIfEmpty(Get("SubMajor_Category", "SUB_MAJOR_CATEGORY", "SubMajorCategory")),
                NullIfEmpty(Get("OverHead", "Overhead")),
                NullIfEmpty(Get("Material_Group", "MATERIAL_GROUP", "MaterialGroup", "Matkl", "MATKL")),
                NullIfEmpty(Get("Material_Group_Desc", "MATERIAL_GROUP_DESC", "MaterialGroupDesc")),
                NullIfEmpty(Get("Origin_Id", "ORIGIN_ID", "OriginId")),
                NullIfEmpty(Get("Origin_Name", "ORIGIN_NAME", "Origin", "OriginName")),
                NullIfEmpty(Get("Procurement_type", "PROCUREMENT_TYPE", "ProcurementType")),
                NullIfEmpty(Get("Procurement_type_Name", "PROCUREMENT_TYPE_NAME", "ProcurementTypeName")),
                NullIfEmpty(Get("VarietyID", "VARIETY_ID", "VarietyId")),
                NullIfEmpty(Get("Variety_Name", "VARIETY_NAME", "Variety", "VarietyName")),
                NullIfEmpty(Get("ClassID", "CLASS_ID", "ClassId")),
                NullIfEmpty(Get("Class_Name", "CLASS_NAME", "Class", "ClassName")),
                NullIfEmpty(Get("SizeID", "SIZE_ID", "SizeId")),
                NullIfEmpty(Get("Size_Name", "SIZE_NAME", "SizeName", "Size")),
                NullIfEmpty(Get("Material_Weight", "MATERIAL_WEIGHT", "MaterialWeight")),
                NullIfEmpty(Get("Material_Weight_Name", "MATERIAL_WEIGHT_NAME", "MaterialWeightName")),
                NullIfEmpty(Get("OldMaterialCode", "OLD_MATERIAL_CODE", "OldMaterialCode")),
                (object?)weight ?? DBNull.Value,
                NullIfEmpty(Get("Weight_Unit", "WEIGHT_UNIT", "WeightUnit")),
                NullIfEmpty(Get("Material_Type", "MATERIAL_TYPE", "MaterialType", "Mtart", "MTART")),
                NullIfEmpty(Get("Base_Unit", "BASE_UNIT", "BaseUnit", "Meins", "MEINS")),
                NullIfEmpty(Get("Base_Unit_Name", "BASE_UNIT_NAME", "BaseUnitName")),
                (object?)createdOn ?? DBNull.Value,
                NullIfEmpty(Get("Created_By", "CREATED_BY", "CreatedBy")));
        }

        using var c = new SqlConnection(_cs);
        await c.OpenAsync(ct);
        using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);

        const string stage = "#mat_stage";
        await c.ExecuteAsync($@"
            CREATE TABLE {stage} (
                material_no            VARCHAR(40)   NOT NULL,
                material_desc          NVARCHAR(255) NULL,
                major_category         VARCHAR(40)   NULL,
                major_category_desc    NVARCHAR(120) NULL,
                sub_major_category     NVARCHAR(120) NULL,
                overhead               VARCHAR(40)   NULL,
                material_group         VARCHAR(20)   NULL,
                material_group_desc    NVARCHAR(120) NULL,
                origin_id              VARCHAR(20)   NULL,
                origin_name            NVARCHAR(80)  NULL,
                procurement_type       VARCHAR(20)   NULL,
                procurement_type_name  NVARCHAR(80)  NULL,
                variety_id             VARCHAR(20)   NULL,
                variety_name           NVARCHAR(80)  NULL,
                class_id               VARCHAR(20)   NULL,
                class_name             NVARCHAR(80)  NULL,
                size_id                VARCHAR(20)   NULL,
                size_name              NVARCHAR(80)  NULL,
                material_weight        VARCHAR(40)   NULL,
                material_weight_name   NVARCHAR(80)  NULL,
                old_material_code      NVARCHAR(40)  NULL,
                weight                 DECIMAL(18,3) NULL,
                weight_unit            VARCHAR(10)   NULL,
                material_type          VARCHAR(20)   NULL,
                base_unit              VARCHAR(10)   NULL,
                base_unit_name         NVARCHAR(40)  NULL,
                sap_created_on         DATETIME2     NULL,
                sap_created_by         NVARCHAR(40)  NULL);", transaction: tx);

        using (var bulk = new SqlBulkCopy(c, SqlBulkCopyOptions.Default, tx)
        {
            DestinationTableName = stage,
            BulkCopyTimeout      = 120,
            BatchSize            = 1000
        })
        {
            foreach (DataColumn col in dt.Columns)
                bulk.ColumnMappings.Add(col.ColumnName, col.ColumnName);
            await bulk.WriteToServerAsync(dt, ct);
        }

        await c.ExecuteAsync($@"
            MERGE qms_sap_material_cache AS t
            USING {stage} AS s
              ON  t.material_no = s.material_no
            WHEN MATCHED THEN UPDATE SET
                material_desc         = s.material_desc,
                major_category        = s.major_category,
                major_category_desc   = s.major_category_desc,
                sub_major_category    = s.sub_major_category,
                overhead              = s.overhead,
                material_group        = s.material_group,
                material_group_desc   = s.material_group_desc,
                origin_id             = s.origin_id,
                origin_name           = s.origin_name,
                procurement_type      = s.procurement_type,
                procurement_type_name = s.procurement_type_name,
                variety_id            = s.variety_id,
                variety_name          = s.variety_name,
                class_id              = s.class_id,
                class_name            = s.class_name,
                size_id               = s.size_id,
                size_name             = s.size_name,
                material_weight       = s.material_weight,
                material_weight_name  = s.material_weight_name,
                old_material_code     = s.old_material_code,
                weight                = s.weight,
                weight_unit           = s.weight_unit,
                material_type         = s.material_type,
                base_unit             = s.base_unit,
                base_unit_name        = s.base_unit_name,
                sap_created_on        = s.sap_created_on,
                sap_created_by        = s.sap_created_by,
                synced_at             = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (
                material_no, material_desc, major_category, major_category_desc,
                sub_major_category, overhead, material_group, material_group_desc, origin_id,
                origin_name, procurement_type, procurement_type_name, variety_id, variety_name,
                class_id, class_name, size_id, size_name, material_weight, material_weight_name,
                old_material_code, weight, weight_unit, material_type, base_unit, base_unit_name,
                sap_created_on, sap_created_by)
            VALUES (
                s.material_no, s.material_desc, s.major_category, s.major_category_desc,
                s.sub_major_category, s.overhead, s.material_group, s.material_group_desc, s.origin_id,
                s.origin_name, s.procurement_type, s.procurement_type_name, s.variety_id, s.variety_name,
                s.class_id, s.class_name, s.size_id, s.size_name, s.material_weight, s.material_weight_name,
                s.old_material_code, s.weight, s.weight_unit, s.material_type, s.base_unit, s.base_unit_name,
                s.sap_created_on, s.sap_created_by);
            DROP TABLE {stage};", transaction: tx, commandTimeout: 240);

        tx.Commit();
    }

    private static object NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? DBNull.Value : (object)s.Trim();

    private static decimal? TryDecimal(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return decimal.TryParse(s, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static DateTime? TryEdmDate(string? s)
    {
        // SAP /Date(1703808000000)/ -> DateTime, ms since epoch UTC.
        if (string.IsNullOrWhiteSpace(s)) return null;
        var open  = s.IndexOf('(');
        var close = s.IndexOf(')');
        if (open >= 0 && close > open && long.TryParse(s.AsSpan(open + 1, close - open - 1), out var ms))
            return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
        return null;
    }

    // ---- Sync log + settings updates --------------------------------------

    private async Task<long> OpenSyncLogAsync(string endpointKey, string triggeredBy, string source, DateTime startedAt)
    {
        using var c = new SqlConnection(_cs);
        return await c.ExecuteScalarAsync<long>(@"
            INSERT INTO qms_sap_sync_log (endpoint_key, started_at, triggered_by, trigger_source)
            VALUES (@endpointKey, @startedAt, @triggeredBy, @source);
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
            new { endpointKey, startedAt, triggeredBy, source });
    }

    private async Task CloseSyncLogAsync(long logId, bool success, int rows, string message)
    {
        using var c = new SqlConnection(_cs);
        await c.ExecuteAsync(@"
            UPDATE qms_sap_sync_log SET completed_at=SYSUTCDATETIME(),
                   success=@success, rows_synced=@rows, message=@message
            WHERE  sync_log_id=@logId",
            new { logId, success, rows, message = Trim(message, 1900) });
    }

    private async Task UpdateLastRunAsync(string endpointKey, bool ok, int rows, string message)
    {
        // Persist all three "last run" keys in a single transaction so a
        // process kill mid-write can never leave LastRunUtc updated but
        // LastResult / LastRowCount stale. Mirrors the wall-clock atomicity
        // we get on sync_log.completed_at + success + rows_synced.
        var entries = new List<KeyValuePair<string, string?>>
        {
            new($"Sap.Sync.{endpointKey}.LastRunUtc",   DateTime.UtcNow.ToString("o")),
            new($"Sap.Sync.{endpointKey}.LastResult",   (ok ? "OK: " : "FAIL: ") + Trim(message, 400)),
            new($"Sap.Sync.{endpointKey}.LastRowCount", rows.ToString())
        };
        await _settings.SetManyAsync(entries);
    }

    private static string Trim(string s, int max) => s == null ? "" : (s.Length <= max ? s : s.Substring(0, max));
}
