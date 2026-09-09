using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.ViewModels;
using Xunit;

namespace SharbatlyQMS.Tests;

/// <summary>
/// The dashboard's aggregates, against the live database.
///
/// The invariants here are the ones a reader relies on without being told. The
/// first was broken in production: "received vs committed" counted the
/// containers that arrived in the period against the quality orders raised in
/// it — two different sets — so Dammam showed 3 received and 13 committed, a
/// 433% coverage that describes nothing. Coverage only means anything when both
/// halves are the same cohort, and a test is the only thing that keeps it that
/// way.
/// </summary>
[Collection("workflow")]
public class DashboardServiceTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    public DashboardServiceTests(QmsAppFactory factory) => _factory = factory;

    private async Task<DashboardVm> LoadAsync(DashboardFilter filter)
    {
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IDashboardService>();
        return await svc.GetSummaryAsync(filter, PlantScope.All);
    }

    public static TheoryData<string> Periods => new()
    {
        DashboardFilter.Periods.Today,
        DashboardFilter.Periods.Week,
        DashboardFilter.Periods.Month,
        DashboardFilter.Periods.Quarter,
    };

    [Theory]
    [MemberData(nameof(Periods))]
    public async Task Committed_never_exceeds_received(string period)
    {
        var vm = await LoadAsync(new DashboardFilter { Period = period });

        foreach (var row in vm.Commitment)
        {
            Assert.True(row.Committed <= row.Received,
                $"{row.Plant} ({period}): {row.Committed} committed against {row.Received} received. " +
                "Committed must be a subset of the containers received in the period — " +
                "quality orders raised against EARLIER arrivals belong in QosCreated.");
            Assert.True(row.Outstanding >= 0, $"{row.Plant}: negative outstanding.");
            if (row.CoveragePct.HasValue)
                Assert.True(row.CoveragePct <= 100m,
                    $"{row.Plant} ({period}): coverage {row.CoveragePct}% — over 100% means the two " +
                    "halves are describing different sets of containers again.");
        }
    }

    /// <summary>
    /// Throughput is a real number and must still be reported — it is simply not
    /// the other half of a ratio. It legitimately EXCEEDS what arrived when a
    /// team works through a backlog, which is exactly the case that produced the
    /// nonsense reading before.
    /// </summary>
    [Fact]
    public async Task Orders_raised_is_reported_separately_and_may_exceed_arrivals()
    {
        var vm = await LoadAsync(new DashboardFilter { Period = DashboardFilter.Periods.Month });
        Assert.All(vm.Commitment, r => Assert.True(r.QosCreated >= 0));
        // Not asserting that it exceeds Received anywhere (data-dependent), only
        // that it is carried independently rather than folded into Committed.
        Assert.True(vm.Commitment.Sum(r => r.QosCreated) >= vm.Commitment.Sum(r => r.Committed)
                    || vm.Commitment.Count == 0,
            "Orders raised should account for at least the covered containers.");
    }

    /// <summary>
    /// A single day bucketed by DAY is one point, which draws as an empty chart.
    /// The default view is Today, so this was the first thing anyone saw.
    /// </summary>
    [Fact]
    public void A_single_day_is_bucketed_by_hour()
    {
        var today = new DashboardFilter { Period = DashboardFilter.Periods.Today };
        Assert.True(today.BucketByHour);
        Assert.Equal("hour", today.BucketPart);

        Assert.Equal("day",  new DashboardFilter { Period = DashboardFilter.Periods.Week }.BucketPart);
        Assert.Equal("day",  new DashboardFilter { Period = DashboardFilter.Periods.Month }.BucketPart);
        Assert.Equal("week", new DashboardFilter { Period = DashboardFilter.Periods.Quarter }.BucketPart);
    }

    /// <summary>
    /// Open orders are bucketed one bar per day now; ranges hid whether a
    /// specific day was piling up.
    /// </summary>
    [Fact]
    public async Task Open_order_age_has_a_bucket_per_day()
    {
        var vm = await LoadAsync(new DashboardFilter { Period = DashboardFilter.Periods.Today });
        Assert.Equal(15, vm.OpenQoAgeBuckets.Length);
        Assert.All(vm.OpenQoAgeBuckets, n => Assert.True(n >= 0));
    }

    /// <summary>
    /// The gauge used to be a bare number: "which defect, which order, which
    /// material?" had no answer anywhere on the page. Whenever there is a rate
    /// at all there must be evidence behind it.
    /// </summary>
    [Fact]
    public async Task The_defect_rate_carries_its_evidence()
    {
        var vm = await LoadAsync(new DashboardFilter { Period = DashboardFilter.Periods.Quarter });
        if (vm.AvgDefectPctLast30 == null) return;   // nothing closed in the window

        Assert.True(vm.DefectRate.Samples > 0, "A defect rate with no samples behind it.");
        Assert.True(vm.DefectRate.InspectedUnits > 0, "A defect rate with nothing inspected.");
        Assert.True(vm.DefectRate.Orders > 0, "A defect rate belonging to no orders.");

        // Each contributor is expressed as a share of the same denominator as
        // the headline, so the rows can be read against it.
        Assert.All(vm.DefectRate.TopDefects, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.DefectName));
            Assert.True(d.PctOfInspected >= 0);
        });
    }

    /// <summary>
    /// A percentage per material group is not actionable on its own — the units
    /// and sample counts behind it have to travel with it.
    /// </summary>
    [Fact]
    public async Task Defect_by_group_carries_the_numbers_behind_the_ratio()
    {
        var vm = await LoadAsync(new DashboardFilter { Period = DashboardFilter.Periods.Quarter });
        Assert.All(vm.DefectByGroup, g =>
        {
            Assert.True(g.Samples > 0, $"{g.MaterialGroup}: a percentage over zero samples.");
            Assert.True(g.InspectedUnits > 0, $"{g.MaterialGroup}: nothing inspected.");
            // Defective cannot exceed inspected; if it does, the sample fan-out
            // from the defect join has crept back in.
            Assert.True(g.DefectiveUnits <= g.InspectedUnits,
                $"{g.MaterialGroup}: {g.DefectiveUnits} defective of {g.InspectedUnits} inspected — " +
                "the sample rows are being counted once per defect again.");
        });
    }
}
