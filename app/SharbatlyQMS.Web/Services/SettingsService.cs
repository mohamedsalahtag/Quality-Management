using System.Globalization;

namespace SharbatlyQMS.Web.Services;

public class SettingsService : ISettingsService
{
    private readonly IDbService _db;

    public SettingsService(IDbService db) => _db = db;

    public Task<string?> GetAsync(string key) => _db.GetConfigAsync(key);
    public Task SetAsync(string key, string? value, int? updatedBy = null) =>
        _db.SetConfigAsync(key, value ?? "", updatedBy);
    public Task<IReadOnlyDictionary<string, string?>> GetManyAsync(IEnumerable<string> keys) =>
        _db.GetConfigManyAsync(keys);

    // ---- SAP ----
    public async Task<string> GetSapUrlAsync(string endpointKey) =>
        (await _db.GetConfigAsync(endpointKey)) ?? "";

    public async Task<SapEndpointConfig> GetSapConfigAsync()
    {
        var keys = new[] {
            SettingKeys.SapUser,                SettingKeys.SapPassword,
            SettingKeys.SapUrlContainer,        SettingKeys.SapUserContainer,    SettingKeys.SapPasswordContainer,
            SettingKeys.SapUrlMaterial,         SettingKeys.SapUserMaterial,     SettingKeys.SapPasswordMaterial,
            SettingKeys.SapUrlVendor,           SettingKeys.SapUserVendor,       SettingKeys.SapPasswordVendor,
            SettingKeys.SapUrlShipment,         SettingKeys.SapUserShipment,     SettingKeys.SapPasswordShipment,
            SettingKeys.SapUrlPo,               SettingKeys.SapUserPo,           SettingKeys.SapPasswordPo
        };
        var c = await _db.GetConfigManyAsync(keys);
        return new SapEndpointConfig
        {
            User                    = c.GetValueOrDefault(SettingKeys.SapUser) ?? "",
            Password                = c.GetValueOrDefault(SettingKeys.SapPassword) ?? "",
            ContainerSearchUrl      = c.GetValueOrDefault(SettingKeys.SapUrlContainer) ?? "",
            ContainerSearchUser     = c.GetValueOrDefault(SettingKeys.SapUserContainer) ?? "",
            ContainerSearchPassword = c.GetValueOrDefault(SettingKeys.SapPasswordContainer) ?? "",
            MaterialMasterUrl       = c.GetValueOrDefault(SettingKeys.SapUrlMaterial) ?? "",
            MaterialMasterUser      = c.GetValueOrDefault(SettingKeys.SapUserMaterial) ?? "",
            MaterialMasterPassword  = c.GetValueOrDefault(SettingKeys.SapPasswordMaterial) ?? "",
            VendorMasterUrl         = c.GetValueOrDefault(SettingKeys.SapUrlVendor) ?? "",
            VendorMasterUser        = c.GetValueOrDefault(SettingKeys.SapUserVendor) ?? "",
            VendorMasterPassword    = c.GetValueOrDefault(SettingKeys.SapPasswordVendor) ?? "",
            ShipmentDataUrl         = c.GetValueOrDefault(SettingKeys.SapUrlShipment) ?? "",
            ShipmentDataUser        = c.GetValueOrDefault(SettingKeys.SapUserShipment) ?? "",
            ShipmentDataPassword    = c.GetValueOrDefault(SettingKeys.SapPasswordShipment) ?? "",
            PurchaseOrderUrl        = c.GetValueOrDefault(SettingKeys.SapUrlPo) ?? "",
            PurchaseOrderUser       = c.GetValueOrDefault(SettingKeys.SapUserPo) ?? "",
            PurchaseOrderPassword   = c.GetValueOrDefault(SettingKeys.SapPasswordPo) ?? ""
        };
    }

