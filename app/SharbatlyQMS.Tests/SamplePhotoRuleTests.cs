using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;
using Xunit;

namespace SharbatlyQMS.Tests;

/// <summary>
/// "Every sample needs at least one photo before the order can be submitted."
///
/// Photographic evidence is the basis of a claim, and a sample recorded without
/// it cannot be re-examined once the fruit is gone — so this rule is what stops
/// an order reaching a supplier with a hole in it.
///
/// Tested as a pure function on purpose: driving the real Submit endpoint would,
/// if the rule ever broke, submit an actual quality order in order to prove it
/// had broken.
/// </summary>
public class SamplePhotoRuleTests
{
    private static Sample S(long id, int no, long materialId = 1) =>
        new() { SampleId = id, SampleNo = no, QoMaterialId = materialId };

    private static readonly Dictionary<long, string> Materials = new() { [1] = "APPLE-001", [2] = "PEAR-002" };

    [Fact]
    public void Passes_when_every_sample_has_a_photo()
    {
        var samples = new[] { S(10, 1), S(11, 2) };
        var counts  = new Dictionary<long, int> { [10] = 1, [11] = 6 };

        Assert.Null(SamplePhotoRule.Check(samples, counts, Materials));
    }

    [Fact]
    public void Blocks_a_sample_with_no_photos_and_names_it()
    {
        var samples = new[] { S(10, 1), S(11, 2) };
        var counts  = new Dictionary<long, int> { [10] = 3, [11] = 0 };

        var msg = SamplePhotoRule.Check(samples, counts, Materials);

        Assert.NotNull(msg);
        // Named the way the operator sees it on the page, so they can go
        // straight to the gap instead of hunting for it.
        Assert.Contains("sample 2 of APPLE-001", msg);
        Assert.DoesNotContain("sample 1 of", msg);
    }

    [Fact]
    public void A_sample_absent_from_the_counts_is_treated_as_having_none()
    {
        // CountByOwnersAsync fills in zeros today, but the rule must not depend
        // on that: a missing key is the same fact as a zero.
        var samples = new[] { S(10, 1) };

        var msg = SamplePhotoRule.Check(samples, new Dictionary<long, int>(), Materials);

        Assert.NotNull(msg);
        Assert.Contains("sample 1", msg);
    }

    [Fact]
    public void Reports_every_gap_not_just_the_first()
    {
        var samples = new[] { S(10, 1), S(11, 2, 2), S(12, 3) };
        var counts  = new Dictionary<long, int> { [10] = 0, [11] = 0, [12] = 4 };

        var msg = SamplePhotoRule.Check(samples, counts, Materials);

        Assert.Contains("2 sample(s)", msg);
        Assert.Contains("sample 1 of APPLE-001", msg);
        Assert.Contains("sample 2 of PEAR-002", msg);
    }

    [Fact]
    public void Stays_quiet_when_there_are_no_samples_at_all()
    {
        // A different rule already reports "this material has no samples".
        // Saying both would be two errors for one cause.
        Assert.Null(SamplePhotoRule.Check(Array.Empty<Sample>(), new Dictionary<long, int>(), Materials));
    }

    [Fact]
    public void Still_names_a_sample_whose_material_cannot_be_resolved()
    {
        var samples = new[] { S(10, 7, materialId: 999) };

        var msg = SamplePhotoRule.Check(samples, new Dictionary<long, int> { [10] = 0 }, Materials);

        Assert.Contains("sample 7", msg);
    }
}

/// <summary>
/// The same rule against the live database, read-only: it must actually fire on
/// the orders that have the problem, and stay silent on the ones that do not.
/// Nothing here submits anything.
/// </summary>
[Collection("workflow")]
public class SamplePhotoRuleLiveTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    public SamplePhotoRuleLiveTests(QmsAppFactory f) => _factory = f;

    [Fact]
    public async Task Fires_on_a_real_order_that_has_a_photoless_sample()
    {
        using var scope = _factory.Services.CreateScope();
        var qos    = scope.ServiceProvider.GetRequiredService<IQualityOrderService>();
        var images = scope.ServiceProvider.GetRequiredService<IImageService>();

        // Known at the time of writing to have samples without photos.
        var qo = (await qos.ListAsync(new SharbatlyQMS.Web.ViewModels.QoListFilter { QoNo = "QO-2026-000684" },
                                      PlantScope.All)).FirstOrDefault();
        if (qo == null) return;

        var samples   = await qos.ListSamplesAsync(qo.QualityOrderId);
        var counts    = await images.CountByOwnersAsync("Sample", samples.Select(s => s.SampleId));
        var materials = (await qos.GetMaterialsAsync(qo.QualityOrderId))
            .ToDictionary(m => m.QoMaterialId, m => m.MaterialNo);

        // Only assert the rule agrees with the data — if somebody uploads the
        // missing photos this stops being a gap, and the test must not then fail
        // for the wrong reason.
        var gaps = samples.Count(s => !counts.TryGetValue(s.SampleId, out var n) || n == 0);
        var msg  = SamplePhotoRule.Check(samples, counts, materials);

        if (gaps > 0) Assert.Contains($"{gaps} sample(s) have no photos", msg);
        else          Assert.Null(msg);
    }
}
