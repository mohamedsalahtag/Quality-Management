namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Typed accessors over the SiteConfiguration key/value table.
/// Inspired by ProductionControl.Services.SettingsService -- same pattern
/// (constants for keys, async getters/setters), extended for QMS needs:
/// multiple OData endpoints, SMTP, thumbnails, alert thresholds.
/// </summary>
public interface ISettingsService
{
    Task<string?> GetAsync(string key);
    Task SetAsync(string key, string? value, int? updatedBy = null);
    /// <summary>Writes many config keys in a single transaction so a crash
    /// between writes cannot leave a related group of keys partially
    /// updated (e.g. LastRunUtc / LastResult / LastRowCount).</summary>
    Task SetManyAsync(IEnumerable<KeyValuePair<string, string?>> entries, int? updatedBy = null);
    Task<IReadOnlyDictionary<string, string?>> GetManyAsync(IEnumerable<string> keys);

    // ---- SAP OData ----
    Task<string> GetSapUrlAsync(string endpointKey);
    Task<SapEndpointConfig> GetSapConfigAsync();
    Task SaveSapConfigAsync(SapEndpointConfig cfg, int? updatedBy);

    // ---- SMTP ----
    Task<SmtpConfig> GetSmtpConfigAsync();
    Task SaveSmtpConfigAsync(SmtpConfig cfg, int? updatedBy);

    // ---- Thumbnails ----
    Task<ThumbnailConfig> GetThumbnailConfigAsync();
    Task SaveThumbnailConfigAsync(ThumbnailConfig cfg, int? updatedBy);

    // ---- Alerts ----
    Task<AlertConfig> GetAlertConfigAsync();

    /// <summary>Thresholds and clock basis for the Time Bar page.</summary>
    Task<TimeBarConfig> GetTimeBarConfigAsync();
    Task SaveTimeBarConfigAsync(TimeBarConfig cfg, int? updatedBy);
    Task SaveAlertConfigAsync(AlertConfig cfg, int? updatedBy);

    // ---- Report options (QO report) ----
    Task<ReportConfig> GetReportConfigAsync();
    Task SaveReportConfigAsync(ReportConfig cfg, int? updatedBy);

    // ---- Storage (configurable uploads folder) ----
    Task<StorageConfig> GetStorageConfigAsync();
    Task SaveStorageConfigAsync(StorageConfig cfg, int? updatedBy);

    // ---- SAP Container polling (Pending Containers feature) ----
    Task<ContainerPollConfig> GetContainerPollConfigAsync();
    Task SaveContainerPollConfigAsync(ContainerPollConfig cfg, int? updatedBy);

    // ---- Auto Sync schedule (legacy global; superseded by per-endpoint below) ----
    Task<AutoSyncConfig> GetAutoSyncConfigAsync();
    Task SaveAutoSyncConfigAsync(AutoSyncConfig cfg, int? updatedBy);

    // ---- Per-endpoint sync (one schedule + status per syncable URL) ----
    Task<EndpointSyncConfig> GetEndpointSyncAsync(string endpointKey);
    Task SaveEndpointSyncAsync(string endpointKey, bool enabled, IEnumerable<int> hours, int? updatedBy);

    // ---- Branding (company logo + report header text) ----
    Task<BrandingConfig> GetBrandingConfigAsync();
    Task SaveBrandingConfigAsync(BrandingConfig cfg, int? updatedBy);
    Task SaveLogoFilenameAsync(string filename, int? updatedBy);
    Task SaveFaviconChoiceAsync(string choice, int? updatedBy);
    Task SaveFaviconCustomFilenameAsync(string filename, int? updatedBy);

    Task<QoMailTemplate> GetQoMailTemplateAsync();
    Task SaveQoMailTemplateAsync(QoMailTemplate cfg, int? updatedBy);

    // ---- Active Directory ----
    Task<AdConfig> GetAdConfigAsync();
    Task SaveAdConfigAsync(AdConfig cfg, int? updatedBy);
}