    public async Task SaveSapConfigAsync(SapEndpointConfig cfg, int? updatedBy)
    {
        // Global default credentials. Blank password keeps the existing stored value
        // -- this is the same "write-only" convention as SMTP and per-URL passwords below.
        await _db.SetConfigAsync(SettingKeys.SapUser, cfg.User ?? "", updatedBy);
        if (!string.IsNullOrWhiteSpace(cfg.Password))
            await _db.SetConfigAsync(SettingKeys.SapPassword, cfg.Password, updatedBy);

        // URLs are always overwritten (including back to empty -- that's how an admin
        // "removes" an endpoint configuration).
        await _db.SetConfigAsync(SettingKeys.SapUrlContainer, cfg.ContainerSearchUrl ?? "", updatedBy);
        await _db.SetConfigAsync(SettingKeys.SapUrlMaterial,  cfg.MaterialMasterUrl  ?? "", updatedBy);
        await _db.SetConfigAsync(SettingKeys.SapUrlVendor,    cfg.VendorMasterUrl    ?? "", updatedBy);
        await _db.SetConfigAsync(SettingKeys.SapUrlShipment,  cfg.ShipmentDataUrl    ?? "", updatedBy);
        await _db.SetConfigAsync(SettingKeys.SapUrlPo,        cfg.PurchaseOrderUrl   ?? "", updatedBy);

        // Per-URL usernames -- overwrite (empty = "fall back to global").
        await _db.SetConfigAsync(SettingKeys.SapUserContainer, cfg.ContainerSearchUser ?? "", updatedBy);
        await _db.SetConfigAsync(SettingKeys.SapUserMaterial,  cfg.MaterialMasterUser  ?? "", updatedBy);
        await _db.SetConfigAsync(SettingKeys.SapUserVendor,    cfg.VendorMasterUser    ?? "", updatedBy);
        await _db.SetConfigAsync(SettingKeys.SapUserShipment,  cfg.ShipmentDataUser    ?? "", updatedBy);
        await _db.SetConfigAsync(SettingKeys.SapUserPo,        cfg.PurchaseOrderUser   ?? "", updatedBy);

        // Per-URL passwords. Blank means "keep what's stored" -- same convention as
        // the global password. To explicitly clear a per-URL password, the admin
        // would need to also clear the per-URL user field (which falls back to
        // global) and save.
        if (!string.IsNullOrWhiteSpace(cfg.ContainerSearchPassword))
            await _db.SetConfigAsync(SettingKeys.SapPasswordContainer, cfg.ContainerSearchPassword, updatedBy);
        if (!string.IsNullOrWhiteSpace(cfg.MaterialMasterPassword))
            await _db.SetConfigAsync(SettingKeys.SapPasswordMaterial, cfg.MaterialMasterPassword, updatedBy);
        if (!string.IsNullOrWhiteSpace(cfg.VendorMasterPassword))
            await _db.SetConfigAsync(SettingKeys.SapPasswordVendor, cfg.VendorMasterPassword, updatedBy);
        if (!string.IsNullOrWhiteSpace(cfg.ShipmentDataPassword))
            await _db.SetConfigAsync(SettingKeys.SapPasswordShipment, cfg.ShipmentDataPassword, updatedBy);
        if (!string.IsNullOrWhiteSpace(cfg.PurchaseOrderPassword))
            await _db.SetConfigAsync(SettingKeys.SapPasswordPo, cfg.PurchaseOrderPassword, updatedBy);
    }

    // ---- SMTP ----
    public async Task<SmtpConfig> GetSmtpConfigAsync()
    {
        var c = await _db.GetConfigManyAsync(new[] {
            SettingKeys.SmtpHost, SettingKeys.SmtpPort, SettingKeys.SmtpUser,
            SettingKeys.SmtpPassword, SettingKeys.SmtpFromEmail, SettingKeys.SmtpFromName,
            SettingKeys.SmtpEnableSsl, SettingKeys.SiteName, SettingKeys.SiteUrl
        });
        return new SmtpConfig
        {
            Host      = c.GetValueOrDefault(SettingKeys.SmtpHost)      ?? "",
            Port      = ParseInt(c.GetValueOrDefault(SettingKeys.SmtpPort), 587),
            User      = c.GetValueOrDefault(SettingKeys.SmtpUser)      ?? "",
            Password  = c.GetValueOrDefault(SettingKeys.SmtpPassword)  ?? "",
            FromEmail = c.GetValueOrDefault(SettingKeys.SmtpFromEmail) ?? "",
            FromName  = c.GetValueOrDefault(SettingKeys.SmtpFromName)  ?? "",
            EnableSsl = ParseBool(c.GetValueOrDefault(SettingKeys.SmtpEnableSsl), true),
            SiteName  = c.GetValueOrDefault(SettingKeys.SiteName)      ?? "Sharbatly QMS",
            SiteUrl   = c.GetValueOrDefault(SettingKeys.SiteUrl)       ?? ""
        };
    }

