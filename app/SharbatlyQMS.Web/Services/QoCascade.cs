using Dapper;
using Microsoft.Data.SqlClient;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Deletes a quality order's entire subtree. Shared by
/// <see cref="ArrivalService"/> (which must clear cancelled QOs before it can
/// delete their arrival) and <see cref="QualityOrderService"/> (the SiteAdmin
/// hard-delete of a single QO).
///
/// Static on purpose: an injected service here would make ArrivalService depend
/// on QualityOrderService for one SQL block. The caller owns the connection and
/// transaction, so both deletes stay atomic with the rest of their work.
///
/// Most of the child FKs are NO ACTION, so order matters: leaves first, QO last.
/// The two that ARE ON DELETE CASCADE — qms_sample_header_value and
/// qms_qo_material_header_value — are deliberately absent; SQL Server clears
/// them when their parent row goes.
///
/// Physical files (photos under the uploads root, documents under the document
/// root) are NOT touched here: the caller decides, because it knows whether the
/// delete is going to commit. <see cref="CollectDocumentPathsAsync"/> hands back
/// the relative paths so the caller can unlink them afterwards.
/// </summary>
public static class QoCascade
{
    /// <summary>Relative storage paths of the documents attached to these QOs
    /// and their samples. Call BEFORE <see cref="DeleteAsync"/> — the rows are
    /// gone afterwards — and unlink the files only once the transaction commits.</summary>
    public static async Task<IReadOnlyList<string>> CollectDocumentPathsAsync(
        SqlConnection c, SqlTransaction tx, IReadOnlyCollection<long> qoIds)
    {
        if (qoIds.Count == 0) return Array.Empty<string>();
        var paths = await c.QueryAsync<string>(@"
            SELECT storage_path FROM qms_document
            WHERE  (owner_type = 'QualityOrder' AND owner_id IN @qoIds)
               OR  (owner_type = 'Sample'
                    AND owner_id IN (SELECT sample_id FROM qms_sample WHERE quality_order_id IN @qoIds))",
            new { qoIds }, tx);
        return paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
    }

    /// <summary>
    /// Removes every row belonging to the given quality orders, then the QOs
    /// themselves. Caller-supplied transaction; nothing is committed here.
    ///
    /// Note what this does NOT do: it never touches qms_sap_container_cache.
    /// Deleting a QO leaves its arrival in place, so resetting has_arrival would
    /// put the container back in the Pending queue and invite a duplicate
    /// arrival. Only ArrivalService (which really is removing the arrival) may
    /// release the container, and it does that itself.
    /// </summary>
    public static async Task DeleteAsync(SqlConnection c, SqlTransaction tx, IReadOnlyCollection<long> qoIds)
    {
        if (qoIds.Count == 0) return;

        var sampleIds = (await c.QueryAsync<long>(
            "SELECT sample_id FROM qms_sample WHERE quality_order_id IN @qoIds",
            new { qoIds }, tx)).ToList();

        if (sampleIds.Count > 0)
        {
            await c.ExecuteAsync("DELETE FROM qms_sample_defect      WHERE sample_id IN @sampleIds", new { sampleIds }, tx);
            await c.ExecuteAsync("DELETE FROM qms_sample_reading     WHERE sample_id IN @sampleIds", new { sampleIds }, tx);
            await c.ExecuteAsync("DELETE FROM qms_sample_observation WHERE sample_id IN @sampleIds", new { sampleIds }, tx);
            await c.ExecuteAsync("DELETE FROM qms_image_link WHERE owner_type='Sample' AND owner_id IN @sampleIds", new { sampleIds }, tx);
            // V39 documents. Missing from the original arrival cascade, which
            // left rows pointing at files no page could ever reach again.
            await c.ExecuteAsync("DELETE FROM qms_document  WHERE owner_type='Sample' AND owner_id IN @sampleIds", new { sampleIds }, tx);
        }
        await c.ExecuteAsync("DELETE FROM qms_sample WHERE quality_order_id IN @qoIds", new { qoIds }, tx);

        // qms_report_log FKs to quality_order, so a QO that ever produced a PDF
        // blocks the delete below without this.
        await c.ExecuteAsync("DELETE FROM qms_report_log WHERE quality_order_id IN @qoIds", new { qoIds }, tx);

        var matIds = (await c.QueryAsync<long>(
            "SELECT qo_material_id FROM qms_quality_order_material WHERE quality_order_id IN @qoIds",
            new { qoIds }, tx)).ToList();
        if (matIds.Count > 0)
            await c.ExecuteAsync("DELETE FROM qms_image_link WHERE owner_type='QualityOrderMaterial' AND owner_id IN @matIds", new { matIds }, tx);
        await c.ExecuteAsync("DELETE FROM qms_quality_order_material WHERE quality_order_id IN @qoIds", new { qoIds }, tx);

        // Polymorphic, no FK -- these orphan silently if we skip them.
        await c.ExecuteAsync("DELETE FROM qms_image_link WHERE owner_type='QualityOrder' AND owner_id IN @qoIds", new { qoIds }, tx);
        await c.ExecuteAsync("DELETE FROM qms_document   WHERE owner_type='QualityOrder' AND owner_id IN @qoIds", new { qoIds }, tx);

        // Claims: FK is NO ACTION, so clear the tree before the QO row.
        await c.ExecuteAsync("DELETE FROM qms_claim_read_marker WHERE claim_id IN (SELECT claim_id FROM qms_claim WHERE quality_order_id IN @qoIds)", new { qoIds }, tx);
        await c.ExecuteAsync("DELETE FROM qms_claim_note        WHERE claim_id IN (SELECT claim_id FROM qms_claim WHERE quality_order_id IN @qoIds)", new { qoIds }, tx);
        await c.ExecuteAsync("DELETE FROM qms_claim             WHERE quality_order_id IN @qoIds", new { qoIds }, tx);

        await c.ExecuteAsync("DELETE FROM qms_quality_order WHERE quality_order_id IN @qoIds", new { qoIds }, tx);

        // Status history is polymorphic with no FK, so it survives the QO and
        // would accumulate forever. The audit log deliberately does NOT: an
        // audit trail that vanishes with the record it describes is useless.
        await c.ExecuteAsync(
            "DELETE FROM qms_status_history WHERE entity_type='QualityOrder' AND entity_id IN @qoIds",
            new { qoIds }, tx);
    }
}
