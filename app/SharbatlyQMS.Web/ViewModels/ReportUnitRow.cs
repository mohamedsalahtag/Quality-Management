namespace SharbatlyQMS.Web.ViewModels;

/// <summary>
/// One row of Parameters &gt; Report Units: a material group and the unit
/// label the Quality Order PDF prints for it (Sample Size in the group
/// summary, and the count column above every defect list).
///
/// The backing table (qms_material_group_unit, M11) is deliberately sparse --
/// groups left at the default have no row at all -- so this view model carries
/// <see cref="IsConfigured"/> to tell "explicitly set to Pieces" apart from
/// "never touched", which the page shows as a subtle badge.
/// </summary>
public class ReportUnitRow
{
    public string  MaterialGroup { get; set; } = "";
    /// <summary>MARA description of the group, when the cache knows it.</summary>
    public string? GroupName     { get; set; }
    public string  UnitLabel     { get; set; } = "";
    public bool    IsConfigured  { get; set; }
}