    public async Task SaveSmtpConfigAsync(SmtpConfig cfg, int? updatedBy)
    {
        await _db.SetConfigAsync(SettingKeys.SmtpHost,      cfg.Host ?? "",                       updatedBy);
        await _db.SetConfigAsync(SettingKeys.SmtpPort,      cfg.Port.ToString(CultureInfo.InvariantCulture), updatedBy);
        await _db.SetConfigAsync(SettingKeys.SmtpUser,      cfg.User ?? "",                       updatedBy);
        if (!string.IsNullOrWhiteSpace(cfg.Password))
            await _db.SetConfigAsync(SettingKeys.SmtpPassword, cfg.Password,                      updatedBy);
        await _db.SetConfigAsync(SettingKeys.SmtpFromEmail, cfg.FromEmail ?? "",                  updatedBy);
        await _db.SetConfigAsync(SettingKeys.SmtpFromName,  cfg.FromName ?? "",                   updatedBy);
        await _db.SetConfigAsync(SettingKeys.SmtpEnableSsl, cfg.EnableSsl ? "true" : "false",     updatedBy);
        await _db.SetConfigAsync(SettingKeys.SiteName,      cfg.SiteName ?? "Sharbatly QMS",      updatedBy);
        await _db.SetConfigAsync(SettingKeys.SiteUrl,       cfg.SiteUrl ?? "",                    updatedBy);
    }

    // ---- Thumbnails ----
    public async Task<ThumbnailConfig> GetThumbnailConfigAsync()
    {
        var c = await _db.GetConfigManyAsync(new[] {
            SettingKeys.ThumbScreenW, SettingKeys.ThumbScreenH,
            SettingKeys.ThumbPdfW,    SettingKeys.ThumbPdfH,
            SettingKeys.ImageFitMode
        });
        return new ThumbnailConfig
        {
            ScreenWidth  = ParseInt(c.GetValueOrDefault(SettingKeys.ThumbScreenW), 160),
            ScreenHeight = ParseInt(c.GetValueOrDefault(SettingKeys.ThumbScreenH), 120),
            PdfWidth     = ParseInt(c.GetValueOrDefault(SettingKeys.ThumbPdfW), 120),
            PdfHeight    = ParseInt(c.GetValueOrDefault(SettingKeys.ThumbPdfH), 90),
            FitMode      = c.GetValueOrDefault(SettingKeys.ImageFitMode) ?? "Cover"
        };
    }

    public async Task SaveThumbnailConfigAsync(ThumbnailConfig cfg, int? updatedBy)
    {
        await _db.SetConfigAsync(SettingKeys.ThumbScreenW, cfg.ScreenWidth.ToString(CultureInfo.InvariantCulture),  updatedBy);
        await _db.SetConfigAsync(SettingKeys.ThumbScreenH, cfg.ScreenHeight.ToString(CultureInfo.InvariantCulture), updatedBy);
        await _db.SetConfigAsync(SettingKeys.ThumbPdfW,    cfg.PdfWidth.ToString(CultureInfo.InvariantCulture),     updatedBy);
        await _db.SetConfigAsync(SettingKeys.ThumbPdfH,    cfg.PdfHeight.ToString(CultureInfo.InvariantCulture),    updatedBy);
        await _db.SetConfigAsync(SettingKeys.ImageFitMode, cfg.FitMode ?? "Cover", updatedBy);
    }

