using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Throwaway arrivals and quality orders for the survey tests, and the one
/// place that knows how to remove everything such a test can leave behind.
/// The suite runs against the live database, so every seed is paired with a
/// cleanup in a finally block.
/// </summary>
internal static class SurveyTestData
{
    public static SqlConnection Db(QmsAppFactory factory)
    {
        using var s = factory.Services.CreateScope();
        var cfg = s.ServiceProvider.GetRequiredService<IConfiguration>();
        return new SqlConnection(cfg.GetConnectionString("Default"));
    }

    /// <summary>An arrival with one line and a shipment snapshot, optionally
    /// carrying one quality order in the given status. <paramref name="rejected"/>
    /// marks that order as a rejection order (finished, potential claim).</summary>
    public static async Task<(long ArrivalId, long? QoId, string Tag)> SeedAsync(
        QmsAppFactory factory, string arrivalStatus, string? qoStatus, bool rejected = false)
    {
        var tag = $"TESTSV{Guid.NewGuid():N}".Substring(0, 12).ToUpperInvariant();
        using var c = Db(factory);
        await c.OpenAsync();

        var arrivalId = await c.ExecuteScalarAsync<long>(@"
            INSERT INTO qms_arrival
                (arrival_no, source_system, bol_no, container_no, ebeln,
                 vendor_no, vendor_name, plant, status_code, created_at, created_by)
            VALUES (@tag, 'TEST', @tag, @tag, '4700000000',
                 'V1', 'Test vendor', 'JD01', @arrivalStatus, SYSUTCDATETIME(), 'test');
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);", new { tag, arrivalStatus });

        await c.ExecuteAsync(@"
            INSERT INTO qms_arrival_item (arrival_id, ebeln, ebelp, material_no, material_desc, material_group)
            VALUES (@arrivalId, '4700000000', '00010', 'TESTMAT', 'Test material', 'APPLE')",
            new { arrivalId });

        await c.ExecuteAsync(@"
            INSERT INTO qms_shipment_snapshot (arrival_id, internal_shipment_no, status_code, arrival_date)
            VALUES (@arrivalId, @tag, 'Confirmed', CAST(SYSUTCDATETIME() AS DATE))",
            new { arrivalId, tag });

        long? qoId = null;
        if (qoStatus != null)
        {
            qoId = await c.ExecuteScalarAsync<long>(@"
                INSERT INTO qms_quality_order
                    (quality_order_no, arrival_id, status_code, created_at, created_by, opened_at, opened_by,
                     closed_at, closed_by, container_rejected, potential_claim)
                VALUES (@no, @arrivalId, @qoStatus, SYSUTCDATETIME(), 'test', SYSUTCDATETIME(), 'test',
                     CASE WHEN @qoStatus = 'Closed' THEN SYSUTCDATETIME() END,
                     CASE WHEN @qoStatus = 'Closed' THEN 'test' END,
                     @rejected, CASE WHEN @rejected = 1 THEN 1 END);
                SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
                new { no = "QO-TEST-" + tag, arrivalId, qoStatus, rejected });
            await c.ExecuteAsync(@"
                INSERT INTO qms_quality_order_material
                    (quality_order_id, arrival_item_id, material_no, material_desc, material_group)
                SELECT @qoId, ai.arrival_item_id, ai.material_no, ai.material_desc, ai.material_group
                FROM   qms_arrival_item ai WHERE ai.arrival_id = @arrivalId",
                new { qoId, arrivalId });
        }
        return (arrivalId, qoId, tag);
    }

    public static async Task CleanupAsync(QmsAppFactory factory, long arrivalId)
    {
        using var c = Db(factory);
        await c.OpenAsync();
        await c.ExecuteAsync(@"
            DECLARE @qos TABLE (id BIGINT);
            INSERT INTO @qos SELECT quality_order_id FROM qms_quality_order WHERE arrival_id = @arrivalId;
            DECLARE @surveys TABLE (id BIGINT);
            INSERT INTO @surveys SELECT survey_id FROM qms_survey WHERE arrival_id = @arrivalId;

            IF OBJECT_ID('qms_notification') IS NOT NULL
                DELETE FROM qms_notification
                 WHERE (entity_type = 'Survey' AND entity_id IN (SELECT id FROM @surveys))
                    OR (entity_type = 'QualityOrder' AND entity_id IN (SELECT id FROM @qos));
            IF OBJECT_ID('qms_mail_outbox') IS NOT NULL
                DELETE FROM qms_mail_outbox
                 WHERE (entity_type = 'Survey' AND entity_id IN (SELECT id FROM @surveys))
                    OR (entity_type = 'QualityOrder' AND entity_id IN (SELECT id FROM @qos));

            DELETE FROM qms_status_history WHERE entity_type = 'Survey' AND entity_id IN (SELECT id FROM @surveys);
            DELETE FROM qms_survey WHERE arrival_id = @arrivalId;
            DELETE FROM qms_claim_read_marker WHERE claim_id IN
                (SELECT claim_id FROM qms_claim WHERE quality_order_id IN (SELECT id FROM @qos));
            DELETE FROM qms_claim_note WHERE claim_id IN
                (SELECT claim_id FROM qms_claim WHERE quality_order_id IN (SELECT id FROM @qos));
            DELETE FROM qms_claim WHERE quality_order_id IN (SELECT id FROM @qos);
            DELETE FROM qms_status_history
             WHERE (entity_type = 'QualityOrder' AND entity_id IN (SELECT id FROM @qos))
                OR (entity_type = 'Arrival' AND entity_id = @arrivalId);
            DELETE FROM qms_quality_order_material WHERE quality_order_id IN (SELECT id FROM @qos);
            DELETE FROM qms_quality_order WHERE arrival_id = @arrivalId;
            DELETE FROM qms_shipment_snapshot WHERE arrival_id = @arrivalId;
            DELETE FROM qms_arrival_item WHERE arrival_id = @arrivalId;
            DELETE FROM qms_arrival WHERE arrival_id = @arrivalId;", new { arrivalId });
    }
}
