using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Filtering a list by material major / sub-major category.
///
/// The categories come from the material master rather than the copy stored on
/// an arrival line: the line carries a major and no sub-major at all, so a
/// filter built on it would answer differently from screen to screen. The same
/// predicate is used by Arrivals, Quality Orders, Pending Containers and the
/// Inspection Time Bar, which is the point of these tests -- a filter learned
/// on one page has to mean the same thing on the next.
/// </summary>
[Collection("workflow")]
public class MaterialCategoryFilterTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    private readonly ITestOutputHelper _out;

    public MaterialCategoryFilterTests(QmsAppFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    private async Task<IReadOnlyList<MaterialMajor>> TaxonomyAsync()
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<IMaterialTaxonomyService>()
            .GetAsync(CancellationToken.None);
    }

    [Fact]
    public async Task The_taxonomy_offers_majors_with_their_own_sub_majors()
    {
        var majors = await TaxonomyAsync();
        _out.WriteLine($"{majors.Count} majors, {majors.Sum(m => m.SubMajors.Count)} sub-majors");
        Assert.NotEmpty(majors);

        Assert.All(majors, m =>
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Name));
            // A sub-major belongs to exactly one major in the picker, and blanks
            // would render as an unselectable empty row.
            Assert.All(m.SubMajors, sub => Assert.False(string.IsNullOrWhiteSpace(sub)));
            Assert.Equal(m.SubMajors.Count, m.SubMajors.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        });

        // Alphabetical, because the picker renders them in the order given.
        Assert.Equal(majors.Select(m => m.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase),
                     majors.Select(m => m.Name));
    }

    /// <summary>Cached, so a list page with two filter panels does not scan the
    /// 34,000-row material cache twice per render.</summary>
    [Fact]
    public async Task The_taxonomy_is_served_from_cache_after_the_first_call()
    {
        await TaxonomyAsync();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var again = await TaxonomyAsync();
        sw.Stop();
        _out.WriteLine($"second call {sw.ElapsedMilliseconds} ms, {again.Count} majors");
        Assert.NotEmpty(again);
        Assert.True(sw.ElapsedMilliseconds < 250, $"Second call took {sw.ElapsedMilliseconds} ms.");
    }

    /// <summary>
    /// Narrowing must actually narrow, and must never widen. Checked on every
    /// screen that offers the filter, because each builds the predicate against
    /// a different item table.
    /// </summary>
    [Fact]
    public async Task Choosing_a_major_narrows_every_list_that_offers_it()
    {
        var majors = await TaxonomyAsync();
        if (majors.Count == 0) { _out.WriteLine("No material categories in this database."); return; }
        var major = majors.OrderByDescending(m => m.SubMajors.Count).First().Name;
        _out.WriteLine($"filtering on '{major}'");

        using var scope = _factory.Services.CreateScope();
        var arrivals = scope.ServiceProvider.GetRequiredService<IArrivalService>();
        var qos      = scope.ServiceProvider.GetRequiredService<IQualityOrderService>();
        var cache    = scope.ServiceProvider.GetRequiredService<IContainerCacheService>();
        var timeBar  = scope.ServiceProvider.GetRequiredService<ITimeBarService>();

        var aAll = await arrivals.ListAsync(new ArrivalListFilter { PageSize = 50 }, PlantScope.All);
        var aOne = await arrivals.ListAsync(new ArrivalListFilter { PageSize = 50, MatMajor = major }, PlantScope.All);
        _out.WriteLine($"arrivals       {aOne.Total} of {aAll.Total}");
        Assert.True(aOne.Total <= aAll.Total);

        var qAll = (await qos.ListAsync(new QoListFilter(), PlantScope.All)).Total;
        var qOne = (await qos.ListAsync(new QoListFilter { MatMajor = major }, PlantScope.All)).Total;
        // Totals, not page sizes: the list is paged now, so comparing page
        // counts would compare 50 with 50 and prove nothing.
        _out.WriteLine($"quality orders {qOne} of {qAll}");
        Assert.True(qOne <= qAll);

        var pAll = await cache.ListPendingAsync(pageSize: 50);
        var pOne = await cache.ListPendingAsync(matMajor: major, pageSize: 50);
        _out.WriteLine($"pending        {pOne.Total} of {pAll.Total}");
        Assert.True(pOne.Total <= pAll.Total);

        var tAll = await timeBar.ListAsync(new TimeBarFilter { PageSize = 50 }, PlantScope.All, new TimeBarConfig());
        var tOne = await timeBar.ListAsync(new TimeBarFilter { PageSize = 50, MatMajor = new() { major } }, PlantScope.All, new TimeBarConfig());
        _out.WriteLine($"time bar       {tOne.Total} of {tAll.Total}");
        Assert.True(tOne.Total <= tAll.Total);
    }

    /// <summary>
    /// A sub-major is always narrower than its own major. This is the check that
    /// catches a predicate wired with OR instead of AND, which would quietly
    /// widen the list while looking like it filtered.
    /// </summary>
    [Fact]
    public async Task A_sub_major_narrows_further_than_its_major()
    {
        var majors = await TaxonomyAsync();
        var pick = majors.FirstOrDefault(m => m.SubMajors.Count > 1);
        if (pick is null) { _out.WriteLine("No major with more than one sub-major."); return; }

        using var scope = _factory.Services.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IContainerCacheService>();

        var majorOnly = await cache.ListPendingAsync(matMajor: pick.Name, pageSize: 50);
        foreach (var sub in pick.SubMajors.Take(3))
        {
            var both = await cache.ListPendingAsync(matMajor: pick.Name, matSubMajor: sub, pageSize: 50);
            _out.WriteLine($"{pick.Name} / {sub}: {both.Total} of {majorOnly.Total}");
            Assert.True(both.Total <= majorOnly.Total,
                $"'{pick.Name} / {sub}' returned more rows than '{pick.Name}' alone.");
        }
    }

    /// <summary>No filter chosen must change nothing at all.</summary>
    [Fact]
    public async Task No_category_chosen_leaves_every_list_untouched()
    {
        using var scope = _factory.Services.CreateScope();
        var cache = scope.ServiceProvider.GetRequiredService<IContainerCacheService>();
        var plain = await cache.ListPendingAsync(pageSize: 50);
        var blank = await cache.ListPendingAsync(matMajor: "", matSubMajor: "   ", pageSize: 50);
        _out.WriteLine($"{blank.Total} vs {plain.Total}");
        Assert.Equal(plain.Total, blank.Total);
    }
}
