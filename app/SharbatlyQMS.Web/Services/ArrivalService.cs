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

    public ArrivalService(IConfiguration config)
    {
        _cs = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
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

    public async Task<IReadOnlyList<Arrival>> ListAsync(string? status, string? search)
    {
        using var c = Open();
        var rows = await c.QueryAsync<Arrival>(ArrivalSelect + @"
            WHERE  (@status IS NULL OR a.status_code = @status)
              AND  (@search IS NULL
                    OR a.arrival_no   LIKE '%' + @search + '%'
                    OR a.container_no LIKE '%' + @search + '%'
                    OR a.bol_no       LIKE '%' + @search + '%'
                    OR a.vendor_name  LIKE '%' + @search + '%')
            ORDER BY a.created_at DESC", new { status, search });
        return rows.ToList();
    }

    public async Task<Arrival?> GetAsync(long arrivalId)
    {
        using var c = Open();
        return await c.QuerySingleOrDefaultAsync<Arrival>(
            ArrivalSelect + " WHERE a.arrival_id = @arrivalId", new { arrivalId });
    }

    public async Task<Arrival?> FindByContainerAndBolAsync(string containerNo, string bolNo)
    {
        if (string.IsNullOrWhiteSpace(containerNo) || string.IsNullOrWhiteSpace(bolNo)) return null;
        using var c = Open();
        return await c.QuerySingleOrDefaultAsync<Arrival>(ArrivalSelect + @"
            WHERE a.container_no = @containerNo AND a.bol_no = @bolNo
            ORDER BY a.created_at DESC",
            new { containerNo, bolNo });
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
                          !string.Equals(r.BolNo,       first.BolNo,       StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "All selected rows must share the same container and BOL (plan §5.3 disambiguation).");
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
                 vendor_no, vendor_name, status_code, created_at, created_by)
            VALUES
                (@arrivalNo, 'S4HANA', @bol, @container, @ebeln, @bukrs,
                 @vendorNo, @vendorName, 'Draft', SYSUTCDATETIME(), @createdBy);
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
                createdBy
            }, tx);

        foreach (var r in rows)
        {
            await c.ExecuteAsync(@"
                INSERT INTO qms_arrival_item
                    (arrival_id, ebeln, ebelp, material_no, material_desc,
                     plant, storage_location, batch_no, quantity, uom,
                     material_group, material_group_desc, major_category)
                VALUES
                    (@arrivalId, @ebeln, @ebelp, @materialNo, @materialDesc,
                     @plant, @storageLocation, @batchNo, @quantity, @uom,
                     @materialGroup, @materialGroupDesc, @majorCategory);",
                new
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
                }, tx);
        }

        var shipmentSeq = await c.ExecuteScalarAsync<int>(
            "SELECT NEXT VALUE FOR seq_qms_shipment_no", transaction: tx);
        var internalShipmentNo = $"SHP-{DateTime.UtcNow:yyyy}-{shipmentSeq:D6}";

        await c.ExecuteAsync(@"
            INSERT INTO qms_shipment_snapshot
                (arrival_id, internal_shipment_no, loading_date, sailing_date,
                 examination_date, arrival_date, unloading_date,
                 transit_days, loading_port, loading_country, arrival_place,
                 vessel_name, voyage_number, status_code)
            VALUES
                (@arrivalId, @internalShipmentNo, @loading, @sailing,
                 @examination, @arrival, @unloading,
                 @transit, @loadingPort, @loadingCountry, @arrivalPlace,
                 @vessel, @voyage, 'Draft');",
            new
            {
                arrivalId,
                internalShipmentNo,
                loading        = first.LoadingDate?.ToDateTime(TimeOnly.MinValue),
                sailing        = first.SailingDate?.ToDateTime(TimeOnly.MinValue),
                examination    = first.ExaminationDate?.ToDateTime(TimeOnly.MinValue),
                arrival        = first.ArrivalDate?.ToDateTime(TimeOnly.MinValue),
                unloading      = first.UnloadingDate?.ToDateTime(TimeOnly.MinValue),
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
                @arrivalId, @sealNo, @vendor,
                0, 0, 0,
                0, 0, 0,
                0, 0, 0,
                0,
                0, 0, 0,
                0, 0, 0,
                0, 0);",
            new { arrivalId, sealNo = first.SealNo, vendor = first.VendorName }, tx);

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

        tx.Commit();
        return arrivalId;
    }

    public async Task SaveChecklistAsync(ArrivalChecklist cl, string updatedBy)
    {
        using var c = Open();
        await c.ExecuteAsync(@"
            UPDATE qms_arrival_checklist SET
              seal_no = @SealNo, carrier_name = @CarrierName,
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
                cl.ArrivalId, cl.SealNo, cl.CarrierName,
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
            });
    }

    public async Task SaveShipmentAsync(ShipmentSnapshot ss, string updatedBy)
    {
        using var c = Open();
        await c.ExecuteAsync(@"
            UPDATE qms_shipment_snapshot SET
              loading_date = @LoadingDate, sailing_date = @SailingDate,
              examination_date = @ExaminationDate, arrival_date = @ArrivalDate,
              unloading_date = @UnloadingDate, inspection_date = @InspectionDate,
              transit_days = @TransitDays, time_bar = @TimeBar,
              loading_port = @LoadingPort, loading_country = @LoadingCountry,
              arrival_place = @ArrivalPlace, vessel_name = @VesselName, voyage_number = @VoyageNumber,
              pullout_date = @PullOutDate, receive_date = @ReceiveDate,
              time_bar_exceeded = @TimeBarExceeded, inspection_point = @InspectionPoint,
              joint_survey = @JointSurvey
            WHERE arrival_id = @ArrivalId", ss);
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
