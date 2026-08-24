using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// "Every sample needs at least one photo before the order can be submitted."
///
/// Kept as a pure function of the data rather than inline in the controller so
/// it can be tested without a database and without posting a real submit — a
/// test that drives the endpoint against live data would, if the rule ever
/// broke, submit an actual quality order to prove that it had.
/// </summary>
public static class SamplePhotoRule
{
    /// <summary>
    /// The message to show the operator, or null when every sample is covered.
    /// </summary>
    /// <param name="samples">Samples on the order.</param>
    /// <param name="photoCounts">sample id -> photo count. A sample missing
    /// from the dictionary counts as zero.</param>
    /// <param name="materialNoById">qo material id -> material number, used to
    /// name the gap the way the operator sees it on screen.</param>
    public static string? Check(
        IReadOnlyList<Sample> samples,
        IReadOnlyDictionary<long, int> photoCounts,
        IReadOnlyDictionary<long, string> materialNoById)
    {
        // No samples at all is a different problem, and the "every material must
        // have a sample" rule already reports it. Saying both at once would be
        // two errors for one cause.
        if (samples.Count == 0) return null;

        var missing = samples
            .Where(s => !photoCounts.TryGetValue(s.SampleId, out var n) || n <= 0)
            .ToList();
        if (missing.Count == 0) return null;

        var named = missing.Select(s =>
            materialNoById.TryGetValue(s.QoMaterialId, out var mat) && !string.IsNullOrWhiteSpace(mat)
                ? $"sample {s.SampleNo} of {mat}"
                : $"sample {s.SampleNo}");

        return $"{missing.Count} sample(s) have no photos — {string.Join(", ", named)}. "
             + "Every sample needs at least one photo before the report can be submitted.";
    }
}
