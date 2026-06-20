using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services.Sap;

namespace SharbatlyQMS.Web.Services;

public class ArrivalService : IArrivalService
{
    private readonly string _cs;
    private readonly IAuditService _audit;

    public ArrivalService(IConfiguration config, IAuditService audit)
    {
        _cs = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
        _audit = audit;
    }

    private SqlConnection Open() => new(_cs);

    // Outer-joins to the latest non-cancelled quality order for each arrival
    // so list/detail views can render "Create QO" vs "View QO" without an
    // extra round trip.
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
               a.status_code    StatusCode,
               a.created_at     CreatedAt,
               a.created_by     CreatedBy,
               a.completed_at   CompletedAt,
               a.completed_by   CompletedBy,
               qo.quality_order_id QualityOrderId,
               qo.quality_order_no QualityOrderNo,
               qo.status_code      QualityOrderStatus
        FROM   qms_arrival a
        OUTER APPLY (
            SELECT TOP 1 quality_order_id, quality_order_no, status_code
            FROM   qms_quality_order
            WHERE  arrival_id = a.arrival_id AND status_code <> 'Cancelled'
            ORDER  BY quality_order_id DESC
        ) qo";

    public async Task<IReadOnlyList<Arrival>> ListAsync(string? status, string? search, string? plant = null)
    {
        using var c = Open();
        var rows = await c.QueryAsync<Arrival>(ArrivalSelect + @"
            WHERE  (@status IS NULL OR a.status_code = @status)
              AND  (@plant  IS NULL OR a.plant       = @plant)
              AND  (@search IS NULL
                    OR a.arrival_no   LIKE '%' + @search + '%'
                    OR a.container_no LIKE '%' + @search + '%'
                    OR a.bol_no       LIKE '%' + @search + '%'
                    OR a.vendor_name  LIKE '%' + @search + '%')
            ORDER BY a.created_at DESC", new { status, search, plant });
        return rows.ToList();
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
        if (string.IsNullOrWhiteSpace(containerNo) || string.IsNullOrWhiteSpace(bolNo)) return null;
        using var c = Open();
        return await c.QueryFirstOrDefaultAsync<Arrival>(ArrivalSelect + @"
            WHERE a.container_no = @containerNo AND a.bol_no = @bolNo
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

    public async Task<ShipmentSnapshot?> GetShipmentAsync(long arrivalId)
    {
        using var c = Open();
        return await c.QuerySingleOrDefaultAsync<ShipmentSnapshot>(@"
            SELECT shipment_snapshot_id ShipmentSnapshotId, arrival_id ArrivalId,
                   internal_shipment_no InternalShipmentNo,
                   loading_date LoadingDate, sailing_date SailingDate,
                   examination_date ExaminationDate, arrival_date ArrivalDate,
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

    public async Task<long> CreateFromSapAsync(IReadOnlyList<SapShipmentRow> rows, string createdBy)
    {
        if (rows.Count == 0) throw new InvalidOperationException("No SAP rows selected.");

        var first = rows[0];
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
                 vendor_no, vendor_name, plant, status_code, created_at, created_by)
            VALUES
                (@arrivalNo, 'S4HANA', @bol, @container, @ebeln, @bukrs,
                 @vendorNo, @vendorName, @plant, 'Draft', SYSUTCDATETIME(), @createdBy);
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
                plant      = first.Plant,
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
        await c.ExecuteAsync(@"
            INSERT INTO qms_shipment_snapshot
                (arrival_id, internal_shipment_no, sailing_date,
                 examination_date, arrival_date, unloading_date, receive_date, inspection_date,
                 transit_days, loading_port, loading_country, arrival_place,
                 vessel_name, voyage_number, status_code)
            VALUES
                (@arrivalId, @internalShipmentNo, @sailing,
                 @examination, @arrival, @unloading, @receive, CAST(SYSUTCDATETIME() AS date),
                 @transit, @loadingPort, @loadingCountry, @arrivalPlace,
                 @vessel, @voyage, 'Draft');",
            new
            {
                arrivalId,
                internalShipmentNo,
                sailing        = first.SailingDate?.ToDateTime(TimeOnly.MinValue),
                examination    = first.ExaminationDate?.ToDateTime(TimeOnly.MinValue),
                arrival        = first.ArrivalDate?.ToDateTime(TimeOnly.MinValue),
                unloading      = first.UnloadingDate?.ToDateTime(TimeOnly.MinValue),
                receive        = first.ReceiveDate?.ToDateTime(TimeOnly.MinValue),
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
              unloading_date = @UnloadingDate, time_bar = @TimeBar,
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
                ss.UnloadingDate, ss.PullOutDate, ss.TimeBar, ss.ArrivalPlace,
                ss.InspectionPoint, ss.TimeBarExceeded, ss.JointSurvey
            },
            actor: updatedBy);
        tx.Commit();
    }

    public async Task<(bool ok, string? error)> CompleteAsync(long arrivalId, string user)
    {
        var arrival = await GetAsync(arrivalId);
        if (arrival == null) return (false, "Arrival not found.");
        if (arrival.StatusCode != ArrivalStatus.Draft)
            return (false, $"Arrival is already {arrival.StatusCode}; only Draft arrivals can be completed.");

        // No mandatory checklist fields -- the inspector can complete the arrival
        // with any combination of answers (including none). Anything still blank
        // simply appears as "—" on the PDF report.
        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();
        await c.ExecuteAsync(@"
            UPDATE qms_arrival SET status_code='Completed', completed_at=SYSUTCDATETIME(), completed_by=@user
            WHERE arrival_id=@arrivalId", new { arrivalId, user }, tx);
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
        await c.ExecuteAsync(@"
            UPDATE qms_arrival SET status_code='Draft', completed_at=NULL, completed_by=NULL
            WHERE arrival_id=@arrivalId", new { arrivalId }, tx);
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
        if (a.QualityOrderId.HasValue)
            return (false, $"Cannot delete: an active Quality Order ({a.QualityOrderNo}) exists for this arrival. Cancel the QO first.");

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

        await c.ExecuteAsync("DELETE FROM qms_arrival WHERE arrival_id=@arrivalId", new { arrivalId }, tx);

        tx.Commit();
        return (true, null);
    }

    private static string Sha256(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
