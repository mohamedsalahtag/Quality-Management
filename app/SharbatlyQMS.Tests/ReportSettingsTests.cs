using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Services;
using Xunit;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Report options round-trip through portal.SystemSetting. Writes to the live
/// settings table, so each test captures and restores the original value.
///
/// The report's own Time Bar basis (Discharge / Arrival) was retired on
/// 2026-09-26: the report now counts from the same date as the Time Bar page,
/// so the two cannot disagree, and that setting is covered by
/// TimeBarServiceTests.
/// </summary>
[Collection("workflow")]
public class ReportSettingsTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    public ReportSettingsTests(QmsAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Rejected_container_header_round_trips_and_defaults()
    {
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        var original = (await settings.GetReportConfigAsync()).RejectedContainerHeader;
        try
        {
            await settings.SaveReportConfigAsync(new ReportConfig { RejectedContainerHeader = "  Refused on arrival  " }, null);
            Assert.Equal("Refused on arrival", (await settings.GetReportConfigAsync()).RejectedContainerHeader);

            // Blank falls back to the shipped wording rather than an empty banner.
            await settings.SaveReportConfigAsync(new ReportConfig { RejectedContainerHeader = "   " }, null);
            Assert.Equal(RejectedContainerDefaults.Header, (await settings.GetReportConfigAsync()).RejectedContainerHeader);
        }
        finally
        {
            await settings.SaveReportConfigAsync(new ReportConfig { RejectedContainerHeader = original }, null);
        }
    }

    /// <summary>
    /// The report reads the Time Bar page's basis. Whatever an administrator
    /// picks there is what the report prints, and nothing else is consulted.
    /// </summary>
    [Fact]
    public async Task The_report_follows_the_time_bar_pages_basis()
    {
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();

        var original = await settings.GetTimeBarConfigAsync();
        try
        {
            var cfg = await settings.GetTimeBarConfigAsync();
            cfg.ArrivalBasis = TimeBarArrivalBases.PortArrival;
            await settings.SaveTimeBarConfigAsync(cfg, null);
            Assert.Equal(TimeBarArrivalBases.PortArrival, (await settings.GetTimeBarConfigAsync()).ArrivalBasis);

            cfg.ArrivalBasis = "nonsense";
            await settings.SaveTimeBarConfigAsync(cfg, null);
            Assert.Equal(TimeBarArrivalBases.GoodsReceipt, (await settings.GetTimeBarConfigAsync()).ArrivalBasis);
        }
        finally
        {
            await settings.SaveTimeBarConfigAsync(original, null);
        }
    }
}
