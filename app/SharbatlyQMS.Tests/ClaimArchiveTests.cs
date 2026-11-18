using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Security;
using SharbatlyQMS.Web.Services;
using Xunit;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Archive / restore claims by finished-date range: administrator only by
/// default, counted per claim status before anything moves, and every refusal
/// leaves the orders where they were. Nothing here archives live data.
/// </summary>
[Collection("workflow")]
public class ClaimArchiveTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    public ClaimArchiveTests(QmsAppFactory factory) => _factory = factory;

    private HttpClient Client(string role)
    {
        var c = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        c.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader, role);
        return c;
    }

    [Fact]
    public async Task Only_the_administrator_holds_it_by_default()
    {
        var perms = _factory.Services.GetRequiredService<IPermissionResolver>();
        await perms.EnsureLoadedAsync();
        Assert.True(perms.RoleHas(RoleCodes.Admin, Perm.Claims.Archive, AccessLevel.Edit, isScreen: false));
        foreach (var r in new[] { RoleCodes.Manager, RoleCodes.ClaimManager, RoleCodes.Supervisor, RoleCodes.Operator })
            Assert.False(perms.RoleHas(r, Perm.Claims.Archive, AccessLevel.Edit, isScreen: false));
    }

    [Fact]
    public async Task Admin_sees_the_button_and_gets_counts_per_status()
    {
        var admin = Client(RoleCodes.Admin);
        Assert.Contains("id=\"archiveRangeModal\"", await admin.GetStringAsync("/ClaimManagement"));
        Assert.Contains("Restore by date range", await admin.GetStringAsync("/ClaimManagement/Archived"));

        var to = DateTime.Today; var from = to.AddDays(-30);
        var json = await admin.GetStringAsync($"/ClaimManagement/ArchivePreview?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}&restore=false");
        Assert.Contains("\"ok\":true", json);

        using var s = _factory.Services.CreateScope();
        var svc = s.ServiceProvider.GetRequiredService<IClaimArchiveService>();
        var buckets = await svc.PreviewAsync(from, to, false, PlantScope.All);
        Assert.All(buckets, b => Assert.True(b.Count > 0 && b.Label.Length > 0));
        Assert.Equal(buckets.Count, buckets.Select(b => b.Status).Distinct().Count());
    }

    [Theory]
    [InlineData(RoleCodes.Manager)]
    [InlineData(RoleCodes.Operator)]
    public async Task Other_roles_are_refused_and_see_no_button(string role)
    {
        var client = Client(role);
        var res = await client.GetAsync($"/ClaimManagement/ArchivePreview?from={DateTime.Today.AddDays(-7):yyyy-MM-dd}&to={DateTime.Today:yyyy-MM-dd}");
        Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
        var page = await client.GetAsync("/ClaimManagement");
        if (page.StatusCode == HttpStatusCode.OK)
            Assert.DoesNotContain("id=\"archiveRangeModal\"", await page.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Refusals_move_nothing()
    {
        using var s = _factory.Services.CreateScope();
        var svc = s.ServiceProvider.GetRequiredService<IClaimArchiveService>();
        var to = DateTime.Today; var from = to.AddDays(-30);
        var before = await svc.PreviewAsync(from, to, false, PlantScope.All);
        var any = before.Select(b => b.Status).ToArray();

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ApplyAsync(from, to, Array.Empty<string>(), false, "Testing a refusal", "test", PlantScope.All));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ApplyAsync(from, to, any.Length > 0 ? any : new[] { "PassedQC" }, false, "no", "test", PlantScope.All));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ApplyAsync(from.AddYears(-3), to, new[] { "PassedQC" }, false, "Testing a refusal", "test", PlantScope.All));
        // A range in the future matches nothing.
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ApplyAsync(to.AddYears(5), to.AddYears(5).AddDays(1), new[] { "PassedQC" }, false, "Testing a refusal", "test", PlantScope.All));

        var after = await svc.PreviewAsync(from, to, false, PlantScope.All);
        // More can finish meanwhile on a live database; none may have left.
        Assert.True(after.Sum(b => b.Count) >= before.Sum(b => b.Count));
    }
}
