using System.Net;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Services;
using Xunit;

namespace SharbatlyQMS.Tests;

/// <summary>
/// The date checks on the arrival's shipment form, through the real action: a
/// refused save says why and leaves the row exactly as it was. The rules
/// themselves are pinned in ShipmentDatesTests.
/// </summary>
[Collection("workflow")]
public class ArrivalShipmentDateCheckTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;

    public ArrivalShipmentDateCheckTests(QmsAppFactory factory) => _factory = factory;

    [Fact]
    public async Task The_allowed_days_default_to_one_are_kept_in_range_and_show_on_the_rules_page()
    {
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var db = scope.ServiceProvider.GetRequiredService<IDbService>();
        var before = await db.GetConfigAsync(SettingKeys.DischargeBeforeLoadingDays);
        try
        {
            await settings.SaveDischargeBeforeLoadingDaysAsync(99, null);
            Assert.Equal(7, await settings.GetDischargeBeforeLoadingDaysAsync());
            await settings.SaveDischargeBeforeLoadingDaysAsync(-5, null);
            Assert.Equal(0, await settings.GetDischargeBeforeLoadingDaysAsync());

            await settings.SaveDischargeBeforeLoadingDaysAsync(3, null);
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader, RoleCodes.Admin);
            var page = await client.GetStringAsync("/Admin/ArrivalFieldRules");
            Assert.Matches("name=\"dischargeBeforeLoadingDays\"[^>]*value=\"3\"", page);
            Assert.Contains("Days a discharge date may be before the loading date", page);

            // A row nobody has set reads as the default.
            await db.SetConfigAsync(SettingKeys.DischargeBeforeLoadingDays, "", null);
            Assert.Equal(1, await settings.GetDischargeBeforeLoadingDaysAsync());
        }
        finally
        {
            await db.SetConfigAsync(SettingKeys.DischargeBeforeLoadingDays, before ?? "", null);
        }
    }

    [Fact]
    public async Task A_future_discharge_date_is_refused_with_the_reason_and_nothing_is_saved()
    {
        using var scope = _factory.Services.CreateScope();
        var arrivals = scope.ServiceProvider.GetRequiredService<IArrivalService>();
        using var db = new Microsoft.Data.SqlClient.SqlConnection(
            scope.ServiceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Default"));
        var id = await db.ExecuteScalarAsync<long?>(@"
            SELECT TOP 1 a.arrival_id FROM qms_arrival a
            JOIN qms_shipment_snapshot s ON s.arrival_id = a.arrival_id
            WHERE a.status_code = 'Completed' AND s.discharge_date IS NOT NULL
            ORDER BY a.arrival_id DESC");
        if (id is null) return;

        var before = await arrivals.GetShipmentAsync(id.Value);
        var future = DateTime.Today.AddDays(30);
        try
        {
            var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            client.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader, RoleCodes.Admin);

            var page = await client.GetStringAsync($"/Arrivals/Details/{id}");
            var form = Regex.Match(page, "<form[^>]*action=\"/Arrivals/SaveShipment\"[^>]*>.*?</form>", RegexOptions.Singleline);
            Assert.True(form.Success, "The page has no shipment form.");
            var token = Regex.Match(form.Value, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
            Assert.True(token.Success, "The shipment form has no token.");

            var res = await client.PostAsync("/Arrivals/SaveShipment", new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("__RequestVerificationToken", token.Groups[1].Value),
                new KeyValuePair<string, string>("ArrivalId", id.Value.ToString()),
                new KeyValuePair<string, string>("DischargeDate", future.ToString("yyyy-MM-dd")),
            }));
            Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

            var after = await client.GetStringAsync($"/Arrivals/Details/{id}");
            Assert.Contains("Shipment details not saved", after);
            Assert.Contains("is in the future", after);

            var stored = await arrivals.GetShipmentAsync(id.Value);
            Assert.Equal(before!.DischargeDate, stored!.DischargeDate);
            Assert.Equal(before.UnloadingDate, stored.UnloadingDate);
            Assert.Equal(before.PullOutDate, stored.PullOutDate);
            Assert.Equal(before.ArrivalPlace, stored.ArrivalPlace);
        }
        finally
        {
            // Only if the check let it through: never leave a live row damaged.
            var now = await arrivals.GetShipmentAsync(id.Value);
            if (now!.DischargeDate != before!.DischargeDate)
                await arrivals.SaveShipmentAsync(before, "test-restore");
        }
    }
}