    // ---- Alerts ----
    public async Task<AlertConfig> GetAlertConfigAsync()
    {
        var c = await _db.GetConfigManyAsync(new[] {
            SettingKeys.AlertStaleArrivalDays, SettingKeys.AlertOpenQoDays,
            SettingKeys.AlertDefectPctRed,     SettingKeys.AlertDefectPctYellow
        });
        return new AlertConfig
        {
            StaleArrivalDays = ParseInt(c.GetValueOrDefault(SettingKeys.AlertStaleArrivalDays), 3),
            OpenQoDays       = ParseInt(c.GetValueOrDefault(SettingKeys.AlertOpenQoDays), 7),
            DefectPctRed     = ParseInt(c.GetValueOrDefault(SettingKeys.AlertDefectPctRed), 10),
            DefectPctYellow  = ParseInt(c.GetValueOrDefault(SettingKeys.AlertDefectPctYellow), 5)
        };
    }

    public async Task SaveAlertConfigAsync(AlertConfig cfg, int? updatedBy)
    {
        await _db.SetConfigAsync(SettingKeys.AlertStaleArrivalDays, cfg.StaleArrivalDays.ToString(CultureInfo.InvariantCulture), updatedBy);
        await _db.SetConfigAsync(SettingKeys.AlertOpenQoDays,       cfg.OpenQoDays.ToString(CultureInfo.InvariantCulture),       updatedBy);
        await _db.SetConfigAsync(SettingKeys.AlertDefectPctRed,     cfg.DefectPctRed.ToString(CultureInfo.InvariantCulture),     updatedBy);
        await _db.SetConfigAsync(SettingKeys.AlertDefectPctYellow,  cfg.DefectPctYellow.ToString(CultureInfo.InvariantCulture),  updatedBy);
    }

    // ---- SAP Container polling ----
    public async Task<ContainerPollConfig> GetContainerPollConfigAsync()
    {
        var c = await _db.GetConfigManyAsync(new[] {
            SettingKeys.ContainerStartDate, SettingKeys.ContainerPollMinutes
        });
        DateOnly? startDate = null;
        var raw = c.GetValueOrDefault(SettingKeys.ContainerStartDate);
        if (!string.IsNullOrWhiteSpace(raw) &&
            DateOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            startDate = d;
        return new ContainerPollConfig
        {
            StartDate      = startDate,
            PollingMinutes = ParseInt(c.GetValueOrDefault(SettingKeys.ContainerPollMinutes), 60)
        };
    }

    public async Task SaveContainerPollConfigAsync(ContainerPollConfig cfg, int? updatedBy)
    {
        await _db.SetConfigAsync(SettingKeys.ContainerStartDate,
            cfg.StartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "", updatedBy);
        await _db.SetConfigAsync(SettingKeys.ContainerPollMinutes,
            cfg.PollingMinutes.ToString(CultureInfo.InvariantCulture), updatedBy);
    }

    // ---- Auto sync ----
    public async Task<AutoSyncConfig> GetAutoSyncConfigAsync()
    {
        var c = await _db.GetConfigManyAsync(new[] {
            SettingKeys.AutoSyncEnabled, SettingKeys.AutoSyncHours, SettingKeys.AutoSyncLastRunUtc
        });
        DateTime? last = null;
        if (DateTime.TryParse(c.GetValueOrDefault(SettingKeys.AutoSyncLastRunUtc), null,
            DateTimeStyles.RoundtripKind, out var d))
            last = d;
        var hours = (c.GetValueOrDefault(SettingKeys.AutoSyncHours) ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var n) ? n : -1)
            .Where(n => n is >= 0 and <= 23)
            .ToHashSet();
        return new AutoSyncConfig
        {
            Enabled    = ParseBool(c.GetValueOrDefault(SettingKeys.AutoSyncEnabled), false),
            Hours      = hours,
            LastRunUtc = last
        };
    }

    public async Task SaveAutoSyncConfigAsync(AutoSyncConfig cfg, int? updatedBy)
    {
        await _db.SetConfigAsync(SettingKeys.AutoSyncEnabled, cfg.Enabled ? "true" : "false", updatedBy);
        await _db.SetConfigAsync(SettingKeys.AutoSyncHours,
            string.Join(",", cfg.Hours.OrderBy(h => h)),
            updatedBy);
    }

