using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Services;
using Xunit;

namespace SharbatlyQMS.Tests;

/// <summary>
/// The label layer has one job: print the administrator's wording when there is
/// one and the shipped English otherwise. Both halves matter — a lookup that
/// failed open would blank every caption in the application at once.
///
/// Read-mostly: the one test that writes puts the label back afterwards.
/// </summary>
[Collection("workflow")]
public class LabelServiceTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    public LabelServiceTests(QmsAppFactory factory) => _factory = factory;

    private ILabelService Labels()
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ILabelService>();
    }

    [Fact]
    public void An_unknown_label_prints_the_text_the_code_ships()
    {
        var labels = Labels();
        // The overwhelmingly common path: nothing customised, so the view has
        // to render exactly what its markup says.
        Assert.Equal("Container", labels["Container"]);
        Assert.Equal("A caption nobody has ever renamed", labels["A caption nobody has ever renamed"]);
    }

    [Fact]
    public void Empty_and_oversized_text_passes_straight_through()
    {
        var labels = Labels();
        Assert.Equal("", labels[""]);
        // Longer than the key column: storing it would truncate, and a
        // truncated key would never match again.
        var sentence = new string('x', 400);
        Assert.Equal(sentence, labels[sentence]);
    }

    [Fact]
    public async Task A_renamed_label_prints_the_new_text_everywhere()
    {
        var labels = Labels();
        const string key = "Pending Containers";
        var original = (await labels.ListAsync()).FirstOrDefault(l => l.Key == key)?.CustomText;

        try
        {
            await labels.SaveAsync(key, "Awaiting Inspection", "test");
            Assert.Equal("Awaiting Inspection", labels[key]);

            // Clearing is how "reset to default" works, so it must return the
            // ORIGINAL text rather than an empty string.
            await labels.SaveAsync(key, "", "test");
            Assert.Equal(key, labels[key]);
        }
        finally
        {
            await labels.SaveAsync(key, original, "test");
        }
    }

    /// <summary>
    /// The seed migration put the shipped captions in the catalogue so the
    /// admin screen is usable straight after a deployment rather than filling
    /// in as people wander the application.
    /// </summary>
    [Fact]
    public async Task The_catalogue_is_populated_with_the_shipped_captions()
    {
        var all = await Labels().ListAsync();
        Assert.True(all.Count > 300, $"Only {all.Count} labels in the catalogue; the seed did not run.");

        var keys = all.Select(l => l.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var expected in new[] { "Container", "Vendor", "Save changes", "Pending Containers" })
            Assert.Contains(expected, keys);
    }

    /// <summary>
    /// A thousand labels in one list is not usable, so each carries the KIND of
    /// thing it is. The two that matter most are the ones this screen exists
    /// for: the field labels beside a value ("Discharge Date") and the grid
    /// column headers.
    /// </summary>
    [Fact]
    public async Task Every_kind_of_label_is_represented()
    {
        var all = await Labels().ListAsync();
        var byKind = all.GroupBy(l => l.KindLabel).ToDictionary(g => g.Key, g => g.Count());

        foreach (var kind in new[] { "Field label", "Column header", "Button",
                                     "Heading", "Dropdown option", "Placeholder", "Caption" })
            Assert.True(byKind.TryGetValue(kind, out var n) && n > 0,
                $"No labels classified as '{kind}'. Kinds present: {string.Join(", ", byKind.Keys)}");

        // The example that started this: a field label sitting beside its value
        // in the shipment block, which the first pass never reached.
        var discharge = all.FirstOrDefault(l => l.Key == "Discharge Date");
        Assert.True(discharge != null, "'Discharge Date' is not in the label catalogue.");
        Assert.Equal("Field label", discharge!.KindLabel);
    }

    /// <summary>
    /// A label discovered at runtime has no kind — the renderer sees the text,
    /// not the markup around it — and must fall back to a bucket rather than a
    /// blank cell.
    /// </summary>
    [Fact]
    public void An_unclassified_label_reads_as_Other()
    {
        var l = new UiLabel("x", null, null, null, DateTime.UtcNow, null, null);
        Assert.Equal("Other", l.KindLabel);
        Assert.Equal("x", l.Effective);
        Assert.False(l.IsOverridden);
    }

    /// <summary>
    /// The views really do ship both "Created By" and "Created by". The key
    /// column is BIN2-collated so those stay two rows: under the server's
    /// default case-insensitive collation renaming one would silently rename
    /// the other, and the C# lookup (StringComparer.Ordinal) would disagree
    /// with the database about which row it just wrote.
    /// </summary>
    [Fact]
    public async Task Labels_differing_only_in_case_are_separate_rows()
    {
        var keys = (await Labels().ListAsync()).Select(l => l.Key).ToList();
        var pairs = keys.GroupBy(k => k.ToLowerInvariant()).Where(g => g.Count() > 1).ToList();
        Assert.True(pairs.Count > 0,
            "Expected case-only label pairs to survive as distinct rows; none found, " +
            "which means the key column collapsed them.");
    }
}
