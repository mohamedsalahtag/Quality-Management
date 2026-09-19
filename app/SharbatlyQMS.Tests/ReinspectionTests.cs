using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Reinspecting a finished quality order.
///
/// A container could never carry two quality orders before this: a filtered
/// UNIQUE index on arrival_id refused the second row outright, and several
/// screens were built on that guarantee without saying so. The tests here hold
/// the two halves of the bargain — a reinspection can be raised, and NOTHING
/// that counted containers starts counting it as a second one.
///
/// Every test creates its own throwaway arrival and order and removes them
/// again; this suite runs against the live database.
/// </summary>
[Collection("workflow")]
public class ReinspectionTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    private readonly ITestOutputHelper _out;

    public ReinspectionTests(QmsAppFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    private Microsoft.Data.SqlClient.SqlConnection Db()
    {
        using var s = _factory.Services.CreateScope();
        var cfg = s.ServiceProvider.GetRequiredService<IConfiguration>();
        return new Microsoft.Data.SqlClient.SqlConnection(cfg.GetConnectionString("Default"));
    }

    /// <summary>A Completed arrival with one item, carrying one Closed order.</summary>
    private async Task<(long ArrivalId, long QoId, string Tag)> SeedClosedOrderAsync()
    {
        var tag = $"TESTRI{Guid.NewGuid():N}".Substring(0, 12).ToUpperInvariant();
        using var c = Db();
        await c.OpenAsync();

        var arrivalId = await Dapper.SqlMapper.ExecuteScalarAsync<long>(c, @"
            INSERT INTO qms_arrival
                (arrival_no, source_system, bol_no, container_no, ebeln,
                 vendor_no, vendor_name, plant, status_code, created_at, created_by)
            VALUES (@tag, 'TEST', @tag, @tag, '4700000000',
                 'V1', 'Test vendor', 'JD01', 'Completed', SYSUTCDATETIME(), 'test');
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);", new { tag });

        await Dapper.SqlMapper.ExecuteAsync(c, @"
            INSERT INTO qms_arrival_item (arrival_id, ebeln, ebelp, material_no, material_desc, material_group)
            VALUES (@arrivalId, '4700000000', '00010', 'TESTMAT', 'Test material', 'APPLE')",
            new { arrivalId });

        await Dapper.SqlMapper.ExecuteAsync(c, @"
            INSERT INTO qms_shipment_snapshot (arrival_id, internal_shipment_no, status_code, arrival_date)
            VALUES (@arrivalId, @tag, 'Confirmed', CAST(SYSUTCDATETIME() AS DATE))",
            new { arrivalId, tag });

        var qoId = await Dapper.SqlMapper.ExecuteScalarAsync<long>(c, @"
            INSERT INTO qms_quality_order
                (quality_order_no, arrival_id, status_code, created_at, created_by,
                 opened_at, opened_by, closed_at, closed_by, close_reason)
            VALUES (@no, @arrivalId, 'Closed', SYSUTCDATETIME(), 'test',
                 SYSUTCDATETIME(), 'test', SYSUTCDATETIME(), 'test', 'test');
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
            new { no = "QO-TEST-" + tag, arrivalId });

        await Dapper.SqlMapper.ExecuteAsync(c, @"
            INSERT INTO qms_quality_order_material
                (quality_order_id, arrival_item_id, material_no, material_desc, material_group)
            SELECT @qoId, ai.arrival_item_id, ai.material_no, ai.material_desc, ai.material_group
            FROM   qms_arrival_item ai WHERE ai.arrival_id = @arrivalId",
            new { qoId, arrivalId });

        return (arrivalId, qoId, tag);
    }

    private async Task CleanupAsync(long arrivalId)
    {
        using var c = Db();
        await c.OpenAsync();
        await Dapper.SqlMapper.ExecuteAsync(c, @"
            DECLARE @qos TABLE (id BIGINT);
            INSERT INTO @qos SELECT quality_order_id FROM qms_quality_order WHERE arrival_id = @arrivalId;

            DELETE FROM qms_claim_note WHERE claim_id IN
                (SELECT claim_id FROM qms_claim WHERE quality_order_id IN (SELECT id FROM @qos));
            DELETE FROM qms_claim WHERE quality_order_id IN (SELECT id FROM @qos);
            DELETE FROM qms_status_history
             WHERE (entity_type = 'QualityOrder' AND entity_id IN (SELECT id FROM @qos))
                OR (entity_type = 'Arrival' AND entity_id = @arrivalId);
            DELETE FROM qms_quality_order_material WHERE quality_order_id IN (SELECT id FROM @qos);
            -- Children before parents: a reinspection points at its original.
            DELETE FROM qms_quality_order WHERE arrival_id = @arrivalId AND reinspection_of IS NOT NULL;
            DELETE FROM qms_quality_order WHERE arrival_id = @arrivalId;
            DELETE FROM qms_shipment_snapshot WHERE arrival_id = @arrivalId;
            DELETE FROM qms_arrival_item WHERE arrival_id = @arrivalId;
            DELETE FROM qms_arrival WHERE arrival_id = @arrivalId;", new { arrivalId });
    }

    private IQualityOrderService Qos(IServiceScope s) =>
        s.ServiceProvider.GetRequiredService<IQualityOrderService>();

    /// <summary>
    /// The whole point: a second order exists for the same container, the
    /// original survives untouched, and the new one starts empty.
    /// </summary>
    [Fact]
    public async Task A_reinspection_is_a_second_order_on_the_same_container()
    {
        var (arrivalId, qoId, _) = await SeedClosedOrderAsync();
        try
        {
            using var scope = _factory.Services.CreateScope();
            var qos = Qos(scope);

            var (newId, newNo) = await qos.ReinspectAsync(qoId, "First result looked wrong.", "test");
            _out.WriteLine($"original {qoId} -> reinspection {newId} ({newNo})");

            var original = await qos.GetAsync(qoId);
            var repeat   = await qos.GetAsync(newId);

            Assert.NotNull(original);
            Assert.NotNull(repeat);

            // Same container, two orders.
            Assert.Equal(arrivalId, repeat!.ArrivalId);
            Assert.Equal(original!.ArrivalId, repeat.ArrivalId);

            // The original is kept exactly as it was finished.
            Assert.Equal(QualityOrderStatus.Closed, original.StatusCode);
            Assert.True(original.IsSuperseded);
            Assert.Equal("test", original.SupersededBy);

            // The new one is a fresh inspection.
            Assert.Equal(QualityOrderStatus.Initial, repeat.StatusCode);
            Assert.True(repeat.IsReinspection);
            Assert.Equal(qoId, repeat.ReinspectionOf);

            // Materials copied, no samples: the work is to be done again.
            var materials = await qos.GetMaterialsAsync(newId);
            var samples   = await qos.ListSamplesAsync(newId);
            _out.WriteLine($"reinspection carries {materials.Count} material(s), {samples.Count} sample(s)");
            Assert.NotEmpty(materials);
            Assert.Empty(samples);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    /// <summary>
    /// The claim moves to the reinspection, and there is only ever ONE of them.
    ///
    /// One container, one commercial position: leaving a claim on the discarded
    /// inspection would put the container on the worklist twice carrying two
    /// different answers, which is the thing this design exists to avoid.
    /// </summary>
    [Fact]
    public async Task The_claim_moves_to_the_reinspection()
    {
        var (arrivalId, qoId, _) = await SeedClosedOrderAsync();
        try
        {
            using var scope = _factory.Services.CreateScope();
            var (newId, _) = await Qos(scope).ReinspectAsync(
                qoId, "Sampling looked unrepresentative.", "test");

            using var c = Db();
            await c.OpenAsync();

            // The original keeps its report and gives up its claim.
            var onOriginal = await Dapper.SqlMapper.ExecuteScalarAsync<int>(c,
                "SELECT COUNT(*) FROM qms_claim WHERE quality_order_id = @qoId", new { qoId });
            _out.WriteLine($"claims still on the original: {onOriginal}");
            Assert.Equal(0, onOriginal);

            // One claim across BOTH orders -- not a second raised alongside.
            var across = await Dapper.SqlMapper.ExecuteScalarAsync<int>(c,
                "SELECT COUNT(*) FROM qms_claim WHERE quality_order_id IN (@qoId, @newId)",
                new { qoId, newId });
            Assert.Equal(1, across);

            var claim = await Dapper.SqlMapper.QuerySingleAsync(c,
                "SELECT claim_status, claim_id FROM qms_claim WHERE quality_order_id = @newId",
                new { newId });
            _out.WriteLine($"claim status on the reinspection: {claim.claim_status}");
            Assert.Equal(ClaimStatus.Reinspection, (string)claim.claim_status);

            // And the reason is in the conversation the Quality Manager reads.
            var note = await Dapper.SqlMapper.ExecuteScalarAsync<string>(c,
                "SELECT TOP 1 note_text FROM qms_claim_note WHERE claim_id = @id ORDER BY note_id DESC",
                new { id = (long)claim.claim_id });
            _out.WriteLine($"note: {note}");
            Assert.Contains("unrepresentative", note);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    /// <summary>
    /// A superseded inspection is a reference copy: readable, printable, and
    /// unchangeable.
    ///
    /// Reopen is the one that actually bites. It accepts any Closed order, so
    /// without the guard the original could be reopened straight back into Open,
    /// giving the container two editable inspections -- and the unique index
    /// does not catch that, because a superseded row sits outside its filter.
    ///
    /// Asserted against the SERVICE, not the hidden button.
    /// </summary>
    [Fact]
    public async Task A_superseded_inspection_is_frozen()
    {
        var (arrivalId, qoId, _) = await SeedClosedOrderAsync();
        try
        {
            using var scope = _factory.Services.CreateScope();
            var qos = Qos(scope);
            await qos.ReinspectAsync(qoId, "The original must freeze.", "test");

            var reopen = await qos.ReopenAsync(qoId, "test", "Trying to reopen a reference copy.");
            _out.WriteLine($"reopen: {reopen.error}");
            Assert.False(reopen.ok);
            Assert.Contains("superseded", reopen.error!, StringComparison.OrdinalIgnoreCase);

            // Still Closed, still superseded -- nothing moved.
            var after = await qos.GetAsync(qoId);
            Assert.Equal(QualityOrderStatus.Closed, after!.StatusCode);
            Assert.True(after.IsSuperseded);
            Assert.True(after.IsFrozen);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    /// <summary>
    /// The conversation MOVES -- same claim_id, so its notes and every reader's
    /// last-seen marker come with it. Copying would restart the thread and
    /// re-flag already-read notes as unread for everybody.
    /// </summary>
    [Fact]
    public async Task An_existing_conversation_follows_the_claim()
    {
        var (arrivalId, qoId, _) = await SeedClosedOrderAsync();
        try
        {
            using var scope = _factory.Services.CreateScope();
            var claims = scope.ServiceProvider.GetRequiredService<IClaimService>();

            // A real thread on the original, and a reader who has caught up.
            var raised = await claims.MarkClaimRequestAsync(
                qoId, "Brix below spec on four pallets.", "qm-user", "QualityManager");
            Assert.True(raised.ok, raised.error);
            await claims.MarkSeenAsync(qoId, "reader");

            using var c = Db();
            await c.OpenAsync();
            var claimIdBefore = await Dapper.SqlMapper.ExecuteScalarAsync<long>(c,
                "SELECT claim_id FROM qms_claim WHERE quality_order_id = @qoId", new { qoId });

            var (newId, _) = await Qos(scope).ReinspectAsync(
                qoId, "Sampling looked unrepresentative.", "test");

            var claimIdAfter = await Dapper.SqlMapper.ExecuteScalarAsync<long>(c,
                "SELECT claim_id FROM qms_claim WHERE quality_order_id = @newId", new { newId });
            _out.WriteLine($"claim {claimIdBefore} -> order {newId} as claim {claimIdAfter}");

            // The SAME row moved. This is the whole reason the children survive.
            Assert.Equal(claimIdBefore, claimIdAfter);

            // Its notes came too: the original request plus the reinspection note.
            var notes = (await Dapper.SqlMapper.QueryAsync<string>(c,
                "SELECT note_text FROM qms_claim_note WHERE claim_id = @id ORDER BY note_id",
                new { id = claimIdAfter })).ToList();
            _out.WriteLine($"notes carried over: {notes.Count}");
            Assert.Equal(2, notes.Count);
            Assert.Contains(notes, n => n.Contains("Brix below spec"));

            // And the reader is still caught up -- no false unread flood.
            var markers = await Dapper.SqlMapper.ExecuteScalarAsync<int>(c,
                "SELECT COUNT(*) FROM qms_claim_read_marker WHERE claim_id = @id AND user_name = 'reader'",
                new { id = claimIdAfter });
            Assert.Equal(1, markers);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    /// <summary>
    /// The highest-value guard in this change.
    ///
    /// Once the claim moves, a lookup by the original's id finds nothing, so the
    /// chat panel's "no claim yet" branch would let a Quality Manager raise a
    /// SECOND claim there. UQ_qms_claim_qo does not stop it -- the two would sit
    /// on different orders. Without the superseded arm this fails with two
    /// claims on one container.
    /// </summary>
    [Fact]
    public async Task The_superseded_original_cannot_start_a_second_claim()
    {
        var (arrivalId, qoId, _) = await SeedClosedOrderAsync();
        try
        {
            using var scope = _factory.Services.CreateScope();
            var (newId, _) = await Qos(scope).ReinspectAsync(qoId, "Claim moves away.", "test");
            var claims = scope.ServiceProvider.GetRequiredService<IClaimService>();

            var second = await claims.MarkClaimRequestAsync(
                qoId, "Raising a claim on the discarded inspection.", "qm-user", "QualityManager");
            _out.WriteLine($"second claim attempt: {second.error}");
            Assert.False(second.ok);
            Assert.Contains("superseded", second.error!, StringComparison.OrdinalIgnoreCase);

            using var c = Db();
            await c.OpenAsync();
            var across = await Dapper.SqlMapper.ExecuteScalarAsync<int>(c,
                "SELECT COUNT(*) FROM qms_claim WHERE quality_order_id IN (@qoId, @newId)",
                new { qoId, newId });
            _out.WriteLine($"claims across both orders: {across}");
            Assert.Equal(1, across);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    /// <summary>
    /// No verdict while the inspection that produces it is still being carried
    /// out -- but the thread stays open, which is the point of moving it rather
    /// than starting a new one.
    /// </summary>
    [Fact]
    public async Task The_claim_waits_for_the_reinspection_but_the_conversation_does_not()
    {
        var (arrivalId, qoId, _) = await SeedClosedOrderAsync();
        try
        {
            using var scope = _factory.Services.CreateScope();
            var (newId, _) = await Qos(scope).ReinspectAsync(qoId, "Not decidable yet.", "test");
            var claims = scope.ServiceProvider.GetRequiredService<IClaimService>();

            var decide = await claims.MarkClaimRequestAsync(newId, "Too early.", "qm-user", "QualityManager");
            _out.WriteLine($"decision while open: {decide.error}");
            Assert.False(decide.ok);
            Assert.Contains("reinspection", decide.error!, StringComparison.OrdinalIgnoreCase);

            // Speech is not a verdict.
            var note = await claims.AddNoteAsync(
                newId, "Any news from the warehouse?", "qm-user", "QualityManager");
            _out.WriteLine($"note while open: ok={note.ok} {note.error}");
            Assert.True(note.ok, note.error);

            // Finish the reinspection and the decision becomes available. Set
            // directly: the inspection workflow is not what is under test here.
            using var c = Db();
            await c.OpenAsync();
            await Dapper.SqlMapper.ExecuteAsync(c, @"
                UPDATE qms_quality_order
                SET    status_code = 'Closed', closed_at = SYSUTCDATETIME(), closed_by = 'test'
                WHERE  quality_order_id = @newId", new { newId });

            var now = await claims.MarkClaimRequestAsync(newId, "Second inspection agrees.", "qm-user", "QualityManager");
            _out.WriteLine($"decision once closed: ok={now.ok} {now.error}");
            Assert.True(now.ok, now.error);

            var status = await Dapper.SqlMapper.ExecuteScalarAsync<string>(c,
                "SELECT claim_status FROM qms_claim WHERE quality_order_id = @newId", new { newId });
            Assert.Equal(ClaimStatus.ClaimRequest, status);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    /// <summary>
    /// A reinspection is Initial or Open for its whole life, so it would
    /// otherwise be deletable throughout -- and it holds the container's claim.
    /// QoCascade deletes claims by quality_order_id, so deleting it would take
    /// the entire thread, its notes and every read marker with it.
    /// </summary>
    [Fact]
    public async Task Deleting_a_reinspection_does_not_destroy_the_claim_history()
    {
        var (arrivalId, qoId, _) = await SeedClosedOrderAsync();
        try
        {
            using var scope = _factory.Services.CreateScope();
            var qos = Qos(scope);
            var (newId, _) = await qos.ReinspectAsync(qoId, "Must not be deletable.", "test");

            var del = await qos.DeleteAsync(newId, "test");
            _out.WriteLine($"delete: {del.error}");
            Assert.False(del.ok);
            Assert.Contains("reinspection", del.error!, StringComparison.OrdinalIgnoreCase);

            using var c = Db();
            await c.OpenAsync();
            var claims = await Dapper.SqlMapper.ExecuteScalarAsync<int>(c,
                "SELECT COUNT(*) FROM qms_claim WHERE quality_order_id IN (@qoId, @newId)",
                new { qoId, newId });
            Assert.Equal(1, claims);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    /// <summary>
    /// One reinspection per container, refused by the DATABASE as well as the
    /// service — a second request arriving in the same instant cannot slip past
    /// a C# check.
    /// </summary>
    [Fact]
    public async Task A_container_is_reinspected_only_once()
    {
        var (arrivalId, qoId, _) = await SeedClosedOrderAsync();
        try
        {
            using var scope = _factory.Services.CreateScope();
            var qos = Qos(scope);
            var (newId, _) = await qos.ReinspectAsync(qoId, "First attempt at reinspection.", "test");

            // The original cannot be reinspected twice.
            var again = await Assert.ThrowsAsync<InvalidOperationException>(
                () => qos.ReinspectAsync(qoId, "Trying a second time.", "test"));
            _out.WriteLine($"second attempt on the original -> {again.Message}");
            Assert.Contains("already been reinspected", again.Message);

            // Nor can the reinspection itself be reinspected -- and it is not
            // Closed yet either, so the status guard speaks first.
            var chained = await Assert.ThrowsAsync<InvalidOperationException>(
                () => qos.ReinspectAsync(newId, "Trying to chain.", "test"));
            _out.WriteLine($"attempt on the reinspection -> {chained.Message}");
        }
        finally { await CleanupAsync(arrivalId); }
    }

    /// <summary>Each refusal says something different, because each is a different mistake.</summary>
    [Fact]
    public async Task The_guards_each_refuse_with_their_own_reason()
    {
        var (arrivalId, qoId, _) = await SeedClosedOrderAsync();
        try
        {
            using var scope = _factory.Services.CreateScope();
            var qos = Qos(scope);

            var noReason = await Assert.ThrowsAsync<InvalidOperationException>(
                () => qos.ReinspectAsync(qoId, "  ", "test"));
            Assert.Contains("Say why", noReason.Message);

            using (var c = Db())
            {
                await c.OpenAsync();
                await Dapper.SqlMapper.ExecuteAsync(c,
                    "UPDATE qms_quality_order SET archived_at = SYSUTCDATETIME() WHERE quality_order_id = @qoId",
                    new { qoId });
            }
            var archived = await Assert.ThrowsAsync<InvalidOperationException>(
                () => qos.ReinspectAsync(qoId, "Archived order.", "test"));
            _out.WriteLine($"archived -> {archived.Message}");
            Assert.Contains("archived", archived.Message, StringComparison.OrdinalIgnoreCase);

            using (var c = Db())
            {
                await c.OpenAsync();
                await Dapper.SqlMapper.ExecuteAsync(c, @"
                    UPDATE qms_quality_order
                    SET    archived_at = NULL, status_code = 'Open'
                    WHERE  quality_order_id = @qoId", new { qoId });
            }
            var notClosed = await Assert.ThrowsAsync<InvalidOperationException>(
                () => qos.ReinspectAsync(qoId, "Not finished yet.", "test"));
            _out.WriteLine($"not finished -> {notClosed.Message}");
            Assert.Contains("finished", notClosed.Message, StringComparison.OrdinalIgnoreCase);

            using (var c = Db())
            {
                await c.OpenAsync();
                await Dapper.SqlMapper.ExecuteAsync(c, @"
                    UPDATE qms_quality_order
                    SET    status_code = 'Closed', container_rejected = 1
                    WHERE  quality_order_id = @qoId", new { qoId });
            }
            var rejected = await Assert.ThrowsAsync<InvalidOperationException>(
                () => qos.ReinspectAsync(qoId, "Rejected container.", "test"));
            _out.WriteLine($"rejected container -> {rejected.Message}");
            Assert.Contains("refused on arrival", rejected.Message);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    /// <summary>
    /// Nothing is left behind when a guard refuses. A supersede stamp without
    /// an order would strand the container with no live inspection at all.
    /// </summary>
    [Fact]
    public async Task A_refused_reinspection_leaves_nothing_behind()
    {
        var (arrivalId, qoId, _) = await SeedClosedOrderAsync();
        try
        {
            using var scope = _factory.Services.CreateScope();
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => Qos(scope).ReinspectAsync(qoId, "no", "test"));   // too short

            using var c = Db();
            await c.OpenAsync();
            var counts = await Dapper.SqlMapper.QuerySingleAsync(c, @"
                SELECT (SELECT COUNT(*) FROM qms_quality_order WHERE arrival_id = @arrivalId) AS Orders,
                       (SELECT COUNT(*) FROM qms_quality_order
                         WHERE arrival_id = @arrivalId AND superseded_at IS NOT NULL) AS Superseded",
                new { arrivalId });
            _out.WriteLine($"orders {counts.Orders}, superseded {counts.Superseded}");
            Assert.Equal(1, (int)counts.Orders);
            Assert.Equal(0, (int)counts.Superseded);
        }
        finally { await CleanupAsync(arrivalId); }
    }
}
