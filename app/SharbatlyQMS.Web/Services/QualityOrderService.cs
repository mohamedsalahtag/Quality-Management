using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services;

public class QualityOrderService : IQualityOrderService
{
    private readonly string _cs;
    private readonly IAuditService _audit;
    public QualityOrderService(IConfiguration config, IAuditService audit)
    {
        _cs = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
        _audit = audit;
    }
    private SqlConnection Open() => new(_cs);

    // Joined view across qms_quality_order + qms_arrival so QO list/detail
    // pages can show container, BOL, vendor and arrival number alongside QO
    // identity without a second round trip.
    private const string QoSelect = @"
        SELECT qo.quality_order_id  QualityOrderId,
               qo.quality_order_no  QualityOrderNo,
               qo.arrival_id        ArrivalId,
               qo.status_code       StatusCode,
               qo.opened_at         OpenedAt,    qo.opened_by    OpenedBy,
               qo.closed_at         ClosedAt,    qo.closed_by    ClosedBy,
               qo.close_reason      CloseReason,
               qo.reopened_at       ReopenedAt,  qo.reopened_by  ReopenedBy,
               qo.reopen_reason     ReopenReason,
               qo.created_at        CreatedAt,   qo.created_by   CreatedBy,
               a.container_no       ContainerNo,
               a.bol_no             BolNo,
               a.ebeln              Ebeln,
               a.vendor_name        VendorName,
               a.arrival_no         ArrivalNo
        FROM   qms_quality_order qo
        LEFT   JOIN qms_arrival  a ON a.arrival_id = qo.arrival_id";

