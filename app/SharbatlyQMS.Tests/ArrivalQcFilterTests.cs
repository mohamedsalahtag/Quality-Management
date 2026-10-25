using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace SharbatlyQMS.Tests;

/// <summary>The Arrivals list filtered by its Quality Order column.</summary>
[Collection("workflow")]
public class ArrivalQcFilterTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    private readonly ITestOutputHelper _out;

    public ArrivalQcFilterTests(QmsAppFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    private async Task<ArrivalPage> ListAsync(ArrivalListFilter f)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IArrivalService>().ListAsync(f, PlantScope.All);
    }

    [Fact]
    public async Task The_qc_states_partition_the_list()
    {
        var all      = await ListAsync(new ArrivalListFilter { PageSize = 25 });
        var none     = await ListAsync(new ArrivalListFilter { PageSize = 100, QcState = "none" });
        var open     = await ListAsync(new ArrivalListFilter { PageSize = 100, QcState = "open" });
        var finished = await ListAsync(new ArrivalListFilter { PageSize = 100, QcState = "finished" });
        _out.WriteLine($"all {all.Total} = none {none.Total} + open {open.Total} + finished {finished.Total}");

        Assert.Equal(all.Total, none.Total + open.Total + finished.Total);
        Assert.All(none.Rows, a => Assert.Null(a.QualityOrderId));
        Assert.All(open.Rows, a => Assert.NotEqual(QualityOrderStatus.Closed, a.QualityOrderStatus));
        Assert.All(finished.Rows, a => Assert.Equal(QualityOrderStatus.Closed, a.QualityOrderStatus));
    }

    [Fact]
    public async Task The_qc_number_finds_its_arrival()
    {
        var withQo = (await ListAsync(new ArrivalListFilter { PageSize = 25, QcState = "finished" })).Rows
            .FirstOrDefault(a => !string.IsNullOrEmpty(a.QualityOrderNo));
        if (withQo is null) return;
        var hit = await ListAsync(new ArrivalListFilter { QualityOrder = withQo.QualityOrderNo });
        Assert.Contains(hit.Rows, a => a.ArrivalId == withQo.ArrivalId);
        Assert.True(hit.Total <= 3, $"A full QC order number matched {hit.Total} arrivals.");
    }
}