public class EndpointSyncConfig
{
    public string EndpointKey { get; set; } = "";
    public bool   Enabled     { get; set; }
    public HashSet<int> Hours { get; set; } = new();
    public DateTime? LastRunUtc  { get; set; }
    public string?   LastResult  { get; set; }
    public int?      LastRowCount{ get; set; }
}

public static class SettingKeys
{
    // SAP OData -- global default credentials (used when a per-URL pair is blank).
    public const string SapUser            = "Sap.User";
    public const string SapPassword        = "Sap.Password";

    // SAP OData -- per-endpoint URLs.
    public const string SapUrlContainer    = "Sap.Url.ContainerSearch";
    public const string SapUrlMaterial     = "Sap.Url.MaterialMaster";
    public const string SapUrlVendor       = "Sap.Url.VendorMaster";
    public const string SapUrlShipment     = "Sap.Url.ShipmentData";
    public const string SapUrlPo           = "Sap.Url.PurchaseOrder";

    // SAP OData -- per-endpoint credential overrides. Blank means "use the
    // global Sap.User / Sap.Password". This supports the case where each
    // OData URL points at a different SAP server (e.g. dev vs prod).
    public const string SapUserContainer       = "Sap.Url.ContainerSearch.User";
    public const string SapPasswordContainer   = "Sap.Url.ContainerSearch.Password";
    public const string SapUserMaterial        = "Sap.Url.MaterialMaster.User";
    public const string SapPasswordMaterial    = "Sap.Url.MaterialMaster.Password";
    public const string SapUserVendor          = "Sap.Url.VendorMaster.User";
    public const string SapPasswordVendor      = "Sap.Url.VendorMaster.Password";
    public const string SapUserShipment        = "Sap.Url.ShipmentData.User";
    public const string SapPasswordShipment    = "Sap.Url.ShipmentData.Password";
    public const string SapUserPo              = "Sap.Url.PurchaseOrder.User";
    public const string SapPasswordPo          = "Sap.Url.PurchaseOrder.Password";

    // SMTP (also seeded by the email pack)
    public const string SmtpHost           = "SmtpHost";
    public const string SmtpPort           = "SmtpPort";
    public const string SmtpUser           = "SmtpUser";
    public const string SmtpPassword       = "SmtpPassword";
    public const string SmtpFromEmail      = "SmtpFromEmail";
    public const string SmtpFromName       = "SmtpFromName";
    public const string SmtpEnableSsl      = "SmtpEnableSsl";
    public const string SiteName           = "SiteName";
    public const string SiteUrl            = "SiteUrl";

    // Thumbnails / images
    public const string ThumbScreenW       = "thumbnail_size_screen_w";
    public const string ThumbScreenH       = "thumbnail_size_screen_h";
    public const string ThumbPdfW          = "thumbnail_size_pdf_w";
    public const string ThumbPdfH          = "thumbnail_size_pdf_h";
    public const string ImageFitMode       = "image_fit_mode";

    // Alert thresholds
    public const string RejectedContainerHeader = "report_rejected_container_header";

    public const string TimeBarGoodDays       = "timebar_good_days";
    public const string TimeBarWarnDays       = "timebar_warn_days";
    public const string TimeBarArrivalBasis   = "timebar_arrival_basis";
    public const string TimeBarStartDate      = "timebar_start_date";
    public const string PendingArchiveBefore  = "pending_archive_before";

    public const string AlertStaleArrivalDays = "alert_stale_arrival_days";
    public const string AlertOpenQoDays       = "alert_open_qo_days";
    public const string AlertDefectPctRed     = "alert_defect_pct_red";
    public const string AlertDefectPctYellow  = "alert_defect_pct_yellow";
    // Disk-full alert: email admins when the uploads partition reaches this %.
    public const string AlertDiskFullPercent  = "alert_disk_full_percent";

    // Storage — configurable uploads folder + disk-alert bookkeeping.
    public const string StorageUploadsRoot       = "Storage.UploadsRoot";
    public const string StorageDiskAlertLastSent = "Storage.DiskAlertLastSentUtc";

    // SAP container polling (Pending Containers page)
    public const string ContainerStartDate   = "Container.PreCollectedStartDate";
    public const string ContainerPollMinutes = "Container.PollingIntervalMinutes";

