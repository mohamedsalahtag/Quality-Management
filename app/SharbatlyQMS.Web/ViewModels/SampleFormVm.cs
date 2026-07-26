using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.ViewModels;

/// <summary>
/// Everything the _SampleForm partial needs to render. Built once per
/// sample by either the QO Details page (which renders one offcanvas per
/// sample for fast in-context editing) or by the standalone Sample page
/// (kept for direct-link / deep-link access).
/// </summary>
public class SampleFormVm
{
    public QualityOrder            Qo               { get; set; } = new();
    public Sample                  Sample           { get; set; } = new();
    public QualityOrderMaterial?   QoMaterial       { get; set; }
    public IReadOnlyList<ReadingTypeEntry>   ReadingTypes    { get; set; } = Array.Empty<ReadingTypeEntry>();
    public IReadOnlyList<DefectCatalogEntry> Defects         { get; set; } = Array.Empty<DefectCatalogEntry>();
    public IReadOnlyDictionary<string,string> SectionMap     { get; set; } = new Dictionary<string,string>();
    public IReadOnlyList<SampleReading>      ExistingReadings{ get; set; } = Array.Empty<SampleReading>();
    public IReadOnlyList<SampleDefect>       ExistingDefects { get; set; } = Array.Empty<SampleDefect>();
    public bool                              Editable        { get; set; }

    // ---- Sample header fields (V20+) ----
    // The active catalog drives the dynamic inputs at the top of the
    // form; the existing values pre-fill them when editing. Sample Size
    // stays hardcoded (denominator for defect percentages) so it is NOT
    // part of this list.
    public IReadOnlyList<SampleHeaderField>  HeaderFields    { get; set; } = Array.Empty<SampleHeaderField>();
    public IReadOnlyList<SampleHeaderValue>  ExistingHeader  { get; set; } = Array.Empty<SampleHeaderValue>();

    // Material-scoped header fields + their current values. Rendered as an
    // EDITABLE "Material details" card inside every sample (2026-07-26): the
    // values are shared by the whole material, so filling them from any sample
    // updates the material and every other sample. Keeps material details in
    // front of the operator so they aren't forgotten.
    public IReadOnlyList<SampleHeaderField>   MaterialHeaderFields { get; set; } = Array.Empty<SampleHeaderField>();
    public IReadOnlyList<MaterialHeaderValue> MaterialHeaderValues { get; set; } = Array.Empty<MaterialHeaderValue>();

    // Defect category master (V22+) — drives the dynamic per-category defect
    // sections (order + colour). The defects themselves come from Defects.
    public IReadOnlyList<DefectCategory> Categories { get; set; } = Array.Empty<DefectCategory>();
}
