using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Clicking a number on the Received vs Inspected portlet.
///
/// The whole value of a drill-through is that it answers with the rows that
/// were counted. If the detail page approximated the figure with list-page
/// filters it would land near it and not on it, and the first person to count
/// the rows would stop trusting both screens. So the detail query shares the
/// portlet's definitions, and these tests hold the two to each other.
/// </summary>
[Collection("workflow")]
public class CommitmentDetailTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    private readonly ITestOutputHelper _out;

    public CommitmentDetailTests(QmsAppFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    // TODAY, on purpose. It is the sharpest window: a container SAP received
    // today whose inspection was opened yesterday -- SAP posts the branch
    // receipt after the inspection has begun on about one container in a
    // hundred -- must count as inspected, not pending, and only a one-day
    // window makes that case visible. (This used to read "month", which is not
    // a real period key and silently resolved to today anyway.)
    private const string Period = DashboardFilter.Periods.Today;

    private async Task<(DashboardVm Vm, IDashboardService Svc, IServiceScope Scope)> LoadAsync()
    {
        var scope = _factory.Services.CreateScope();
        var svc   = scope.ServiceProvider.GetRequiredService<IDashboardService>();
        var vm    = await svc.GetSummaryAsync(new DashboardFilter { Period = Period },
                                              PlantScope.All, CancellationToken.None);
        return (vm, svc, scope);
    }

    private static Task<IReadOnlyList<CommitmentDetailRow>> DetailAsync(
        IDashboardService svc, string bucket, string? plant = null) =>
        svc.GetCommitmentDetailAsync(new DashboardFilter { Period = Period },
                                     PlantScope.All, bucket, plant, CancellationToken.None);

    /// <summary>
    /// Every bucket's row count equals the number printed in its column. This
    /// is the assertion the feature exists for.
    /// </summary>
    [Fact]
    public async Task Each_bucket_returns_exactly_the_rows_its_column_counted()
    {
        var (vm, svc, scope) = await LoadAsync();
        using (scope)
        {
            var expected = new Dictionary<string, int>
            {
                [CommitmentBuckets.Received]          = vm.Commitment.Sum(c => c.Received),
                [CommitmentBuckets.PeriodInspection]  = vm.Commitment.Sum(c => c.Committed),
                [CommitmentBuckets.PendingInspection] = vm.Commitment.Sum(c => c.Outstanding),
                [CommitmentBuckets.BacklogInspected]  = vm.Commitment.Sum(c => c.QosCatchUp),
                [CommitmentBuckets.TotalInspected]    = vm.Commitment.Sum(c => c.QosCreated),
            };

            foreach (var (bucket, count) in expected)
            {
                var rows = await DetailAsync(svc, bucket);
                _out.WriteLine($"{CommitmentBuckets.Caption(bucket),-20} column {count,5}   rows {rows.Count,5}");
                Assert.Equal(count, rows.Count);
            }
        }
    }

    /// <summary>
    /// The two identities the portlet promises, checked on the ROWS rather than
    /// on the numbers -- the numbers could agree with each other and still both
    /// be wrong about which containers they describe.
    /// </summary>
    [Fact]
    public async Task The_buckets_partition_the_period_exactly()
    {
        var (_, svc, scope) = await LoadAsync();
        using (scope)
        {
            var received = await DetailAsync(svc, CommitmentBuckets.Received);
            var period   = await DetailAsync(svc, CommitmentBuckets.PeriodInspection);
            var pending  = await DetailAsync(svc, CommitmentBuckets.PendingInspection);
            var backlog  = await DetailAsync(svc, CommitmentBuckets.BacklogInspected);
            var total    = await DetailAsync(svc, CommitmentBuckets.TotalInspected);

            _out.WriteLine($"received {received.Count} = period {period.Count} + pending {pending.Count}");
            _out.WriteLine($"total    {total.Count} = backlog {backlog.Count} + on-period + ahead-of-receipt");

            // Identity 1, exact: every container received in the period is either
            // inspected or pending, and never both.
            Assert.Equal(received.Count, period.Count + pending.Count);
            var periodIds  = period.Select(r => r.ArrivalId).ToHashSet();
            var pendingIds = pending.Select(r => r.ArrivalId).ToHashSet();
            Assert.Empty(periodIds.Intersect(pendingIds));
            Assert.Equal(received.Select(r => r.ArrivalId).ToHashSet(),
                         periodIds.Union(pendingIds).ToHashSet());

            // Identity 2: the orders opened in the period partition by WHEN their
            // container was received -- before the period (backlog), in it, or
            // after it (SAP posted the receipt later than the inspection began).
            // Backlog is exactly the "before" slice, and every order opened in the
            // period on a container received in it is in Period Inspection.
            var (from, to) = new DashboardFilter { Period = Period }.Resolve();
            var toEx       = to.AddDays(1);
            var before     = total.Where(r => r.ArrivalCreatedAt.Date <  from.Date).Select(r => r.ArrivalId).ToHashSet();
            var inPeriod   = total.Where(r => r.ArrivalCreatedAt.Date >= from.Date && r.ArrivalCreatedAt.Date < toEx.Date)
                                  .Select(r => r.ArrivalId).ToHashSet();
            var ahead      = total.Count - before.Count - inPeriod.Count;
            _out.WriteLine($"         backlog {before.Count}, on-period {inPeriod.Count}, ahead of receipt {ahead}");

            Assert.Equal(before, backlog.Select(r => r.ArrivalId).ToHashSet());
            Assert.True(inPeriod.IsSubsetOf(periodIds),
                "an order opened in the period on a container received in it is missing from Period Inspection");
            Assert.True(ahead >= 0);
        }
    }

    /// <summary>Each bucket must contain what its caption claims.</summary>
    [Fact]
    public async Task Pending_rows_have_no_inspection_and_backlog_rows_arrived_earlier()
    {
        var (_, svc, scope) = await LoadAsync();
        using (scope)
        {
            var pending = await DetailAsync(svc, CommitmentBuckets.PendingInspection);
            Assert.All(pending, r => Assert.Null(r.QualityOrderId));

            var backlog = await DetailAsync(svc, CommitmentBuckets.BacklogInspected);
            var (from, _) = new DashboardFilter { Period = Period }.Resolve();
            var fromUtc = DateTime.SpecifyKind(from, DateTimeKind.Local).ToUniversalTime();
            _out.WriteLine($"{backlog.Count} backlog rows, period starts {fromUtc:yyyy-MM-dd HH:mm}Z");
            Assert.All(backlog, r =>
            {
                Assert.NotNull(r.QualityOrderId);
                Assert.True(r.ArrivalCreatedAt < fromUtc,
                    $"{r.ArrivalNo} arrived {r.ArrivalCreatedAt:u}, which is not before the period.");
            });

            var period = await DetailAsync(svc, CommitmentBuckets.PeriodInspection);
            Assert.All(period, r =>
            {
                Assert.NotNull(r.QualityOrderId);
                Assert.True(r.ArrivalCreatedAt >= fromUtc);
            });
        }
    }

    /// <summary>
    /// Asking for one plant returns that plant's slice of the same number, so a
    /// row's figure and the row's drill-through agree too.
    /// </summary>
    [Fact]
    public async Task A_single_plant_drill_matches_that_row()
    {
        var (vm, svc, scope) = await LoadAsync();
        using (scope)
        {
            var row = vm.Commitment.OrderByDescending(c => c.Received).FirstOrDefault();
            if (row is null) { _out.WriteLine("Nothing received this month."); return; }

            var rows = await DetailAsync(svc, CommitmentBuckets.Received, row.Plant);
            _out.WriteLine($"{row.Plant}: column {row.Received}, rows {rows.Count}");
            Assert.Equal(row.Received, rows.Count);
            Assert.All(rows, r => Assert.Equal(row.Plant, r.Plant, StringComparer.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// The drill-through is a second door onto the same data, so it has to be
    /// bounded by the same plant entitlement -- a door that skipped the check
    /// would undo the scoping on the page it was reached from.
    /// </summary>
    [Fact]
    public async Task The_drill_through_is_bounded_by_the_plant_scope()
    {
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IDashboardService>();

        var all = await svc.GetSummaryAsync(new DashboardFilter { Period = Period },
                                            PlantScope.All, CancellationToken.None);
        var plants = all.Commitment.Select(c => c.Plant)
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (plants.Count < 2) { _out.WriteLine("Single-plant database."); return; }

        var mine   = plants[0];
        var theirs = plants[1];
        var mineScope = new PlantScope(false, new[] { mine });

        // Scoped, with no plant asked for: only mine.
        var rows = await svc.GetCommitmentDetailAsync(
            new DashboardFilter { Period = Period }, mineScope,
            CommitmentBuckets.Received, null, CancellationToken.None);
        Assert.All(rows, r => Assert.Equal(mine, r.Plant, StringComparer.OrdinalIgnoreCase));

        // Scoped, ASKING for somebody else's plant: still only mine, never theirs.
        var crafted = await svc.GetCommitmentDetailAsync(
            new DashboardFilter { Period = Period }, mineScope,
            CommitmentBuckets.Received, theirs, CancellationToken.None);
        _out.WriteLine($"asked for '{theirs}' while scoped to '{mine}': {crafted.Count} row(s)");
        Assert.All(crafted, r => Assert.Equal(mine, r.Plant, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>An unknown bucket must not become an unfiltered dump.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("everything")]
    [InlineData("'; DROP TABLE qms_arrival--")]
    public async Task An_unrecognised_bucket_falls_back_to_received(string bucket)
    {
        var (_, svc, scope) = await LoadAsync();
        using (scope)
        {
            var rows     = await DetailAsync(svc, bucket);
            var received = await DetailAsync(svc, CommitmentBuckets.Received);
            Assert.Equal(received.Count, rows.Count);
        }
    }

    /// <summary>
    /// The workbook: a summary sheet and one sheet per column, each carrying
    /// the containers behind its number rather than the number alone.
    /// </summary>
    [Fact]
    public async Task The_workbook_has_a_summary_sheet_and_one_per_column()
    {
        TestAuthHandler.Role = SharbatlyQMS.Web.Models.Security.RoleCodes.Admin;
        var client = _factory.CreateClient();
        var res = await client.GetAsync($"/Home/CommitmentExcel?period={Period}");
        Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);

        var bytes = await res.Content.ReadAsByteArrayAsync();
        _out.WriteLine($"{bytes.Length:N0} bytes");
        Assert.True(bytes.Length > 1000);

        using var ms = new MemoryStream(bytes);
        using var wb = new ClosedXML.Excel.XLWorkbook(ms);
        var names = wb.Worksheets.Select(w => w.Name).ToList();
        _out.WriteLine("sheets: " + string.Join(" | ", names));

        Assert.Equal(6, names.Count);
        foreach (var bucket in CommitmentBuckets.All)
            Assert.Contains(CommitmentBuckets.Caption(bucket), names);

        // Every detail sheet's rows must match its column, through the endpoint
        // rather than the service -- the export builds its own queries.
        var (vm, svc, scope) = await LoadAsync();
        using (scope)
        {
            var expected = new Dictionary<string, int>
            {
                [CommitmentBuckets.Received]          = vm.Commitment.Sum(c => c.Received),
                [CommitmentBuckets.PeriodInspection]  = vm.Commitment.Sum(c => c.Committed),
                [CommitmentBuckets.PendingInspection] = vm.Commitment.Sum(c => c.Outstanding),
                [CommitmentBuckets.BacklogInspected]  = vm.Commitment.Sum(c => c.QosCatchUp),
                [CommitmentBuckets.TotalInspected]    = vm.Commitment.Sum(c => c.QosCreated),
            };
            foreach (var (bucket, count) in expected)
            {
                var ws = wb.Worksheet(CommitmentBuckets.Caption(bucket));
                // Row 1 explains the sheet, row 2 is the header, data starts at 3.
                var dataRows = ws.LastRowUsed() is null ? 0 : Math.Max(0, ws.LastRowUsed()!.RowNumber() - 2);
                _out.WriteLine($"  {CommitmentBuckets.Caption(bucket),-20} column {count,5}  sheet {dataRows,5}");
                Assert.Equal(count, dataRows);
            }
        }
    }
}
