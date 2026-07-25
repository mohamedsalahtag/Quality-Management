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
    Task SaveAlertConfigAsync(AlertConfig cfg, int? updatedBy);

    // ---- Report options (QO report) ----
    Task<ReportConfig> GetReportConfigAsync();
    Task SaveReportConfigAsync(ReportConfig cfg, int? updatedBy);

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
    public const string AlertStaleArrivalDays = "alert_stale_arrival_days";
    public const string AlertOpenQoDays       = "alert_open_qo_days";
    public const string AlertDefectPctRed     = "alert_defect_pct_red";
    public const string AlertDefectPctYellow  = "alert_defect_pct_yellow";

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

    // Mail template for "Send Quality Order report to supplier".
    public const string QoMailSubject = "Mail.QualityReport.Subject";
    public const string QoMailBody    = "Mail.QualityReport.Body";
    public const string QoMailEnabled = "Mail.QualityReport.Enabled";   // "true" / "false"

    // QO report options.
    //   TimeBarBasis: which date the report Time Bar counts from to the QO
    //   finish date. "Discharge" (default) or "Arrival".
    public const string TimeBarBasis       = "Report.TimeBarBasis";

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
    public string Subject  { get; set; } = "Quality Control Report for Quality Order {QO_NO}";
    public string Body     { get; set; } =
        "Dear Supplier,\n\n" +
        "Please find attached the Quality Control Report for Quality Order {QO_NO}\n" +
        "(Container {CONTAINER}, BOL {BOL}, PO {PO}).\n\n" +
        "Best regards,\nSharbatly Quality Team";
    /// <summary>Controls whether the Send Report button is shown on the QO Details page.</summary>
    public bool   Enabled  { get; set; } = false;
}

public class BrandingConfig
{
    public string CompanyName { get; set; } = "Mohamed Abdullah Sharbatly CO. LTD";
    public string FooterLine  { get; set; } = "Sharbatly Fruit - Al Safa District 11, N 25 E Street Abdullah Sharbatly ST.(2053) - 21491 - Jeddah - Email: info@sharbatlyfruit.com";

    /// <summary>Filename of the uploaded logo under wwwroot/branding/. Empty when no logo uploaded.</summary>
    public string LogoFilename { get; set; } = "";

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

public class ReportConfig
{
    /// <summary>Which date the QO report Time Bar counts from to the QO finish
    /// date. One of <see cref="TimeBarBases"/>. Defaults to Discharge.</summary>
    public string TimeBarBasis { get; set; } = TimeBarBases.Discharge;
}

/// <summary>Allowed values for <see cref="ReportConfig.TimeBarBasis"/>.</summary>
public static class TimeBarBases
{
    public const string Discharge = "Discharge";
    public const string Arrival   = "Arrival";

    public static bool IsValid(string? v) =>
        v == Discharge || v == Arrival;
}

public class AlertConfig
{
    public int StaleArrivalDays { get; set; } = 3;
    public int OpenQoDays       { get; set; } = 7;
    public int DefectPctRed     { get; set; } = 10;
    public int DefectPctYellow  { get; set; } = 5;
}

public class ContainerPollConfig
{
    /// <summary>Earliest SAP Doc_Date to consider. Null = polling disabled.</summary>
    public DateOnly? StartDate     { get; set; }
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