    // Auto sync
    public const string AutoSyncEnabled      = "Sync.Auto.Enabled";
    public const string AutoSyncHours        = "Sync.Auto.Hours";
    public const string AutoSyncLastRunUtc   = "Sync.Auto.LastRunUtc";
    public const string LastManualSync       = "Sync.LastManualAt";

    // Branding -- company logo + name shown on every printed report.
    public const string BrandingLogoFilename = "Branding.LogoFilename"; // file inside wwwroot/branding/
    public const string BrandingCompanyName  = "Branding.CompanyName";
    public const string BrandingFooterLine   = "Branding.FooterLine";
    // Page icon (favicon). Value is one of the built-in fruit keys
    // (see FaviconChoices.BuiltIn), the literal "custom" (use the file
    // pointed at by BrandingFaviconCustomFilename), or "" for the default
    // favicon.ico.
    public const string BrandingFaviconChoice         = "Branding.FaviconChoice";
    public const string BrandingFaviconCustomFilename = "Branding.FaviconCustomFilename";
    // Company logo size on the Quality Control Report, as a percentage of the
    // base size (100 = original). Default 100. See BrandingConfig.
    public const string BrandingLogoScalePercent      = "Branding.LogoScalePercent";

    // Mail template for "Send Quality Order report to supplier".
    public const string QoMailSubject = "Mail.QualityReport.Subject";
    public const string QoMailBody    = "Mail.QualityReport.Body";
    /// <summary>Standing CC list applied to every supplier report mail.
    /// Comma/semicolon separated.</summary>
    public const string QoMailCc      = "Mail.QualityReport.Cc";
    public const string QoNotifySubject = "Mail.QoNotify.Subject";
    // Mail.QualityReport.Enabled was removed 2026-08-20. It gated a "Send report
    // to supplier" button on the QO Details page that no longer exists -- sending
    // happens from the Claims page -- so the switch controlled nothing a user
    // could see. The stored setting row is left in place, unread.

    // QO report options.
    //   Report.TimeBarBasis was removed 2026-09-26. The report's Time Bar now
    //   counts from the same date as the Time Bar page (timebar_arrival_basis,
    //   goods receipt by default), so the two can no longer disagree. M36
    //   deletes the stored row.
    //   LayoutVersion: which visual layout the QO report PDF renders with.
    //   Fixed at "Soft" — the layout is no longer selectable. The key is kept
    //   so old stored values stay readable (and are ignored).
    public const string ReportLayoutVersion = "Report.LayoutVersion";

    // Active Directory (LDAP bind-only). Configured by SiteAdmin from
    // Admin -> AD Settings.
    public const string AdDomain           = "Ad.Domain";
    public const string AdLdapPath         = "Ad.LdapPath";
    public const string AdServiceUser      = "Ad.ServiceUser";
    public const string AdServicePassword  = "Ad.ServicePassword";
    public const string AdAutoCreateOnLogin = "Ad.AutoCreateOnLogin"; // "true" / "false"
}

public class QoMailTemplate
{
    /// <summary>Default only — an installation that has edited the subject in
    /// Parameters → Mail Template keeps its own. Mirrors the standard line
    /// QcSubjectLine builds for the finish notification, minus the inspection
    /// status (a supplier should not learn of a potential claim from a subject
    /// line).</summary>
    public string Subject  { get; set; } = "Quality Control Report · QC {QC_NO} · Cont {CONTAINER} · {SUPPLIER} · BOL {BOL}";
    public string Body     { get; set; } =
        "Dear Supplier,\n\n" +
        "Please find attached the Quality Control Report for Quality Order {QO_NO}\n" +
        "(Container {CONTAINER}, BOL {BOL}, PO {PO}).\n\n" +
        "Best regards,\nSharbatly Quality Team";

    /// <summary>
    /// Subject for the INTERNAL notification sent when a quality order is
    /// finished -- a different mail from the supplier report above, with a
    /// different audience.
    ///
    /// Blank means the standard automatic line, which is what every
    /// installation had before this was settable: it drops empty fields rather
    /// than leaving dangling separators, shortens a long supplier name, and
    /// stops growing before a narrow mail client would truncate it. A template
    /// cannot do any of that, so the automatic line stays the default and this
    /// is the override for anyone who wants their own wording.
    /// </summary>
    public string NotifySubject { get; set; } = "";

