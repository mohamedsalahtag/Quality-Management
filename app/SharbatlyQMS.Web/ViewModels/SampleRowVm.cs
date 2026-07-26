using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.ViewModels;

/// <summary>One <c>&lt;tr&gt;</c> in the samples table of QO Details. Rendered by the <c>_SampleRow</c> partial and used both server-side (initial render) and via the <c>SaveSampleAjax</c> JSON <c>rowHtml</c> field so the same markup ships from one source.</summary>
public class SampleRowVm
{
    public Sample Sample   { get; set; } = new();
    public bool   Editable { get; set; }
    /// <summary>The sample's header values (joined to the catalog) so the row's
    /// Grower / Pallet / Lot / Date-code columns show the live values
    /// (including Material-scoped values copied down) rather than the frozen
    /// legacy columns on qms_sample.</summary>
    public IReadOnlyList<SampleHeaderValue> HeaderValues { get; set; } = Array.Empty<SampleHeaderValue>();
    /// <summary>The sample's readings. Some material groups store attributes like
    /// Grower / Date Code / Brix / Firmness as reading TYPES rather than header
    /// fields, so the preview row falls back to these when no header value exists
    /// (2026-07-26).</summary>
    public IReadOnlyList<SampleReading> Readings { get; set; } = Array.Empty<SampleReading>();
    /// <summary>V31 (2026-06-20): photo count for this sample (qms_image_link
    /// rows with owner_type='Sample'). Renders the camera badge on the row.</summary>
    public int PhotoCount { get; set; }
}
