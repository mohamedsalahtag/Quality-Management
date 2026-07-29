namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Friendly names for the raw SAP codes the UI shows: plants, storage
/// locations and PO (document) types. Backed by qms_code_description (M10),
/// editable from Parameters &gt; Code Descriptions.
///
/// Deliberately SYNCHRONOUS. Views call these once per table cell — the
/// Pending Containers page alone resolves several hundred per render — so an
/// async, per-call, scoped cache (the ICatalogCache shape) would mean hundreds
/// of awaits per page, and sync-over-async in Razor is worse. Instead this is a
/// singleton holding immutable dictionaries, primed at startup and swapped
/// wholesale by <see cref="Refresh"/> after an admin save. Reads are lock-free.
///
/// Resolution order for every lookup: DB row → hard-coded
/// <see cref="Models.SapPlantDirectorySeed"/> → the bare code. A missing code
/// never throws and never renders blank.
/// </summary>
public interface ICodeDescriptionDirectory
{
    /// <summary>Friendly plant name, or null when the code is unknown.</summary>
    string? PlantName(string? code);

    /// <summary>Friendly storage-location name, or null when the (plant, code)
    /// pair is unknown. Storage codes repeat across plants, hence the plant.</summary>
    string? StorageLocationName(string? plant, string? code);

    /// <summary>Friendly PO / document-type name (ZFAS → Air Shipment), or null.</summary>
    string? PoTypeName(string? code);

    /// <summary>"CODE — Name" when the name is known, otherwise the bare code.
    /// Used for &lt;option&gt; text where the code still needs to be visible.</summary>
    string PlantLabel(string? code);
    string StorageLocationLabel(string? plant, string? code);
    string PoTypeLabel(string? code);

    /// <summary>Name when known, otherwise the bare code — never empty for a
    /// non-empty code. This is what table cells should print.</summary>
    string PlantDisplay(string? code);
    string StorageLocationDisplay(string? plant, string? code);
    string PoTypeDisplay(string? code);

    /// <summary>Every plant known to the directory, ordered by code. Feeds the
    /// plant dropdown on the user admin page.</summary>
    IReadOnlyList<CodeDescriptionEntry> Plants { get; }

    /// <summary>Reloads from the database and swaps the lookup atomically.
    /// Called at startup and after every save on the admin page.</summary>
    Task RefreshAsync(CancellationToken ct = default);
}

/// <summary>One row of qms_code_description.</summary>
public sealed class CodeDescriptionEntry
{
    public int     CodeDescId  { get; set; }
    public string  Domain      { get; set; } = "";
    /// <summary>Owning plant for a storage location; null for plants and PO types.</summary>
    public string? ParentCode  { get; set; }
    public string  Code        { get; set; } = "";
    public string  Description { get; set; } = "";
    public int     SortOrder   { get; set; }
    public bool    IsActive    { get; set; }
    public DateTime UpdatedAt  { get; set; }
    public string? UpdatedBy   { get; set; }
}

/// <summary>The three domains qms_code_description accepts (CHECK-constrained).</summary>
public static class CodeDomains
{
    public const string Plant           = "Plant";
    public const string StorageLocation = "StorageLocation";
    public const string PoType          = "PoType";

    public static readonly string[] All = { Plant, StorageLocation, PoType };

    public static bool IsValid(string? d) =>
        !string.IsNullOrEmpty(d) && Array.IndexOf(All, d) >= 0;

    /// <summary>Label for the admin page's domain tabs.</summary>
    public static string DisplayName(string? d) => d switch
    {
        Plant           => "Plants",
        StorageLocation => "Storage Locations",
        PoType          => "PO Types",
        _               => d ?? ""
    };
}
