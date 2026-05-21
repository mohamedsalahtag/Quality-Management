using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services;

public class QualityOrderService : IQualityOrderService
{
    private readonly string _cs;
    public QualityOrderService(IConfiguration config)
    {
        _cs = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
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
        var rows = await c.QueryAsync<QualityOrderMaterial>(@"
            SELECT qo_material_id          QoMaterialId,
                   quality_order_id        QualityOrderId,
                   arrival_item_id         ArrivalItemId,
                   material_no             MaterialNo,
                   material_desc           MaterialDesc,
                   origin                  Origin,
                   variety                 Variety,
                   material_class          MaterialClass,
                   net_weight              NetWeight,
                   material_size           MaterialSize,
                   material_group          MaterialGroup,
                   material_group_desc     MaterialGroupDesc,
                   major_category          MajorCategory,
                   brand                   Brand,
                   pack_type               PackType,
                   size_overridden         SizeOverridden,
                   original_material_size  OriginalMaterialSize,
                   override_material_size  OverrideMaterialSize,
                   override_reason         OverrideReason,
                   override_approved_by    OverrideApprovedBy,
                   override_approved_at    OverrideApprovedAt
            FROM   qms_quality_order_material
            WHERE  quality_order_id = @qualityOrderId
            ORDER  BY qo_material_id", new { qualityOrderId });
        return rows.ToList();
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
        tx.Commit();
        return (true, null);
    }

    public async Task SaveOverrideAsync(long qoMaterialId, string newSize, string? reason, string user)
    {
        using var c = Open();
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
            new { qoMaterialId, newSize, reason = (object?)reason ?? DBNull.Value, user });
        // Build the audit JSON in C# so quotes / backslashes / newlines in
        // newSize or reason are escaped properly. The previous CONCAT in SQL
        // produced malformed JSON whenever the user's reason contained a `"`.
        var auditJson = JsonSerializer.Serialize(new { newSize, reason = reason ?? "" });
        await c.ExecuteAsync(@"
            INSERT INTO qms_audit_log (entity_type, entity_id, action_code, new_values_json, changed_at, changed_by)
            VALUES ('QualityOrderMaterial', @qoMaterialId, 'Override',
                    @auditJson,
                    SYSUTCDATETIME(), @user)",
            new { qoMaterialId, auditJson, user });
    }

    public async Task ClearOverrideAsync(long qoMaterialId, string user)
    {
        using var c = Open();
        // Restore the original size (if one was captured) and clear all
        // override metadata so the material line looks like it never had
        // one. Audit log keeps a trail for compliance.
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
            new { qoMaterialId });
        await c.ExecuteAsync(@"
            INSERT INTO qms_audit_log (entity_type, entity_id, action_code, changed_at, changed_by)
            VALUES ('QualityOrderMaterial', @qoMaterialId, 'OverrideCleared', SYSUTCDATETIME(), @user)",
            new { qoMaterialId, user });
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
        using var c = Open();
        // Auto-number per material.
        var nextNo = await c.ExecuteScalarAsync<int>(
            "SELECT ISNULL(MAX(sample_no), 0) + 1 FROM qms_sample WHERE qo_material_id = @QoMaterialId AND is_deleted = 0",
            new { s.QoMaterialId });
        s.SampleNo = nextNo;
        return await c.ExecuteScalarAsync<long>(@"
            INSERT INTO qms_sample
                (quality_order_id, qo_material_id, sample_no, carton_count, carton_identifier,
                 sample_scope, sample_size, grower, pallet_no, grower_pallet, pack_code,
                 date_code, label_value, lot_no, packaging_material,
                 created_at, created_by)
            VALUES
                (@QualityOrderId, @QoMaterialId, @SampleNo, @CartonCount, @CartonIdentifier,
                 @SampleScope, @SampleSize, @Grower, @PalletNo, @GrowerPallet, @PackCode,
                 @DateCode, @LabelValue, @LotNo, @PackagingMaterial,
                 SYSUTCDATETIME(), @CreatedBy);
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);", s);
    }

    public async Task UpdateSampleAsync(Sample s)
    {
        using var c = Open();
        await c.ExecuteAsync(@"
            UPDATE qms_sample SET
              carton_count = @CartonCount, carton_identifier = @CartonIdentifier,
              sample_scope = @SampleScope, sample_size = @SampleSize,
              grower = @Grower, pallet_no = @PalletNo, grower_pallet = @GrowerPallet,
              pack_code = @PackCode, date_code = @DateCode, label_value = @LabelValue,
              lot_no = @LotNo, packaging_material = @PackagingMaterial,
              updated_at = SYSUTCDATETIME(), updated_by = @UpdatedBy
            WHERE sample_id = @SampleId", s);
    }

    public async Task SoftDeleteSampleAsync(long sampleId, string user)
    {
        using var c = Open();
        await c.ExecuteAsync(@"
            UPDATE qms_sample SET is_deleted = 1, deleted_at = SYSUTCDATETIME(), deleted_by = @user
            WHERE sample_id = @sampleId", new { sampleId, user });
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
                   material_group MaterialGroup, is_mandatory IsMandatory
            FROM   qms_reading_type WHERE is_active = 1
            ORDER  BY material_group, sort_order, reading_name");
        return rows.ToList();
    }

    /// <summary>
    /// Reading types scoped to one material group. Mirrors
    /// <see cref="GetActiveDefectsForGroupAsync"/> so the sample form shows
    /// only the readings defined for the QO material line's group.
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
                   material_group MaterialGroup, is_mandatory IsMandatory
            FROM   qms_reading_type
            WHERE  is_active = 1 AND material_group = @materialGroup
            ORDER  BY sort_order, reading_name", new { materialGroup });
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

    public async Task<IReadOnlyDictionary<string, string>> GetDisplaySectionMapAsync(string? materialGroup, string? majorCategory)
    {
        using var c = Open();
        var rows = await c.QueryAsync<(string DefectCode, string DisplaySection)>(@"
            SELECT dc.defect_code, mgd.display_section
            FROM   qms_material_group_defect mgd
            JOIN   qms_defect_catalog dc ON dc.defect_id = mgd.defect_id
            WHERE  mgd.is_active = 1
              AND  (@materialGroup IS NULL OR mgd.material_group = @materialGroup)
              AND  (@majorCategory IS NULL OR mgd.major_category = @majorCategory OR mgd.major_category IS NULL)",
            new { materialGroup, majorCategory });
        return rows.GroupBy(r => r.DefectCode)
            .ToDictionary(g => g.Key, g => g.First().DisplaySection);
    }
}
