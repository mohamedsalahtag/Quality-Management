using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Where the time goes on the three list pages, split between the QUERY and
/// everything after it.
///
/// Not an assertion of speed — a measurement. "The page is slow" has at least
/// three candidate causes (the SQL, the volume of HTML, the browser parsing it)
/// and they call for completely different fixes, so the first job is to find out
/// which one it is rather than optimise the one that happens to be easiest.
/// </summary>
[Collection("workflow")]
public class ListPageTimingTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    private readonly ITestOutputHelper _out;

    public ListPageTimingTests(QmsAppFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    [Fact]
    public async Task Where_the_time_goes_on_the_list_pages()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader, RoleCodes.Admin);

        // One warm request first: the first hit of ANY page pays for JIT, the
        // permission catalogue and the connection pool, and attributing that to
        // whichever page happened to go first is how a "slow page" gets blamed
        // for a cold start.
        await client.GetAsync("/Account/Login");

        foreach (var url in new[] { "/QualityOrders", "/ClaimManagement", "/Arrivals" })
        {
            // Twice: the first pass still carries per-page view compilation.
            for (var pass = 1; pass <= 2; pass++)
            {
                var sw = Stopwatch.StartNew();
                var res = await client.GetAsync(url);
                var body = await res.Content.ReadAsStringAsync();
                sw.Stop();

                var rows = System.Text.RegularExpressions.Regex.Matches(body, "<tr").Count;
                _out.WriteLine($"{url,-18} pass {pass}: {sw.ElapsedMilliseconds,6} ms   " +
                               $"{body.Length / 1024,6} KB   ~{rows,5} <tr>");
            }
        }
    }

    /// <summary>The SQL alone, so the query can be told apart from the render.</summary>
    [Fact]
    public async Task Where_the_time_goes_in_the_queries()
    {
        using var scope = _factory.Services.CreateScope();
        var qos    = scope.ServiceProvider.GetRequiredService<IQualityOrderService>();
        var claims = scope.ServiceProvider.GetRequiredService<IClaimService>();
        var arr    = scope.ServiceProvider.GetRequiredService<IArrivalService>();

        // Warm the pool and the plan cache before timing anything.
        await qos.ListAsync(new QoListFilter(), PlantScope.All);
        await claims.ListClosedQosAsync(new ClaimListFilter(), PlantScope.All, "test");
        await arr.ListAsync(new ArrivalListFilter(), PlantScope.All);

        var sw = Stopwatch.StartNew();
        var qoPage = await qos.ListAsync(new QoListFilter(), PlantScope.All);
        _out.WriteLine($"QO list      : {sw.ElapsedMilliseconds,6} ms  {qoPage.Rows.Count,5} rows (of {qoPage.Total} total)");

        sw.Restart();
        var claimRows = (await claims.ListClosedQosAsync(new ClaimListFilter(), PlantScope.All, "test")).Rows;
        _out.WriteLine($"Claims list  : {sw.ElapsedMilliseconds,6} ms  {claimRows.Count,5} rows");

        sw.Restart();
        var arrPage = await arr.ListAsync(new ArrivalListFilter(), PlantScope.All);
        _out.WriteLine($"Arrivals page: {sw.ElapsedMilliseconds,6} ms  {arrPage.Rows.Count,5} rows " +
                       $"(of {arrPage.Total} total)");

        sw.Restart();
        await qos.GetQoFilterOptionsAsync(PlantScope.All);
        _out.WriteLine($"QO options   : {sw.ElapsedMilliseconds,6} ms");

        sw.Restart();
        await claims.GetClaimFilterOptionsAsync(PlantScope.All);
        _out.WriteLine($"Claim options: {sw.ElapsedMilliseconds,6} ms");
    }
}