    /// <summary>Addresses copied on EVERY supplier report mail, on top of
    /// whatever the sender types. Enforced server-side in
    /// ReportsController.SendQualityReport, not just pre-filled in the dialog —
    /// a standing CC that the sender can delete is not a standing CC.
    /// Comma- or semicolon-separated; blank means none.</summary>
    public string Cc { get; set; } = "";
}

public class BrandingConfig
{
    public string CompanyName { get; set; } = "Mohamed Abdullah Sharbatly CO. LTD";
    public string FooterLine  { get; set; } = "Sharbatly Fruit - Al Safa District 11, N 25 E Street Abdullah Sharbatly ST.(2053) - 21491 - Jeddah - Email: info@sharbatlyfruit.com";

    /// <summary>Filename of the uploaded logo under wwwroot/branding/. Empty when no logo uploaded.</summary>
    public string LogoFilename { get; set; } = "";

    /// <summary>Company-logo size on the Quality Control Report, as a percentage
    /// of the base size (100 = the original size, 150 = 50% larger). Admin sets
    /// this on Site Configuration → Branding; the report clamps it to 50–400.
    /// Defaults to 100 (original size); the admin drags the slider to enlarge it.</summary>
    public int LogoScalePercent { get; set; } = 100;

    /// <summary>Web path used by Razor img tags (e.g. /branding/logo.png) or empty.</summary>
    public string LogoWebPath => string.IsNullOrEmpty(LogoFilename) ? "" : $"/branding/{LogoFilename}";

    public bool HasLogo => !string.IsNullOrEmpty(LogoFilename);

    /// <summary>
    /// One of: built-in fruit key (see <see cref="FaviconChoices.BuiltIn"/>),
    /// the literal "custom" (use <see cref="FaviconCustomFilename"/>), or ""
    /// for the default favicon.ico bundled with the app.
    /// </summary>
    public string FaviconChoice         { get; set; } = "";
    public string FaviconCustomFilename { get; set; } = "";

    public bool HasCustomFavicon => !string.IsNullOrEmpty(FaviconCustomFilename);

    /// <summary>Web path to render in `<link rel="icon" href="...">`.</summary>
    public string FaviconHref
    {
        get
        {
            if (FaviconChoice == FaviconChoices.Custom && HasCustomFavicon)
                return $"/branding/{FaviconCustomFilename}";
            if (FaviconChoices.IsBuiltIn(FaviconChoice))
                return FaviconChoices.WebPath(FaviconChoice);
            return "/favicon.ico";
        }
    }

    /// <summary>MIME type matching <see cref="FaviconHref"/>.</summary>
    public string FaviconType
    {
        get
        {
            if (FaviconChoice == FaviconChoices.Custom && HasCustomFavicon)
            {
                var ext = System.IO.Path.GetExtension(FaviconCustomFilename).ToLowerInvariant();
                return ext switch
                {
                    ".png"  => "image/png",
                    ".jpg"  => "image/jpeg",
                    ".jpeg" => "image/jpeg",
                    ".gif"  => "image/gif",
                    ".webp" => "image/webp",
                    ".svg"  => "image/svg+xml",
                    ".ico"  => "image/x-icon",
                    _        => "image/png"
                };
            }
            if (FaviconChoices.IsBuiltIn(FaviconChoice))
                return "image/png";
            return "image/x-icon";
        }
    }
}

/// <summary>
/// Fruit-themed built-in favicon choices. Each one is a real 3D-rendered
/// PNG from Microsoft's Fluent UI Emoji set (MIT-licensed, shipped under
/// wwwroot/branding/icons/{key}.png). Photo-style glossy fruit images
/// that render identically on every browser / OS.
/// </summary>
public static class FaviconChoices
{
    /// <summary>Marker for "use the user-uploaded custom file".</summary>
    public const string Custom = "custom";

