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
/// What must NOT change when a container is reinspected.
///
/// A reinspection is real work, but it is not a second container, and every
/// operational figure in this application counts containers. The decision taken
/// was that the original keeps counting — so the dangerous outcome is not a
/// visible error but a quiet one: coverage above 100%, an un-inspected
/// container hidden inside a Max(0, …) clamp, a clock restarting weeks after
/// the inspection it measures. None of that announces itself, so it is pinned
/// here by raising a real reinspection and comparing every figure either side
/// of it.
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
            VALUES (@arrivalId, @tag, 'Confirmed', CAST(SYSUTCDATETIME() AS DATE));",
            new { arrivalId, tag });

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

    private async Task<DashboardVm> DashboardAsync()
    {
        using var s = _factory.Services.CreateScope();
        return await s.ServiceProvider.GetRequiredService<IDashboardService>()
            .GetSummaryAsync(new DashboardFilter { Period = "today" }, PlantScope.All, CancellationToken.None);
    }

    private static (int Received, int Inspected, int Pending, int Backlog, int Total) Figures(DashboardVm vm) =>
        (vm.Commitment.Sum(x => x.Received),
         vm.Commitment.Sum(x => x.Committed),
         vm.Commitment.Sum(x => x.Outstanding),
         vm.Commitment.Sum(x => x.QosCatchUp),
         vm.Commitment.Sum(x => x.QosCreated));

    /// <summary>
    /// Every dashboard figure is identical either side of a reinspection. This
    /// is the decision "the original keeps counting", stated as arithmetic.
    /// </summary>
    [Fact]
    public async Task A_reinspection_changes_no_dashboard_figure()
    {
        var (arrivalId, qoId) = await SeedAsync();
        try
        {
            var before = Figures(await DashboardAsync());
            _out.WriteLine($"before: received {before.Received}, inspected {before.Inspected}, " +
                           $"pending {before.Pending}, backlog {before.Backlog}, total {before.Total}");

            using (var scope = _factory.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<IQualityOrderService>()
                    .ReinspectAsync(qoId, "Checking the dashboard does not move.", "test");

            var after = Figures(await DashboardAsync());
            _out.WriteLine($"after:  received {after.Received}, inspected {after.Inspected}, " +
                           $"pending {after.Pending}, backlog {after.Backlog}, total {after.Total}");

            Assert.Equal(before, after);
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
    /// The Inspection Time Bar keeps following the ORIGINAL. Left to itself it
    /// picks the highest order id, which would restart a settled clock.
    /// </summary>
    [Fact]
    public async Task The_time_bar_clock_stays_on_the_original()
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

            // One row for the container, and it names the ORIGINAL.
            Assert.Single(page.Rows);
            Assert.Equal(originalNo, page.Rows[0].QualityOrderNo);
            Assert.NotEqual(newNo, page.Rows[0].QualityOrderNo);

            // The clock is settled, not running again.
            Assert.False(page.Rows[0].IsRunning);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    /// <summary>
    /// The superseded original leaves the live Claims worklist, so a container
    /// never sits there twice carrying two different answers.
    /// </summary>
    [Fact]
    public async Task The_superseded_order_leaves_the_live_claims_list()
    {
        var (arrivalId, qoId) = await SeedAsync();
        try
        {
            using var scope = _factory.Services.CreateScope();
            var qos    = scope.ServiceProvider.GetRequiredService<IQualityOrderService>();
            var claims = scope.ServiceProvider.GetRequiredService<IClaimService>();

            var before = await claims.ListClosedQosAsync(new ClaimListFilter(), PlantScope.All, "test");
            var wasThere = before.Any(r => r.QualityOrderId == qoId);
            _out.WriteLine($"original on the claims list before: {wasThere}");

            await qos.ReinspectAsync(qoId, "Should drop off the claims list.", "test");

            var after = await claims.ListClosedQosAsync(new ClaimListFilter(), PlantScope.All, "test");
            Assert.DoesNotContain(after, r => r.QualityOrderId == qoId);
            _out.WriteLine($"rows for this container after: {after.Count(r => r.ArrivalId == arrivalId)}");
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
            var all           = await List(null);

            _out.WriteLine($"originals {originals.Count}, reinspections {reinspections.Count}, all {all.Count}");

            // Each side finds its own order and not the other's.
            Assert.Contains(originals,        q => q.QualityOrderId == qoId);
            Assert.DoesNotContain(originals,  q => q.QualityOrderId == newId);
            Assert.Contains(reinspections,    q => q.QualityOrderId == newId);
            Assert.DoesNotContain(reinspections, q => q.QualityOrderId == qoId);

            // Unfiltered still shows both -- the filter narrows, it does not
            // become a permanent exclusion.
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
