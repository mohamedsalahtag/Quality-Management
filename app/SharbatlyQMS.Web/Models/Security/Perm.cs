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
        /// <summary>QC Manager / Admin: reassign a pending SAP container to a
        /// different plant so its Arrival + Quality Order are created there and
        /// the target plant's users can see it.</summary>
        public const string OverridePlant  = Screens.ArrivalsPending + ".OverridePlant";
        /// <summary>QC Manager / Admin: file stale pending containers away into
        /// the archive (by PO-date range, or one at a time) and restore them.
        /// Bulk-archiving hides containers from everyone on the plant, so it is
        /// gated the same way as reassigning one.</summary>
        public const string Archive        = Screens.ArrivalsPending + ".Archive";
        public const string Create         = Screens.ArrivalsIndex   + ".Create";
        public const string SaveChecklist  = Screens.ArrivalsDetails + ".SaveChecklist";
        public const string SaveShipment   = Screens.ArrivalsDetails + ".SaveShipment";
        public const string Complete       = Screens.ArrivalsDetails + ".Complete";
        public const string ReopenForEdit  = Screens.ArrivalsDetails + ".ReopenForEdit";
        public const string Delete         = Screens.ArrivalsDetails + ".Delete";
        public const string ChecklistPdf   = Screens.ArrivalsDetails + ".ChecklistPdf";
        /// <summary>Edit ANY field on a Completed arrival, past the per-field
        /// "editable when closed" rules. Was a hardcoded <c>IsInRole(QcAdmin)</c>
        /// inside the controller, which no administrator could see or change.</summary>
        public const string EditClosedFields = Screens.ArrivalsDetails + ".EditClosedFields";
        /// <summary>Refuse a container that arrived in bad condition. It ends
        /// the inspection before it starts and raises a finished quality order
        /// carrying a potential claim, so it is weighted like finishing an
        /// order rather than like editing one.</summary>
        public const string Reject          = Screens.ArrivalsDetails + ".Reject";
        /// <summary>Undo a rejection. Manager/Admin, because it cancels a
        /// CLOSED quality order — something no other path in the application
        /// can do, and the only way back from a mis-click.</summary>
        public const string CancelRejection = Screens.ArrivalsDetails + ".CancelRejection";
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
        public const string Reinspect      = Screens.QoDetails + ".Reinspect";
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
        /// <summary>Remove a photo from a record that has already been signed
        /// off -- a Completed (or Rejected) arrival, or a quality order past
        /// Open. The ordinary rule makes a signed-off photo set append-only,
        /// which is right: the photos are the evidence. This is the deliberate
        /// exception for a wrong or accidental upload, and every use of it is
        /// audited with the record's status.</summary>
        public const string PhotoDeleteAfterClose = Screens.Attachments + ".PhotoDeleteAfterClose";
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
        /// <summary>Save a perspective with scope='shared', i.e. publish it to
        /// everyone rather than keeping it private. Distinct from
        /// <see cref="SharePerspective"/>, which only gates saving at all.
        /// Replaces a hardcoded <c>IsInRole("Manager") || IsInRole("SiteAdmin")</c>
        /// that compared against the pre-overhaul role names and was therefore
        /// always false -- nobody could publish a perspective at all.</summary>
        public const string PublishPerspective = Screens.ReportsDataHub + ".PublishPerspective";
        /// <summary>Delete a perspective belonging to somebody else. Replaces a
        /// hardcoded <c>IsInRole("SiteAdmin")</c> with the same always-false
        /// defect as above.</summary>
        public const string ManagePerspectives = Screens.ReportsDataHub + ".ManagePerspectives";
        public const string BuilderExport = Screens.ReportsBuilder + ".Export";
    }

    public static class Parameters
    {
        public const string DefectCatalogEdit    = Screens.DefectCatalog    + ".Edit";
        public const string DefectCategoriesEdit = Screens.DefectCategories + ".Edit";
        public const string ReadingTypesEdit     = Screens.ReadingTypes     + ".Edit";
        public const string SampleHeadersEdit    = Screens.SampleHeaders    + ".Edit";
        public const string ArrivalFieldsEdit    = Screens.ArrivalFields    + ".Edit";
        public const string ArrivalFieldRulesEdit= Screens.ArrivalFieldRules+ ".Edit";
        public const string ReportUnitsEdit      = Screens.ReportUnits      + ".Edit";
        public const string CodeDescriptionsEdit = Screens.CodeDescriptions + ".Edit";
        public const string MailTemplateEdit     = Screens.MailTemplate     + ".Edit";
        public const string NotificationsEdit    = Screens.Notifications    + ".Edit";
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

        /// <summary>Rename a label, or reset one back to the English the code
        /// ships. Reading the Labels screen is the screen permission; changing
        /// what every user sees is this.</summary>
        public const string LabelsEdit    = Screens.AdminLabels + ".Edit";

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

    /// <summary>
    /// Permissions that no single action can declare with an attribute, because
    /// they qualify HOW an action behaves rather than whether it may run at all:
    /// "may edit a closed arrival's locked fields", "may publish a perspective
    /// to everyone", "may delete someone else's". Each of these used to be a
    /// role name compiled into a controller — invisible on the Security screen
    /// and, in two cases, comparing against pre-overhaul role names so it
    /// matched nobody.
    ///
    /// They are registered by <c>PermissionCatalog.Discover</c> exactly like an
    /// attribute-declared one, so they appear on the Security screen, seed on
    /// first discovery, and go through the same resolver. Declaring one here is
    /// the ONLY sanctioned alternative to an attribute; anything checking a role
    /// name directly is caught by SecurityMatrixTests.
    /// </summary>
    public sealed record CodeOnlyPermission(string Code, string DisplayName, Seed SeedFor, int SortOrder = 500);

    public static readonly CodeOnlyPermission[] CodeOnly =
    {
        new(Arrivals.EditClosedFields,
            "Edit locked fields on a completed arrival", Seed.AdminOnly),
        new(Reports.PublishPerspective,
            "Publish a perspective to everyone", Seed.ManagerOrAdmin),
        new(Reports.ManagePerspectives,
            "Delete another user's perspective", Seed.AdminOnly),
        new(Attachments.PhotoDeleteAfterClose,
            "Remove a photo after the record is signed off", Seed.AdminOnly),
    };
}
