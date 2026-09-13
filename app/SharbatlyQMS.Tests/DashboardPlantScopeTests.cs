using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace SharbatlyQMS.Tests;

/// <summary>
/// The dashboard must never show a user a plant they are not entitled to.
///
/// Every portlet is a separate query against the same connection, so this is
/// exactly the kind of rule that holds in twelve places and is forgotten in the
/// thirteenth — and a leak here is invisible, because the numbers still look
/// plausible. So the check is made against the WHOLE view model rather than
/// against the queries one at a time.
/// </summary>
[Collection("workflow")]
public class DashboardPlantScopeTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    private readonly ITestOutputHelper _out;

    public DashboardPlantScopeTests(QmsAppFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    private async Task<DashboardVm> LoadAsync(PlantScope scope, string period = "month")
    {
        using var s = _factory.Services.CreateScope();
        var svc = s.ServiceProvider.GetRequiredService<IDashboardService>();
        return await svc.GetSummaryAsync(new DashboardFilter { Period = period }, scope, CancellationToken.None);
    }

    /// <summary>Every plant code the view model mentions, wherever it appears.</summary>
    private static IEnumerable<string> PlantsMentioned(DashboardVm vm)
    {
        foreach (var r in vm.Commitment)      if (!string.IsNullOrWhiteSpace(r.Plant)) yield return r.Plant;
        foreach (var p in vm.PlantOptions)    if (!string.IsNullOrWhiteSpace(p))       yield return p;
        foreach (var a in vm.OpenArrivals)    if (!string.IsNullOrWhiteSpace(a.Plant)) yield return a.Plant;
        foreach (var q in vm.OpenQos)         if (!string.IsNullOrWhiteSpace(q.Plant)) yield return q.Plant;
    }

    [Fact]
    public async Task A_single_plant_user_sees_only_that_plant()
    {
        var everything = await LoadAsync(PlantScope.All);
        var plants = everything.Commitment.Select(c => c.Plant)
                               .Where(p => !string.IsNullOrWhiteSpace(p))
                               .Distinct(StringComparer.OrdinalIgnoreCase)
                               .ToList();
        if (plants.Count < 2)
        {
            _out.WriteLine($"Only {plants.Count} plant(s) active this month - nothing to leak.");
            return;
        }

        var mine = plants[0];
        var vm   = await LoadAsync(new PlantScope(false, new[] { mine }));
        var seen = PlantsMentioned(vm).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _out.WriteLine($"scope '{mine}' -> saw: {string.Join(", ", seen)}");

        Assert.All(seen, p => Assert.Equal(mine, p, StringComparer.OrdinalIgnoreCase));

        // And the picker must not offer a plant the user cannot open, or the
        // dashboard advertises data it will then refuse to show.
        Assert.All(vm.PlantOptions, p => Assert.Equal(mine, p, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_two_plant_user_sees_those_two_and_no_others()
    {
        var everything = await LoadAsync(PlantScope.All);
        var plants = everything.Commitment.Select(c => c.Plant)
                               .Where(p => !string.IsNullOrWhiteSpace(p))
                               .Distinct(StringComparer.OrdinalIgnoreCase)
                               .ToList();
        if (plants.Count < 3)
        {
            _out.WriteLine($"Only {plants.Count} plant(s) active - not enough to prove exclusion.");
            return;
        }

        var mine = new[] { plants[0], plants[1] };
        var vm   = await LoadAsync(new PlantScope(false, mine));
        var seen = PlantsMentioned(vm).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _out.WriteLine($"scope '{string.Join("+", mine)}' -> saw: {string.Join(", ", seen)}");

        Assert.All(seen, p => Assert.Contains(p, mine, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Totals have to shrink too. A portlet could filter its ROWS correctly and
    /// still count every plant in a headline number, which is the harder leak
    /// to notice because nothing on screen names the other plant.
    /// </summary>
    [Fact]
    public async Task The_headline_counts_shrink_with_the_scope()
    {
        var everything = await LoadAsync(PlantScope.All);
        var plants = everything.Commitment.Select(c => c.Plant)
                               .Where(p => !string.IsNullOrWhiteSpace(p))
                               .Distinct(StringComparer.OrdinalIgnoreCase)
                               .ToList();
        if (plants.Count < 2) { _out.WriteLine("Single-plant database."); return; }

        var one = await LoadAsync(new PlantScope(false, new[] { plants[0] }));

        _out.WriteLine($"all: received {everything.Commitment.Sum(c => c.Received)}, " +
                       $"opened {everything.Commitment.Sum(c => c.QosCreated)}");
        _out.WriteLine($"one: received {one.Commitment.Sum(c => c.Received)}, " +
                       $"opened {one.Commitment.Sum(c => c.QosCreated)}");

        Assert.True(one.Commitment.Sum(c => c.Received)   <= everything.Commitment.Sum(c => c.Received));
        Assert.True(one.Commitment.Sum(c => c.QosCreated) <= everything.Commitment.Sum(c => c.QosCreated));
        Assert.True(one.OpenArrivals.Count <= everything.OpenArrivals.Count);
        Assert.True(one.OpenQos.Count      <= everything.OpenQos.Count);
    }

    /// <summary>
    /// Assigned no plants means see nothing -- deliberately strict, because the
    /// alternative reading ("no restriction recorded, so show everything") is
    /// how a half-configured account quietly becomes an unrestricted one.
    /// </summary>
    [Fact]
    public async Task A_user_with_no_plants_sees_nothing()
    {
        var vm = await LoadAsync(new PlantScope(false, Array.Empty<string>()));
        _out.WriteLine($"rows: commitment {vm.Commitment.Count}, arrivals {vm.OpenArrivals.Count}, " +
                       $"qos {vm.OpenQos.Count}, plant options {vm.PlantOptions.Count}");
        Assert.Empty(vm.Commitment);
        Assert.Empty(vm.OpenArrivals);
        Assert.Empty(vm.OpenQos);
        Assert.Empty(vm.PlantOptions);
    }

    /// <summary>
    /// A plant typed into the query string that the user does not hold must not
    /// widen anything. The service drops it rather than refusing, so a shared
    /// link degrades to the user's own scope instead of 403-ing -- but it must
    /// degrade, not pass through.
    /// </summary>
    [Fact]
    public async Task A_plant_filter_outside_the_scope_is_ignored_not_honoured()
    {
        var everything = await LoadAsync(PlantScope.All);
        var plants = everything.Commitment.Select(c => c.Plant)
                               .Where(p => !string.IsNullOrWhiteSpace(p))
                               .Distinct(StringComparer.OrdinalIgnoreCase)
                               .ToList();
        if (plants.Count < 2) { _out.WriteLine("Single-plant database."); return; }

        var mine   = plants[0];
        var theirs = plants[1];

        using var s = _factory.Services.CreateScope();
        var svc = s.ServiceProvider.GetRequiredService<IDashboardService>();
        var vm  = await svc.GetSummaryAsync(
            new DashboardFilter { Period = "month", Plant = theirs },
            new PlantScope(false, new[] { mine }), CancellationToken.None);

        var seen = PlantsMentioned(vm).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _out.WriteLine($"asked for '{theirs}' while scoped to '{mine}' -> saw: {string.Join(", ", seen)}");
        Assert.DoesNotContain(theirs, seen, StringComparer.OrdinalIgnoreCase);
    }
}
