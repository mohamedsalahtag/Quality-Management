using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Services;
using Xunit;

namespace SharbatlyQMS.Tests;

/// <summary>
/// The Time Bar basis setting round-trips through portal.SystemSetting and
/// defaults to Discharge. Writes to the live settings table, so it captures and
/// restores the original value.
/// </summary>
[Collection("workflow")]
public class ReportSettingsTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    public ReportSettingsTests(QmsAppFactory factory) => _factory = factory;

    [Fact]
    public async Task TimeBarBasis_round_trips_and_validates()
    {
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        var original = (await settings.GetReportConfigAsync()).TimeBarBasis;
        try
        {
            await settings.SaveReportConfigAsync(new ReportConfig { TimeBarBasis = TimeBarBases.Arrival }, null);
            Assert.Equal(TimeBarBases.Arrival, (await settings.GetReportConfigAsync()).TimeBarBasis);

            await settings.SaveReportConfigAsync(new ReportConfig { TimeBarBasis = TimeBarBases.Discharge }, null);
            Assert.Equal(TimeBarBases.Discharge, (await settings.GetReportConfigAsync()).TimeBarBasis);

            // An invalid value falls back to the Discharge default rather than persisting garbage.
            await settings.SaveReportConfigAsync(new ReportConfig { TimeBarBasis = "nonsense" }, null);
            Assert.Equal(TimeBarBases.Discharge, (await settings.GetReportConfigAsync()).TimeBarBasis);
        }
        finally
        {
            await settings.SaveReportConfigAsync(new ReportConfig { TimeBarBasis = original }, null);
        }
    }
}
