using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Services;
using Xunit;
using Xunit.Abstractions;

namespace SharbatlyQMS.Tests;

/// <summary>
/// Archiving a pending container has to STAY done.
///
/// It did not. The SAP sweep re-reads every container from the configured start
/// date on each run, so a purchase order confirmed late lands in the cache as a
/// brand-new row weeks after an operator archived that period by hand: 963
/// containers that arrived before 18 August 2026 appeared in the pending list
/// on 9 September, two days after that period had been archived. A manual sweep
/// cannot win against a source that keeps producing history, so the archive is
/// now a date the sync enforces.
///
/// These tests write; each cleans up after itself.
/// </summary>
[Collection("workflow")]
public class PendingArchiveTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    private readonly ITestOutputHelper _out;

    public PendingArchiveTests(QmsAppFactory factory, ITestOutputHelper output)
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

    /// <summary>Two cache lines for one container, as SAP delivers a two-line PO.</summary>
    private async Task<string> SeedContainerAsync(DateOnly arrived, int lines = 2)
    {
        var container = ("TEST" + Guid.NewGuid().ToString("N")).Substring(0, 11).ToUpperInvariant();
        using var c = Db();
        await c.OpenAsync();
        for (var i = 1; i <= lines; i++)
            await Dapper.SqlMapper.ExecuteAsync(c, @"
                INSERT INTO qms_sap_container_cache
                    (container_no, bol_no, ebeln, ebelp, material_no, plant, storage_loc,
                     vendor_no, vendor_name, arrival_date, doc_date, has_arrival)
                VALUES
                    (@container, 'TESTBOL', '4700000000', @ebelp, @mat, 'JD01', '0001',
                     'V1', 'Test vendor', @arrived, @arrived, 0)",
                new
                {
                    container,
                    ebelp   = i.ToString("00000"),
                    mat     = "TESTMAT" + i,
                    arrived = arrived.ToDateTime(TimeOnly.MinValue)
                });
        return container;
    }

    private async Task CleanupAsync(string container)
    {
        using var c = Db();
        await c.OpenAsync();
        await Dapper.SqlMapper.ExecuteAsync(c,
            "DELETE FROM qms_sap_container_cache WHERE container_no = @container", new { container });
    }

    private async Task<(int Rows, int Archived)> StateAsync(string container)
    {
        using var c = Db();
        await c.OpenAsync();
        var r = await Dapper.SqlMapper.QuerySingleAsync(c, @"
            SELECT COUNT(*) AS Rows_, COUNT(archived_at) AS Archived
            FROM   qms_sap_container_cache WHERE container_no = @container", new { container });
        return ((int)r.Rows_, (int)r.Archived);
    }

    private static IContainerCacheService Cache(IServiceScope s) =>
        s.ServiceProvider.GetRequiredService<IContainerCacheService>();

    /// <summary>
    /// The floor archives what arrived before it and leaves everything else
    /// alone. Both halves matter: a filter that swallowed current containers
    /// would empty the working list.
    /// </summary>
    [Fact]
    public async Task The_floor_archives_older_containers_and_spares_newer_ones()
    {
        var older = await SeedContainerAsync(DateOnly.FromDateTime(DateTime.Today.AddDays(-60)));
        var newer = await SeedContainerAsync(DateOnly.FromDateTime(DateTime.Today.AddDays(-1)));
        try
        {
            using var scope = _factory.Services.CreateScope();
            var floor = DateOnly.FromDateTime(DateTime.Today.AddDays(-30));
            await Cache(scope).ArchiveArrivalsBeforeAsync(floor, CancellationToken.None);

            var o = await StateAsync(older);
            var n = await StateAsync(newer);
            _out.WriteLine($"older: {o.Archived}/{o.Rows} archived   newer: {n.Archived}/{n.Rows} archived");

            // Every LINE of the old container, not just one: the pending list
            // filters rows before it groups them, so a half-archived container
            // reappears in full.
            Assert.Equal(o.Rows, o.Archived);
            Assert.Equal(0, n.Archived);
        }
        finally { await CleanupAsync(older); await CleanupAsync(newer); }
    }

    /// <summary>
    /// The exact regression: a new PO line arrives on a container that was
    /// already archived. The cache key includes the PO line, so it inserts
    /// unarchived and drags the whole container back onto the pending list.
    /// </summary>
    [Fact]
    public async Task A_new_line_on_an_archived_container_does_not_bring_it_back()
    {
        var container = await SeedContainerAsync(DateOnly.FromDateTime(DateTime.Today.AddDays(-60)));
        try
        {
            using var scope = _factory.Services.CreateScope();
            var cache = Cache(scope);
            await cache.SetArchivedAsync(container, "TESTBOL", "4700000000", true, "test");
            Assert.Equal((2, 2), await StateAsync(container));

            // SAP delivers a third line for the same container.
            using (var c = Db())
            {
                await c.OpenAsync();
                await Dapper.SqlMapper.ExecuteAsync(c, @"
                    INSERT INTO qms_sap_container_cache
                        (container_no, bol_no, ebeln, ebelp, material_no, plant, storage_loc,
                         vendor_no, vendor_name, arrival_date, doc_date, has_arrival)
                    VALUES
                        (@container, 'TESTBOL', '4700000000', '00003', 'TESTMAT3', 'JD01', '0001',
                         'V1', 'Test vendor', @arrived, @arrived, 0)",
                    new { container, arrived = DateTime.Today.AddDays(-60) });
            }
            var mixed = await StateAsync(container);
            _out.WriteLine($"after the new line: {mixed.Archived}/{mixed.Rows} archived");
            Assert.NotEqual(mixed.Rows, mixed.Archived);   // the regression, reproduced

            // The repair runs with no floor set at all -- it is not a filter,
            // it is the archive state being a property of the container.
            await cache.ArchiveArrivalsBeforeAsync(null, CancellationToken.None);

            var healed = await StateAsync(container);
            _out.WriteLine($"after the repair: {healed.Archived}/{healed.Rows} archived");
            Assert.Equal(healed.Rows, healed.Archived);
        }
        finally { await CleanupAsync(container); }
    }

    /// <summary>Running it twice must not re-stamp what is already filed away.</summary>
    [Fact]
    public async Task Running_the_floor_twice_changes_nothing_the_second_time()
    {
        var container = await SeedContainerAsync(DateOnly.FromDateTime(DateTime.Today.AddDays(-60)));
        try
        {
            using var scope = _factory.Services.CreateScope();
            var floor  = DateOnly.FromDateTime(DateTime.Today.AddDays(-30));
            var first  = await Cache(scope).ArchiveArrivalsBeforeAsync(floor, CancellationToken.None);
            var second = await Cache(scope).ArchiveArrivalsBeforeAsync(floor, CancellationToken.None);
            _out.WriteLine($"first pass archived {first}, second {second}");
            Assert.True(first >= 1);
            Assert.Equal(0, second);
        }
        finally { await CleanupAsync(container); }
    }

    /// <summary>A round trip through Site Configuration, since it is stored as text.</summary>
    [Fact]
    public async Task The_floor_survives_a_round_trip_and_can_be_cleared()
    {
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsService>();
        var original = await settings.GetContainerPollConfigAsync();
        try
        {
            var cfg = await settings.GetContainerPollConfigAsync();
            cfg.ArchiveArrivalsBefore = new DateOnly(2026, 8, 18);
            await settings.SaveContainerPollConfigAsync(cfg, null);
            Assert.Equal(new DateOnly(2026, 8, 18),
                         (await settings.GetContainerPollConfigAsync()).ArchiveArrivalsBefore);

            cfg.ArchiveArrivalsBefore = null;
            await settings.SaveContainerPollConfigAsync(cfg, null);
            Assert.Null((await settings.GetContainerPollConfigAsync()).ArchiveArrivalsBefore);
        }
        finally { await settings.SaveContainerPollConfigAsync(original, null); }
    }
}