    /// <summary>Ordered list of (key, label) for the picker tiles.
    /// The matching PNG lives at <c>wwwroot/branding/icons/{key}.png</c>.</summary>
    public static readonly (string Key, string Label)[] BuiltIn = new[]
    {
        ("apple",      "Apple"),         // Sharbatly major
        ("banana",     "Banana"),        // Sharbatly major
        ("orange",     "Orange"),
        ("grapes",     "Grapes"),
        ("strawberry", "Strawberry"),
        ("lemon",      "Lemon"),
        ("mango",      "Mango"),
        ("watermelon", "Watermelon"),
        ("pineapple",  "Pineapple"),
        ("cherries",   "Cherries"),
    };

    public static bool IsBuiltIn(string? key) =>
        !string.IsNullOrEmpty(key) && Array.Exists(BuiltIn, b => b.Key == key);

    /// <summary>Web path served by the static-file middleware.</summary>
    public static string WebPath(string key) => $"/branding/icons/{key}.png";
}

public class SapEndpointConfig
{
    // ---- Global defaults (used when an endpoint's own User/Password are blank) ----
    public string User     { get; set; } = "";
    public string Password { get; set; } = "";

    // ---- Per-endpoint URL + optional credential override ----
    public string ContainerSearchUrl      { get; set; } = "";
    public string ContainerSearchUser     { get; set; } = "";
    public string ContainerSearchPassword { get; set; } = "";

    public string MaterialMasterUrl       { get; set; } = "";
    public string MaterialMasterUser      { get; set; } = "";
    public string MaterialMasterPassword  { get; set; } = "";

    public string VendorMasterUrl         { get; set; } = "";
    public string VendorMasterUser        { get; set; } = "";
    public string VendorMasterPassword    { get; set; } = "";

    public string ShipmentDataUrl         { get; set; } = "";
    public string ShipmentDataUser        { get; set; } = "";
    public string ShipmentDataPassword    { get; set; } = "";

    public string PurchaseOrderUrl        { get; set; } = "";
    public string PurchaseOrderUser       { get; set; } = "";
    public string PurchaseOrderPassword   { get; set; } = "";

    public bool IsAnyConfigured =>
        new[] { ContainerSearchUrl, MaterialMasterUrl, VendorMasterUrl, ShipmentDataUrl, PurchaseOrderUrl }
            .Any(u => !string.IsNullOrWhiteSpace(u));

    /// <summary>
    /// Returns the credentials this config thinks should be used for the named
    /// endpoint. If the endpoint's own User is non-empty, those win; otherwise
    /// the global Sap.User/Sap.Password are returned. Pass the endpoint key
    /// constants from <see cref="SyncableEndpoints"/> or
    /// <see cref="SearchOnlyEndpoints"/>.
    /// </summary>
    public (string User, string Password) ResolveCredentials(string endpointKey) =>
        endpointKey switch
        {
            "ContainerSearch" => PickPair(ContainerSearchUser, ContainerSearchPassword),
            "MaterialMaster"  => PickPair(MaterialMasterUser,  MaterialMasterPassword),
            "VendorMaster"    => PickPair(VendorMasterUser,    VendorMasterPassword),
            "ShipmentData"    => PickPair(ShipmentDataUser,    ShipmentDataPassword),
            "PurchaseOrder"   => PickPair(PurchaseOrderUser,   PurchaseOrderPassword),
            _ => (User, Password)
        };

    private (string, string) PickPair(string overrideUser, string overridePassword)
        => string.IsNullOrWhiteSpace(overrideUser) ? (User, Password) : (overrideUser, overridePassword);
}

public class SmtpConfig
{
    public string Host       { get; set; } = "";
    public int    Port       { get; set; } = 587;
    public string User       { get; set; } = "";
    public string Password   { get; set; } = "";
    public string FromEmail  { get; set; } = "";
    public string FromName   { get; set; } = "";
    public bool   EnableSsl  { get; set; } = true;
    public string SiteName   { get; set; } = "Sharbatly QMS";
    public string SiteUrl    { get; set; } = "";
}

