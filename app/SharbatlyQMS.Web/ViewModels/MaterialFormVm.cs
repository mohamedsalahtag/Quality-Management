using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.ViewModels;

/// <summary>
/// Backs the per-material "Material details" panel on the QO Details page,
/// where the Material-scoped header fields (Grower Pallet, Pack Code, Date
/// Code, Label, Label Number) and the material's Sample Size are entered once
/// and inherited by every sample of that material.
/// </summary>
public class MaterialFormVm
{
    public QualityOrder          Qo            { get; set; } = new();
    public QualityOrderMaterial  Material      { get; set; } = new();
    /// <summary>Material-scoped header fields only.</summary>
    public IReadOnlyList<SampleHeaderField>  HeaderFields   { get; set; } = Array.Empty<SampleHeaderField>();
    public IReadOnlyList<MaterialHeaderValue> ExistingValues { get; set; } = Array.Empty<MaterialHeaderValue>();
    public short? SampleSize { get; set; }
    public bool   Editable   { get; set; }
}
