namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Read-only lookup over the qms_sap_material_cache table -- the local copy
/// of SAP MARA (material master) data populated by Material-Master Sync.
/// Used by the Quality Report PDF so per-sample material details (origin,
/// variety, brand, pack type, etc.) reflect the current MARA record rather
/// than the snapshot taken when the QO was created.
/// </summary>
public interface IMaraService
{
    Task<IReadOnlyDictionary<string, MaraMaterial>> LookupAsync(IEnumerable<string> materialNos);
    Task<MaraMaterial?> GetAsync(string materialNo);
    /// <summary>
    /// Distinct material groups found in the SAP MARA cache, ordered by
    /// code. Used to populate the material-group dropdown on the Defect
    /// Catalog admin page so defects can only be assigned to groups that
    /// actually exist in SAP.
    /// </summary>
    Task<IReadOnlyList<MaraGroup>> ListMaterialGroupsAsync();
}

public class MaraGroup
{
    public string  Code { get; set; } = "";
    public string? Name { get; set; }
}

public class MaraMaterial
{
    public string MaterialNo        { get; set; } = "";
    public string? MaterialDesc     { get; set; }
    public string? Origin           { get; set; }
    public string? Variety          { get; set; }
    public string? MaterialClass    { get; set; }
    public string? MaterialSize     { get; set; }
    public string? MaterialGroup    { get; set; }
    public string? MaterialGroupDesc{ get; set; }
    public string? MajorCategory    { get; set; }
    public string? SubMajorCategory { get; set; }
    public string? Brand            { get; set; }
    public string? PackType         { get; set; }
    public string? PackCode         { get; set; }
    public decimal? NetWeight       { get; set; }
}

public static class MaraMergeExtensions
{
    /// <summary>
    /// Copies MARA fields onto a Quality Order material line, preserving
    /// site-specific overrides (size override flag) and existing non-empty
    /// values. Used by both the QO Details page and the Quality Report PDF
    /// so the screen and the report always reflect the same MARA snapshot.
    /// </summary>
    public static void ApplyMara(this Models.QualityOrderMaterial m, MaraMaterial mm)
    {
        m.MaterialDesc      = mm.MaterialDesc      ?? m.MaterialDesc;
        m.Origin            = mm.Origin            ?? m.Origin;
        m.Variety           = mm.Variety           ?? m.Variety;
        m.MaterialClass     = mm.MaterialClass     ?? m.MaterialClass;
        if (!m.SizeOverridden && !string.IsNullOrWhiteSpace(mm.MaterialSize))
            m.MaterialSize  = mm.MaterialSize;
        m.MaterialGroup     = mm.MaterialGroup     ?? m.MaterialGroup;
        m.MaterialGroupDesc = mm.MaterialGroupDesc ?? m.MaterialGroupDesc;
        m.MajorCategory     = mm.MajorCategory     ?? m.MajorCategory;
        m.SubMajorCategory  = mm.SubMajorCategory  ?? m.SubMajorCategory;
        m.Brand             = mm.Brand             ?? m.Brand;
        m.PackType          = mm.PackType          ?? m.PackType;
        m.PackCode          = mm.PackCode          ?? m.PackCode;
        if ((m.NetWeight == null || m.NetWeight == 0) && mm.NetWeight.HasValue)
            m.NetWeight     = mm.NetWeight;
    }
}