public class ThumbnailConfig
{
    public int    ScreenWidth  { get; set; } = 160;
    public int    ScreenHeight { get; set; } = 120;
    public int    PdfWidth     { get; set; } = 120;
    public int    PdfHeight    { get; set; } = 90;
    public string FitMode      { get; set; } = "Cover";
}

/// <summary>The shipped wording, in one place so the default cannot drift
/// between the config class, the renderer and the settings form.</summary>
public static class RejectedContainerDefaults
{
    public const string Header = "Not accepted container condition";
}

public class ReportConfig
{
    /// <summary>
    /// The banner printed across the quality report of a container that was
    /// refused on arrival. Editable under Site Configuration -> Report: the
    /// exact wording is a commercial matter between the business and its
    /// suppliers, not a development one.
    /// </summary>
    public string RejectedContainerHeader { get; set; } = RejectedContainerDefaults.Header;

    /// <summary>Which visual layout the QO report PDF renders with. Fixed at
    /// <see cref="ReportLayouts.Soft"/> — no longer selectable from Site
    /// Configuration. Kept as a property so the report code has one place to
    /// read the layout from.</summary>
    public string LayoutVersion { get; set; } = ReportLayouts.Soft;
}

/// <summary>Values for <see cref="ReportConfig.LayoutVersion"/>. Soft is the
/// permanent QC-report layout; Classic is kept only so the legacy renderer and
/// any old stored setting value still resolve.</summary>
public static class ReportLayouts
{
    public const string Classic = "Classic";
    public const string Soft    = "Soft";

    public static bool IsValid(string? v) =>
        v == Classic || v == Soft;
}

/// <summary>
/// How the Time Bar page judges an inspection clock (Site Configuration →
/// Alerts). Separate from <see cref="AlertConfig"/> on purpose: those
/// thresholds drive the alert engine and its email fan-out, and these drive
/// nothing but the colour of a bar.
/// </summary>
public class TimeBarConfig
{
    /// <summary>At or below this many days the bar is green. The optimum is one
    /// day: a container arrives and is inspected the same or the next day.</summary>
    public int GoodDays { get; set; } = 1;

    /// <summary>Above this many days the bar is red. Between the two it is
    /// amber. Clamped to at least GoodDays on save -- otherwise every row would
    /// render red and nobody could work out why.</summary>
    public int WarnDays { get; set; } = 3;

    /// <summary>
    /// Which date starts the clock -- on the Time Bar page AND on the QC
    /// report's Time Bar, which read this same setting since 2026-09-26.
    /// GoodsReceipt (the default) is SAP's Receive_Date, the branch goods
    /// receipt, which is also the date the dashboard counts a container in.
    /// PortArrival is SAP's Arrival_Date, the vessel reaching port: a truer
    /// basis for a claim window and a LARGER number. See
    /// <see cref="ShipmentDates"/> for what each date means.
    /// </summary>
    public string ArrivalBasis { get; set; } = TimeBarArrivalBases.GoodsReceipt;

    /// <summary>
    /// A go-live floor: containers that arrived BEFORE this date are left out of
    /// the page entirely -- not counted, not aged, not summarised.
    ///
    /// The page measures how long a container waited for its QC, and the years
    /// of shipments that predate the process being enforced would otherwise
    /// swamp it: 3,843 of today's 4,411 containers already sit above the three
    /// day threshold, so without a floor the page opens on a wall of red that
    /// says nothing about how the team is working now.
    ///
    /// Null means no floor, which is the shipped default -- a cutoff is a
    /// deliberate act by whoever runs the site, not something that quietly
    /// hides data. A container with NO arrival date at all is also dropped once
    /// a floor is set: it cannot be shown to have arrived after it, and it has
    /// no clock to measure either way.
    /// </summary>
    public DateOnly? StartDate { get; set; }
}

/// <summary>
/// The two clock bases, shared by the Time Bar page and the QC report. The
/// report used to have its own Discharge/Arrival setting; it counted from the
/// inspector's discharge date while the page counted from the goods receipt,
/// and the two "time bars" disagreed by days on the same container.
/// </summary>
public static class TimeBarArrivalBases
{
    public const string GoodsReceipt = "GoodsReceipt";
    public const string PortArrival  = "PortArrival";
    public static bool IsValid(string? v) =>
        v == GoodsReceipt || v == PortArrival;

