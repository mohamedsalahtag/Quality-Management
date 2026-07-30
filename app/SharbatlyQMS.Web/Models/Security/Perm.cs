namespace SharbatlyQMS.Web.Models.Security;

/// <summary>
/// Every ACTION permission in the application, as compile-time constants so
/// controllers and views reference the same string.
///
/// A code is <c>&lt;screen key&gt;.&lt;action&gt;</c>; the screen it belongs to is
/// everything before the last dot (see <see cref="Screens.OwnerOf"/>). SCREEN
/// permissions have no constant here — the screen key from <see cref="Screens"/>
/// is itself the permission code.
///
/// The rows in <c>qms_permission</c> are created from the attributes at startup,
/// not from this file; these constants exist so a typo is a compile error rather
/// than a silently-denied button.
/// </summary>
public static class Perm
{
    public static class Arrivals
    {
        public const string Retrieve       = Screens.ArrivalsPending + ".Retrieve";
        public const string Create         = Screens.ArrivalsIndex   + ".Create";
        public const string SaveChecklist  = Screens.ArrivalsDetails + ".SaveChecklist";
        public const string SaveShipment   = Screens.ArrivalsDetails + ".SaveShipment";
        public const string Complete       = Screens.ArrivalsDetails + ".Complete";
        public const string ReopenForEdit  = Screens.ArrivalsDetails + ".ReopenForEdit";
        public const string Delete         = Screens.ArrivalsDetails + ".Delete";
        public const string ChecklistPdf   = Screens.ArrivalsDetails + ".ChecklistPdf";
    }

    public static class Qo
    {
        public const string Create         = Screens.QoIndex   + ".Create";
        public const string Open           = Screens.QoDetails + ".Open";
        public const string Submit         = Screens.QoDetails + ".Submit";
        public const string CancelSubmit   = Screens.QoDetails + ".CancelSubmit";
        public const string Finish         = Screens.QoDetails + ".Finish";
        public const string Reopen         = Screens.QoDetails + ".Reopen";
        public const string Cancel         = Screens.QoDetails + ".Cancel";
        public const string Delete         = Screens.QoDetails + ".Delete";
        public const string EditMaterial   = Screens.QoDetails + ".EditMaterial";
        public const string OverrideSize   = Screens.QoDetails + ".OverrideSize";
        public const string EditSample     = Screens.QoDetails + ".EditSample";
        public const string DeleteSample   = Screens.QoDetails + ".DeleteSample";
        public const string Pdf            = Screens.QoDetails + ".Pdf";
        public const string SendReport     = Screens.QoDetails + ".SendReport";
    }

    public static class Claims
    {
        public const string MarkClaimRequest = Screens.Claims + ".MarkClaimRequest";
        public const string MarkPassedQc     = Screens.Claims + ".MarkPassedQc";
        public const string Approve          = Screens.Claims + ".Approve";
        public const string Hold             = Screens.Claims + ".Hold";
        public const string AddNote          = Screens.Claims + ".AddNote";
    }

    public static class Attachments
    {
        public const string Download    = Screens.Attachments + ".Download";
        public const string Upload      = Screens.Attachments + ".Upload";
        public const string Delete      = Screens.Attachments + ".Delete";
        public const string PhotoUpload = Screens.Attachments + ".PhotoUpload";
        public const string PhotoDelete = Screens.Attachments + ".PhotoDelete";
    }

    public static class Reports
    {
        public const string DataHubExport = Screens.ReportsDataHub + ".Export";
        public const string Pivot         = Screens.ReportsDataHub + ".Pivot";
        public const string PivotExport   = Screens.ReportsDataHub + ".PivotExport";
        /// <summary>Saving a perspective for everyone, not just yourself. Was
        /// enforced only in the view before, so a Supervisor could share by
        /// calling the endpoint directly.</summary>
        public const string SharePerspective = Screens.ReportsDataHub + ".SharePerspective";
        public const string BuilderExport = Screens.ReportsBuilder + ".Export";
    }

    public static class Parameters
    {
        public const string DefectCatalogEdit    = Screens.DefectCatalog    + ".Edit";
        public const string DefectCategoriesEdit = Screens.DefectCategories + ".Edit";
        public const string ReadingTypesEdit     = Screens.ReadingTypes     + ".Edit";
        public const string SampleHeadersEdit    = Screens.SampleHeaders    + ".Edit";
        public const string ArrivalFieldsEdit    = Screens.ArrivalFields    + ".Edit";
        public const string ReportUnitsEdit      = Screens.ReportUnits      + ".Edit";
        public const string CodeDescriptionsEdit = Screens.CodeDescriptions + ".Edit";
        public const string MailTemplateEdit     = Screens.MailTemplate     + ".Edit";
    }

    public static class Admin
    {
        public const string SettingsEdit  = Screens.AdminSettings + ".Edit";
        public const string SapSync       = Screens.AdminSettings + ".SapSync";
        public const string SendTestEmail = Screens.AdminSettings + ".SendTestEmail";
        public const string Branding      = Screens.AdminSettings + ".Branding";
        /// <summary>The "delete every transaction" danger-zone button.</summary>
        public const string PurgeAll      = Screens.AdminSettings + ".PurgeAll";

        public const string UsersEdit     = Screens.AdminUsers + ".Edit";
        public const string UsersDelete   = Screens.AdminUsers + ".Delete";

        public const string AuditExport   = Screens.AdminAuditLog + ".Export";

        /// <summary>Compose roles on the Security screen. Together with
        /// <see cref="Screens.AdminSecurity"/> this is the pair the
        /// administrator role can never lose — see PermissionResolver.</summary>
        public const string SecurityEdit  = Screens.AdminSecurity + ".Edit";
        /// <summary>Impersonate another role to preview what it can see.</summary>
        public const string ViewAs        = Screens.AdminSecurity + ".ViewAs";
    }

    /// <summary>
    /// The two codes an administrator can never be denied. Enforced as string
    /// constants in the resolver rather than as a database flag: a flag can be
    /// flipped by a bug or a bad bulk edit, a constant cannot. This is what
    /// makes the Security screen unlosable and the break-glass script a
    /// last resort rather than a routine tool.
    /// </summary>
    public static readonly string[] AdminFloor = { Screens.AdminSecurity, Admin.SecurityEdit };
}
