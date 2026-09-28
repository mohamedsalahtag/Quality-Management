using Xunit;

namespace SharbatlyQMS.Tests;

/// <summary>
/// The Supplier filter on the Quality Orders, Arrivals and Claims lists has a
/// search box that narrows the supplier list by name. The box must have no
/// name attribute -- otherwise the text typed in it would be submitted with the
/// form and become a filter of its own.
/// </summary>
[Collection("workflow")]
public class SupplierFilterSearchTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    public SupplierFilterSearchTests(QmsAppFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/QualityOrders")]
    [InlineData("/Arrivals")]
    [InlineData("/ClaimManagement")]
    public async Task The_supplier_filter_offers_a_name_search(string url)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader,
                                         SharbatlyQMS.Web.Models.Security.RoleCodes.Admin);
        var res = await client.GetAsync(url);
        Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();

        var at = html.IndexOf("data-ms-search", StringComparison.Ordinal);
        Assert.True(at > 0, $"{url}: no search box in the supplier filter");

        // The search input's own tag must not carry a name.
        var tagStart = html.LastIndexOf('<', at);
        var tagEnd   = html.IndexOf('>', at);
        var tag      = html[tagStart..(tagEnd + 1)];
        Assert.DoesNotContain(" name=", tag);
    }
}
