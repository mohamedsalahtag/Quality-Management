using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;
using Xunit;
using Xunit.Abstractions;

namespace SharbatlyQMS.Tests;

/// <summary>
/// A container SAP cannot date does not belong on the pending list.
///
/// SAP leaves Receive_Date blank until the goods are actually received. The
/// sweep fetches on `Receive_Date ge {start}` and a blank never satisfies a
/// `ge`, so such a container silently stops being refreshed — and the row keeps
/// whatever date it was last given. That is how containers came to sit on the
/// pending list showing an arrival date SAP had already withdrawn: nothing
/// errored, the date just stopped being true.
/// </summary>
[Collection("workflow")]
public class PendingReceiveDateTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    private readonly ITestOutputHelper _out;

    public PendingReceiveDateTests(QmsAppFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    private SqlConnection Db()
    {
        using var s = _factory.Services.CreateScope();
        var cfg = s.ServiceProvider.GetRequiredService<IConfiguration>();
        return new SqlConnection(cfg.GetConnectionString("Default"));
    }

    private async Task<string> SeedAsync(DateTime? receiveDate)
    {
        var tag = $"TSTRD{Guid.NewGuid():N}".Substring(0, 11).ToUpperInvariant();
        using var c = Db();
        await c.OpenAsync();
        await c.ExecuteAsync(@"
            INSERT INTO qms_sap_container_cache
                (container_no, bol_no, ebeln, ebelp, material_no, plant, storage_loc, batch_no,
                 vendor_no, vendor_name, material_desc, material_group, po_type,
                 doc_date, arrival_date, receive_date, quantity, uom, payload_json,
                 has_arrival, last_seen_at)
            VALUES
                (@tag, @tag, '4700000000', '00010', 'TESTMAT', 'JD01', 'A001', NULL,
                 'V1', 'Test vendor', 'Test material', 'APPLE', 'STO',
                 @d, @d, @receiveDate, 1, 'KG', '{}',
                 0, SYSUTCDATETIME());",
            new { tag, d = DateTime.Today, receiveDate });
        return tag;
    }

    private async Task CleanupAsync(string tag)
    {
        using var c = Db();
        await c.OpenAsync();
        await c.ExecuteAsync(
            "DELETE FROM qms_sap_container_cache WHERE container_no = @tag", new { tag });
    }

    private async Task<bool> OnPendingListAsync(string tag)
    {
        using var s = _factory.Services.CreateScope();
        var cache = s.ServiceProvider.GetRequiredService<IContainerCacheService>();
        var page  = await cache.ListPendingAsync(container: tag, scope: PlantScope.All);
        return page.Rows.Any();
    }

    [Fact]
    public async Task A_container_with_a_receive_date_is_listed()
    {
        var tag = await SeedAsync(DateTime.Today);
        try
        {
            Assert.True(await OnPendingListAsync(tag), "a dated container should be pending");
        }
        finally { await CleanupAsync(tag); }
    }

    /// <summary>
    /// The rule itself. Asserted through the LIST rather than the SQL, because
    /// the thing that matters is what an operator sees.
    /// </summary>
    [Fact]
    public async Task A_container_SAP_cannot_date_is_not_listed()
    {
        var tag = await SeedAsync(null);
        try
        {
            Assert.False(await OnPendingListAsync(tag),
                "a container with no Receive_Date must not appear on the pending list");
        }
        finally { await CleanupAsync(tag); }
    }

    /// <summary>
    /// And it comes back on its own. If SAP dates the container again the next
    /// sweep returns it and the MERGE refills both columns, so the operator does
    /// not have to do anything to recover it.
    /// </summary>
    [Fact]
    public async Task It_returns_to_the_list_when_SAP_dates_it_again()
    {
        var tag = await SeedAsync(null);
        try
        {
            Assert.False(await OnPendingListAsync(tag));

            using (var c = Db())
            {
                await c.OpenAsync();
                await c.ExecuteAsync(@"
                    UPDATE qms_sap_container_cache
                    SET    receive_date = @d, arrival_date = @d
                    WHERE  container_no = @tag",
                    new { tag, d = DateTime.Today });
            }

            Assert.True(await OnPendingListAsync(tag),
                "once SAP supplies a date again the container should return");
        }
        finally { await CleanupAsync(tag); }
    }

    /// <summary>
    /// The archive is a record, not a worklist: a row archived while it had a
    /// date keeps showing there even if its date is later withdrawn. Without
    /// this exemption, clearing a stale date would erase the row from the
    /// archive too.
    /// </summary>
    [Fact]
    public async Task The_archive_still_shows_an_undated_row()
    {
        var tag = await SeedAsync(null);
        try
        {
            using (var c = Db())
            {
                await c.OpenAsync();
                await c.ExecuteAsync(@"
                    UPDATE qms_sap_container_cache
                    SET    archived_at = SYSUTCDATETIME(), archived_by = 'test'
                    WHERE  container_no = @tag", new { tag });
            }

            using var s = _factory.Services.CreateScope();
            var cache = s.ServiceProvider.GetRequiredService<IContainerCacheService>();
            var page  = await cache.ListPendingAsync(container: tag, scope: PlantScope.All, archived: true);
            _out.WriteLine($"archive rows for {tag}: {page.Rows.Count}");
            Assert.True(page.Rows.Any(), "the archive should still list it");
        }
        finally { await CleanupAsync(tag); }
    }
}