    /// <summary>The caption printed beside the number, e.g. "Time Bar (Receipt)".</summary>
    public static string Caption(string? basis) =>
        basis == PortArrival ? "Port arrival" : "Receipt";
}

public class AlertConfig
{
    public int StaleArrivalDays { get; set; } = 3;
    public int OpenQoDays       { get; set; } = 7;
    public int DefectPctRed     { get; set; } = 10;
    public int DefectPctYellow  { get; set; } = 5;
    /// <summary>Email admins when the uploads partition reaches this % full.
    /// Default 90. Clamped 50–99 on save.</summary>
    public int DiskFullPercent  { get; set; } = 90;
}

/// <summary>Configurable storage locations (Site Configuration → Storage).</summary>
public class StorageConfig
{
    /// <summary>Absolute filesystem folder where uploaded inspection photos are
    /// stored and served from. Blank = fall back to appsettings
    /// QMS:UploadsPhysicalRoot, then wwwroot/uploads. Applied at service startup
    /// (see <see cref="UploadStorage"/>); changing it needs a restart + moving
    /// the existing files.</summary>
    public string UploadsRoot { get; set; } = "";

    /// <summary>Read-only display for the admin UI: the folder photos are
    /// ACTUALLY stored/served from right now (the resolved
    /// <see cref="UploadStorage.Root"/> — the saved value, else appsettings,
    /// else wwwroot/uploads). Not persisted; populated by the controller.</summary>
    public string EffectiveRoot { get; set; } = "";
}

public class ContainerPollConfig
{
    /// <summary>Earliest SAP Receive_Date to consider. Null = polling disabled.</summary>
    public DateOnly? StartDate     { get; set; }

    /// <summary>
    /// Containers that arrived before this date are archived automatically at
    /// the end of every sync.
    ///
    /// Archiving by hand does not stay done. The sweep re-reads SAP from
    /// <see cref="StartDate"/> every run, and a purchase order whose container
    /// was only confirmed later arrives in the cache as a brand-new row long
    /// after the operator archived that period -- 963 containers that arrived
    /// before 18 August 2026 landed in the pending list on 9 September, none of
    /// which existed when that period was archived two days earlier. A date
    /// holds; a one-off sweep does not.
    ///
    /// Nothing is deleted: archived containers stay on the Archived tab and can
    /// be restored one by one or by range. Null disables it, which is the
    /// shipped default.
    /// </summary>
    public DateOnly? ArchiveArrivalsBefore { get; set; }
    /// <summary>How often the polling service should hit SAP, in minutes. Default 60.</summary>
    public int       PollingMinutes{ get; set; } = 60;
    /// <summary>Read-only metadata for the Settings UI status line (last
    /// COMPLETED pull -- in-flight runs are surfaced via <see cref="IsRunning"/>).</summary>
    public DateTime? LastRunUtc    { get; set; }
    public string?   LastResult    { get; set; }
    public int?      LastRowCount  { get; set; }
    /// <summary>"Manual" or "Auto" for the last completed pull.</summary>
    public string?   LastTriggerSource    { get; set; }
    /// <summary>True when a pull is currently in flight (sync_log row with no completed_at).</summary>
    public bool      IsRunning     { get; set; }
    /// <summary>When the in-flight pull started; only set when <see cref="IsRunning"/> is true.</summary>
    public DateTime? RunningSince  { get; set; }
    /// <summary>"Manual" or "Auto" for the in-flight pull.</summary>
    public string?   RunningTriggerSource { get; set; }
}

public class AutoSyncConfig
{
    public bool         Enabled    { get; set; }
    public HashSet<int> Hours      { get; set; } = new();
    public DateTime?    LastRunUtc { get; set; }
}

public class AdConfig
{
    public string Domain          { get; set; } = "";
    public string LdapPath        { get; set; } = "";
    public string ServiceUser     { get; set; } = "";
    public string ServicePassword { get; set; } = "";
    public bool   AutoCreateOnLogin { get; set; }

    /// <summary>True when at least a Domain is configured -- enough to try a bind.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Domain);
}
