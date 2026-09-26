using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Every dropdown filter on the Inspection Time Bar page takes several values.
/// Ticking two plants must return exactly the union of the two single-plant
/// views -- not the first one only, and not everything.
/// </summary>
[Collection("workflow")]
public class TimeBarMultiSelectTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    private readonly ITestOutputHelper _out;

    public TimeBarMultiSelectTests(QmsAppFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    private async Task<TimeBarPage> LoadAsync(TimeBarFilter f)
    {
        using var s = _factory.Services.CreateScope();
        return await s.ServiceProvider.GetRequiredService<ITimeBarService>()
            .ListAsync(f, PlantScope.All, new TimeBarConfig());
    }

    [Fact]
    public async Task Two_plants_return_the_union_of_each()
    {
        using var s = _factory.Services.CreateScope();
        var options = await s.ServiceProvider.GetRequiredService<ITimeBarService>()
            .GetFilterOptionsAsync(PlantScope.All);
        if (options.Plants.Count < 2) { _out.WriteLine("Fewer than two plants to combine."); return; }

        var (a, b) = (options.Plants[0], options.Plants[1]);
        var onlyA = await LoadAsync(new TimeBarFilter { Plant = new() { a }, IncludeArchived = true });
        var onlyB = await LoadAsync(new TimeBarFilter { Plant = new() { b }, IncludeArchived = true });
        var both  = await LoadAsync(new TimeBarFilter { Plant = new() { a, b }, IncludeArchived = true });

        _out.WriteLine($"{a}: {onlyA.Total}, {b}: {onlyB.Total}, both: {both.Total}");
        Assert.Equal(onlyA.Total + onlyB.Total, both.Total);
        Assert.All(both.Rows, r => Assert.Contains(r.Plant, new[] { a, b }));
    }

    [Fact]
    public async Task Two_statuses_return_the_union_of_each()
    {
        using var s = _factory.Services.CreateScope();
        var options = await s.ServiceProvider.GetRequiredService<ITimeBarService>()
            .GetFilterOptionsAsync(PlantScope.All);
        if (options.Statuses.Count < 2) { _out.WriteLine("Fewer than two statuses to combine."); return; }

        var (a, b) = (options.Statuses[0], options.Statuses[1]);
        var onlyA = await LoadAsync(new TimeBarFilter { Status = new() { a }, IncludeArchived = true });
        var onlyB = await LoadAsync(new TimeBarFilter { Status = new() { b }, IncludeArchived = true });
        var both  = await LoadAsync(new TimeBarFilter { Status = new() { a, b }, IncludeArchived = true });

        _out.WriteLine($"{a}: {onlyA.Total}, {b}: {onlyB.Total}, both: {both.Total}");
        Assert.Equal(onlyA.Total + onlyB.Total, both.Total);
    }

    /// <summary>A blank value from a hand-edited URL must not empty the page.</summary>
    [Fact]
    public async Task A_blank_value_is_ignored_rather_than_matching_nothing()
    {
        var none  = await LoadAsync(new TimeBarFilter { IncludeArchived = true });
        var blank = await LoadAsync(new TimeBarFilter { Plant = new() { "", " " }, IncludeArchived = true });
        Assert.Equal(none.Total, blank.Total);
    }

    /// <summary>Every multi-value survives the page's links (pager, chips, Excel).</summary>
    [Fact]
    public async Task The_page_keeps_every_ticked_value_in_its_links()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader,
                                         SharbatlyQMS.Web.Models.Security.RoleCodes.Admin);
        var res  = await client.GetAsync("/TimeBar?plant=JD01&plant=RD01&includeArchived=true");
        Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        Assert.Contains("plant=JD01&amp;plant=RD01", html);
    }
}
