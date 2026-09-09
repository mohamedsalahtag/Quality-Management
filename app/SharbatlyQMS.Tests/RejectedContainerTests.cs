using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;
using Xunit;
using Xunit.Abstractions;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Refusing a container that arrived damaged.
///
/// The point of the flow is that a rejection still produces something the
/// supplier can be shown: an order carrying the shipment details, the damage
/// photos and a potential claim, finished on creation because there is nothing
/// to inspect. The tests here pin the parts that would fail silently — the
/// order landing Closed, the claim flag, the materials coming across, and the
/// undo path existing at all.
///
/// This class WRITES. Every test cleans up after itself, and each works on an
/// arrival it created rather than one somebody is using.
/// </summary>
[Collection("workflow")]
public class RejectedContainerTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    private readonly ITestOutputHelper _out;

    public RejectedContainerTests(QmsAppFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    private Microsoft.Data.SqlClient.SqlConnection Db()
    {
        using var s = _factory.Services.CreateScope();
        var cfg = s.ServiceProvider.GetRequiredService<IConfiguration>();
        return new Microsoft.Data.SqlClient.SqlConnection(cfg.GetConnectionString("Default"));
    }

    /// <summary>Creates a throwaway Draft arrival with one item.</summary>
    private async Task<long> MakeDraftArrivalAsync()
    {
        using var c = Db();
        await c.OpenAsync();
        var no = $"TEST-REJ-{Guid.NewGuid():N}".Substring(0, 20);
        var id = await Dapper.SqlMapper.ExecuteScalarAsync<long>(c, @"
            INSERT INTO qms_arrival
                (arrival_no, source_system, bol_no, container_no, ebeln,
                 vendor_no, vendor_name, plant, status_code, created_at, created_by)
            VALUES (@no, 'TEST', 'TESTBOL', 'TESTCONT', '4700000000',
                 'V1', 'Test vendor', 'JD01', 'Draft', SYSUTCDATETIME(), 'test');
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);", new { no });

        await Dapper.SqlMapper.ExecuteAsync(c, @"
            INSERT INTO qms_arrival_item (arrival_id, ebeln, ebelp, material_no, material_desc, material_group)
            VALUES (@id, '4700000000', '00010', 'TESTMAT', 'Test material', 'APPLE')", new { id });
        await Dapper.SqlMapper.ExecuteAsync(c, @"
            INSERT INTO qms_shipment_snapshot (arrival_id, internal_shipment_no, status_code)
            VALUES (@id, 'TEST-SHP', 'Draft')", new { id });
        return id;
    }

    private async Task CleanupAsync(long arrivalId)
    {
        using var c = Db();
        await c.OpenAsync();
        await Dapper.SqlMapper.ExecuteAsync(c, @"
            DELETE FROM qms_status_history
             WHERE (entity_type = 'Arrival' AND entity_id = @arrivalId)
                OR (entity_type = 'QualityOrder' AND entity_id IN
                    (SELECT quality_order_id FROM qms_quality_order WHERE arrival_id = @arrivalId));
            DELETE FROM qms_quality_order_material
             WHERE quality_order_id IN (SELECT quality_order_id FROM qms_quality_order WHERE arrival_id = @arrivalId);
            DELETE FROM qms_quality_order   WHERE arrival_id = @arrivalId;
            DELETE FROM qms_shipment_snapshot WHERE arrival_id = @arrivalId;
            DELETE FROM qms_arrival_item    WHERE arrival_id = @arrivalId;
            DELETE FROM qms_arrival         WHERE arrival_id = @arrivalId;", new { arrivalId });
    }

    [Fact]
    public async Task Rejecting_refuses_the_arrival_and_raises_a_finished_claim_order()
    {
        var arrivalId = await MakeDraftArrivalAsync();
        try
        {
            using var scope = _factory.Services.CreateScope();
            var arrivals = scope.ServiceProvider.GetRequiredService<IArrivalService>();
            var qos      = scope.ServiceProvider.GetRequiredService<IQualityOrderService>();

            var (ok, error, qoId) = await arrivals.RejectAsync(
                arrivalId, "Pallets collapsed in transit; fruit crushed throughout.", "test");
            _out.WriteLine($"reject -> ok={ok} error={error} qo={qoId}");

            Assert.True(ok, error);
            Assert.NotNull(qoId);

            var arrival = await arrivals.GetAsync(arrivalId);
            Assert.Equal(ArrivalStatus.Rejected, arrival!.StatusCode);
            Assert.NotNull(arrival.RejectedAt);
            Assert.Equal("test", arrival.RejectedBy);
            Assert.Contains("crushed", arrival.RejectReason);

            var qo = await qos.GetAsync(qoId!.Value);
            Assert.NotNull(qo);
            // Finished on creation, flagged, and carrying the claim — the three
            // things the whole flow exists to produce.
            Assert.Equal(QualityOrderStatus.Closed, qo!.StatusCode);
            Assert.True(qo.ContainerRejected);
            Assert.True(qo.PotentialClaim);
            Assert.NotNull(qo.ClosedAt);

            // Materials come across (the claim must say what was in the
            // container); samples do not (nothing was inspected).
            var materials = await qos.GetMaterialsAsync(qoId.Value);
            Assert.NotEmpty(materials);
            var samples = await qos.ListSamplesAsync(qoId.Value);
            Assert.Empty(samples);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    /// <summary>
    /// The reason is printed on the report the supplier receives, so an empty
    /// or throwaway one is refused server-side — the modal's `required` is
    /// trivially bypassed by a crafted POST.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad")]
    public async Task A_rejection_without_a_real_reason_is_refused(string reason)
    {
        var arrivalId = await MakeDraftArrivalAsync();
        try
        {
            using var scope = _factory.Services.CreateScope();
            var arrivals = scope.ServiceProvider.GetRequiredService<IArrivalService>();

            var (ok, error, qoId) = await arrivals.RejectAsync(arrivalId, reason, "test");
            Assert.False(ok);
            Assert.Null(qoId);
            Assert.NotNull(error);

            // And nothing moved.
            var arrival = await arrivals.GetAsync(arrivalId);
            Assert.Equal(ArrivalStatus.Draft, arrival!.StatusCode);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    [Fact]
    public async Task A_rejected_arrival_cannot_be_completed()
    {
        var arrivalId = await MakeDraftArrivalAsync();
        try
        {
            using var scope = _factory.Services.CreateScope();
            var arrivals = scope.ServiceProvider.GetRequiredService<IArrivalService>();

            await arrivals.RejectAsync(arrivalId, "Container roof torn open; cargo wet.", "test");

            var (ok, error) = await arrivals.CompleteAsync(arrivalId, "test");
            Assert.False(ok);
            _out.WriteLine($"complete after reject -> {error}");
            Assert.Contains("Draft", error!);   // "only Draft arrivals can be completed"
        }
        finally { await CleanupAsync(arrivalId); }
    }

    /// <summary>
    /// The undo path. Without it a mis-click would be permanent: a Closed order
    /// cannot be cancelled or deleted by any other route in the application.
    /// </summary>
    [Fact]
    public async Task A_rejection_can_be_undone()
    {
        var arrivalId = await MakeDraftArrivalAsync();
        try
        {
            using var scope = _factory.Services.CreateScope();
            var arrivals = scope.ServiceProvider.GetRequiredService<IArrivalService>();
            var qos      = scope.ServiceProvider.GetRequiredService<IQualityOrderService>();

            var (_, _, qoId) = await arrivals.RejectAsync(
                arrivalId, "Refused in error — wrong container inspected.", "test");

            var (ok, error) = await arrivals.CancelRejectionAsync(arrivalId, "Mistaken rejection.", "test");
            Assert.True(ok, error);

            var arrival = await arrivals.GetAsync(arrivalId);
            Assert.Equal(ArrivalStatus.Draft, arrival!.StatusCode);
            Assert.Null(arrival.RejectedAt);
            Assert.Null(arrival.RejectReason);

            var qo = await qos.GetAsync(qoId!.Value);
            Assert.Equal(QualityOrderStatus.Cancelled, qo!.StatusCode);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    /// <summary>
    /// Reopening a rejection order would drop it to Open and take it off the
    /// claims worklist while the claim window runs, with no samples to reopen
    /// to. Refused in the service, not just hidden in the view.
    /// </summary>
    [Fact]
    public async Task A_rejection_order_cannot_be_reopened()
    {
        var arrivalId = await MakeDraftArrivalAsync();
        try
        {
            using var scope = _factory.Services.CreateScope();
            var arrivals = scope.ServiceProvider.GetRequiredService<IArrivalService>();
            var qos      = scope.ServiceProvider.GetRequiredService<IQualityOrderService>();

            var (_, _, qoId) = await arrivals.RejectAsync(
                arrivalId, "Severe mould across the whole load.", "test");

            var (ok, error) = await qos.ReopenAsync(qoId!.Value, "test", "trying to reopen");
            Assert.False(ok);
            _out.WriteLine($"reopen -> {error}");
            Assert.Contains("rejected", error!, StringComparison.OrdinalIgnoreCase);
        }
        finally { await CleanupAsync(arrivalId); }
    }

    /// <summary>The report has to render — it is the deliverable.</summary>
    [Fact]
    public async Task The_claim_report_renders_for_a_rejected_container()
    {
        var arrivalId = await MakeDraftArrivalAsync();
        try
        {
            using var scope = _factory.Services.CreateScope();
            var arrivals = scope.ServiceProvider.GetRequiredService<IArrivalService>();
            var (_, _, qoId) = await arrivals.RejectAsync(
                arrivalId, "Reefer failure; pulp temperature far above spec on arrival.", "test");

            TestAuthHandler.Role = SharbatlyQMS.Web.Models.Security.RoleCodes.Admin;
            var client = _factory.CreateClient();
            var res = await client.GetAsync($"/Reports/QualityOrderPdfPreview/{qoId}");
            Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);

            var bytes = await res.Content.ReadAsByteArrayAsync();
            _out.WriteLine($"claim report: {bytes.Length} bytes");
            Assert.True(bytes.Length > 1000, "The claim report came back suspiciously small.");
            Assert.True(bytes[0] == (byte)'%' && bytes[1] == (byte)'P', "Not a PDF.");
        }
        finally { await CleanupAsync(arrivalId); }
    }

    [Fact]
    public async Task The_rejection_banner_text_is_configurable()
    {
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var original = await settings.GetReportConfigAsync();
        try
        {
            Assert.False(string.IsNullOrWhiteSpace(original.RejectedContainerHeader));

            var cfg = await settings.GetReportConfigAsync();
            cfg.RejectedContainerHeader = "Cargo refused at gate";
            await settings.SaveReportConfigAsync(cfg, null);
            Assert.Equal("Cargo refused at gate", (await settings.GetReportConfigAsync()).RejectedContainerHeader);

            // Blank falls back to the shipped wording rather than printing an
            // empty banner, which would read as a rendering fault.
            cfg.RejectedContainerHeader = "   ";
            await settings.SaveReportConfigAsync(cfg, null);
            Assert.Equal(RejectedContainerDefaults.Header,
                         (await settings.GetReportConfigAsync()).RejectedContainerHeader);
        }
        finally { await settings.SaveReportConfigAsync(original, null); }
    }
}