    // ---- Per-endpoint sync ----
    public async Task<EndpointSyncConfig> GetEndpointSyncAsync(string endpointKey)
    {
        var keys = new[]
        {
            $"Sap.Sync.{endpointKey}.Enabled",
            $"Sap.Sync.{endpointKey}.Hours",
            $"Sap.Sync.{endpointKey}.LastRunUtc",
            $"Sap.Sync.{endpointKey}.LastResult",
            $"Sap.Sync.{endpointKey}.LastRowCount"
        };
        var c = await _db.GetConfigManyAsync(keys);

        DateTime? last = null;
        var lastRaw = c.GetValueOrDefault($"Sap.Sync.{endpointKey}.LastRunUtc");
        if (DateTime.TryParse(lastRaw, null, DateTimeStyles.RoundtripKind, out var d)) last = d;

        int? rowCount = null;
        if (int.TryParse(c.GetValueOrDefault($"Sap.Sync.{endpointKey}.LastRowCount"), out var rc))
            rowCount = rc;

        var hours = (c.GetValueOrDefault($"Sap.Sync.{endpointKey}.Hours") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var n) ? n : -1)
            .Where(n => n is >= 0 and <= 23)
            .ToHashSet();

        return new EndpointSyncConfig
        {
            EndpointKey  = endpointKey,
            Enabled      = ParseBool(c.GetValueOrDefault($"Sap.Sync.{endpointKey}.Enabled"), false),
            Hours        = hours,
            LastRunUtc   = last,
            LastResult   = c.GetValueOrDefault($"Sap.Sync.{endpointKey}.LastResult"),
            LastRowCount = rowCount
        };
    }

    public async Task SaveEndpointSyncAsync(string endpointKey, bool enabled, IEnumerable<int> hours, int? updatedBy)
    {
        await _db.SetConfigAsync($"Sap.Sync.{endpointKey}.Enabled",
            enabled ? "true" : "false", updatedBy);
        await _db.SetConfigAsync($"Sap.Sync.{endpointKey}.Hours",
            string.Join(",", hours.Where(h => h is >= 0 and <= 23).Distinct().OrderBy(h => h)),
            updatedBy);
    }

    // ---- Branding ----
    public async Task<BrandingConfig> GetBrandingConfigAsync()
    {
        var c = await _db.GetConfigManyAsync(new[]
        {
            SettingKeys.BrandingCompanyName,
            SettingKeys.BrandingLogoFilename,
            SettingKeys.BrandingFooterLine,
            SettingKeys.BrandingFaviconChoice,
            SettingKeys.BrandingFaviconCustomFilename
        });
        var cfg = new BrandingConfig
        {
            LogoFilename          = c.GetValueOrDefault(SettingKeys.BrandingLogoFilename) ?? "",
            FaviconChoice         = c.GetValueOrDefault(SettingKeys.BrandingFaviconChoice) ?? "",
            FaviconCustomFilename = c.GetValueOrDefault(SettingKeys.BrandingFaviconCustomFilename) ?? ""
        };
        var company = c.GetValueOrDefault(SettingKeys.BrandingCompanyName);
        if (!string.IsNullOrWhiteSpace(company)) cfg.CompanyName = company;
        var footer = c.GetValueOrDefault(SettingKeys.BrandingFooterLine);
        if (!string.IsNullOrWhiteSpace(footer)) cfg.FooterLine = footer;
        return cfg;
    }

    public async Task SaveBrandingConfigAsync(BrandingConfig cfg, int? updatedBy)
    {
        await _db.SetConfigAsync(SettingKeys.BrandingCompanyName, cfg.CompanyName ?? "", updatedBy);
        await _db.SetConfigAsync(SettingKeys.BrandingFooterLine,  cfg.FooterLine ?? "",  updatedBy);
        // LogoFilename / FaviconChoice are written by their own dedicated Save
        // methods after the relevant file / picker action succeeds.
    }

    public async Task SaveLogoFilenameAsync(string filename, int? updatedBy)
    {
        await _db.SetConfigAsync(SettingKeys.BrandingLogoFilename, filename ?? "", updatedBy);
    }

    public async Task SaveFaviconChoiceAsync(string choice, int? updatedBy)
    {
        await _db.SetConfigAsync(SettingKeys.BrandingFaviconChoice, choice ?? "", updatedBy);
    }

    public async Task SaveFaviconCustomFilenameAsync(string filename, int? updatedBy)
    {
        await _db.SetConfigAsync(SettingKeys.BrandingFaviconCustomFilename, filename ?? "", updatedBy);
    }

    public async Task<QoMailTemplate> GetQoMailTemplateAsync()
    {
        var c = await _db.GetConfigManyAsync(new[]
        {
            SettingKeys.QoMailSubject, SettingKeys.QoMailBody, SettingKeys.QoMailEnabled
        });
        var cfg = new QoMailTemplate();
        var sub  = c.GetValueOrDefault(SettingKeys.QoMailSubject);
        var body = c.GetValueOrDefault(SettingKeys.QoMailBody);
        if (!string.IsNullOrWhiteSpace(sub))  cfg.Subject = sub;
        if (!string.IsNullOrWhiteSpace(body)) cfg.Body    = body;
        cfg.Enabled = ParseBool(c.GetValueOrDefault(SettingKeys.QoMailEnabled), false);
        return cfg;
    }

    public async Task SaveQoMailTemplateAsync(QoMailTemplate cfg, int? updatedBy)
    {
        await _db.SetConfigAsync(SettingKeys.QoMailSubject, cfg.Subject ?? "", updatedBy);
        await _db.SetConfigAsync(SettingKeys.QoMailBody,    cfg.Body    ?? "", updatedBy);
        await _db.SetConfigAsync(SettingKeys.QoMailEnabled, cfg.Enabled ? "true" : "false", updatedBy);
    }

    // ---- Active Directory ----
    public async Task<AdConfig> GetAdConfigAsync()
    {
        var c = await _db.GetConfigManyAsync(new[]
        {
            SettingKeys.AdDomain, SettingKeys.AdLdapPath,
            SettingKeys.AdServiceUser, SettingKeys.AdServicePassword,
            SettingKeys.AdAutoCreateOnLogin
        });
        return new AdConfig
        {
            Domain            = c.GetValueOrDefault(SettingKeys.AdDomain) ?? "",
            LdapPath          = c.GetValueOrDefault(SettingKeys.AdLdapPath) ?? "",
            ServiceUser       = c.GetValueOrDefault(SettingKeys.AdServiceUser) ?? "",
            ServicePassword   = c.GetValueOrDefault(SettingKeys.AdServicePassword) ?? "",
            AutoCreateOnLogin = ParseBool(c.GetValueOrDefault(SettingKeys.AdAutoCreateOnLogin), false)
        };
    }

    public async Task SaveAdConfigAsync(AdConfig cfg, int? updatedBy)
    {
        await _db.SetConfigAsync(SettingKeys.AdDomain,      cfg.Domain ?? "",         updatedBy);
        await _db.SetConfigAsync(SettingKeys.AdLdapPath,    cfg.LdapPath ?? "",       updatedBy);
        await _db.SetConfigAsync(SettingKeys.AdServiceUser, cfg.ServiceUser ?? "",    updatedBy);
        // Service password: blank means "keep the existing stored value" -- matches
        // the SMTP/SAP write-only convention.
        if (!string.IsNullOrWhiteSpace(cfg.ServicePassword))
            await _db.SetConfigAsync(SettingKeys.AdServicePassword, cfg.ServicePassword, updatedBy);
        await _db.SetConfigAsync(SettingKeys.AdAutoCreateOnLogin,
            cfg.AutoCreateOnLogin ? "true" : "false", updatedBy);
    }

    // ---- internals ----
    private static int  ParseInt(string? s, int def)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;
    private static bool ParseBool(string? s, bool def)
        => bool.TryParse(s, out var v) ? v : def;
}
