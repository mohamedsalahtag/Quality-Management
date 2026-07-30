namespace SharbatlyQMS.Web.Models.Security;

/// <summary>
/// The screen keys seeded by migration M14. A screen is the unit an
/// administrator thinks in: it owns a permission of its own (which carries the
/// Read-only / Edit level on the six record screens) plus the action
/// permissions for every button on it.
///
/// Keep these in step with the MERGE in <c>M14__security_matrix.sql</c> — a
/// startup check fails loudly if an attribute names a key the database does not
/// have, and a unit test asserts the same thing without touching the database.
/// </summary>
public static class Screens
{
    public const string Dashboard          = "General.Dashboard";
    /// <summary>Photo and document attachments. A function group rather than a
    /// page: one upload action serves arrivals, quality orders and samples, so
    /// it cannot belong to any single record screen. Deliberately not levelled —
    /// a read-only role must still be able to open an attachment.</summary>
    public const string Attachments        = "Attachments";

    // ---- Records. The six with a Read-only / Edit level are marked. ----
    public const string ArrivalsPending    = "Arrivals.Pending";      // levelled
    public const string ArrivalsIndex      = "Arrivals.Index";        // levelled
    public const string ArrivalsSearch     = "Arrivals.Search";
    public const string ArrivalsDetails    = "Arrivals.Details";      // levelled
    public const string QoIndex            = "QualityOrders.Index";   // levelled
    public const string QoDetails          = "QualityOrders.Details"; // levelled
    public const string Claims             = "Claims";                // levelled

    // ---- Reports ----
    public const string ReportsDataHub     = "Reports.DataHub";
    public const string ReportsBuilder     = "Reports.Builder";

    // ---- Parameters ----
    public const string DefectCatalog      = "Parameters.DefectCatalog";
    public const string DefectCategories   = "Parameters.DefectCategories";
    public const string ReadingTypes       = "Parameters.ReadingTypes";
    public const string SampleHeaders      = "Parameters.SampleHeaders";
    public const string ArrivalFields      = "Parameters.ArrivalFields";
    public const string ReportUnits        = "Parameters.ReportUnits";
    public const string CodeDescriptions   = "Parameters.CodeDescriptions";
    public const string MailTemplate       = "Parameters.MailTemplate";

    // ---- Admin ----
    public const string AdminSettings      = "Admin.Settings";
    public const string AdminUsers         = "Admin.Users";
    public const string AdminAuditLog      = "Admin.AuditLog";
    public const string AdminSecurity      = "Admin.Security";

    public static readonly string[] All =
    {
        Dashboard, Attachments,
        ArrivalsPending, ArrivalsIndex, ArrivalsSearch, ArrivalsDetails,
        QoIndex, QoDetails, Claims,
        ReportsDataHub, ReportsBuilder,
        DefectCatalog, DefectCategories, ReadingTypes, SampleHeaders,
        ArrivalFields, ReportUnits, CodeDescriptions, MailTemplate,
        AdminSettings, AdminUsers, AdminAuditLog, AdminSecurity
    };

    /// <summary>Screens whose nav entries live in the Parameters dropdown.</summary>
    public static readonly string[] ParametersGroup =
    {
        DefectCatalog, DefectCategories, ReadingTypes, SampleHeaders,
        ArrivalFields, ReportUnits, CodeDescriptions, MailTemplate
    };

    /// <summary>Screens whose nav entries live in the Admin dropdown.</summary>
    public static readonly string[] AdminGroup =
    {
        AdminSettings, AdminUsers, AdminAuditLog, AdminSecurity
    };

    /// <summary>Screens whose nav entries live in the Reports dropdown.</summary>
    public static readonly string[] ReportsGroup = { ReportsDataHub, ReportsBuilder };

    /// <summary>
    /// The screen an action permission belongs to: everything before its last
    /// dot. "Arrivals.Details.Complete" -> "Arrivals.Details".
    ///
    /// Deriving it rather than declaring it twice keeps the attribute short AND
    /// lets the requirement be built without dependency injection, which
    /// <c>IAuthorizationRequirementData</c> requires.
    /// </summary>
    public static string OwnerOf(string permissionCode)
    {
        var lastDot = permissionCode.LastIndexOf('.');
        return lastDot <= 0 ? permissionCode : permissionCode[..lastDot];
    }
}
