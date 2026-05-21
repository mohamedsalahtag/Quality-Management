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
}
