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
    /// <summary>A branch code no real branch uses, for tests that set up
    /// their own committee: nothing configured for a real branch can match it.</summary>
    public const string TestPlant      = "ZT98";
    /// <summary>A second one, kept without a committee of its own.</summary>
    public const string TestPlantBare  = "ZT97";

    public static async Task<(long ArrivalId, long? QoId, string Tag)> SeedAsync(
        QmsAppFactory factory, string arrivalStatus, string? qoStatus, bool rejected = false, string plant = "JD01")
    {
        var tag = $"TESTSV{Guid.NewGuid():N}".Substring(0, 12).ToUpperInvariant();
        using var c = Db(factory);
        await c.OpenAsync();

        var arrivalId = await c.ExecuteScalarAsync<long>(@"
            INSERT INTO qms_arrival
                (arrival_no, source_system, bol_no, container_no, ebeln,
                 vendor_no, vendor_name, plant, status_code, created_at, created_by)
            VALUES (@tag, 'TEST', @tag, @tag, '4700000000',
                 'V1', 'Test vendor', @plant, @arrivalStatus, SYSUTCDATETIME(), 'test');
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);", new { tag, arrivalStatus, plant });

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

        // Files a test attached to the survey: remove them from disk before
        // the rows that say where they are.
        using (var scope = factory.Services.CreateScope())
        {
            var docs = scope.ServiceProvider.GetRequiredService<SharbatlyQMS.Web.Services.IDocumentService>();
            var surveyIds = (await c.QueryAsync<long>(
                "SELECT survey_id FROM qms_survey WHERE arrival_id = @arrivalId", new { arrivalId })).ToList();
            // Every row, removed ones included: removing an attachment in the
            // application keeps its file, and a test must not leave one behind.
            // (An empty IN list is not valid SQL here, hence the guard.)
            var paths = surveyIds.Count == 0 ? new List<string>() : (await c.QueryAsync<string>(@"
                SELECT storage_path FROM qms_document
                WHERE  owner_type = 'Survey' AND owner_id IN @surveyIds", new { surveyIds })).ToList();
            foreach (var stored in paths)
            {
                var path = docs.ResolveAbsolutePath(new SharbatlyQMS.Web.ViewModels.DocumentInfo { StoragePath = stored });
                try
                {
                    if (path == null) continue;
                    if (File.Exists(path)) File.Delete(path);
                    var dir = Path.GetDirectoryName(path);
                    if (dir != null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch { /* best effort */ }
            }
        }

        await c.ExecuteAsync(@"
            DECLARE @qos TABLE (id BIGINT);
            INSERT INTO @qos SELECT quality_order_id FROM qms_quality_order WHERE arrival_id = @arrivalId;
            DECLARE @surveys TABLE (id BIGINT);
            INSERT INTO @surveys SELECT survey_id FROM qms_survey WHERE arrival_id = @arrivalId;

            DELETE t FROM qms_survey_attendance t
              JOIN qms_survey_appointment ap ON ap.appointment_id = t.appointment_id
             WHERE ap.survey_id IN (SELECT id FROM @surveys);
            DELETE FROM qms_survey_appointment WHERE survey_id IN (SELECT id FROM @surveys);
            DELETE FROM qms_survey_member WHERE survey_id IN (SELECT id FROM @surveys);
            DELETE FROM qms_document WHERE owner_type = 'Survey' AND owner_id IN (SELECT id FROM @surveys);

            IF OBJECT_ID('qms_notification') IS NOT NULL
                DELETE FROM qms_notification
                 WHERE (entity_type = 'Survey' AND entity_id IN (SELECT id FROM @surveys))
                    OR (entity_type = 'QualityOrder' AND entity_id IN (SELECT id FROM @qos));
            IF OBJECT_ID('qms_mail_outbox') IS NOT NULL
                DELETE FROM qms_mail_outbox
                 WHERE (entity_type = 'Survey' AND entity_id IN (SELECT id FROM @surveys))
                    OR (entity_type = 'QualityOrder' AND entity_id IN (SELECT id FROM @qos));

            DELETE FROM qms_status_history WHERE entity_type = 'Survey' AND entity_id IN (SELECT id FROM @surveys);
            -- The survey's audit trail goes too: these tests act under made-up
            -- user names, which must not pile up in the live Activity log.
            DELETE FROM qms_audit_log WHERE entity_type = 'Survey' AND entity_id IN (SELECT id FROM @surveys);
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

    /// <summary>For a test that removed its survey through the application
    /// (deleting the arrival) and so never reaches <see cref="CleanupAsync"/>:
    /// takes away the audit rows the survey left.</summary>
    public static async Task ForgetSurveyAuditAsync(QmsAppFactory factory, long surveyId)
    {
        using var c = Db(factory);
        await c.ExecuteAsync(
            "DELETE FROM qms_audit_log WHERE entity_type = 'Survey' AND entity_id = @surveyId", new { surveyId });
    }

    /// <summary>Removes every committee a test set up for the test branches.
    /// Members go with their committee (cascade). Their audit rows go as well,
    /// including those of a committee the test itself deleted: such a row is
    /// recognised by the test branch code in its values.</summary>
    public static async Task CleanupCommitteesAsync(QmsAppFactory factory)
    {
        using var c = Db(factory);
        await c.ExecuteAsync(@"
            DECLARE @ids TABLE (id BIGINT);
            INSERT INTO @ids SELECT committee_id FROM qms_survey_committee WHERE plant IN (@a, @b);
            INSERT INTO @ids SELECT entity_id FROM qms_audit_log
             WHERE entity_type = 'SurveyCommittee'
               AND (ISNULL(old_values_json, '') + ISNULL(new_values_json, '') LIKE '%""plant"":""' + @a + '""%'
                 OR ISNULL(old_values_json, '') + ISNULL(new_values_json, '') LIKE '%""plant"":""' + @b + '""%');
            DELETE FROM qms_audit_log WHERE entity_type = 'SurveyCommittee' AND entity_id IN (SELECT id FROM @ids);
            DELETE FROM qms_survey_committee WHERE plant IN (@a, @b);", new { a = TestPlant, b = TestPlantBare });
    }
}