    public async Task<IReadOnlyList<QualityOrder>> ListAsync(string? status, string? search)
    {
        using var c = Open();
        var rows = await c.QueryAsync<QualityOrder>(QoSelect + @"
            WHERE  (@status IS NULL OR qo.status_code = @status)
              AND  (@search IS NULL OR
                    qo.quality_order_no LIKE '%' + @search + '%' OR
                    a.container_no      LIKE '%' + @search + '%' OR
                    a.bol_no            LIKE '%' + @search + '%' OR
                    a.ebeln             LIKE '%' + @search + '%' OR
                    a.vendor_name       LIKE '%' + @search + '%' OR
                    qo.created_by       LIKE '%' + @search + '%')
            ORDER BY qo.created_at DESC", new { status, search });
        return rows.ToList();
    }

    /// <summary>
    /// Returns a QO by id with no caller-scope check. QO visibility is
    /// intentionally org-wide: Viewer / Operator / Manager / ClaimManager /
    /// SiteAdmin all need to read every QO (Operator records work,
    /// ClaimManager approves claims tied to a QO, Manager / SiteAdmin
    /// oversee). The QualityOrdersController class-level [Authorize] gate
    /// + AuditContextActionFilter (which logs every read) are the only
    /// row-level controls; this method does not need its own.
    /// </summary>
    public async Task<QualityOrder?> GetAsync(long qualityOrderId)
    {
        using var c = Open();
        return await c.QuerySingleOrDefaultAsync<QualityOrder>(
            QoSelect + " WHERE qo.quality_order_id = @qualityOrderId", new { qualityOrderId });
    }

    public async Task<QualityOrder?> GetByArrivalAsync(long arrivalId)
    {
        using var c = Open();
        return await c.QuerySingleOrDefaultAsync<QualityOrder>(
            QoSelect + " WHERE qo.arrival_id = @arrivalId AND qo.status_code <> 'Cancelled'", new { arrivalId });
    }

    public async Task<IReadOnlyList<QualityOrderMaterial>> GetMaterialsAsync(long qualityOrderId)
    {
        using var c = Open();
        // Joined to qms_arrival_item to surface ai.quantity + ai.uom on the
        // QO material -- previously the QO PDF Material Table rendered these
        // columns as empty strings because the QO model didn't carry the
        // values. ai.arrival_item_id is the parent FK already on the QO
        // material, so this is a single-row INNER JOIN per material.
        var rows = await c.QueryAsync<QualityOrderMaterial>(@"
            SELECT m.qo_material_id          QoMaterialId,
                   m.quality_order_id        QualityOrderId,
                   m.arrival_item_id         ArrivalItemId,
                   m.material_no             MaterialNo,
                   m.material_desc           MaterialDesc,
                   m.origin                  Origin,
                   m.variety                 Variety,
                   m.material_class          MaterialClass,
                   m.net_weight              NetWeight,
                   m.material_size           MaterialSize,
                   m.material_group          MaterialGroup,
                   m.material_group_desc     MaterialGroupDesc,
                   m.major_category          MajorCategory,
                   m.brand                   Brand,
                   m.pack_type               PackType,
                   ai.quantity               Quantity,
                   ai.uom                    Uom,
                   m.size_overridden         SizeOverridden,
                   m.original_material_size  OriginalMaterialSize,
                   m.override_material_size  OverrideMaterialSize,
                   m.override_reason         OverrideReason,
                   m.override_approved_by    OverrideApprovedBy,
                   m.override_approved_at    OverrideApprovedAt,
                   m.sample_size             SampleSize
            FROM   qms_quality_order_material m
            JOIN   qms_arrival_item ai ON ai.arrival_item_id = m.arrival_item_id
            WHERE  m.quality_order_id = @qualityOrderId
            ORDER  BY m.qo_material_id", new { qualityOrderId });
        return rows.ToList();
    }

    public async Task<long?> GetQoIdForMaterialAsync(long qoMaterialId)
    {
        using var c = Open();
        return await c.ExecuteScalarAsync<long?>(
            "SELECT quality_order_id FROM qms_quality_order_material WHERE qo_material_id = @qoMaterialId",
            new { qoMaterialId });
    }

    public async Task<long> CreateForArrivalAsync(long arrivalId, string user)
    {
        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();

        var arrival = await c.QuerySingleAsync<(long Id, string Status)>(
            "SELECT arrival_id, status_code FROM qms_arrival WHERE arrival_id=@arrivalId",
            new { arrivalId }, tx);
        if (arrival.Status != ArrivalStatus.Completed)
            throw new InvalidOperationException($"Arrival must be Completed (currently {arrival.Status}).");

        var existing = await c.QuerySingleOrDefaultAsync<long?>(
            "SELECT quality_order_id FROM qms_quality_order WHERE arrival_id=@arrivalId AND status_code <> 'Cancelled'",
            new { arrivalId }, tx);
        if (existing.HasValue)
            throw new InvalidOperationException($"Arrival already has an active Quality Order (#{existing.Value}).");

        var seq = await c.ExecuteScalarAsync<int>(
            "SELECT NEXT VALUE FOR seq_qms_quality_order_no", transaction: tx);
        var qoNo = $"QO-{DateTime.UtcNow:yyyy}-{seq:D6}";

        var qoId = await c.ExecuteScalarAsync<long>(@"
            INSERT INTO qms_quality_order
                (quality_order_no, arrival_id, status_code, created_at, created_by)
            VALUES (@qoNo, @arrivalId, 'Initial', SYSUTCDATETIME(), @user);
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
            new { qoNo, arrivalId, user }, tx);

        // Copy arrival items into qo_material with full snapshot.
        await c.ExecuteAsync(@"
            INSERT INTO qms_quality_order_material
                (quality_order_id, arrival_item_id, material_no, material_desc,
                 material_group, material_group_desc, major_category)
            SELECT @qoId, ai.arrival_item_id, ai.material_no, ai.material_desc,
                   ai.material_group, ai.material_group_desc, ai.major_category
            FROM   qms_arrival_item ai
            WHERE  ai.arrival_id = @arrivalId;",
            new { qoId, arrivalId }, tx);

        await c.ExecuteAsync(@"
            INSERT INTO qms_status_history (entity_type, entity_id, old_status, new_status, changed_at, changed_by)
            VALUES ('QualityOrder', @qoId, NULL, 'Initial', SYSUTCDATETIME(), @user)",
            new { qoId, user }, tx);

        // T019 (US1) -- Audit trail: record the QO creation, including the
        // generated QO number + arrival pointer so the audit entry survives
        // a future arrival delete (FR-014).
        await _audit.WriteAsync(c, tx,
            EntityTypes.QualityOrder, qoId, ActionCodes.Created,
            oldValues: null,
            newValues: new { quality_order_no = qoNo, arrival_id = arrivalId, status_code = "Initial" },
            actor: user);

        tx.Commit();
        return qoId;
    }

    public Task<(bool ok, string? error)> OpenAsync(long qoId, string user)   => Transition(qoId, user, null, "Open",     new[] { "Initial" });
    public Task<(bool ok, string? error)> CloseAsync(long qoId, string user, string? reason)  => Transition(qoId, user, reason, "Closed",   new[] { "Open" });
    // Reopen folds back into Open -- the user explicitly didn't want a
    // separate "Reopened" status. Audit columns (reopened_at / by /
    // reason) still record that the QO was re-opened.
    public Task<(bool ok, string? error)> ReopenAsync(long qoId, string user, string? reason) => Transition(qoId, user, reason, "Reopened", new[] { "Closed" });
    public Task<(bool ok, string? error)> CancelAsync(long qoId, string user, string? reason) => Transition(qoId, user, reason, "Cancelled",new[] { "Initial", "Open" });

    private async Task<(bool ok, string? error)> Transition(long qoId, string user, string? reason, string toStatus, string[] fromStatuses)
    {
        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();
        var current = await c.QuerySingleOrDefaultAsync<string?>(
            "SELECT status_code FROM qms_quality_order WHERE quality_order_id=@qoId",
            new { qoId }, tx);
        if (current == null) return (false, "Quality Order not found.");
        if (!fromStatuses.Contains(current))
            return (false, $"Cannot transition from {current} to {toStatus}.");

        // Finishing (Closed) is now a one-click confirm -- no reason is
        // required. Reopen / Cancel still demand a justification so the
        // audit trail explains why a finished QO was unblocked.
        if ((toStatus == "Reopened" || toStatus == "Cancelled") && string.IsNullOrWhiteSpace(reason))
            return (false, $"Reason is required to {toStatus.ToLowerInvariant()}.");

        // Reopen physically lands as 'Open' so there's only one
        // editable state, but the reopened_* audit fields still get
        // stamped so we know it was re-opened (and the status history
        // row below records the Closed -> Reopened transition).
        var sql = toStatus switch
        {
            "Open"      => "UPDATE qms_quality_order SET status_code='Open',     opened_at=SYSUTCDATETIME(), opened_by=@user WHERE quality_order_id=@qoId",
            "Closed"    => "UPDATE qms_quality_order SET status_code='Closed',   closed_at=SYSUTCDATETIME(), closed_by=@user, close_reason=@reason WHERE quality_order_id=@qoId",
            "Reopened"  => "UPDATE qms_quality_order SET status_code='Open',     reopened_at=SYSUTCDATETIME(), reopened_by=@user, reopen_reason=@reason WHERE quality_order_id=@qoId",
            "Cancelled" => "UPDATE qms_quality_order SET status_code='Cancelled' WHERE quality_order_id=@qoId",
            _ => throw new InvalidOperationException("Unknown target status.")
        };
        await c.ExecuteAsync(sql, new { qoId, user, reason }, tx);
        await c.ExecuteAsync(@"
            INSERT INTO qms_status_history (entity_type, entity_id, old_status, new_status, reason, changed_at, changed_by)
            VALUES ('QualityOrder', @qoId, @current, @toStatus, @reason, SYSUTCDATETIME(), @user)",
            new { qoId, current, toStatus, reason, user }, tx);

        // T018 (US1) -- Audit trail: capture the status transition as one
        // domain action (Opened / Closed / Reopened / Cancelled) rather than
        // a generic Updated, per FR-002 and the 2026-05-20 clarification on
        // domain action label preservation.
        var auditAction = toStatus switch
        {
            "Open"      => ActionCodes.Opened,
            "Closed"    => ActionCodes.Closed,
            "Reopened"  => ActionCodes.Reopened,
            "Cancelled" => ActionCodes.Cancelled,
            _           => ActionCodes.Updated
        };
        await _audit.WriteAsync(c, tx,
            EntityTypes.QualityOrder, qoId, auditAction,
            oldValues: new { status_code = current },
            newValues: new { status_code = toStatus == "Reopened" ? "Open" : toStatus, reason },
            actor: user);

        tx.Commit();
        return (true, null);
    }

    public async Task SaveOverrideAsync(long qoMaterialId, string newSize, string? reason, string user)
    {
        // PH-1.1 + PH-2.1 (2026-05-20): wrap UPDATE + audit-write in a single
        // transaction so a failure of either rolls both back. Replaces the
        // previous inline INSERT INTO qms_audit_log with the unified
        // IAuditService write path.
        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();
        await c.ExecuteAsync(@"
            UPDATE qms_quality_order_material SET
              size_overridden        = 1,
              original_material_size = ISNULL(original_material_size, material_size),
              override_material_size = @newSize,
              material_size          = @newSize,
              override_reason        = @reason,
              override_approved_by   = @user,
              override_approved_at   = SYSUTCDATETIME()
            WHERE qo_material_id = @qoMaterialId",
            new { qoMaterialId, newSize, reason = (object?)reason ?? DBNull.Value, user },
            transaction: tx);
        await _audit.WriteAsync(c, tx,
            EntityTypes.QualityOrderMaterial, qoMaterialId, ActionCodes.Override,
            oldValues: null,
            newValues: new { newSize, reason = reason ?? "" },
            actor: user);
        tx.Commit();
    }

    public async Task ClearOverrideAsync(long qoMaterialId, string user)
    {
        // PH-1.2 + PH-2.2 (2026-05-20): wrap UPDATE + audit-write in one
        // transaction. Restore the original size (if one was captured) and
        // clear all override metadata so the material line looks like it
        // never had one. Audit log keeps a trail for compliance.
        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();
        await c.ExecuteAsync(@"
            UPDATE qms_quality_order_material SET
              material_size          = ISNULL(original_material_size, material_size),
              size_overridden        = 0,
              original_material_size = NULL,
              override_material_size = NULL,
              override_reason        = NULL,
              override_approved_by   = NULL,
              override_approved_at   = NULL
            WHERE qo_material_id = @qoMaterialId",
            new { qoMaterialId },
            transaction: tx);
        await _audit.WriteAsync(c, tx,
            EntityTypes.QualityOrderMaterial, qoMaterialId, ActionCodes.OverrideCleared,
            oldValues: null, newValues: null, actor: user);
        tx.Commit();
    }

    // ---- Samples ----
    private const string SampleSelect = @"
        SELECT sample_id          SampleId,
               quality_order_id   QualityOrderId,
               qo_material_id     QoMaterialId,
               sample_no          SampleNo,
               carton_count       CartonCount,
               carton_identifier  CartonIdentifier,
               sample_scope       SampleScope,
               sample_size        SampleSize,
               size_overridden    SizeOverridden,
               grower             Grower,
               pallet_no          PalletNo,
               grower_pallet      GrowerPallet,
               pack_code          PackCode,
               date_code          DateCode,
               label_value        LabelValue,
               lot_no             LotNo,
               packaging_material PackagingMaterial,
               created_at         CreatedAt,  created_by  CreatedBy,
               updated_at         UpdatedAt,  updated_by  UpdatedBy
        FROM   qms_sample";

    public async Task<IReadOnlyList<Sample>> ListSamplesAsync(long qualityOrderId)
    {
        using var c = Open();
        var rows = await c.QueryAsync<Sample>(SampleSelect + @"
            WHERE quality_order_id = @qualityOrderId AND is_deleted = 0
            ORDER BY qo_material_id, sample_no", new { qualityOrderId });
        return rows.ToList();
    }

    public async Task<Sample?> GetSampleAsync(long sampleId)
    {
        using var c = Open();
        return await c.QuerySingleOrDefaultAsync<Sample>(
            SampleSelect + " WHERE sample_id = @sampleId AND is_deleted = 0", new { sampleId });
    }

    public async Task<long> CreateSampleAsync(Sample s)
    {
        // T020 (US1) -- wrap in tx so the audit INSERT lands atomically.
        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();
        // Auto-number per material.
        var nextNo = await c.ExecuteScalarAsync<int>(
            "SELECT ISNULL(MAX(sample_no), 0) + 1 FROM qms_sample WHERE qo_material_id = @QoMaterialId AND is_deleted = 0",
            new { s.QoMaterialId }, tx);
        s.SampleNo = nextNo;
        // Inherit sample_size from the material (source of truth). The sample
        // form no longer collects it, so s.SampleSize arrives null and we copy
        // the material's value into the per-sample inherited cache.
        if (!s.SampleSize.HasValue)
            s.SampleSize = await c.ExecuteScalarAsync<short?>(
                "SELECT sample_size FROM qms_quality_order_material WHERE qo_material_id = @QoMaterialId",
                new { s.QoMaterialId }, tx);
        var sampleId = await c.ExecuteScalarAsync<long>(@"
            INSERT INTO qms_sample
                (quality_order_id, qo_material_id, sample_no, carton_count, carton_identifier,
                 sample_scope, sample_size, size_overridden,
                 grower, pallet_no, grower_pallet, pack_code,
                 date_code, label_value, lot_no, packaging_material,
                 created_at, created_by)
            VALUES
                (@QualityOrderId, @QoMaterialId, @SampleNo, @CartonCount, @CartonIdentifier,
                 @SampleScope, @SampleSize, @SizeOverridden,
                 @Grower, @PalletNo, @GrowerPallet, @PackCode,
                 @DateCode, @LabelValue, @LotNo, @PackagingMaterial,
                 SYSUTCDATETIME(), @CreatedBy);
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);", s, tx);
        s.SampleId = sampleId;
        // Copy the material-scoped header values down onto the new sample so it
        // owns its own copy (same model as the material-save propagation).
        await c.ExecuteAsync(@"
            INSERT INTO qms_sample_header_value (sample_id, field_id, text_value, numeric_value, date_value)
            SELECT @sampleId, v.field_id, v.text_value, v.numeric_value, v.date_value
            FROM   qms_qo_material_header_value v
            WHERE  v.qo_material_id = @QoMaterialId", new { sampleId, s.QoMaterialId }, tx);
        await _audit.WriteAsync(c, tx,
            EntityTypes.Sample, sampleId, ActionCodes.Created,
            oldValues: null,
            newValues: new
            {
                s.QualityOrderId, s.QoMaterialId, s.SampleNo, s.CartonCount, s.CartonIdentifier,
                s.SampleScope, s.SampleSize, s.Grower, s.PalletNo, s.GrowerPallet, s.PackCode,
                s.DateCode, s.LabelValue, s.LotNo, s.PackagingMaterial
            },
            actor: s.CreatedBy);
        tx.Commit();
        return sampleId;
    }

    public async Task UpdateSampleAsync(Sample s)
    {
        // 2026-06-13: sample_size is now editable per-sample (V24). The form
        // posts the user's value and SizeOverridden flag; if SizeOverridden=1
        // the material-level propagation UPDATE skips this row. Legacy text
        // columns (grower/pallet/date_code/...) are still owned by the
        // qms_sample_header_value path -- we don't touch them here.
        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();
        await c.ExecuteAsync(@"
            UPDATE qms_sample SET
              sample_size     = @SampleSize,
              size_overridden = @SizeOverridden,
              updated_at      = SYSUTCDATETIME(),
              updated_by      = @UpdatedBy
            WHERE sample_id = @SampleId", s, tx);
        await _audit.WriteAsync(c, tx,
            EntityTypes.Sample, s.SampleId, ActionCodes.Updated,
            oldValues: null,
            newValues: new { touched = true },
            actor: s.UpdatedBy ?? "system");
        tx.Commit();
    }

    public async Task SoftDeleteSampleAsync(long sampleId, string user)
    {
        // T020 (US1) -- soft-delete logged as Deleted in the audit trail.
        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();
        var oldRow = await c.QuerySingleOrDefaultAsync(@"
            SELECT sample_no, qo_material_id, carton_identifier, is_deleted
            FROM   qms_sample WHERE sample_id = @sampleId",
            new { sampleId }, tx);
        await c.ExecuteAsync(@"
            UPDATE qms_sample SET is_deleted = 1, deleted_at = SYSUTCDATETIME(), deleted_by = @user
            WHERE sample_id = @sampleId", new { sampleId, user }, tx);
        await _audit.WriteAsync(c, tx,
            EntityTypes.Sample, sampleId, ActionCodes.Deleted,
            oldValues: oldRow,
            newValues: null,
            actor: user);
        tx.Commit();
    }

    // ---- Readings ----
    public async Task<IReadOnlyList<SampleReading>> GetReadingsAsync(long sampleId)
    {
        using var c = Open();
        var rows = await c.QueryAsync<SampleReading>(@"
            SELECT r.reading_id     ReadingId,
                   r.sample_id      SampleId,
                   r.reading_type_code ReadingTypeCode,
                   r.numeric_value  NumericValue,
                   r.text_value     TextValue,
                   r.unit_code      UnitCode,
                   r.is_within_spec IsWithinSpec,
                   r.reading_sequence ReadingSequence,
                   rt.reading_name  ReadingName,
                   rt.value_kind    ValueKind
            FROM   qms_sample_reading r
            JOIN   qms_reading_type   rt ON rt.reading_type_code = r.reading_type_code
            WHERE  r.sample_id = @sampleId
            ORDER  BY rt.sort_order", new { sampleId });
        return rows.ToList();
    }

    public async Task<ILookup<long, SampleReading>> GetReadingsBatchAsync(IEnumerable<long> sampleIds)
    {
        var ids = sampleIds.Distinct().ToArray();
        if (ids.Length == 0) return Array.Empty<SampleReading>().ToLookup(r => r.SampleId);
        using var c = Open();
        var rows = await c.QueryAsync<SampleReading>(@"
            SELECT r.reading_id     ReadingId,
                   r.sample_id      SampleId,
                   r.reading_type_code ReadingTypeCode,
                   r.numeric_value  NumericValue,
                   r.text_value     TextValue,
                   r.unit_code      UnitCode,
                   r.is_within_spec IsWithinSpec,
                   r.reading_sequence ReadingSequence,
                   rt.reading_name  ReadingName,
                   rt.value_kind    ValueKind
            FROM   qms_sample_reading r
            JOIN   qms_reading_type   rt ON rt.reading_type_code = r.reading_type_code
            WHERE  r.sample_id IN @ids
            ORDER  BY r.sample_id, rt.sort_order", new { ids });
        return rows.ToLookup(r => r.SampleId);
    }

    public async Task SaveReadingsAsync(long sampleId, IEnumerable<SampleReading> readings, string user)
    {
        // One DELETE + one multi-row INSERT instead of a per-row loop. Dapper
        // batches IEnumerable parameters automatically when used with VALUES.
        var rows = readings
            .Where(r => r.NumericValue != null || !string.IsNullOrWhiteSpace(r.TextValue))
            .Select(r => new
            {
                sampleId,
                r.ReadingTypeCode,
                r.NumericValue,
                r.TextValue,
                r.UnitCode,
                r.IsWithinSpec,
                r.ReadingSequence,
                user
            })
            .ToArray();
        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();
        // T020 (US1) -- capture the OLD reading set as JSON for the audit diff,
        // then DELETE + INSERT, then write one Updated entry for the sample's
        // readings collection.
        var oldReadings = (await c.QueryAsync(@"
            SELECT reading_type_code, numeric_value, text_value, unit_code, is_within_spec
            FROM   qms_sample_reading WHERE sample_id = @sampleId
            ORDER  BY reading_sequence",
            new { sampleId }, tx)).ToList();
        await c.ExecuteAsync("DELETE FROM qms_sample_reading WHERE sample_id = @sampleId",
            new { sampleId }, tx);
        if (rows.Length > 0)
        {
            await c.ExecuteAsync(@"
                INSERT INTO qms_sample_reading
                    (sample_id, reading_type_code, numeric_value, text_value,
                     unit_code, is_within_spec, reading_sequence, created_at, created_by)
                VALUES
                    (@sampleId, @ReadingTypeCode, @NumericValue, @TextValue,
                     @UnitCode, @IsWithinSpec, @ReadingSequence, SYSUTCDATETIME(), @user)",
                rows, tx);
        }
        await _audit.WriteAsync(c, tx,
            EntityTypes.SampleReading, sampleId, ActionCodes.Updated,
            oldValues: new { count = oldReadings.Count, readings = oldReadings },
            newValues: new { count = rows.Length,
                              readings = rows.Select(r => new { r.ReadingTypeCode, r.NumericValue, r.TextValue, r.UnitCode, r.IsWithinSpec }) },
            actor: user);
        tx.Commit();
    }

    // ---- Defects ----
    public async Task<IReadOnlyList<SampleDefect>> GetDefectsAsync(long sampleId)
    {
        using var c = Open();
        var rows = await c.QueryAsync<SampleDefect>(@"
            SELECT d.sample_defect_id   SampleDefectId,
                   d.sample_id          SampleId,
                   d.defect_id          DefectId,
                   d.defect_value       DefectValue,
                   d.defect_percentage  DefectPercentage,
                   d.severity_code      SeverityCode,
                   d.comment            Comment,
                   d.is_within_tolerance IsWithinTolerance,
                   dc.defect_code       DefectCode,
                   dc.defect_name       DefectName,
                   dc.defect_category   DefectCategory
            FROM   qms_sample_defect d
            JOIN   qms_defect_catalog dc ON dc.defect_id = d.defect_id
            WHERE  d.sample_id = @sampleId
            ORDER  BY dc.sort_order", new { sampleId });
        return rows.ToList();
    }

    public async Task<ILookup<long, SampleDefect>> GetDefectsBatchAsync(IEnumerable<long> sampleIds)
    {
        var ids = sampleIds.Distinct().ToArray();
        if (ids.Length == 0) return Array.Empty<SampleDefect>().ToLookup(d => d.SampleId);
        using var c = Open();
        var rows = await c.QueryAsync<SampleDefect>(@"
            SELECT d.sample_defect_id   SampleDefectId,
                   d.sample_id          SampleId,
                   d.defect_id          DefectId,
                   d.defect_value       DefectValue,
                   d.defect_percentage  DefectPercentage,
                   d.severity_code      SeverityCode,
                   d.comment            Comment,
                   d.is_within_tolerance IsWithinTolerance,
                   dc.defect_code       DefectCode,
                   dc.defect_name       DefectName,
                   dc.defect_category   DefectCategory
            FROM   qms_sample_defect d
            JOIN   qms_defect_catalog dc ON dc.defect_id = d.defect_id
            WHERE  d.sample_id IN @ids
            ORDER  BY d.sample_id, dc.sort_order", new { ids });
        return rows.ToLookup(d => d.SampleId);
    }

    public async Task SaveDefectsAsync(long sampleId, IEnumerable<SampleDefect> defects, string user)
    {
        var rows = defects
            .Where(d => d.DefectValue != null || d.DefectPercentage != null)
            .Select(d => new
            {
                sampleId,
                d.DefectId,
                d.DefectValue,
                d.DefectPercentage,
                d.SeverityCode,
                d.Comment,
                d.IsWithinTolerance,
                user
            })
            .ToArray();
        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();
        // T020 (US1) -- same batched-diff pattern as SaveReadingsAsync.
        var oldDefects = (await c.QueryAsync(@"
            SELECT defect_id, defect_value, defect_percentage, severity_code, comment, is_within_tolerance
            FROM   qms_sample_defect WHERE sample_id = @sampleId",
            new { sampleId }, tx)).ToList();
        await c.ExecuteAsync("DELETE FROM qms_sample_defect WHERE sample_id = @sampleId",
            new { sampleId }, tx);
        if (rows.Length > 0)
        {
            await c.ExecuteAsync(@"
                INSERT INTO qms_sample_defect
                    (sample_id, defect_id, defect_value, defect_percentage,
                     severity_code, comment, is_within_tolerance, created_at, created_by)
                VALUES
                    (@sampleId, @DefectId, @DefectValue, @DefectPercentage,
                     @SeverityCode, @Comment, @IsWithinTolerance, SYSUTCDATETIME(), @user)",
                rows, tx);
        }
        await _audit.WriteAsync(c, tx,
            EntityTypes.SampleDefect, sampleId, ActionCodes.Updated,
            oldValues: new { count = oldDefects.Count, defects = oldDefects },
            newValues: new { count = rows.Length,
                              defects = rows.Select(d => new { d.DefectId, d.DefectValue, d.DefectPercentage, d.SeverityCode, d.Comment, d.IsWithinTolerance }) },
            actor: user);
        tx.Commit();
    }

    // ---- Catalogs ----
    public async Task<IReadOnlyList<ReadingTypeEntry>> GetActiveReadingTypesAsync()
    {
        using var c = Open();
        var rows = await c.QueryAsync<ReadingTypeEntry>(@"
            SELECT reading_type_id ReadingTypeId, reading_type_code ReadingTypeCode,
                   reading_name ReadingName, value_kind ValueKind,
                   default_unit DefaultUnit, is_active IsActive, sort_order SortOrder,
                   ISNULL(material_group, '') MaterialGroup, is_mandatory IsMandatory,
                   display_mode DisplayMode
            FROM   qms_reading_type WHERE is_active = 1
            ORDER  BY CASE WHEN material_group IS NULL THEN 0 ELSE 1 END,
                      material_group, sort_order, reading_name");
        return rows.ToList();
    }

    /// <summary>
    /// Reading types applicable to one material group: per-group rows
    /// plus globals (`material_group IS NULL`, V19+). The sample form
    /// uses this so a global reading like TARA defined once shows up on
    /// every fruit's sample form without duplicating the catalog row.
    /// </summary>
    public async Task<IReadOnlyList<ReadingTypeEntry>> GetActiveReadingTypesForGroupAsync(string? materialGroup)
    {
        if (string.IsNullOrWhiteSpace(materialGroup))
            return Array.Empty<ReadingTypeEntry>();
        using var c = Open();
        var rows = await c.QueryAsync<ReadingTypeEntry>(@"
            SELECT reading_type_id ReadingTypeId, reading_type_code ReadingTypeCode,
                   reading_name ReadingName, value_kind ValueKind,
                   default_unit DefaultUnit, is_active IsActive, sort_order SortOrder,
                   ISNULL(material_group, '') MaterialGroup, is_mandatory IsMandatory,
                   display_mode DisplayMode
            FROM   qms_reading_type
            WHERE  is_active = 1
              AND  (material_group = @materialGroup OR material_group IS NULL)
            ORDER  BY CASE WHEN material_group IS NULL THEN 0 ELSE 1 END,
                      sort_order, reading_name", new { materialGroup });
        return rows.ToList();
    }

    public async Task<IReadOnlyList<DefectCatalogEntry>> GetActiveDefectsAsync()
    {
        using var c = Open();
        var rows = await c.QueryAsync<DefectCatalogEntry>(@"
            SELECT defect_id DefectId, defect_code DefectCode, defect_name DefectName,
                   defect_category DefectCategory,
                   is_active IsActive, sort_order SortOrder,
                   material_group MaterialGroup, value_type ValueType
            FROM   qms_defect_catalog WHERE is_active = 1
            ORDER  BY material_group, sort_order, defect_name");
        return rows.ToList();
    }

    /// <summary>
    /// Defects scoped to one material group. The sample form uses this so
    /// each material only shows the defects defined for its own group.
    /// </summary>
    public async Task<IReadOnlyList<DefectCatalogEntry>> GetActiveDefectsForGroupAsync(string? materialGroup)
    {
        if (string.IsNullOrWhiteSpace(materialGroup))
            return Array.Empty<DefectCatalogEntry>();
        using var c = Open();
        var rows = await c.QueryAsync<DefectCatalogEntry>(@"
            SELECT defect_id DefectId, defect_code DefectCode, defect_name DefectName,
                   defect_category DefectCategory,
                   is_active IsActive, sort_order SortOrder,
                   material_group MaterialGroup, value_type ValueType
            FROM   qms_defect_catalog
            WHERE  is_active = 1 AND material_group = @materialGroup
            ORDER  BY sort_order, defect_name", new { materialGroup });
        return rows.ToList();
    }

    /// <summary>Active defect categories (the master list), ordered for
    /// display. Drives the dynamic per-category sections + their colours.</summary>
    public async Task<IReadOnlyList<DefectCategory>> GetActiveCategoriesAsync()
    {
        using var c = Open();
        var rows = await c.QueryAsync<DefectCategory>(@"
            SELECT category_id   CategoryId,
                   category_name CategoryName,
                   sort_order    SortOrder,
                   color_hex     ColorHex,
                   is_active     IsActive
            FROM   qms_defect_category
            WHERE  is_active = 1
            ORDER  BY sort_order, category_name");
        return rows.ToList();
    }

    public async Task<IReadOnlyDictionary<string, string>> GetDisplaySectionMapAsync(string? materialGroup, string? majorCategory)
    {
        // Returns defect_code -> its REAL category name (V22+). Source of
        // truth is qms_defect_catalog.defect_category. Categories are now
        // dynamic (see qms_defect_category), so this no longer collapses to
        // the old Major/Minor two buckets -- consumers group by the actual
        // category. The major_category parameter is preserved for call-site
        // compatibility but unused (catalog rows are already per material_group).
        _ = majorCategory;
        using var c = Open();
        var rows = await c.QueryAsync<(string DefectCode, string Category)>(@"
            SELECT defect_code, defect_category
            FROM   qms_defect_catalog
            WHERE  is_active = 1
              AND  (@materialGroup IS NULL OR material_group = @materialGroup)",
            new { materialGroup });
        return rows.GroupBy(r => r.DefectCode)
            .ToDictionary(g => g.Key, g => g.First().Category);
    }

    // ===================================================================
    // Sample header fields (V20+) -- configurable per-sample identification
    // fields. Always global (no material_group binding). Replaces the
    // hardcoded CartonCount / Grower / PalletNo / DateCode / etc. columns
    // on qms_sample. sample_size stays on qms_sample (denominator for
    // defect percentages).
    // ===================================================================
    public async Task<IReadOnlyList<SampleHeaderField>> GetActiveSampleHeaderFieldsAsync()
    {
        using var c = Open();
        var rows = await c.QueryAsync<SampleHeaderField>(@"
            SELECT field_id    AS FieldId,
                   field_code  AS FieldCode,
                   field_name  AS FieldName,
                   value_kind  AS ValueKind,
                   default_unit AS DefaultUnit,
                   is_active   AS IsActive,
                   is_mandatory AS IsMandatory,
                   sort_order  AS SortOrder,
                   scope       AS Scope
            FROM   qms_sample_header_field
            WHERE  is_active = 1
            ORDER  BY sort_order, field_name");
        return rows.ToList();
    }

    public async Task<IReadOnlyList<SampleHeaderValue>> GetSampleHeaderValuesAsync(long sampleId)
    {
        using var c = Open();
        var rows = await c.QueryAsync<SampleHeaderValue>(@"
            SELECT v.sample_id     AS SampleId,
                   v.field_id      AS FieldId,
                   v.text_value    AS TextValue,
                   v.numeric_value AS NumericValue,
                   v.date_value    AS DateValue,
                   f.field_code    AS FieldCode,
                   f.field_name    AS FieldName,
                   f.value_kind    AS ValueKind,
                   f.default_unit  AS DefaultUnit,
                   f.sort_order    AS SortOrder
            FROM   qms_sample_header_value v
            JOIN   qms_sample_header_field f ON f.field_id = v.field_id
            WHERE  v.sample_id = @sampleId
            ORDER  BY f.sort_order, f.field_name", new { sampleId });
        return rows.ToList();
    }

    public async Task<ILookup<long, SampleHeaderValue>> GetSampleHeaderValuesBatchAsync(IEnumerable<long> sampleIds)
    {
        var ids = sampleIds.Distinct().ToArray();
        if (ids.Length == 0) return Array.Empty<SampleHeaderValue>().ToLookup(v => v.SampleId);
        using var c = Open();
        var rows = await c.QueryAsync<SampleHeaderValue>(@"
            SELECT v.sample_id     AS SampleId,
                   v.field_id      AS FieldId,
                   v.text_value    AS TextValue,
                   v.numeric_value AS NumericValue,
                   v.date_value    AS DateValue,
                   f.field_code    AS FieldCode,
                   f.field_name    AS FieldName,
                   f.value_kind    AS ValueKind,
                   f.default_unit  AS DefaultUnit,
                   f.sort_order    AS SortOrder
            FROM   qms_sample_header_value v
            JOIN   qms_sample_header_field f ON f.field_id = v.field_id
            WHERE  v.sample_id IN @ids
            ORDER  BY v.sample_id, f.sort_order, f.field_name", new { ids });
        return rows.ToLookup(r => r.SampleId);
    }

    public async Task SaveSampleHeaderValuesAsync(long sampleId, IEnumerable<SampleHeaderValue> values, string user)
    {
        // Replace the whole header-value set for the sample in one
        // transaction. Drop empty rows -- a Text field with blank
        // text_value AND no numeric or date value is the operator
        // saying "this field is not applicable to this sample".
        var keep = values
            .Where(v => !string.IsNullOrWhiteSpace(v.TextValue)
                        || v.NumericValue.HasValue
                        || v.DateValue.HasValue)
            .Select(v => new
            {
                sampleId,
                v.FieldId,
                v.TextValue,
                v.NumericValue,
                v.DateValue
            })
            .ToArray();

        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();
        // Only replace the SAMPLE-scoped rows. Material-scoped values are
        // copied down from the material (SaveMaterialHeaderValuesAndSizeAsync /
        // CreateSampleAsync) and must survive a per-sample save.
        await c.ExecuteAsync(@"
            DELETE v FROM qms_sample_header_value v
            JOIN   qms_sample_header_field f ON f.field_id = v.field_id
            WHERE  v.sample_id = @sampleId AND f.scope = 'Sample'",
            new { sampleId }, tx);
        if (keep.Length > 0)
        {
            await c.ExecuteAsync(@"
                INSERT INTO qms_sample_header_value
                    (sample_id, field_id, text_value, numeric_value, date_value)
                VALUES
                    (@sampleId, @FieldId, @TextValue, @NumericValue, @DateValue)",
                keep, tx);
        }
        // Audit (single batched entry per sample save, same pattern as
        // SaveReadings / SaveDefects).
        await _audit.WriteAsync(c, tx,
            EntityTypes.Sample, sampleId, ActionCodes.Updated,
            oldValues: null,
            newValues: new { header_fields = keep.Length, values = keep.Select(k => new { k.FieldId, k.TextValue, k.NumericValue, k.DateValue }) },
            actor: user);
        tx.Commit();
    }

    // ===================================================================
    // Material-level header values (Material-scoped fields + sample_size).
    // Entered once per qms_quality_order_material; every sample inherits.
    // ===================================================================
    public async Task<IReadOnlyList<MaterialHeaderValue>> GetMaterialHeaderValuesAsync(long qoMaterialId)
    {
        using var c = Open();
        var rows = await c.QueryAsync<MaterialHeaderValue>(@"
            SELECT v.qo_material_id AS QoMaterialId,
                   v.field_id       AS FieldId,
                   v.text_value     AS TextValue,
                   v.numeric_value  AS NumericValue,
                   v.date_value     AS DateValue,
                   f.field_code     AS FieldCode,
                   f.field_name     AS FieldName,
                   f.value_kind     AS ValueKind,
                   f.default_unit   AS DefaultUnit,
                   f.sort_order     AS SortOrder
            FROM   qms_qo_material_header_value v
            JOIN   qms_sample_header_field f ON f.field_id = v.field_id
            WHERE  v.qo_material_id = @qoMaterialId
            ORDER  BY f.sort_order, f.field_name", new { qoMaterialId });
        return rows.ToList();
    }

    public async Task<ILookup<long, MaterialHeaderValue>> GetMaterialHeaderValuesBatchAsync(IEnumerable<long> qoMaterialIds)
    {
        var ids = qoMaterialIds.Distinct().ToArray();
        if (ids.Length == 0) return Array.Empty<MaterialHeaderValue>().ToLookup(v => v.QoMaterialId);
        using var c = Open();
        var rows = await c.QueryAsync<MaterialHeaderValue>(@"
            SELECT v.qo_material_id AS QoMaterialId,
                   v.field_id       AS FieldId,
                   v.text_value     AS TextValue,
                   v.numeric_value  AS NumericValue,
                   v.date_value     AS DateValue,
                   f.field_code     AS FieldCode,
                   f.field_name     AS FieldName,
                   f.value_kind     AS ValueKind,
                   f.default_unit   AS DefaultUnit,
                   f.sort_order     AS SortOrder
            FROM   qms_qo_material_header_value v
            JOIN   qms_sample_header_field f ON f.field_id = v.field_id
            WHERE  v.qo_material_id IN @ids
            ORDER  BY v.qo_material_id, f.sort_order, f.field_name", new { ids });
        return rows.ToLookup(r => r.QoMaterialId);
    }

    public async Task SaveMaterialHeaderValuesAndSizeAsync(long qoMaterialId, short? sampleSize,
        IEnumerable<MaterialHeaderValue> values, string user)
    {
        // Drop empty rows -- same "not applicable" convention as the sample path.
        var keep = values
            .Where(v => !string.IsNullOrWhiteSpace(v.TextValue)
                        || v.NumericValue.HasValue
                        || v.DateValue.HasValue)
            .Select(v => new { qoMaterialId, v.FieldId, v.TextValue, v.NumericValue, v.DateValue })
            .ToArray();

        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();

        var oldSize = await c.ExecuteScalarAsync<short?>(
            "SELECT sample_size FROM qms_quality_order_material WHERE qo_material_id = @qoMaterialId",
            new { qoMaterialId }, tx);

        // Source-of-truth sample size on the material...
        await c.ExecuteAsync(
            "UPDATE qms_quality_order_material SET sample_size = @sampleSize WHERE qo_material_id = @qoMaterialId",
            new { qoMaterialId, sampleSize }, tx);

        // ...propagated to every (non-deleted, non-overridden) sample of this
        // material so the per-sample inherited cache that every defect-% calc
        // divides by stays in step. Samples flagged size_overridden=1 keep
        // their user-entered value (V24+). Only writes when a size is set, to
        // avoid blanking samples when the material size is cleared.
        if (sampleSize.HasValue)
            await c.ExecuteAsync(@"
                UPDATE qms_sample SET sample_size = @sampleSize,
                       updated_at = SYSUTCDATETIME(), updated_by = @user
                WHERE qo_material_id = @qoMaterialId
                  AND is_deleted = 0
                  AND size_overridden = 0",
                new { qoMaterialId, sampleSize, user }, tx);

        // Replace the whole material header-value set.
        await c.ExecuteAsync(
            "DELETE FROM qms_qo_material_header_value WHERE qo_material_id = @qoMaterialId",
            new { qoMaterialId }, tx);
        if (keep.Length > 0)
            await c.ExecuteAsync(@"
                INSERT INTO qms_qo_material_header_value
                    (qo_material_id, field_id, text_value, numeric_value, date_value)
                VALUES
                    (@qoMaterialId, @FieldId, @TextValue, @NumericValue, @DateValue)",
                keep, tx);

        // Copy the material-scoped values DOWN onto every sample of this
        // material (new + existing) so each sample owns its own copy -- the
        // sample form / sample row / PDF all read the sample's own header
        // values, with no separate "inherited" recap. Material-scoped rows are
        // wiped then re-inserted from the just-saved material values.
        await c.ExecuteAsync(@"
            DELETE shv FROM qms_sample_header_value shv
            JOIN   qms_sample s ON s.sample_id = shv.sample_id
            JOIN   qms_sample_header_field f ON f.field_id = shv.field_id
            WHERE  s.qo_material_id = @qoMaterialId AND s.is_deleted = 0 AND f.scope = 'Material'",
            new { qoMaterialId }, tx);
        await c.ExecuteAsync(@"
            INSERT INTO qms_sample_header_value (sample_id, field_id, text_value, numeric_value, date_value)
            SELECT s.sample_id, v.field_id, v.text_value, v.numeric_value, v.date_value
            FROM   qms_sample s
            JOIN   qms_qo_material_header_value v ON v.qo_material_id = s.qo_material_id
            WHERE  s.qo_material_id = @qoMaterialId AND s.is_deleted = 0",
            new { qoMaterialId }, tx);

        await _audit.WriteAsync(c, tx,
            EntityTypes.QualityOrderMaterial, qoMaterialId, ActionCodes.Updated,
            oldValues: new { sample_size = oldSize },
            newValues: new { sample_size = sampleSize, header_fields = keep.Length,
                             values = keep.Select(k => new { k.FieldId, k.TextValue, k.NumericValue, k.DateValue }) },
            actor: user);
        tx.Commit();
    }

    // ===================================================================
    // Quality Order PDF -- grouped summary
    // ===================================================================
    //
    // Rolls every material in the QO into (MaterialGroup, Brand, Variety,
    // Grade) groups. The caller (ReportsController.BuildDataAsync) has
    // already enriched `materials` with ApplyMara, so Brand / Variety /
    // MaterialClass / NetWeight reflect the current MARA snapshot rather
    // than a stale copy on qms_quality_order_material. Six queries up
    // front, then everything else is in-memory aggregation -- no per-group
    // round trip, no per-defect round trip.
    //
    // Major/Minor bucketing matches the existing _SampleForm.cshtml rule:
    // "Major" + "Critical" -> Major bucket; everything else -> Minor.
    public async Task<IReadOnlyList<MaterialGroupSummary>> BuildGroupSummariesAsync(
        long qualityOrderId, IReadOnlyList<QualityOrderMaterial> materials)
    {
        if (materials == null || materials.Count == 0)
            return Array.Empty<MaterialGroupSummary>();

        using var c = Open();

        // 1) Samples (non-deleted) for the QO, with size + their parent material.
        var samples = (await c.QueryAsync<(long SampleId, long QoMaterialId, int? SampleSize)>(@"
            SELECT sample_id      AS SampleId,
                   qo_material_id AS QoMaterialId,
                   sample_size    AS SampleSize
            FROM   qms_sample
            WHERE  quality_order_id = @qualityOrderId AND is_deleted = 0",
            new { qualityOrderId })).ToList();

        // 2) arrival_item.quantity per QO material -- needed for the gross
        //    weight calculation (Σ qty × MARA.weight). Joining at the QO
        //    material rather than reloading arrival items keeps this single
        //    table-scan.
        var qoMaterialIds = materials.Select(m => m.QoMaterialId).ToArray();
        var qtyByMaterial = (await c.QueryAsync<(long QoMaterialId, decimal? Quantity)>(@"
            SELECT m.qo_material_id AS QoMaterialId, ai.quantity AS Quantity
            FROM   qms_quality_order_material m
            JOIN   qms_arrival_item ai ON ai.arrival_item_id = m.arrival_item_id
            WHERE  m.qo_material_id IN @ids",
            new { ids = qoMaterialIds })).ToDictionary(r => r.QoMaterialId, r => r.Quantity);

        var sampleIds = samples.Select(s => s.SampleId).ToArray();

        // 3) Defects across every sample in the QO, with defect_code/name/category
        //    so we can aggregate per defect_id and also know how to bucket it.
        var sampleDefects = sampleIds.Length == 0
            ? new List<(long SampleId, int DefectId, decimal? DefectValue)>()
            : (await c.QueryAsync<(long SampleId, int DefectId, decimal? DefectValue)>(@"
                SELECT sample_id    AS SampleId,
                       defect_id    AS DefectId,
                       defect_value AS DefectValue
                FROM   qms_sample_defect
                WHERE  sample_id IN @ids",
                new { ids = sampleIds })).ToList();

        // 4) Sample readings -- TARA + the per-display_mode aggregations.
        var sampleReadings = sampleIds.Length == 0
            ? new List<(long SampleId, string ReadingTypeCode, decimal? NumericValue, string? TextValue)>()
            : (await c.QueryAsync<(long SampleId, string ReadingTypeCode, decimal? NumericValue, string? TextValue)>(@"
                SELECT sr.sample_id        AS SampleId,
                       sr.reading_type_code AS ReadingTypeCode,
                       sr.numeric_value    AS NumericValue,
                       sr.text_value       AS TextValue
                FROM   qms_sample_reading sr
                WHERE  sr.sample_id IN @ids",
                new { ids = sampleIds })).ToList();

        // 5) Active defect catalog for every distinct material_group in the QO.
        //    Pre-loaded so each group can render its FULL catalog (zeros
        //    included) without an N+1 pattern. Codes can repeat across
        //    groups, so we key by (material_group, defect_id).
        var distinctGroups = materials
            .Select(m => m.MaterialGroup ?? "")
            .Where(g => g.Length > 0)
            .Distinct()
            .ToArray();

        var defectCatalog = distinctGroups.Length == 0
            ? new List<DefectCatalogEntry>()
            : (await c.QueryAsync<DefectCatalogEntry>(@"
                SELECT defect_id      AS DefectId,
                       defect_code    AS DefectCode,
                       defect_name    AS DefectName,
                       defect_category AS DefectCategory,
                       is_active      AS IsActive,
                       sort_order     AS SortOrder,
                       material_group AS MaterialGroup
                FROM   qms_defect_catalog
                WHERE  is_active = 1 AND material_group IN @groups
                ORDER  BY material_group, sort_order, defect_name",
                new { groups = distinctGroups })).ToList();

        // 5b) Defect category master (V22+) -- drives the per-category
        //     sections and their order/colour. Keyed by name (case-insensitive).
        var categoryByName = (await c.QueryAsync<DefectCategory>(@"
                SELECT category_id CategoryId, category_name CategoryName,
                       sort_order SortOrder, color_hex ColorHex, is_active IsActive
                FROM   qms_defect_category WHERE is_active = 1"))
            .ToDictionary(x => x.CategoryName, StringComparer.OrdinalIgnoreCase);

        // 6) Active reading types: per-group rows AND globals (V19+,
        //    material_group IS NULL). A global reading type applies to
        //    every group's summary; the in-memory bucketing below ORs the
        //    group filter against an empty MaterialGroup to pick them up.
        var readingCatalog = distinctGroups.Length == 0
            ? new List<ReadingTypeEntry>()
            : (await c.QueryAsync<ReadingTypeEntry>(@"
                SELECT reading_type_id            AS ReadingTypeId,
                       reading_type_code          AS ReadingTypeCode,
                       reading_name               AS ReadingName,
                       value_kind                 AS ValueKind,
                       default_unit               AS DefaultUnit,
                       is_active                  AS IsActive,
                       sort_order                 AS SortOrder,
                       ISNULL(material_group, '') AS MaterialGroup,
                       is_mandatory               AS IsMandatory,
                       display_mode               AS DisplayMode
                FROM   qms_reading_type
                WHERE  is_active = 1
                  AND  (material_group IN @groups OR material_group IS NULL)
                ORDER  BY CASE WHEN material_group IS NULL THEN 0 ELSE 1 END,
                          material_group, sort_order, reading_name",
                new { groups = distinctGroups })).ToList();

        // ---------- Aggregate in memory ----------
        // (MaterialGroup, Brand, Variety, Grade) -> list of materials.
        // Null-safe: empty-string keys collapse so two materials missing
        // the same field still end up in the same group.
        static string Norm(string? s) => (s ?? "").Trim();

        var bySample = samples.ToLookup(s => s.QoMaterialId);
        var defectsBySample = sampleDefects.ToLookup(d => d.SampleId);
        var readingsBySample = sampleReadings.ToLookup(r => r.SampleId);

        var groups = materials
            .GroupBy(m => (
                MaterialGroup: Norm(m.MaterialGroup),
                Brand:         Norm(m.Brand),
                Variety:       Norm(m.Variety),
                Grade:         Norm(m.MaterialClass)))
            .OrderBy(g => g.Key.MaterialGroup)
            .ThenBy(g => g.Key.Brand)
            .ThenBy(g => g.Key.Variety)
            .ThenBy(g => g.Key.Grade)
            .ToList();

        var result = new List<MaterialGroupSummary>(groups.Count);

        foreach (var g in groups)
        {
            var mats         = g.ToList();
            var matIds       = mats.Select(m => m.QoMaterialId).ToHashSet();
            var groupSamples = samples.Where(s => matIds.Contains(s.QoMaterialId)).ToList();
            var groupSampleIds = groupSamples.Select(s => s.SampleId).ToHashSet();
            var sumSize      = groupSamples.Sum(s => s.SampleSize ?? 0);

            // Gross = Σ (arrival_item.quantity × qoMaterial.NetWeight).
            // Both halves can be null (legacy rows) -- treat as 0.
            decimal sumGross = 0m;
            foreach (var m in mats)
            {
                var qty = qtyByMaterial.TryGetValue(m.QoMaterialId, out var q) ? (q ?? 0m) : 0m;
                var w   = m.NetWeight ?? 0m;
                sumGross += qty * w;
            }

            // Tara = Σ TARA reading.numeric_value for samples in this group.
            decimal sumTara = sampleReadings
                .Where(r => groupSampleIds.Contains(r.SampleId)
                            && string.Equals(r.ReadingTypeCode, "TARA", StringComparison.OrdinalIgnoreCase)
                            && r.NumericValue.HasValue)
                .Sum(r => r.NumericValue!.Value);

            var summary = new MaterialGroupSummary
            {
                MaterialGroup     = g.Key.MaterialGroup,
                MaterialGroupDesc = mats.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.MaterialGroupDesc))?.MaterialGroupDesc,
                Brand             = string.IsNullOrEmpty(g.Key.Brand)   ? null : g.Key.Brand,
                Variety           = string.IsNullOrEmpty(g.Key.Variety) ? null : g.Key.Variety,
                Grade             = string.IsNullOrEmpty(g.Key.Grade)   ? null : g.Key.Grade,
                MajorCategory     = mats.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.MajorCategory))?.MajorCategory,
                SumSampleSize     = sumSize,
                SumGross          = sumGross,
                SumTara           = sumTara,
                MaterialCount     = mats.Count,
                SampleCount       = groupSamples.Count
            };

            // ---- Defects: iterate the FULL active catalog for this group's
            // material_group (zeros included). Σ defect_value comes from the
            // sample_defect rows belonging to this group's samples.
            var groupDefectCatalog = defectCatalog
                .Where(d => string.Equals(d.MaterialGroup, g.Key.MaterialGroup, StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d.SortOrder)
                .ThenBy(d => d.DefectName)
                .ToList();

            var sumValueByDefectId = sampleDefects
                .Where(d => groupSampleIds.Contains(d.SampleId) && d.DefectValue.HasValue)
                .GroupBy(d => d.DefectId)
                .ToDictionary(gr => gr.Key, gr => gr.Sum(x => x.DefectValue!.Value));

            // Build one section per defect category (V22+), ordered by the
            // master sort_order. A defect whose category isn't in the master
            // (shouldn't happen with the FK, but be safe) gets its own
            // trailing section with no colour.
            var sectionsByCat = new Dictionary<string, DefectCategorySection>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in groupDefectCatalog)
            {
                var sumVal = sumValueByDefectId.TryGetValue(d.DefectId, out var sv) ? sv : 0m;
                var pct    = sumSize > 0 ? (sumVal / sumSize) * 100m : 0m;
                var row = new DefectAggRow
                {
                    DefectId   = d.DefectId,
                    Code       = d.DefectCode,
                    Name       = d.DefectName,
                    Category   = d.DefectCategory,
                    SumValue   = sumVal,
                    Percentage = pct
                };
                if (!sectionsByCat.TryGetValue(d.DefectCategory, out var sec))
                {
                    categoryByName.TryGetValue(d.DefectCategory, out var cat);
                    sec = new DefectCategorySection
                    {
                        CategoryName = d.DefectCategory,
                        ColorHex     = cat?.ColorHex,
                        SortOrder    = cat?.SortOrder ?? 999
                    };
                    sectionsByCat[d.DefectCategory] = sec;
                }
                sec.Rows.Add(row);
            }
            summary.DefectSections = sectionsByCat.Values
                .OrderBy(s => s.SortOrder).ThenBy(s => s.CategoryName).ToList();

            // ---- Readings: one row per active reading type for this
            // group's material_group, PLUS every global reading type
            // (MaterialGroup == ""). Per-group rows are listed first
            // (preserving SortOrder) so a fruit-specific reading appears
            // before a global one in the rendered table.
            var groupReadingCatalog = readingCatalog
                .Where(r => string.Equals(r.MaterialGroup, g.Key.MaterialGroup, StringComparison.OrdinalIgnoreCase)
                            || string.IsNullOrEmpty(r.MaterialGroup))
                .OrderBy(r => string.IsNullOrEmpty(r.MaterialGroup) ? 1 : 0)
                .ThenBy(r => r.SortOrder)
                .ThenBy(r => r.ReadingName)
                .ToList();

            foreach (var rt in groupReadingCatalog)
            {
                var rowsForType = sampleReadings
                    .Where(r => groupSampleIds.Contains(r.SampleId)
                                && string.Equals(r.ReadingTypeCode, rt.ReadingTypeCode, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                string display = rt.DisplayMode switch
                {
                    "text" => string.Join(", ", rowsForType
                                .Select(r => (r.TextValue ?? "").Trim())
                                .Where(s => s.Length > 0)
                                .Distinct(StringComparer.OrdinalIgnoreCase)),
                    "count" => rowsForType
                                .Count(r => r.NumericValue.HasValue
                                            || !string.IsNullOrWhiteSpace(r.TextValue))
                                .ToString(),
                    "sum" => rowsForType
                                .Where(r => r.NumericValue.HasValue)
                                .Sum(r => r.NumericValue!.Value)
                                .ToString("0.##"),
                    "sum_over_size" => sumSize > 0
                                ? ((rowsForType.Where(r => r.NumericValue.HasValue)
                                                .Sum(r => r.NumericValue!.Value) / sumSize) * 100m)
                                    .ToString("0.##") + "%"
                                : "",
                    "formula" => "",
                    _ => ""
                };

                summary.Readings.Add(new ReadingAggRow
                {
                    Code        = rt.ReadingTypeCode,
                    Name        = rt.ReadingName,
                    DisplayMode = rt.DisplayMode,
                    Unit        = rt.DefaultUnit,
                    DisplayValue= display
                });
            }

            result.Add(summary);
        }

        return result;
    }
}
