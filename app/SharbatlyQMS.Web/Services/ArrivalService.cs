using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services.Sap;
using SharbatlyQMS.Web.ViewModels;

namespace SharbatlyQMS.Web.Services;

public class ArrivalService : IArrivalService
{
    private readonly string _cs;
    private readonly IAuditService _audit;
    // Only used by DeleteAsync, to unlink attachment files once the DB rows are
    // gone. Documents live outside wwwroot, so a row deleted without its file
    // leaves bytes nothing can ever reach again.
    private readonly IDocumentService _docs;

    public ArrivalService(IConfiguration config, IAuditService audit, IDocumentService docs)
    {
        _cs = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
        _audit = audit;
        _docs  = docs;
    }

    private SqlConnection Open() => new(_cs);

    // Outer-joins to the LIVE quality order for each arrival so list/detail
    // views can render "Create QO" vs "View QO" without an extra round trip,
    // plus the superseded one behind it when the container was reinspected --
    // kept reachable as a reference, never as the order that speaks for the
    // container.
    private const string ArrivalSelect = @"
        SELECT a.arrival_id     ArrivalId,
               a.arrival_no     ArrivalNo,
               a.source_system  SourceSystem,
               a.bol_no         BolNo,
               a.container_no   ContainerNo,
               a.ebeln          Ebeln,
               a.bukrs          Bukrs,
               a.vendor_no      VendorNo,
               a.vendor_name    VendorName,
               a.plant          Plant,
               a.storage_location StorageLocation,
               a.po_type          PoType,
               a.status_code    StatusCode,
               a.created_at     CreatedAt,
               a.created_by     CreatedBy,
               a.completed_at   CompletedAt,
               a.completed_by   CompletedBy,
               a.rejected_at    RejectedAt,
               a.rejected_by    RejectedBy,
               a.reject_reason  RejectReason,
               qo.quality_order_id QualityOrderId,
               qo.quality_order_no QualityOrderNo,
               qo.status_code      QualityOrderStatus,
               prev.quality_order_id PreviousQualityOrderId,
               prev.quality_order_no PreviousQualityOrderNo
        FROM   qms_arrival a
        OUTER APPLY (
            -- superseded_at IS NULL, not TOP 1 by id. Both land on the
            -- reinspection today, but only one of them SAYS so: this is the
            -- same filter as UX_qms_quality_order_active_per_arrival, which is
            -- what actually defines the live order.
            SELECT TOP 1 quality_order_id, quality_order_no, status_code, reinspection_of
            FROM   qms_quality_order
            WHERE  arrival_id = a.arrival_id AND status_code <> 'Cancelled'
              AND  superseded_at IS NULL
            ORDER  BY quality_order_id DESC
        ) qo
        -- The inspection the live one replaced, so the screen can offer it as a
        -- reference instead of silently dropping it. NULL for the ordinary
        -- container, which has only ever been inspected once.
        LEFT   JOIN qms_quality_order prev ON prev.quality_order_id = qo.reinspection_of";

