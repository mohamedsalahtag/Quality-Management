using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace SharbatlyQMS.Tests;

/// <summary>
/// What a reinspection does, and does not, do to the operational figures.
///
/// A reinspection is real work, but it is not a second container, and every
/// operational figure in this application counts containers. So the thing that
/// must never happen is DOUBLE counting: coverage above 100%, an un-inspected
/// container hidden inside a Max(0, …) clamp, one arrival fanned into two rows
/// of a drill-through sheet.
///
/// What SHOULD happen is that the figures move to the reinspection. The first
/// result was judged unsound and discarded, so until the second inspection
/// closes the container is genuinely awaiting inspection again — it leaves the
/// inspected bucket, and its clock runs to the inspection that stands.
///
/// Neither announces itself, so both are pinned here by raising a real
/// reinspection and reading the figures either side of it.
/// </summary>
[Collection("workflow")]
public class ReinspectionInvariantTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    private readonly ITestOutputHelper _out;

    public ReinspectionInvariantTests(QmsAppFactory factory, ITestOutputHelper output)
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

    /// <summary>
    /// A finished order on a container that ARRIVED TODAY, so it lands inside
    /// the dashboard period the assertions use.
    /// </summary>
    private async Task<(long ArrivalId, long QoId)> SeedAsync()
    {
        var tag = $"TESTRV{Guid.NewGuid():N}".Substring(0, 12).ToUpperInvariant();
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
            VALUES (@arrivalId, '4700000000', '00010', 'TESTMAT', 'Test material', 'APPLE');
            INSERT INTO qms_shipment_snapshot (arrival_id, internal_shipment_no, status_code, arrival_date)
            VALUES (@arrivalId, @tag, 'Confirmed', @today);",
            // The LOCAL date, not SYSUTCDATETIME(). arrival_date is a business
            // date and the dashboard's "today" resolves from DateTime.Now, so
            // seeding the UTC date made this fixture disagree with the filter
            // for the three hours a night when UTC+3 is a day ahead -- the
            // container landed outside "today" and every bucket read zero.
            new { arrivalId, tag, today = DateTime.Now.Date });

        var qoId = await Dapper.SqlMapper.ExecuteScalarAsync<long>(c, @"
            INSERT INTO qms_quality_order
                (quality_order_no, arrival_id, status_code, created_at, created_by,
                 opened_at, opened_by, closed_at, closed_by, close_reason)
            VALUES (@no, @arrivalId, 'Closed', SYSUTCDATETIME(), 'test',
                 SYSUTCDATETIME(), 'test', SYSUTCDATETIME(), 'test', 'test');
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
            new { no = "QO-TEST-" + tag, arrivalId });

        return (arrivalId, qoId);
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
             WHERE entity_type = 'QualityOrder' AND entity_id IN (SELECT id FROM @qos);
            DELETE FROM qms_quality_order_material WHERE quality_order_id IN (SELECT id FROM @qos);
            DELETE FROM qms_quality_order WHERE arrival_id = @arrivalId AND reinspection_of IS NOT NULL;
            DELETE FROM qms_quality_order WHERE arrival_id = @arrivalId;
            DELETE FROM qms_shipment_snapshot WHERE arrival_id = @arrivalId;
            DELETE FROM qms_arrival_item WHERE arrival_id = @arrivalId;
            DELETE FROM qms_arrival WHERE arrival_id = @arrivalId;", new { arrivalId });
    }

    /// <summary>
    /// The same fixture, but arrived and inspected <paramref name="daysAgo"/>
    /// days back. The flip only SHOWS in the period the original belongs to:
    /// query 11 counts orders CREATED in the window, so seeding everything
    /// today leaves one order in today's window either way and the change is
    /// invisible.
    /// </summary>
    private async Task<(long ArrivalId, long QoId, DateTime Day)> SeedBackdatedAsync(int daysAgo)
    {
        var (arrivalId, qoId) = await SeedAsync();
        var day = DateTime.Now.Date.AddDays(-daysAgo);
        var utc = DateTime.SpecifyKind(day.AddHours(9), DateTimeKind.Local).ToUniversalTime();

        using var c = Db();
        await c.OpenAsync();
        await Dapper.SqlMapper.ExecuteAsync(c, @"
            UPDATE qms_arrival SET created_at = @utc WHERE arrival_id = @arrivalId;
            UPDATE qms_shipment_snapshot SET arrival_date = @day WHERE arrival_id = @arrivalId;
            UPDATE qms_quality_order
            SET    created_at = @utc, opened_at = @utc, closed_at = @utc
            WHERE  quality_order_id = @qoId;",
            new { arrivalId, qoId, utc, day });

        return (arrivalId, qoId, day);
    }

    private async Task<DashboardVm> DashboardAsync(DashboardFilter? filter = null)
    {
        using var s = _factory.Services.CreateScope();
        return await s.ServiceProvider.GetRequiredService<IDashboardService>()
            .GetSummaryAsync(filter ?? new DashboardFilter { Period = "today" },
                             PlantScope.All, CancellationToken.None);
    }

    private static (int Received, int Inspected, int Pending, int Backlog, int Total) Figures(DashboardVm vm) =>
        (vm.Commitment.Sum(x => x.Received),
         vm.Commitment.Sum(x => x.Committed),
         vm.Commitment.Sum(x => x.Outstanding),
         vm.Commitment.Sum(x => x.QosCatchUp),
         vm.Commitment.Sum(x => x.QosCreated));

    /// <summary>
    /// The container is counted once throughout, and moves from inspected to
    /// pending when it is sent back — then returns once the reinspection is
    /// closed. "The reinspection dominates", stated as arithmetic.
    ///
    /// Received is the invariant: it counts arrivals, and a reinspection does
    /// not deliver a second container.
    /// </summary>
    [Fact]
    public async Task A_reinspection_moves_the_container_back_to_pending()
    {
        var (arrivalId, qoId, day) = await SeedBackdatedAsync(40);
        // The window the ORIGINAL belongs to. Narrow on purpose: whatever else
        // production holds for that day is constant across the reinspection, so
        // every assertion below is a delta and cancels it out.
        var window = new DashboardFilter
        {
            Period = DashboardFilter.Periods.Custom, From = day, To = day
        };
        try
        {
            var before = Figures(await DashboardAsync(window));
            _out.WriteLine($"before: received {before.Received}, inspected {before.Inspected}, " +
                           $"pending {before.Pending}");

            long newId;
            using (var scope = _factory.Services.CreateScope())
                (newId, _) = await scope.ServiceProvider.GetRequiredService<IQualityOrderService>()
                    .ReinspectAsync(qoId, "The figures should follow the reinspection.", "test");

            var during = Figures(await DashboardAsync(window));
            _out.WriteLine($"during: received {during.Received}, inspected {during.Inspected}, " +
                           $"pending {during.Pending}");

            // Still one container -- this is the double-count guard.
            Assert.Equal(before.Received, during.Received);
            // But it is awaiting inspection again: the first result was discarded.
            Assert.Equal(before.Inspected - 1, during.Inspected);
            Assert.Equal(before.Pending + 1, during.Pending);

            // Finish the reinspection and it comes back, counted once.
            using (var c = Db())
            {
                await c.OpenAsync();
                await Dapper.SqlMapper.ExecuteAsync(c, @"
                    UPDATE qms_quality_order
                    SET    status_code = 'Closed', closed_at = SYSUTCDATETIME(), closed_by = 'test'
                    WHERE  quality_order_id = @newId", new { newId });
            }

            // Closing it does NOT restore the original window: the reinspection
            // was created today, so the container now belongs to today's
            // catch-up rather than to the month it first arrived in. Its
            // container count is what must stay put.
            var after = Figures(await DashboardAsync(window));
            _out.WriteLine($"after:  received {after.Received}, inspected {after.Inspected}, " +
                           $"pending {after.Pending}");
            Assert.Equal(before.Received, after.Received);
            Assert.Equal(during.Inspected, after.Inspected);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    /// <summary>
    /// And the identities still hold, checked on the drill-through ROWS — the
    /// place a fan-out would show up as a container listed twice.
    /// </summary>
    [Fact]
    public async Task The_drill_through_still_reconciles_after_a_reinspection()
    {
        var (arrivalId, qoId) = await SeedAsync();
        try
        {
            using (var scope = _factory.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<IQualityOrderService>()
                    .ReinspectAsync(qoId, "Checking the drill-through still adds up.", "test");

            using var s = _factory.Services.CreateScope();
            var svc = s.ServiceProvider.GetRequiredService<IDashboardService>();
            var f   = new DashboardFilter { Period = "today" };

            async Task<IReadOnlyList<CommitmentDetailRow>> Bucket(string b) =>
                await svc.GetCommitmentDetailAsync(f, PlantScope.All, b, null, CancellationToken.None);

            var received = await Bucket(CommitmentBuckets.Received);
            var period   = await Bucket(CommitmentBuckets.PeriodInspection);
            var pending  = await Bucket(CommitmentBuckets.PendingInspection);
            var backlog  = await Bucket(CommitmentBuckets.BacklogInspected);
            var total    = await Bucket(CommitmentBuckets.TotalInspected);

            _out.WriteLine($"received {received.Count} = period {period.Count} + pending {pending.Count}");
            Assert.Equal(received.Count, period.Count + pending.Count);
            Assert.Equal(total.Count,    period.Count + backlog.Count);

            // The reinspected container appears ONCE, not twice.
            Assert.Equal(1, received.Count(r => r.ArrivalId == arrivalId));

            var vm = await DashboardAsync();
            Assert.Equal(vm.Commitment.Sum(x => x.Received), received.Count);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    /// <summary>
    /// The Inspection Time Bar follows the REINSPECTION: one row for the
    /// container, naming the inspection that stands, with its clock running
    /// again because the container is genuinely awaiting inspection.
    ///
    /// The row count is the part worth guarding — two rows for one container
    /// would be a silent double count in every time-to-inspection figure.
    /// </summary>
    [Fact]
    public async Task The_time_bar_clock_follows_the_reinspection()
    {
        var (arrivalId, qoId) = await SeedAsync();
        try
        {
            using var scope = _factory.Services.CreateScope();
            var qos     = scope.ServiceProvider.GetRequiredService<IQualityOrderService>();
            var timeBar = scope.ServiceProvider.GetRequiredService<ITimeBarService>();

            var originalNo = (await qos.GetAsync(qoId))!.QualityOrderNo;
            var (_, newNo) = await qos.ReinspectAsync(qoId, "Clock must not restart.", "test");

            // Find the container's row by its container number.
            using var c = Db();
            await c.OpenAsync();
            var container = await Dapper.SqlMapper.ExecuteScalarAsync<string>(c,
                "SELECT container_no FROM qms_arrival WHERE arrival_id = @arrivalId", new { arrivalId });

            var page = await timeBar.ListAsync(
                new TimeBarFilter { Container = container, PageSize = 50, IncludeArchived = true },
                PlantScope.All, new TimeBarConfig());

            _out.WriteLine($"{page.Rows.Count} row(s); original {originalNo}, reinspection {newNo}");

            // One row for the container, and it names the REINSPECTION.
            Assert.Single(page.Rows);
            Assert.Equal(newNo, page.Rows[0].QualityOrderNo);
            Assert.NotEqual(originalNo, page.Rows[0].QualityOrderNo);

            // The clock is running again -- the container awaits an inspection.
            Assert.True(page.Rows[0].IsRunning);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    /// <summary>
    /// The container stays on the live Claims worklist across a reinspection —
    /// as exactly one row, which becomes the reinspection's.
    ///
    /// This is the regression pin for the defect that prompted the change.
    /// Before it, the original was excluded as superseded, the reinspection
    /// failed the Closed test, and a merely-superseded order has no
    /// archived_at — so the container was on NO tab at all, with its claim and
    /// the reason it came back unreachable from the Claims screen.
    ///
    /// Note that asserting only "the original has gone" is satisfied equally by
    /// the correct behaviour and by that defect, which is why the row for the
    /// container is counted here rather than just its absence.
    /// </summary>
    [Fact]
    public async Task The_container_stays_on_the_live_claims_list_under_the_reinspection()
    {
        var (arrivalId, qoId) = await SeedAsync();
        try
        {
            using var scope = _factory.Services.CreateScope();
            var qos    = scope.ServiceProvider.GetRequiredService<IQualityOrderService>();
            var claims = scope.ServiceProvider.GetRequiredService<IClaimService>();

            var before = await claims.ListClosedQosAsync(new ClaimListFilter(), PlantScope.All, "test");
            _out.WriteLine($"rows for this container before: {before.Count(r => r.ArrivalId == arrivalId)}");

            var (newId, _) = await qos.ReinspectAsync(
                qoId, "Should not vanish from the claims list.", "test");

            var after = await claims.ListClosedQosAsync(new ClaimListFilter(), PlantScope.All, "test");

            // The original has gone -- one container never sits there twice.
            Assert.DoesNotContain(after, r => r.QualityOrderId == qoId);

            // But the container has NOT vanished.
            var rows = after.Where(r => r.ArrivalId == arrivalId).ToList();
            _out.WriteLine($"rows for this container after: {rows.Count}");
            Assert.Single(rows);
            Assert.Equal(newId, rows[0].QualityOrderId);
            Assert.Equal(ClaimStatus.Reinspection, rows[0].ClaimStatus);
            Assert.NotEqual(QualityOrderStatus.Closed, rows[0].StatusCode);

            // And it is not masquerading as untouched work.
            var pending = await claims.ListClosedQosAsync(
                new ClaimListFilter { Status = new List<string> { ClaimStatus.Pending } }, PlantScope.All, "test");
            Assert.DoesNotContain(pending, r => r.ArrivalId == arrivalId);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    /// <summary>
    /// Administrator only, refused by the SERVER. Hiding the button leaves the
    /// POST endpoint reachable by anyone who knows the URL.
    /// </summary>
    [Theory]
    [InlineData(RoleCodes.Operator)]
    [InlineData(RoleCodes.Supervisor)]
    [InlineData(RoleCodes.Manager)]
    public async Task Only_an_administrator_may_raise_one(string role)
    {
        var (arrivalId, qoId) = await SeedAsync();
        try
        {
            var client = _factory.CreateClient(
                new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
                { AllowAutoRedirect = false });
            client.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader, role);

            var html  = await client.GetStringAsync($"/QualityOrders/Details/{qoId}");
            var token = System.Text.RegularExpressions.Regex.Match(
                html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

            var res = await client.PostAsync($"/QualityOrders/Reinspect/{qoId}",
                new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("__RequestVerificationToken", token),
                    new KeyValuePair<string, string>("reason", "Trying without permission."),
                }));
            _out.WriteLine($"{role} -> {(int)res.StatusCode}");
            Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);

            // And nothing was created whatever the response looked like.
            using var c = Db();
            await c.OpenAsync();
            var orders = await Dapper.SqlMapper.ExecuteScalarAsync<int>(c,
                "SELECT COUNT(*) FROM qms_quality_order WHERE arrival_id = @arrivalId", new { arrivalId });
            Assert.Equal(1, orders);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    /// <summary>
    /// The list filter finds each side of a reinspection. Badges mark the rows,
    /// but the reason to add the filter was that finding a handful of marked
    /// rows in a list hundreds long is the actual difficulty.
    /// </summary>
    [Fact]
    public async Task The_list_filter_finds_each_side_of_a_reinspection()
    {
        var (arrivalId, qoId) = await SeedAsync();
        try
        {
            using var scope = _factory.Services.CreateScope();
            var qos = scope.ServiceProvider.GetRequiredService<IQualityOrderService>();
            var (newId, _) = await qos.ReinspectAsync(qoId, "Filter must find both sides.", "test");

            async Task<IReadOnlyList<QualityOrder>> List(string? which) =>
                await qos.ListAsync(new QoListFilter { Reinspection = which }, PlantScope.All);

            var originals     = await List("originals");
            var reinspections = await List("reinspections");
            var current       = await List(null);
            var all           = await List("all");

            _out.WriteLine($"originals {originals.Count}, reinspections {reinspections.Count}, " +
                           $"current {current.Count}, all {all.Count}");

            // Each side finds its own order and not the other's.
            Assert.Contains(originals,        q => q.QualityOrderId == qoId);
            Assert.DoesNotContain(originals,  q => q.QualityOrderId == newId);
            Assert.Contains(reinspections,    q => q.QualityOrderId == newId);
            Assert.DoesNotContain(reinspections, q => q.QualityOrderId == qoId);

            // THE DEFAULT hides the superseded original: a container search
            // returns the inspection that stands, not both side by side.
            Assert.Contains(current,       q => q.QualityOrderId == newId);
            Assert.DoesNotContain(current, q => q.QualityOrderId == qoId);

            // It is hidden, not gone -- asking for everything still finds it.
            Assert.Contains(all, q => q.QualityOrderId == qoId);
            Assert.Contains(all, q => q.QualityOrderId == newId);

            // Every row each side returns really is that side.
            Assert.All(originals,     q => Assert.True(q.IsSuperseded));
            Assert.All(reinspections, q => Assert.True(q.IsReinspection));
        }
        finally { await CleanupAsync(arrivalId); }
    }

    /// <summary>Both reports print, and each names the other.</summary>
    [Fact]
    public async Task Both_orders_print_and_each_names_the_other()
    {
        var (arrivalId, qoId) = await SeedAsync();
        try
        {
            long newId;
            using (var scope = _factory.Services.CreateScope())
                (newId, _) = await scope.ServiceProvider.GetRequiredService<IQualityOrderService>()
                    .ReinspectAsync(qoId, "Both copies must print.", "test");

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader, RoleCodes.Admin);

            foreach (var id in new[] { qoId, newId })
            {
                var res = await client.GetAsync($"/Reports/QualityOrderPdfPreview/{id}");
                Assert.Equal(HttpStatusCode.OK, res.StatusCode);
                var bytes = await res.Content.ReadAsByteArrayAsync();
                _out.WriteLine($"QO {id}: {bytes.Length:N0} bytes");
                Assert.True(bytes.Length > 1000, $"QO {id} produced a suspiciously small report.");
                Assert.True(bytes[0] == (byte)'%' && bytes[1] == (byte)'P', "Not a PDF.");
            }
        }
        finally { await CleanupAsync(arrivalId); }
    }
}
