using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace SharbatlyQMS.Tests;

/// <summary>
/// The Time Bar page's invariants, against live data.
///
/// The failures worth guarding here are the ones nobody would notice by eye: a
/// container listed twice on page four, a container missing entirely, a clock
/// that reads a day short. Each has a specific cause in the query — a missing
/// GROUP BY before the join, a NULL-mismatched UNION, a UTC/local subtraction —
/// and each is silent.
/// </summary>
[Collection("workflow")]
public class TimeBarServiceTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    private readonly ITestOutputHelper _out;

    public TimeBarServiceTests(QmsAppFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _out = output;
    }

    private async Task<TimeBarPage> LoadAsync(TimeBarFilter f, PlantScope? scope = null,
        TimeBarConfig? cfg = null)
    {
        using var s = _factory.Services.CreateScope();
        var svc = s.ServiceProvider.GetRequiredService<ITimeBarService>();
        return await svc.ListAsync(f, scope ?? PlantScope.All, cfg ?? new TimeBarConfig());
    }

    private static string Key(TimeBarRow r) => $"{r.ContainerNo}|{r.BolNo}|{r.Ebeln}";

    private Microsoft.Data.SqlClient.SqlConnection Db()
    {
        using var s = _factory.Services.CreateScope();
        var cfg = s.ServiceProvider.GetRequiredService<IConfiguration>();
        return new Microsoft.Data.SqlClient.SqlConnection(cfg.GetConnectionString("Default"));
    }

    /// <summary>
    /// The single highest-value assertion here. The SAP cache holds one row per
    /// PO LINE, so joining it without aggregating first multiplies every
    /// container by its line count — and the duplicates are spread across pages
    /// where no reader would spot them.
    /// </summary>
    [Fact]
    public async Task No_container_is_listed_twice()
    {
        var page = await LoadAsync(new TimeBarFilter { PageSize = 100, IncludeArchived = true });
        var keys = page.Rows.Select(Key).ToList();
        _out.WriteLine($"{page.Total} containers total, {keys.Count} on this page");

        var dupes = keys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dupes.Count == 0,
            "A container appears more than once — the cache was joined without being " +
            "aggregated to one row per triplet first: " + string.Join(", ", dupes.Take(5)));
    }

    /// <summary>
    /// Every container SAP sent must be here. This is the regression net for
    /// anyone "simplifying" the UNION spine back to a cache-only FROM.
    /// </summary>
    [Fact]
    public async Task Every_container_sap_sent_is_present()
    {
        using var c = Db();
        var cacheTriplets = await Dapper.SqlMapper.ExecuteScalarAsync<int>(c, @"
            SELECT COUNT(*) FROM (
                SELECT container_no, bol_no, ebeln
                FROM qms_sap_container_cache
                GROUP BY container_no, bol_no, ebeln) t");

        var page = await LoadAsync(new TimeBarFilter { PageSize = 50, IncludeArchived = true });
        _out.WriteLine($"cache triplets {cacheTriplets}, page total {page.Total}");

        Assert.True(page.Total >= cacheTriplets,
            $"The page lists {page.Total} containers but the cache holds {cacheTriplets} " +
            "distinct triplets. Containers are being dropped.");
    }

    /// <summary>
    /// An arrival created through /Arrivals/Search has no cache row at all. A
    /// cache-first query loses it silently — and it is exactly the container
    /// somebody handled by hand, so it is the one worth watching.
    /// </summary>
    [Fact]
    public async Task An_arrival_with_no_cache_row_still_appears()
    {
        using var c = Db();
        var orphan = await Dapper.SqlMapper.QueryFirstOrDefaultAsync<(string? Container, string? Bol, string? Ebeln)>(c, @"
            SELECT TOP 1 a.container_no, a.bol_no, a.ebeln
            FROM   qms_arrival a
            WHERE  a.status_code <> 'Cancelled'
              AND  NOT EXISTS (SELECT 1 FROM qms_sap_container_cache cc
                               WHERE cc.container_no = ISNULL(a.container_no,'')
                                 AND cc.bol_no       = ISNULL(a.bol_no,'')
                                 AND cc.ebeln        = ISNULL(a.ebeln,''))
            ORDER  BY a.arrival_id DESC");

        if (orphan.Container == null)
        {
            _out.WriteLine("No cache-less arrivals exist right now.");
            return;
        }
        _out.WriteLine($"cache-less arrival: {orphan.Container}|{orphan.Bol}|{orphan.Ebeln}");

        var page = await LoadAsync(new TimeBarFilter
        {
            Container = orphan.Container, PageSize = 100, IncludeArchived = true
        });
        Assert.Contains(page.Rows, r =>
            r.ContainerNo == (orphan.Container ?? "") &&
            r.BolNo       == (orphan.Bol ?? "")       &&
            r.Ebeln       == (orphan.Ebeln ?? ""));
    }

    /// <summary>
    /// A negative clock is a real data condition (back-dated receipts), but
    /// printing "-4 d" reads as a bug in the page. It is clamped and flagged.
    /// </summary>
    [Fact]
    public async Task No_clock_reads_negative()
    {
        var page = await LoadAsync(new TimeBarFilter { PageSize = 100, IncludeArchived = true });
        Assert.All(page.Rows, r =>
            Assert.True(r.ElapsedDays is null or >= 0,
                $"{r.ContainerNo}: elapsed {r.ElapsedDays}. Negative values must be clamped " +
                "to 0 and surfaced through IsBackwards instead."));
    }

    /// <summary>
    /// Stage is derived from which joins matched, so it must agree with the ids
    /// projected alongside it. If these drift, the Status column starts telling
    /// a different story than the links beside it.
    /// </summary>
    [Fact]
    public async Task Stage_agrees_with_the_joins()
    {
        var page = await LoadAsync(new TimeBarFilter { PageSize = 100, IncludeArchived = true });
        foreach (var r in page.Rows)
        {
            if (r.Stage == TimeBarStages.Pending)
            {
                Assert.Null(r.ArrivalId);
                Assert.Null(r.StatusCode);
            }
            else if (r.Stage == TimeBarStages.Qc)
            {
                Assert.NotNull(r.QualityOrderId);
                Assert.NotNull(r.ArrivalId);
            }
            else
            {
                Assert.NotNull(r.ArrivalId);
                Assert.Null(r.QualityOrderId);
            }
            // The clock stops at the inspection date (QO opened); a stopped
            // clock and a running one are opposites, nothing may be both.
            Assert.Equal(r.InspectedAt is null, r.IsRunning);
        }
    }

    /// <summary>
    /// Paging must be a partition: every container exactly once across all
    /// pages. Without a total order in the ORDER BY, OFFSET/FETCH can place a
    /// tied row on two pages or on none — and ties are the normal case here,
    /// because most clocks are the same small number of days.
    /// </summary>
    [Fact]
    public async Task Paging_is_a_partition()
    {
        var first = await LoadAsync(new TimeBarFilter { PageSize = 50, IncludeArchived = true });
        var pages = Math.Min(6, (int)Math.Ceiling(first.Total / 50.0));   // enough to prove it
        var seen  = new List<string>();

        for (var p = 1; p <= pages; p++)
        {
            var pg = await LoadAsync(new TimeBarFilter { PageSize = 50, Page = p, IncludeArchived = true });
            seen.AddRange(pg.Rows.Select(Key));
        }
        _out.WriteLine($"walked {pages} page(s), {seen.Count} rows");

        var dupes = seen.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dupes.Count == 0,
            "A container appeared on more than one page — the ORDER BY is not a total " +
            "order, so OFFSET/FETCH is not partitioning: " + string.Join(", ", dupes.Take(5)));
    }

    /// <summary>
    /// Plant scope is a security boundary here, not a convenience filter: this
    /// page lists every container in the business.
    /// </summary>
    [Fact]
    public async Task Plant_scope_is_enforced()
    {
        var scoped = await LoadAsync(new TimeBarFilter { PageSize = 100, IncludeArchived = true },
                                     new PlantScope(false, new[] { "JD01" }));
        Assert.All(scoped.Rows, r =>
            Assert.True(string.IsNullOrEmpty(r.Plant) || r.Plant == "JD01",
                $"{r.ContainerNo} belongs to plant {r.Plant} but the scope is JD01 only."));

        // A user assigned no plants sees nothing — the deliberately strict case.
        var none = await LoadAsync(new TimeBarFilter { PageSize = 100, IncludeArchived = true },
                                   new PlantScope(false, Array.Empty<string>()));
        Assert.Equal(0, none.Total);
        Assert.Empty(none.Rows);
    }

    /// <summary>The archived toggle has to actually exclude.</summary>
    [Fact]
    public async Task Archived_containers_are_hidden_by_default()
    {
        var withArchived = await LoadAsync(new TimeBarFilter { PageSize = 50, IncludeArchived = true });
        var without      = await LoadAsync(new TimeBarFilter { PageSize = 50, IncludeArchived = false });
        _out.WriteLine($"with archived {withArchived.Total}, without {without.Total}");

        Assert.True(without.Total <= withArchived.Total);
        Assert.All(without.Rows, r => Assert.False(r.IsArchived));
    }

    /// <summary>
    /// The over-threshold filter is the page's main control; it must agree with
    /// the same thresholds the colours use.
    /// </summary>
    [Fact]
    public async Task Over_threshold_returns_only_rows_past_the_warning_limit()
    {
        var cfg  = new TimeBarConfig { GoodDays = 1, WarnDays = 3 };
        var page = await LoadAsync(new TimeBarFilter { OverOnly = true, PageSize = 100, IncludeArchived = true }, cfg: cfg);
        _out.WriteLine($"{page.Total} containers over {cfg.WarnDays} days");

        Assert.All(page.Rows, r =>
        {
            Assert.NotNull(r.ElapsedDays);
            Assert.True(r.ElapsedDays > cfg.WarnDays);
            Assert.Equal("danger", r.Tone(cfg.GoodDays, cfg.WarnDays));
        });
    }

    /// <summary>
    /// The go-live floor. Its whole job is to keep years of pre-process
    /// shipments out of a page that measures how the team works now, so the
    /// test that matters is that NOTHING below the floor survives -- including
    /// the rows with no arrival date, which cannot be shown to be above it.
    /// </summary>
    [Fact]
    public async Task A_start_date_leaves_out_everything_that_arrived_before_it()
    {
        var cutoff = DateOnly.FromDateTime(DateTime.Today.AddDays(-30));
        var cfg    = new TimeBarConfig { StartDate = cutoff };
        var page   = await LoadAsync(new TimeBarFilter { PageSize = 100, IncludeArchived = true }, cfg: cfg);
        var all    = await LoadAsync(new TimeBarFilter { PageSize = 100, IncludeArchived = true });

        _out.WriteLine($"cutoff {cutoff:yyyy-MM-dd}: {page.Total} of {all.Total} containers");
        Assert.True(page.Total <= all.Total, "A floor cannot admit MORE containers than no floor.");

        Assert.All(page.Rows, r =>
        {
            Assert.NotNull(r.ArrivalDate);
            Assert.True(DateOnly.FromDateTime(r.ArrivalDate!.Value) >= cutoff,
                $"{Key(r)} arrived {r.ArrivalDate:yyyy-MM-dd}, before the floor {cutoff:yyyy-MM-dd}.");
        });
    }

    /// <summary>
    /// The header, the chips and the average are separate queries over the same
    /// WHERE clause. A floor applied to the rows but not to the summary would
    /// produce a page whose own heading contradicted it -- the sort of thing
    /// nobody reports as a bug, they just stop trusting the page.
    /// </summary>
    [Fact]
    public async Task The_summary_respects_the_start_date_too()
    {
        var cutoff = DateOnly.FromDateTime(DateTime.Today.AddDays(-30));
        var f      = new TimeBarFilter { PageSize = 100, IncludeArchived = true };
        var floored = await LoadAsync(f, cfg: new TimeBarConfig { StartDate = cutoff });
        var open    = await LoadAsync(f, cfg: new TimeBarConfig());

        _out.WriteLine($"floored total={floored.Total} summary={floored.Summary.Containers} " +
                       $"| open total={open.Total} summary={open.Summary.Containers}");

        Assert.Equal(floored.Total, floored.Summary.Containers);
        Assert.True(floored.Summary.Containers <= open.Summary.Containers);
        Assert.True(floored.Summary.OverThreshold <= open.Summary.OverThreshold);
    }

    /// <summary>No floor is the shipped default, and it must change nothing.</summary>
    [Fact]
    public async Task No_start_date_hides_nothing()
    {
        var f = new TimeBarFilter { PageSize = 100, IncludeArchived = true };
        var a = await LoadAsync(f, cfg: new TimeBarConfig { StartDate = null });
        var b = await LoadAsync(f);
        Assert.Equal(b.Total, a.Total);
    }

    /// <summary>
    /// A date typed into Site Configuration has to come back out of it. It is
    /// stored as a string in a key/value table, so a round trip is the only
    /// thing that proves the format agrees at both ends.
    /// </summary>
    [Fact]
    public async Task The_start_date_survives_a_round_trip_and_can_be_cleared()
    {
        using var s = _factory.Services.CreateScope();
        var settings = s.ServiceProvider.GetRequiredService<ISettingsService>();
        var original = await settings.GetTimeBarConfigAsync();
        try
        {
            var cfg = await settings.GetTimeBarConfigAsync();
            cfg.StartDate = new DateOnly(2026, 9, 9);
            await settings.SaveTimeBarConfigAsync(cfg, null);
            Assert.Equal(new DateOnly(2026, 9, 9), (await settings.GetTimeBarConfigAsync()).StartDate);

            cfg.StartDate = null;
            await settings.SaveTimeBarConfigAsync(cfg, null);
            Assert.Null((await settings.GetTimeBarConfigAsync()).StartDate);
        }
        finally { await settings.SaveTimeBarConfigAsync(original, null); }
    }

    [Theory]
    [InlineData(0,  "success")]
    [InlineData(1,  "success")]
    [InlineData(2,  "warning")]
    [InlineData(3,  "warning")]
    [InlineData(4,  "danger")]
    [InlineData(90, "danger")]
    [InlineData(null, "secondary")]
    public void Thresholds_classify_a_clock(int? elapsed, string expected)
    {
        var row = new TimeBarRow { ElapsedDays = elapsed };
        Assert.Equal(expected, row.Tone(goodDays: 1, warnDays: 3));
    }

    /// <summary>
    /// Inverted thresholds would paint every row red with no way to tell why,
    /// so a save has to correct them rather than store them.
    /// </summary>
    [Fact]
    public async Task Saving_inverted_thresholds_corrects_them()
    {
        using var s = _factory.Services.CreateScope();
        var settings = s.ServiceProvider.GetRequiredService<ISettingsService>();
        var original = await settings.GetTimeBarConfigAsync();
        try
        {
            await settings.SaveTimeBarConfigAsync(
                new TimeBarConfig { GoodDays = 5, WarnDays = 2, ArrivalBasis = "nonsense" }, null);
            var saved = await settings.GetTimeBarConfigAsync();
            Assert.True(saved.WarnDays >= saved.GoodDays,
                $"Stored good={saved.GoodDays} warn={saved.WarnDays}: every row would be red.");
            Assert.True(TimeBarArrivalBases.IsValid(saved.ArrivalBasis));
        }
        finally
        {
            await settings.SaveTimeBarConfigAsync(original, null);
        }
    }
}