    public async Task<ViewModels.ArrivalPage> ListAsync(ViewModels.ArrivalListFilter f, Models.PlantScope scope)
    {
        using var c = Open();

        static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        // Clamped to the sizes the pager offers, so a hand-edited query string
        // cannot ask for the whole table back.
        var pageSize = f.PageSize is 25 or 50 or 100 ? f.PageSize : 50;
        var page     = f.Page < 1 ? 1 : f.Page;

        // a.created_at is UTC; the list renders it local. Convert the picked LOCAL
        // dates to UTC here, upper bound exclusive-next-midnight (mirrors the QO
        // list) so the To date isn't dropped after 00:00:00.000.
        DateTime? fromUtc = f.From.HasValue
            ? DateTime.SpecifyKind(f.From.Value.Date, DateTimeKind.Local).ToUniversalTime()
            : null;
        DateTime? toUtc = f.To.HasValue
            ? DateTime.SpecifyKind(f.To.Value.Date.AddDays(1), DateTimeKind.Local).ToUniversalTime()
            : null;

        // Cleaned for the any-flags, sentinel-wrapped for the IN clauses --
        // see FilterValues for why those are two different things.
        var statuses    = FilterValues.Many(f.Status);
        var plants      = FilterValues.Many(f.Plant);
        var storageLocs = FilterValues.Many(f.StorageLoc);
        var suppliers   = FilterValues.Many(f.Supplier);
        var createdBys  = FilterValues.Many(f.CreatedBy);

        var p = new
        {
            statuses    = FilterValues.ForIn(statuses),    statusAny     = statuses.Count    > 0,
            plants      = FilterValues.ForIn(plants),      plantAny      = plants.Count      > 0,
            storageLocs = FilterValues.ForIn(storageLocs), storageLocAny = storageLocs.Count > 0,
            suppliers   = FilterValues.ForIn(suppliers),   supplierAny   = suppliers.Count   > 0,
            createdBys  = FilterValues.ForIn(createdBys),  createdByAny  = createdBys.Count  > 0,
            search     = Trim(f.Search),
            sUnrestricted = scope.Unrestricted,
            sPlants       = scope.QueryPlants,
            container  = Trim(f.Container),
            bol        = Trim(f.Bol),
            po         = Trim(f.Po),
            arrivalNo  = Trim(f.ArrivalNo),
            material   = Trim(f.Material),
            matMajor    = Trim(f.MatMajor),
            matSubMajor = Trim(f.MatSubMajor),
            fromUtc,
            toUtc,
            offset   = (page - 1) * pageSize,
            pageSize
        };

        // Two result sets off one round trip: the total for the pager, then the
        // page itself. The WHERE is shared so the count can never disagree with
        // the rows it is counting.
        using var grid = await c.QueryMultipleAsync(@"
            SELECT COUNT(*) FROM qms_arrival a
            WHERE " + ArrivalWhere + @";
        " + ArrivalSelect + @"
            WHERE " + ArrivalWhere + @"
            -- arrival_id breaks ties on created_at so a row can never sit on two
            -- pages, or on none, when several arrivals share a timestamp.
            ORDER BY a.created_at DESC, a.arrival_id DESC
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY", p);

        var total = await grid.ReadFirstAsync<int>();
        var rows  = (await grid.ReadAsync<Arrival>()).ToList();
        return new ViewModels.ArrivalPage(rows, total, page, pageSize);
    }

    /// <summary>
    /// Shared by the count and the page query so the pager can never disagree
    /// with the rows it is counting.
    /// </summary>
    // Multi-select filters use an any-flag plus an IN list, matching the
    // plant-scope convention below. Dapper renders an empty list as
    // IN (SELECT 1 WHERE 1=0), so the flag is what turns a filter off.
    private static readonly string ArrivalWhere = @"(@statusAny = 0 OR a.status_code IN @statuses)
              AND  (@plantAny   = 0 OR a.plant           IN @plants)
              AND  (@sUnrestricted = 1 OR a.plant IN @sPlants)
              AND  (@container  IS NULL OR a.container_no     LIKE '%' + @container + '%')
              AND  (@bol        IS NULL OR a.bol_no           LIKE '%' + @bol       + '%')
              AND  (@po         IS NULL OR a.ebeln            LIKE '%' + @po        + '%')
              AND  (@arrivalNo  IS NULL OR a.arrival_no       LIKE '%' + @arrivalNo + '%')
              AND  (@storageLocAny = 0 OR a.storage_location IN @storageLocs)
              AND  (@supplierAny   = 0 OR a.vendor_name      IN @suppliers)
              AND  (@createdByAny  = 0 OR a.created_by       IN @createdBys)
              AND  (@fromUtc    IS NULL OR a.created_at      >= @fromUtc)
              AND  (@toUtc      IS NULL OR a.created_at       < @toUtc)
              AND  (@material   IS NULL OR EXISTS (
                        SELECT 1 FROM qms_arrival_item ai
                        WHERE  ai.arrival_id = a.arrival_id
                          AND (ai.material_no   LIKE '%' + @material + '%'
                            OR ai.material_desc LIKE '%' + @material + '%')))
              -- Major / sub-major come from the material master rather than the
              -- copy on the arrival line: the line stores a major and no
              -- sub-major, and a filter that answered differently on different
              -- screens would be worse than not offering one." + MaterialCategoryFilter.Sql("qms_arrival_item", "aim", "aim.arrival_id = a.arrival_id") + @"
              AND  (@search IS NULL
                    OR a.arrival_no   LIKE '%' + @search + '%'
                    OR a.container_no LIKE '%' + @search + '%'
                    OR a.bol_no       LIKE '%' + @search + '%'
                    OR a.ebeln        LIKE '%' + @search + '%'
                    OR a.vendor_name  LIKE '%' + @search + '%'
                    OR a.created_by   LIKE '%' + @search + '%')";

    /// <summary>
    /// Dropdown sources for the Arrivals filter panel: distinct plants, (plant,
    /// storage-location) pairs, and creators — each drawn only from arrivals that
    /// exist, so picking any option returns at least one row. Mirrors
    /// QualityOrderService.GetQoFilterOptionsAsync.
    /// </summary>
    public async Task<ViewModels.ArrivalFilterOptions> GetArrivalFilterOptionsAsync(Models.PlantScope scope)
    {
        using var c = Open();
        using var grid = await c.QueryMultipleAsync(@"
            SELECT DISTINCT a.plant
            FROM   qms_arrival a
            WHERE  a.plant IS NOT NULL AND a.plant <> ''
              AND (@sUnrestricted = 1 OR a.plant IN @sPlants)
            ORDER  BY a.plant;

            SELECT DISTINCT a.plant AS Plant, a.storage_location AS Code
            FROM   qms_arrival a
            WHERE  a.plant            IS NOT NULL AND a.plant            <> ''
              AND  a.storage_location IS NOT NULL AND a.storage_location <> ''
              AND (@sUnrestricted = 1 OR a.plant IN @sPlants)
            ORDER  BY a.plant, a.storage_location;

            SELECT DISTINCT a.vendor_name
            FROM   qms_arrival a
            WHERE  a.vendor_name IS NOT NULL AND a.vendor_name <> ''
              AND (@sUnrestricted = 1 OR a.plant IN @sPlants)
            ORDER  BY a.vendor_name;

            SELECT DISTINCT a.created_by
            FROM   qms_arrival a
            WHERE  a.created_by IS NOT NULL AND a.created_by <> ''
              AND (@sUnrestricted = 1 OR a.plant IN @sPlants)
            ORDER  BY a.created_by;",
            new { sUnrestricted = scope.Unrestricted, sPlants = scope.QueryPlants });

        var plants    = (await grid.ReadAsync<string>()).ToList();
        var storage   = (await grid.ReadAsync<ViewModels.QoPlantStorage>()).ToList();
        var suppliers = (await grid.ReadAsync<string>()).ToList();
        var createdBy = (await grid.ReadAsync<string>()).ToList();
        return new ViewModels.ArrivalFilterOptions
        {
            Plants = plants, StorageLocations = storage, Suppliers = suppliers, CreatedBy = createdBy
        };
    }

    /// <summary>Returns the arrival's plant code, or null when the arrival doesn't exist.
    /// Used by the controller-level plant-scope gate before letting an Operator open
    /// a Details / Edit page for an arrival outside their plant.</summary>
    public async Task<string?> GetPlantAsync(long arrivalId)
    {
        using var c = Open();
        return await c.ExecuteScalarAsync<string?>(
            "SELECT plant FROM qms_arrival WHERE arrival_id = @arrivalId",
            new { arrivalId });
    }

    public async Task<Arrival?> GetAsync(long arrivalId)
    {
        using var c = Open();
        return await c.QuerySingleOrDefaultAsync<Arrival>(
            ArrivalSelect + " WHERE a.arrival_id = @arrivalId", new { arrivalId });
    }

    public async Task<Arrival?> FindByShipmentAsync(string containerNo, string bolNo, string? po)
    {
        if (string.IsNullOrWhiteSpace(containerNo)) return null;
        // BOL is optional. Normalise null/"" on both sides so a BOL-less shipment
        // still dedups against an existing BOL-less arrival (stored as NULL or '').
        bolNo = (bolNo ?? "").Trim();
        using var c = Open();
        return await c.QueryFirstOrDefaultAsync<Arrival>(ArrivalSelect + @"
            WHERE a.container_no = @containerNo AND ISNULL(a.bol_no, '') = @bolNo
              AND (@po IS NULL OR a.ebeln = @po)
            ORDER BY a.created_at DESC",
            new { containerNo, bolNo, po });
    }

    public async Task<IReadOnlyList<Arrival>> FindByContainersAsync(IReadOnlyCollection<string> containers)
    {
        if (containers == null || containers.Count == 0) return Array.Empty<Arrival>();
        using var c = Open();
        var rows = await c.QueryAsync<Arrival>(ArrivalSelect + @"
            WHERE a.container_no IN @containers", new { containers });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<ArrivalItem>> GetItemsAsync(long arrivalId)
    {
        using var c = Open();
        var rows = await c.QueryAsync<ArrivalItem>(@"
            SELECT arrival_item_id     ArrivalItemId,
                   arrival_id          ArrivalId,
                   ebeln               Ebeln,
                   ebelp               Ebelp,
                   material_no         MaterialNo,
                   material_desc       MaterialDesc,
                   plant               Plant,
                   storage_location    StorageLocation,
                   batch_no            BatchNo,
                   quantity            Quantity,
                   uom                 Uom,
                   material_group      MaterialGroup,
                   material_group_desc MaterialGroupDesc,
                   major_category      MajorCategory
            FROM   qms_arrival_item
            WHERE  arrival_id = @arrivalId
            ORDER  BY ebelp", new { arrivalId });
        return rows.ToList();
    }

    // V36 (2026-07-07): arrival custom fields. A field is "applicable" to an
    // arrival when its material group appears on at least one line item.
    public async Task<IReadOnlyList<ArrivalCustomField>> GetCustomFieldsAsync(long arrivalId)
    {
        using var c = Open();
        var rows = await c.QueryAsync<ArrivalCustomField>(@"
            SELECT f.field_id       FieldId,
                   f.field_name     FieldName,
                   f.value_kind     ValueKind,
                   f.material_group MaterialGroup,
                   f.sort_order     SortOrder,
                   f.is_active      IsActive,
                   v.text_value     TextValue,
                   v.numeric_value  NumericValue,
                   v.date_value     DateValue
            FROM   qms_arrival_field f
            LEFT JOIN qms_arrival_field_value v
                   ON v.field_id = f.field_id AND v.arrival_id = @arrivalId
            WHERE  f.is_active = 1
              AND  f.material_group IN (
                       SELECT DISTINCT material_group FROM qms_arrival_item
                       WHERE arrival_id = @arrivalId AND material_group IS NOT NULL)
            ORDER  BY f.sort_order, f.field_name", new { arrivalId });
        return rows.ToList();
    }

    public async Task SaveCustomFieldValuesAsync(long arrivalId,
        IReadOnlyDictionary<int, string?> rawValues, string updatedBy)
    {
        // Re-derive the applicable set server-side so a crafted POST can't
        // attach values for fields that don't belong to this arrival.
        var applicable = await GetCustomFieldsAsync(arrivalId);
        using var c = Open();
        foreach (var f in applicable)
        {
            if (!rawValues.TryGetValue(f.FieldId, out var raw)) continue;
            raw = raw?.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                await c.ExecuteAsync(@"
                    DELETE FROM qms_arrival_field_value
                    WHERE arrival_id = @arrivalId AND field_id = @fieldId",
                    new { arrivalId, fieldId = f.FieldId });
                continue;
            }
            string?   text = null;
            decimal?  num  = null;
            DateTime? date = null;
            switch (f.ValueKind)
            {
                case "Numeric":
                    if (!decimal.TryParse(raw, System.Globalization.NumberStyles.Number,
                            System.Globalization.CultureInfo.InvariantCulture, out var d))
                        continue;                       // silently skip unparseable input
                    num = d;
                    break;
                case "Date":
                    if (!DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var dt))
                        continue;
                    date = dt.Date;
                    break;
                case "YesNo":
                    text = raw is "Yes" or "No" ? raw : null;
                    if (text == null) continue;
                    break;
                default:
                    text = raw.Length > 400 ? raw[..400] : raw;
                    break;
            }
            await c.ExecuteAsync(@"
                MERGE qms_arrival_field_value AS t
                USING (SELECT @arrivalId AS arrival_id, @fieldId AS field_id) AS s
                   ON t.arrival_id = s.arrival_id AND t.field_id = s.field_id
                WHEN MATCHED THEN UPDATE SET
                    text_value = @text, numeric_value = @num, date_value = @date,
                    updated_at = SYSUTCDATETIME(), updated_by = @updatedBy
                WHEN NOT MATCHED THEN INSERT
                    (arrival_id, field_id, text_value, numeric_value, date_value, updated_by)
                    VALUES (@arrivalId, @fieldId, @text, @num, @date, @updatedBy);",
                new { arrivalId, fieldId = f.FieldId, text, num, date, updatedBy });
        }
    }

    public async Task<ArrivalChecklist?> GetChecklistAsync(long arrivalId)
    {
        using var c = Open();
        return await c.QuerySingleOrDefaultAsync<ArrivalChecklist>(@"
            SELECT checklist_id ChecklistId, arrival_id ArrivalId,
                   seal_no SealNo, carrier_name CarrierName,
                   seal_intact SealIntact, seal_matches_documents SealMatchesDocuments,
                   external_damage_exists ExternalDamageExists,
                   set_temperature SetTemperature, display_temperature DisplayTemperature,
                   cargo_smell_normal CargoSmellNormal, visual_cargo_acceptable VisualCargoAcceptable,
                   cargo_shifted_collapsed_water CargoShiftedCollapsedWater,
                   pulp_temp_front PulpTempFront, pulp_temp_middle PulpTempMiddle, pulp_temp_back PulpTempBack,
                   data_logger_located DataLoggerLocated, data_logger_serial DataLoggerSerial,
                   data_logger_photo_taken DataLoggerPhotoTaken, logger_handed_over LoggerHandedOver,
                   logger_active_data_available LoggerActiveDataAvailable, logger_temperature LoggerTemperature,
                   notes Notes, updated_at UpdatedAt, updated_by UpdatedBy,
                   display_temp_photo_taken          DisplayTempPhotoTaken,
                   internal_inspection_photo_taken   InternalInspectionPhotoTaken,
                   pulp_temp_photo_taken             PulpTempPhotoTaken,
                   container_seal_photo_taken        ContainerSealPhotoTaken,
                   external_container_photo_taken    ExternalContainerPhotoTaken,
                   external_damage_photo_taken       ExternalDamagePhotoTaken,
                   first_view_cargo_photo_taken      FirstViewCargoPhotoTaken,
                   internal_damage_photo_taken       InternalDamagePhotoTaken
            FROM   qms_arrival_checklist
            WHERE  arrival_id = @arrivalId", new { arrivalId });
    }

    public async Task<short?> GetCachedTransitDaysAsync(long arrivalId)
    {
        using var c = Open();
        // TOP 1 over the triplet, not a join: the cache key is wider than
        // (container, BOL, PO) -- one row per PO line -- so a plain join fans
        // out. Ordered the same way the flat views resolve a triplet to one
        // cache row, so the number here and the number in the Data Hub agree.
        return await c.ExecuteScalarAsync<short?>(@"
            SELECT TOP 1 cc.transit_days
            FROM   qms_sap_container_cache cc
            JOIN   qms_arrival a
              ON   a.container_no = cc.container_no
             AND   a.bol_no       = cc.bol_no
             AND   a.ebeln        = cc.ebeln
            WHERE  a.arrival_id = @arrivalId
              AND  cc.transit_days IS NOT NULL
            ORDER  BY cc.doc_date DESC, cc.sto",
            new { arrivalId });
    }

    public async Task<ShipmentSnapshot?> GetShipmentAsync(long arrivalId)
    {
        using var c = Open();
        return await c.QuerySingleOrDefaultAsync<ShipmentSnapshot>(@"
            SELECT shipment_snapshot_id ShipmentSnapshotId, arrival_id ArrivalId,
                   internal_shipment_no InternalShipmentNo,
                   loading_date LoadingDate, sailing_date SailingDate,
                   examination_date ExaminationDate, arrival_date ArrivalDate,
                   port_arrival_date PortArrivalDate,
                   discharge_date DischargeDate,
                   unloading_date UnloadingDate, inspection_date InspectionDate,
                   transit_days TransitDays, time_bar TimeBar,
                   loading_port LoadingPort, loading_country LoadingCountry,
                   arrival_place ArrivalPlace, vessel_name VesselName, voyage_number VoyageNumber,
                   pullout_date PullOutDate, receive_date ReceiveDate,
                   time_bar_exceeded TimeBarExceeded, inspection_point InspectionPoint,
                   joint_survey JointSurvey, status_code StatusCode
            FROM   qms_shipment_snapshot
            WHERE  arrival_id = @arrivalId", new { arrivalId });
    }

    public async Task<long> CreateFromSapAsync(IReadOnlyList<SapShipmentRow> rows, string createdBy, string? overridePlant = null)
    {
        if (rows.Count == 0) throw new InvalidOperationException("No SAP rows selected.");

        var first = rows[0];
        // A manager-set plant override moves the arrival (and the QO that
        // inherits a.plant) into the target plant. Empty/whitespace is treated
        // as "no override" so a stray value can't blank the plant.
        var headerPlant = string.IsNullOrWhiteSpace(overridePlant) ? first.Plant : overridePlant.Trim();
        if (rows.Any(r => !string.Equals(r.ContainerNo, first.ContainerNo, StringComparison.OrdinalIgnoreCase) ||
                          !string.Equals(r.BolNo,       first.BolNo,       StringComparison.OrdinalIgnoreCase) ||
                          !string.Equals(r.Ebeln,       first.Ebeln,       StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "All selected rows must share the same container, BOL, and PO (one arrival = one container × BOL × PO).");
        }

        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();

        var seq = await c.ExecuteScalarAsync<int>(
            "SELECT NEXT VALUE FOR seq_qms_arrival_no", transaction: tx);
        var arrivalNo = $"ARR-{DateTime.UtcNow:yyyy}-{seq:D6}";

        var arrivalId = await c.ExecuteScalarAsync<long>(@"
            INSERT INTO qms_arrival
                (arrival_no, source_system, bol_no, container_no, ebeln, bukrs,
                 vendor_no, vendor_name, plant, storage_location, po_type,
                 status_code, created_at, created_by)
            VALUES
                (@arrivalNo, 'S4HANA', @bol, @container, @ebeln, @bukrs,
                 @vendorNo, @vendorName, @plant, @storageLocation, @poType,
                 'Draft', SYSUTCDATETIME(), @createdBy);
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
            new
            {
                arrivalNo,
                bol        = first.BolNo,
                container  = first.ContainerNo,
                ebeln      = first.Ebeln,
                bukrs      = first.Bukrs,
                vendorNo   = first.VendorNo,
                vendorName = first.VendorName,
                plant      = headerPlant,
                // M13: denormalised onto the header so the Arrivals list can show
                // them as names and the QO list can filter on storage location
                // without an EXISTS over qms_arrival_item.
                storageLocation = first.StorageLocation,
                poType          = first.PoType,
                createdBy
            }, tx);

        // L1: a single Dapper Execute with an array parameter runs the same
        // INSERT statement once per row using a prepared command (per-row
        // round-trip is still N, but the SQL parser cost is amortized and
        // the client-side parameter array means Dapper batches the wire
        // packets). For typical container fan-outs (1-20 lines) this is
        // sufficient; for the rare > 100-line container the explicit
        // batched INSERT below kicks in.
        const string itemInsertSql = @"
            INSERT INTO qms_arrival_item
                (arrival_id, ebeln, ebelp, material_no, material_desc,
                 plant, storage_location, batch_no, quantity, uom,
                 material_group, material_group_desc, major_category)
            VALUES
                (@arrivalId, @ebeln, @ebelp, @materialNo, @materialDesc,
                 @plant, @storageLocation, @batchNo, @quantity, @uom,
                 @materialGroup, @materialGroupDesc, @majorCategory);";
        var itemParams = rows.Select(r => new
        {
            arrivalId,
            ebeln             = r.Ebeln,
            ebelp             = r.Ebelp,
            materialNo        = r.MaterialNo,
            materialDesc      = r.MaterialDesc,
            plant             = r.Plant,
            storageLocation   = r.StorageLocation,
            batchNo           = r.BatchNo,
            quantity          = r.Quantity,
            uom               = r.Uom,
            materialGroup     = r.MaterialGroup,
            materialGroupDesc = r.MaterialGroupDesc,
            majorCategory     = r.MajorCategory
        }).ToList();
        await c.ExecuteAsync(itemInsertSql, itemParams, tx);

        var shipmentSeq = await c.ExecuteScalarAsync<int>(
            "SELECT NEXT VALUE FOR seq_qms_shipment_no", transaction: tx);
        var internalShipmentNo = $"SHP-{DateTime.UtcNow:yyyy}-{shipmentSeq:D6}";

        // inspection_date is the arrival/checklist creation date (read-only in the
        // UI) -- CAST to date so it matches the DATE column and stays in step with
        // created_at on the arrival row above.
        //
        // The receive date is the LATEST over the selected lines, exactly what
        // the pending list showed as MAX(receive_date) for this triplet -- so
        // the date on the new arrival is the date the operator just clicked on.
        // It is only the starting value: the SAP sweep refreshes it afterwards,
        // because SAP advances Receive_Date to the branch goods receipt days
        // after the first sweep sees the container.
        var receive     = rows.Max(r => r.ReceiveDate);
        var portArrival = rows.Max(r => r.PortArrivalDate);
        await c.ExecuteAsync(@"
            INSERT INTO qms_shipment_snapshot
                (arrival_id, internal_shipment_no, sailing_date,
                 examination_date, arrival_date, port_arrival_date, receive_date, inspection_date,
                 transit_days, loading_port, loading_country, arrival_place,
                 vessel_name, voyage_number, status_code)
            VALUES
                (@arrivalId, @internalShipmentNo, @sailing,
                 @examination, @receive, @portArrival, @receive, CAST(SYSUTCDATETIME() AS date),
                 @transit, @loadingPort, @loadingCountry, @arrivalPlace,
                 @vessel, @voyage, 'Draft');",
            new
            {
                arrivalId,
                internalShipmentNo,
                sailing        = first.SailingDate?.ToDateTime(TimeOnly.MinValue),
                examination    = first.ExaminationDate?.ToDateTime(TimeOnly.MinValue),
                receive        = receive?.ToDateTime(TimeOnly.MinValue),
                portArrival    = portArrival?.ToDateTime(TimeOnly.MinValue),
                transit        = first.TransitDays,
                loadingPort    = first.LoadingPort,
                loadingCountry = first.LoadingCountry,
                arrivalPlace   = first.ArrivalPlace,
                vessel         = first.VesselName,
                voyage         = first.VoyageNumber
            }, tx);

        // Checklist row -- all Yes/No questions default to "No" (0) and all
        // photo-flag toggles default to "not taken" (0). The inspector flips
        // each to Yes / ticks each photo flag as they verify the item. SealNo
        // + carrier name come pre-filled from the SAP snapshot.
        await c.ExecuteAsync(@"
            INSERT INTO qms_arrival_checklist (
                arrival_id, seal_no, carrier_name,
                seal_intact, seal_matches_documents, external_damage_exists,
                cargo_smell_normal, visual_cargo_acceptable, cargo_shifted_collapsed_water,
                data_logger_located, data_logger_photo_taken, logger_handed_over,
                logger_active_data_available,
                display_temp_photo_taken, internal_inspection_photo_taken, pulp_temp_photo_taken,
                container_seal_photo_taken, external_container_photo_taken, external_damage_photo_taken,
                first_view_cargo_photo_taken, internal_damage_photo_taken)
            VALUES (
                @arrivalId, @sealNo, @carrier,
                0, 0, 0,
                0, 0, 0,
                0, 0, 0,
                0,
                0, 0, 0,
                0, 0, 0,
                0, 0);",
            new { arrivalId, sealNo = first.SealNo, carrier = first.Carrier }, tx);

        var json = JsonSerializer.Serialize(rows);
        var hash = Sha256(json);
        await c.ExecuteAsync(@"
            INSERT INTO qms_arrival_sap_snapshot
                (arrival_id, odata_service_name, odata_query_hash, payload_json,
                 captured_at, captured_by)
            VALUES
                (@arrivalId, 'stub', @hash, @json, SYSUTCDATETIME(), @createdBy);",
            new { arrivalId, hash, json, createdBy }, tx);

        await c.ExecuteAsync(@"
            INSERT INTO qms_status_history
                (entity_type, entity_id, old_status, new_status, changed_at, changed_by)
            VALUES
                ('Arrival', @arrivalId, NULL, 'Draft', SYSUTCDATETIME(), @createdBy);",
            new { arrivalId, createdBy }, tx);

        // T022 (US1) -- Arrival created from a SAP shipment snapshot.
        await _audit.WriteAsync(c, tx,
            EntityTypes.Arrival, arrivalId, ActionCodes.Created,
            oldValues: null,
            newValues: new
            {
                arrival_no = arrivalNo,
                container_no = first.ContainerNo,
                bol_no = first.BolNo,
                ebeln = first.Ebeln,
                vendor_no = first.VendorNo,
                vendor_name = first.VendorName,
                plant = headerPlant,
                item_count = rows.Count
            },
            actor: createdBy);

        tx.Commit();
        return arrivalId;
    }

    public async Task SaveChecklistAsync(ArrivalChecklist cl, string updatedBy)
    {
        // PH-1.3 (2026-05-20): wrap in a transaction so a future audit
        // INSERT here (Phase 3 instrumentation) lands atomically with the
        // UPDATE. Single-statement today, but transaction-ready.
        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();

        // BUGFIX 2026-05-21: capture the row BEFORE the UPDATE so the audit
        // diff only highlights fields that actually changed. The column
        // aliases (PascalCase) match the keys in the newValues anonymous
        // object below, so ParseDiffs at read-time pairs them correctly.
        // carrier_name is sourced from the SAP CDS 'Carrier' field at creation and
        // is read-only in the UI, so it is deliberately excluded from the
        // SELECT/UPDATE/audit below -- saves must not null it or log a phantom diff.
        var oldRow = await c.QuerySingleOrDefaultAsync(@"
            SELECT seal_no                       AS SealNo,
                   seal_intact                   AS SealIntact,
                   seal_matches_documents        AS SealMatchesDocuments,
                   external_damage_exists        AS ExternalDamageExists,
                   set_temperature               AS SetTemperature,
                   display_temperature           AS DisplayTemperature,
                   cargo_smell_normal            AS CargoSmellNormal,
                   visual_cargo_acceptable       AS VisualCargoAcceptable,
                   cargo_shifted_collapsed_water AS CargoShiftedCollapsedWater,
                   pulp_temp_front               AS PulpTempFront,
                   pulp_temp_middle              AS PulpTempMiddle,
                   pulp_temp_back                AS PulpTempBack,
                   data_logger_located           AS DataLoggerLocated,
                   data_logger_serial            AS DataLoggerSerial,
                   logger_temperature            AS LoggerTemperature,
                   notes                         AS Notes
            FROM   qms_arrival_checklist
            WHERE  arrival_id = @ArrivalId",
            new { cl.ArrivalId }, tx);

        await c.ExecuteAsync(@"
            UPDATE qms_arrival_checklist SET
              seal_no = @SealNo,
              seal_intact = @SealIntact, seal_matches_documents = @SealMatchesDocuments,
              external_damage_exists = @ExternalDamageExists,
              set_temperature = @SetTemperature, display_temperature = @DisplayTemperature,
              cargo_smell_normal = @CargoSmellNormal, visual_cargo_acceptable = @VisualCargoAcceptable,
              cargo_shifted_collapsed_water = @CargoShiftedCollapsedWater,
              pulp_temp_front = @PulpTempFront, pulp_temp_middle = @PulpTempMiddle, pulp_temp_back = @PulpTempBack,
              data_logger_located = @DataLoggerLocated, data_logger_serial = @DataLoggerSerial,
              data_logger_photo_taken = @DataLoggerPhotoTaken, logger_handed_over = @LoggerHandedOver,
              logger_active_data_available = @LoggerActiveDataAvailable, logger_temperature = @LoggerTemperature,
              notes = @Notes,
              display_temp_photo_taken          = @DisplayTempPhotoTaken,
              internal_inspection_photo_taken   = @InternalInspectionPhotoTaken,
              pulp_temp_photo_taken             = @PulpTempPhotoTaken,
              container_seal_photo_taken        = @ContainerSealPhotoTaken,
              external_container_photo_taken    = @ExternalContainerPhotoTaken,
              external_damage_photo_taken       = @ExternalDamagePhotoTaken,
              first_view_cargo_photo_taken      = @FirstViewCargoPhotoTaken,
              internal_damage_photo_taken       = @InternalDamagePhotoTaken,
              updated_at = SYSUTCDATETIME(), updated_by = @updatedBy
            WHERE arrival_id = @ArrivalId",
            new
            {
                cl.ArrivalId, cl.SealNo,
                cl.SealIntact, cl.SealMatchesDocuments, cl.ExternalDamageExists,
                cl.SetTemperature, cl.DisplayTemperature, cl.CargoSmellNormal,
                cl.VisualCargoAcceptable, cl.CargoShiftedCollapsedWater,
                cl.PulpTempFront, cl.PulpTempMiddle, cl.PulpTempBack,
                cl.DataLoggerLocated, cl.DataLoggerSerial, cl.DataLoggerPhotoTaken,
                cl.LoggerHandedOver, cl.LoggerActiveDataAvailable, cl.LoggerTemperature,
                cl.Notes,
                cl.DisplayTempPhotoTaken, cl.InternalInspectionPhotoTaken, cl.PulpTempPhotoTaken,
                cl.ContainerSealPhotoTaken, cl.ExternalContainerPhotoTaken, cl.ExternalDamagePhotoTaken,
                cl.FirstViewCargoPhotoTaken, cl.InternalDamagePhotoTaken,
                updatedBy
            },
            transaction: tx);
        // T022 (US1) -- record the checklist save as Updated under
        // ArrivalChecklist. Field-level diff is computed at read-time by
        // ParseDiffs in AuditService; only changed fields surface in the UI.
        await _audit.WriteAsync(c, tx,
            EntityTypes.ArrivalChecklist, cl.ArrivalId, ActionCodes.Updated,
            oldValues: oldRow,
            newValues: new
            {
                cl.SealNo, cl.SealIntact, cl.SealMatchesDocuments,
                cl.ExternalDamageExists, cl.SetTemperature, cl.DisplayTemperature,
                cl.CargoSmellNormal, cl.VisualCargoAcceptable, cl.CargoShiftedCollapsedWater,
                cl.PulpTempFront, cl.PulpTempMiddle, cl.PulpTempBack,
                cl.DataLoggerLocated, cl.DataLoggerSerial, cl.LoggerTemperature,
                cl.Notes
            },
            actor: updatedBy);
        tx.Commit();
    }

    public async Task SaveShipmentAsync(ShipmentSnapshot ss, string updatedBy)
    {
        // PH-1.4 (2026-05-20): wrap in a transaction so a future audit
        // INSERT here lands atomically with the UPDATE.
        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();

        // BUGFIX 2026-05-21: capture the row BEFORE the UPDATE so the audit
        // diff only highlights fields that actually changed. Column aliases
        // match the newValues anonymous-object keys below.
        // The SAP-sourced fields (loading/sailing/examination/arrival dates,
        // transit days, loading port/country, vessel, voyage, carrier, receive
        // and inspection dates) are read-only in the UI -- they are set once in
        // CreateFromSapAsync and deliberately excluded from the SELECT/UPDATE/audit
        // here so saves can't null them or log phantom diffs. Only the
        // inspector-entered fields below are editable.
        var oldRow = await c.QuerySingleOrDefaultAsync(@"
            SELECT unloading_date    AS UnloadingDate,
                   discharge_date    AS DischargeDate,
                   pullout_date      AS PullOutDate,
                   time_bar          AS TimeBar,
                   arrival_place     AS ArrivalPlace,
                   inspection_point  AS InspectionPoint,
                   time_bar_exceeded AS TimeBarExceeded,
                   joint_survey      AS JointSurvey
            FROM   qms_shipment_snapshot
            WHERE  arrival_id = @ArrivalId",
            new { ss.ArrivalId }, tx);

        await c.ExecuteAsync(@"
            UPDATE qms_shipment_snapshot SET
              unloading_date = @UnloadingDate, discharge_date = @DischargeDate,
              time_bar = @TimeBar,
              arrival_place = @ArrivalPlace, pullout_date = @PullOutDate,
              time_bar_exceeded = @TimeBarExceeded, inspection_point = @InspectionPoint,
              joint_survey = @JointSurvey
            WHERE arrival_id = @ArrivalId", ss, transaction: tx);
        // T022 (US1) -- record the shipment-snapshot save under the parent arrival.
        // BUGFIX 2026-05-21: pass the captured old row so only changed fields
        // show up in the diff (dropped the synthetic "shipment_snapshot_updated"
        // marker -- it forced every save to appear as a change even when the
        // shipment fields were untouched).
        await _audit.WriteAsync(c, tx,
            EntityTypes.Arrival, ss.ArrivalId, ActionCodes.Updated,
            oldValues: oldRow,
            newValues: new
            {
                ss.UnloadingDate, ss.DischargeDate, ss.PullOutDate, ss.TimeBar, ss.ArrivalPlace,
                ss.InspectionPoint, ss.TimeBarExceeded, ss.JointSurvey
            },
            actor: updatedBy);
        tx.Commit();
    }

    public async Task<(bool ok, string? error, long? qoId)> RejectAsync(long arrivalId, string reason, string user)
    {
        // Validated server-side. The modal marks the box required, but a crafted
        // POST ignores that, and this text is printed verbatim on the report the
        // supplier receives -- an empty or one-word rejection is not a claim.
        reason = (reason ?? "").Trim();
        if (reason.Length < 10)
            return (false, "Say why the container is being refused — at least a short sentence. " +
                           "This text is printed on the report sent to the supplier.", null);
        if (reason.Length > 500) reason = reason[..500];

        var arrival = await GetAsync(arrivalId);
        if (arrival == null) return (false, "Arrival not found.", null);
        if (arrival.StatusCode != ArrivalStatus.Draft)
            return (false, $"Only a Draft arrival can be rejected (this one is {arrival.StatusCode}). " +
                           "Reopen it for edit first if the container must be refused.", null);

        // Deliberately NOT running the mandatory-field check CompleteAsync does:
        // those fields describe an inspection, and the point of a rejection is
        // that no inspection happens. The arrival stays editable afterwards so
        // the claim dates can still be filled in.

        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();

        // Guarded on Draft so a Complete that slipped in between the read above
        // and here makes this affect zero rows rather than double-writing.
        var updated = await c.ExecuteAsync(@"
            UPDATE qms_arrival
            SET    status_code='Rejected', rejected_at=SYSUTCDATETIME(),
                   rejected_by=@user, reject_reason=@reason
            WHERE  arrival_id=@arrivalId AND status_code='Draft'",
            new { arrivalId, user, reason }, tx);
        if (updated != 1)
        {
            tx.Rollback();
            return (false, "This arrival was changed by someone else. Please refresh and try again.", null);
        }

        // Freeze the shipment snapshot, exactly as completing does: the dates
        // the claim report prints must stop moving.
        await c.ExecuteAsync(
            "UPDATE qms_shipment_snapshot SET status_code='Confirmed' WHERE arrival_id=@arrivalId",
            new { arrivalId }, tx);

        long qoId;
        try
        {
            (qoId, _) = await RejectionOrder.CreateClosedAsync(c, tx, _audit, arrivalId, reason, user);
        }
        catch (InvalidOperationException ex)
        {
            tx.Rollback();
            return (false, ex.Message, null);
        }

        await c.ExecuteAsync(@"
            INSERT INTO qms_status_history
                (entity_type, entity_id, old_status, new_status, reason, changed_at, changed_by)
            VALUES ('Arrival', @arrivalId, 'Draft', 'Rejected', @reason, SYSUTCDATETIME(), @user)",
            new { arrivalId, reason, user }, tx);

        await _audit.WriteAsync(c, tx,
            EntityTypes.Arrival, arrivalId, ActionCodes.Updated,
            oldValues: new { status_code = "Draft" },
            newValues: new { status_code = "Rejected", reason },
            actor: user);

        tx.Commit();
        return (true, null, qoId);
    }

    public async Task<(bool ok, string? error)> CancelRejectionAsync(long arrivalId, string reason, string user)
    {
        reason = (reason ?? "").Trim();
        if (reason.Length < 5)
            return (false, "Give a reason for undoing the rejection.");
        if (reason.Length > 500) reason = reason[..500];

        var arrival = await GetAsync(arrivalId);
        if (arrival == null) return (false, "Arrival not found.");
        if (arrival.StatusCode != ArrivalStatus.Rejected)
            return (false, $"This arrival is {arrival.StatusCode}, not Rejected.");

        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();

        // The order first: if it cannot be cancelled the arrival must not move,
        // or the container ends up Draft with an orphaned closed claim order.
        await RejectionOrder.CancelAsync(c, tx, _audit, arrivalId, reason, user);

        var updated = await c.ExecuteAsync(@"
            UPDATE qms_arrival
            SET    status_code='Draft', rejected_at=NULL, rejected_by=NULL, reject_reason=NULL
            WHERE  arrival_id=@arrivalId AND status_code='Rejected'",
            new { arrivalId }, tx);
        if (updated != 1)
        {
            tx.Rollback();
            return (false, "This arrival was changed by someone else. Please refresh and try again.");
        }

        await c.ExecuteAsync(
            "UPDATE qms_shipment_snapshot SET status_code='Draft' WHERE arrival_id=@arrivalId",
            new { arrivalId }, tx);

        await c.ExecuteAsync(@"
            INSERT INTO qms_status_history
                (entity_type, entity_id, old_status, new_status, reason, changed_at, changed_by)
            VALUES ('Arrival', @arrivalId, 'Rejected', 'Draft', @reason, SYSUTCDATETIME(), @user)",
            new { arrivalId, reason, user }, tx);

        await _audit.WriteAsync(c, tx,
            EntityTypes.Arrival, arrivalId, ActionCodes.Updated,
            oldValues: new { status_code = "Rejected" },
            newValues: new { status_code = "Draft", reason },
            actor: user);

        tx.Commit();
        return (true, null);
    }

    public async Task<(bool ok, string? error)> CompleteAsync(long arrivalId, string user)
    {
        var arrival = await GetAsync(arrivalId);
        if (arrival == null) return (false, "Arrival not found.");
        if (arrival.StatusCode != ArrivalStatus.Draft)
            return (false, $"Arrival is already {arrival.StatusCode}; only Draft arrivals can be completed.");

        // Mandatory fields (admin-configured on the Arrival Field Rules page;
        // e.g. Unloading/Pull-out/Discharge dates by default) must have a value
        // before the arrival can be completed.
        var policies  = await GetArrivalFieldPoliciesAsync();
        var shipment  = await GetShipmentAsync(arrivalId);
        var checklist = await GetChecklistAsync(arrivalId);
        var missing = ArrivalFieldRegistry.All
            .Where(f => policies.TryGetValue(f.Key, out var p) && p.IsMandatory)
            .Where(f => !FieldHasValue(f.Key, shipment, checklist))
            .Select(f => f.Display)
            .ToList();
        if (missing.Count > 0)
            return (false, $"Cannot complete — these required fields are empty: {string.Join(", ", missing)}. " +
                           "Set them (or an admin can change the rule under Parameters → Arrival Field Rules).");

        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();
        // Guard on status_code='Draft' so a concurrent Complete (or a Reopen that
        // slipped in after our GetAsync) makes this affect 0 rows -> conflict,
        // rather than writing a second Draft->Completed history/audit pair.
        var completed = await c.ExecuteAsync(@"
            UPDATE qms_arrival SET status_code='Completed', completed_at=SYSUTCDATETIME(), completed_by=@user
            WHERE arrival_id=@arrivalId AND status_code='Draft'", new { arrivalId, user }, tx);
        if (completed != 1)
        {
            tx.Rollback();
            return (false, "This arrival was changed by someone else. Please refresh and try again.");
        }
        await c.ExecuteAsync(@"
            UPDATE qms_shipment_snapshot SET status_code='Confirmed' WHERE arrival_id=@arrivalId",
            new { arrivalId }, tx);
        await c.ExecuteAsync(@"
            INSERT INTO qms_status_history
                (entity_type, entity_id, old_status, new_status, changed_at, changed_by)
            VALUES ('Arrival', @arrivalId, 'Draft', 'Completed', SYSUTCDATETIME(), @user)",
            new { arrivalId, user }, tx);
        // T022 (US1) -- record arrival completion. No specific domain action
        // code defined for arrivals; using generic Updated with a clear field
        // diff that shows status_code: Draft -> Completed.
        await _audit.WriteAsync(c, tx,
            EntityTypes.Arrival, arrivalId, ActionCodes.Updated,
            oldValues: new { status_code = "Draft" },
            newValues: new { status_code = "Completed" },
            actor: user);
        tx.Commit();
        return (true, null);
    }

    public async Task<IReadOnlyDictionary<string, ArrivalFieldPolicy>> GetArrivalFieldPoliciesAsync()
    {
        using var c = Open();
        var stored = (await c.QueryAsync<ArrivalFieldPolicy>(
            "SELECT field_key FieldKey, is_mandatory IsMandatory, editable_when_closed EditableWhenClosed FROM qms_arrival_field_policy"))
            .ToDictionary(p => p.FieldKey, StringComparer.OrdinalIgnoreCase);

        // Every registry field gets an effective policy: the stored override
        // when present, else the code default (so a field with no row still
        // reflects the day-one seed, and new fields appear without a migration).
        var result = new Dictionary<string, ArrivalFieldPolicy>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in ArrivalFieldRegistry.All)
        {
            result[f.Key] = stored.TryGetValue(f.Key, out var p)
                ? p
                : new ArrivalFieldPolicy
                {
                    FieldKey           = f.Key,
                    IsMandatory        = ArrivalFieldRegistry.DefaultMandatory.Contains(f.Key),
                    EditableWhenClosed = ArrivalFieldRegistry.DefaultEditableWhenClosed.Contains(f.Key)
                };
        }
        return result;
    }

    public async Task SaveArrivalFieldPoliciesAsync(IEnumerable<ArrivalFieldPolicy> policies, string user)
    {
        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();
        foreach (var p in policies)
        {
            // Only keep real registry keys — ignore anything a crafted post adds.
            if (ArrivalFieldRegistry.Find(p.FieldKey) is null) continue;
            await c.ExecuteAsync(@"
                MERGE qms_arrival_field_policy AS t
                USING (SELECT @FieldKey AS field_key) AS s ON t.field_key = s.field_key
                WHEN MATCHED THEN UPDATE SET
                    is_mandatory = @IsMandatory, editable_when_closed = @EditableWhenClosed,
                    updated_at = SYSUTCDATETIME(), updated_by = @user
                WHEN NOT MATCHED THEN INSERT
                    (field_key, is_mandatory, editable_when_closed, updated_at, updated_by)
                    VALUES (@FieldKey, @IsMandatory, @EditableWhenClosed, SYSUTCDATETIME(), @user);",
                new { p.FieldKey, p.IsMandatory, p.EditableWhenClosed, user }, tx);
        }
        tx.Commit();
    }

    public async Task<(bool ok, string? error)> ReopenForEditAsync(long arrivalId, string user, string? reason)
    {
        var a = await GetAsync(arrivalId);
        if (a == null) return (false, "Arrival not found.");
        if (a.StatusCode != ArrivalStatus.Completed) return (false, "Only Completed arrivals can be re-opened for editing.");
        if (a.QualityOrderId.HasValue)
            return (false, $"Cannot edit: an active Quality Order ({a.QualityOrderNo}) already exists for this arrival. Cancel the QO first.");

        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();
        // Guard on status_code='Completed' so this can't race a concurrent
        // CompleteAsync/CreateForArrivalAsync and leave a QO attached to a Draft
        // (editable) arrival.
        var reopened = await c.ExecuteAsync(@"
            UPDATE qms_arrival SET status_code='Draft', completed_at=NULL, completed_by=NULL
            WHERE arrival_id=@arrivalId AND status_code='Completed'", new { arrivalId }, tx);
        if (reopened != 1)
        {
            tx.Rollback();
            return (false, "This arrival was changed by someone else. Please refresh and try again.");
        }
        await c.ExecuteAsync(@"
            UPDATE qms_shipment_snapshot SET status_code='Draft' WHERE arrival_id=@arrivalId",
            new { arrivalId }, tx);
        await c.ExecuteAsync(@"
            INSERT INTO qms_status_history (entity_type, entity_id, old_status, new_status, reason, changed_at, changed_by)
            VALUES ('Arrival', @arrivalId, 'Completed', 'Draft', @reason, SYSUTCDATETIME(), @user)",
            new { arrivalId, reason, user }, tx);
        // T022 (US1) -- arrival re-opened for editing.
        await _audit.WriteAsync(c, tx,
            EntityTypes.Arrival, arrivalId, ActionCodes.Reopened,
            oldValues: new { status_code = "Completed" },
            newValues: new { status_code = "Draft", reason },
            actor: user);
        tx.Commit();
        return (true, null);
    }

    public async Task<(bool ok, string? error)> DeleteAsync(long arrivalId, string user)
    {
        var a = await GetAsync(arrivalId);
        if (a == null) return (false, "Arrival not found.");
        // Named from the LIVE order, but tested against every non-cancelled one.
        // A reinspected container carries two: cancelling only the live order
        // would leave the superseded original behind, so a guard that looked at
        // the live order alone would clear and then fail at the foreign key.
        if (a.QualityOrderId.HasValue)
            return (false, a.PreviousQualityOrderId.HasValue
                ? $"Cannot delete: this container has been inspected twice ({a.QualityOrderNo} and {a.PreviousQualityOrderNo}). Both must be cancelled first."
                : $"Cannot delete: an active Quality Order ({a.QualityOrderNo}) exists for this arrival. Cancel the QO first.");

        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();

        // Cascade child rows manually (FKs are NO ACTION). Images detach by
        // owner_type+owner_id; physical files in /uploads/Arrival/{id}/ are
        // left on disk -- a separate retention job (plan §6.5) cleans those up.
        var checklistIds = (await c.QueryAsync<long>(
            "SELECT checklist_id FROM qms_arrival_checklist WHERE arrival_id=@arrivalId",
            new { arrivalId }, tx)).ToList();

        if (checklistIds.Count > 0)
        {
            await c.ExecuteAsync(@"
                DELETE FROM qms_image_link
                WHERE  owner_type='ArrivalChecklist' AND owner_id IN @ids",
                new { ids = checklistIds }, tx);
        }
        await c.ExecuteAsync(@"
            DELETE FROM qms_image_link
            WHERE  owner_type='Arrival' AND owner_id=@arrivalId",
            new { arrivalId }, tx);

        // Cancelled QOs still reference this arrival (the active-QO guard above
        // only blocks non-cancelled ones), so their subtree must be removed
        // first or the final arrival delete hits the QO->arrival FK and the whole
        // delete rolls back. Only cancelled QOs can be present at this point.
        var qoIds = (await c.QueryAsync<long>(
            "SELECT quality_order_id FROM qms_quality_order WHERE arrival_id=@arrivalId",
            new { arrivalId }, tx)).ToList();
        // The whole QO subtree now lives in one shared helper -- the SiteAdmin
        // "delete this quality order" action runs exactly the same code, so the
        // two paths can't drift on which child tables they remember.
        var qoDocPaths = await QoCascade.CollectDocumentPathsAsync(c, tx, qoIds);
        await QoCascade.DeleteAsync(c, tx, qoIds);

        // The arrival's own attachments (V39). Collect the paths first: after
        // the DELETE the rows are gone and the files would be unreachable.
        var arrivalDocPaths = (await c.QueryAsync<string>(
            "SELECT storage_path FROM qms_document WHERE owner_type='Arrival' AND owner_id=@arrivalId",
            new { arrivalId }, tx)).Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        await c.ExecuteAsync("DELETE FROM qms_document WHERE owner_type='Arrival' AND owner_id=@arrivalId", new { arrivalId }, tx);

        await c.ExecuteAsync("DELETE FROM qms_arrival_sap_snapshot  WHERE arrival_id=@arrivalId", new { arrivalId }, tx);
        await c.ExecuteAsync("DELETE FROM qms_arrival_item          WHERE arrival_id=@arrivalId", new { arrivalId }, tx);
        await c.ExecuteAsync("DELETE FROM qms_arrival_checklist     WHERE arrival_id=@arrivalId", new { arrivalId }, tx);
        await c.ExecuteAsync("DELETE FROM qms_shipment_snapshot     WHERE arrival_id=@arrivalId", new { arrivalId }, tx);

        await c.ExecuteAsync(@"
            INSERT INTO qms_status_history (entity_type, entity_id, old_status, new_status, reason, changed_at, changed_by)
            VALUES ('Arrival', @arrivalId, @prev, 'Deleted', 'Admin hard-delete', SYSUTCDATETIME(), @user)",
            new { arrivalId, prev = a.StatusCode, user }, tx);

        // T022 (US1) -- record hard-delete with full snapshot of the arrival
        // before it disappears (FR-014: audit survives the deletion).
        await _audit.WriteAsync(c, tx,
            EntityTypes.Arrival, arrivalId, ActionCodes.Deleted,
            oldValues: new
            {
                a.ArrivalNo, a.ContainerNo, a.BolNo, a.Ebeln,
                a.VendorNo, a.VendorName, status_code = a.StatusCode
            },
            newValues: null,
            actor: user);

        // Release the container back to the Pending queue: nothing else resets
        // has_arrival, so without this the container stays hidden after delete.
        await c.ExecuteAsync(
            "UPDATE qms_sap_container_cache SET has_arrival = 0, arrival_id = NULL WHERE arrival_id=@arrivalId",
            new { arrivalId }, tx);

        await c.ExecuteAsync("DELETE FROM qms_arrival WHERE arrival_id=@arrivalId", new { arrivalId }, tx);

        tx.Commit();

        // Files only after the commit -- if the transaction had rolled back,
        // deleting them first would leave rows pointing at nothing.
        _docs.DeleteFiles(arrivalDocPaths.Concat(qoDocPaths));
        return (true, null);
    }

    /// <summary>Whether an editable arrival field currently holds a value —
    /// the basis of the mandatory check. YesNo/Number/Date fields test null;
    /// text fields test whitespace. Unknown keys are treated as present.</summary>
    private static bool FieldHasValue(string key, ShipmentSnapshot? s, ArrivalChecklist? cl)
    {
        static bool T(string? v) => !string.IsNullOrWhiteSpace(v);
        return key switch
        {
            "discharge_date"                => s?.DischargeDate != null,
            "unloading_date"                => s?.UnloadingDate != null,
            "pullout_date"                  => s?.PullOutDate != null,
            "time_bar"                      => s?.TimeBar != null,
            "arrival_place"                 => T(s?.ArrivalPlace),
            "inspection_point"              => T(s?.InspectionPoint),
            "joint_survey"                  => s?.JointSurvey != null,
            "time_bar_exceeded"             => s?.TimeBarExceeded != null,
            "seal_no"                       => T(cl?.SealNo),
            "seal_intact"                   => cl?.SealIntact != null,
            "seal_matches_documents"        => cl?.SealMatchesDocuments != null,
            "external_damage_exists"        => cl?.ExternalDamageExists != null,
            "set_temperature"               => cl?.SetTemperature != null,
            "display_temperature"           => cl?.DisplayTemperature != null,
            "cargo_smell_normal"            => cl?.CargoSmellNormal != null,
            "visual_cargo_acceptable"       => cl?.VisualCargoAcceptable != null,
            "cargo_shifted_collapsed_water" => cl?.CargoShiftedCollapsedWater != null,
            "pulp_temp_front"               => cl?.PulpTempFront != null,
            "pulp_temp_middle"              => cl?.PulpTempMiddle != null,
            "pulp_temp_back"                => cl?.PulpTempBack != null,
            "data_logger_located"           => cl?.DataLoggerLocated != null,
            "data_logger_serial"            => T(cl?.DataLoggerSerial),
            "logger_handed_over"            => cl?.LoggerHandedOver != null,
            "logger_active_data_available"  => cl?.LoggerActiveDataAvailable != null,
            "logger_temperature"            => cl?.LoggerTemperature != null,
            "notes"                         => T(cl?.Notes),
            _                               => true
        };
    }

    private static string Sha256(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
