namespace SharbatlyQMS.Web.Models;

/// <summary>
/// One editable arrival field an administrator can govern on the
/// "Arrival Field Rules" page. <see cref="Kind"/> drives how "has a value" is
/// judged for the mandatory check: Date/Text/Number check emptiness, YesNo
/// checks the tri-state bool for null. <see cref="Form"/> is which form the
/// field belongs to (Shipment snapshot or Checklist).
/// </summary>
public sealed record ArrivalFieldDef(string Key, string Display, string Form, string Kind);

/// <summary>Per-field policy stored in qms_arrival_field_policy.</summary>
public class ArrivalFieldPolicy
{
    public string FieldKey           { get; set; } = "";
    public bool   IsMandatory        { get; set; }
    public bool   EditableWhenClosed { get; set; }
}

/// <summary>
/// The catalogue of arrival fields the rules page governs. It is code-defined
/// (not a DB table) because each field maps to a fixed column + input; the
/// per-field flags live in qms_arrival_field_policy keyed by <c>Key</c>.
/// Photo-"taken" toggles are intentionally excluded — they aren't data fields
/// a user would make mandatory.
/// </summary>
public static class ArrivalFieldRegistry
{
    public const string Shipment  = "Shipment";
    public const string Checklist = "Checklist";

    public static readonly IReadOnlyList<ArrivalFieldDef> All = new[]
    {
        // ---- Shipment snapshot (qms_shipment_snapshot) ----
        new ArrivalFieldDef("discharge_date",   "Discharge date",     Shipment,  "Date"),
        new ArrivalFieldDef("unloading_date",   "Unloading date",     Shipment,  "Date"),
        new ArrivalFieldDef("pullout_date",     "Pull-out date",      Shipment,  "Date"),
        new ArrivalFieldDef("time_bar",         "Time bar (days)",    Shipment,  "Number"),
        new ArrivalFieldDef("arrival_place",    "Arrival port",       Shipment,  "Text"),
        new ArrivalFieldDef("inspection_point", "Inspection point",   Shipment,  "Text"),
        new ArrivalFieldDef("joint_survey",     "Joint survey",       Shipment,  "YesNo"),
        new ArrivalFieldDef("time_bar_exceeded","Time bar exceeded",  Shipment,  "YesNo"),
        // ---- Checklist (qms_arrival_checklist) ----
        new ArrivalFieldDef("seal_no",                      "Seal number",                Checklist, "Text"),
        new ArrivalFieldDef("seal_intact",                  "Seal intact",                Checklist, "YesNo"),
        new ArrivalFieldDef("seal_matches_documents",       "Seal matches documents",     Checklist, "YesNo"),
        new ArrivalFieldDef("external_damage_exists",       "External damage",            Checklist, "YesNo"),
        new ArrivalFieldDef("set_temperature",              "Set temperature",            Checklist, "Number"),
        new ArrivalFieldDef("display_temperature",          "Display temperature",        Checklist, "Number"),
        new ArrivalFieldDef("cargo_smell_normal",           "Cargo smell normal",         Checklist, "YesNo"),
        new ArrivalFieldDef("visual_cargo_acceptable",      "Visual cargo acceptable",    Checklist, "YesNo"),
        new ArrivalFieldDef("cargo_shifted_collapsed_water","Cargo shifted/collapsed",    Checklist, "YesNo"),
        new ArrivalFieldDef("pulp_temp_front",              "Pulp temp front",            Checklist, "Number"),
        new ArrivalFieldDef("pulp_temp_middle",             "Pulp temp middle",           Checklist, "Number"),
        new ArrivalFieldDef("pulp_temp_back",               "Pulp temp back",             Checklist, "Number"),
        new ArrivalFieldDef("data_logger_located",          "Data logger located",        Checklist, "YesNo"),
        new ArrivalFieldDef("data_logger_serial",           "Data logger serial",         Checklist, "Text"),
        new ArrivalFieldDef("logger_handed_over",           "Logger handed over",         Checklist, "YesNo"),
        new ArrivalFieldDef("logger_active_data_available", "Logger active & data",       Checklist, "YesNo"),
        new ArrivalFieldDef("logger_temperature",           "Logger temperature",         Checklist, "Number"),
        new ArrivalFieldDef("notes",                        "Notes",                      Checklist, "Text"),
    };

    /// <summary>Day-one defaults (the user's explicit asks): the three dates are
    /// mandatory; joint survey is editable on a closed arrival.</summary>
    public static readonly IReadOnlySet<string> DefaultMandatory =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "unloading_date", "pullout_date", "discharge_date" };
    public static readonly IReadOnlySet<string> DefaultEditableWhenClosed =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "joint_survey" };

    public static ArrivalFieldDef? Find(string key) =>
        All.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
}
