// ============================================================
// AdService - Reference Implementation (.NET 9 + System.DirectoryServices)
// ============================================================
// This is the actual shipping LDAP integration from c:\HelpDesk. It speaks
// the contract described in SPEC.md. When porting to another stack:
//   - Preserve the bind-only login rule (no LDAP search at login time).
//   - Preserve the strategy ladder for browse (try multiple URL/bind combos).
//   - Preserve the LDAP path parser.
//   - Preserve the wrong-password short-circuit (don't try other formats).
//
// External assumptions:
//   - IDbService.GetConfigAsync(key) returns the AD config rows by key.
// ============================================================

using HelpDesk.Models;

namespace HelpDesk.Services;

public interface IAdService
{
    Task<AdUser?> AuthenticateAsync(string username, string password);
    Task<AdUser?> GetUserInfoAsync(string username);
    Task<List<AdUser>> GetAllActiveUsersAsync();
    Task<(bool ok, string message)> TestConnectionAsync();
    bool IsConfigured { get; }
}

public class AdService : IAdService
{
    private readonly IDbService _db;
    private readonly ILogger<AdService> _logger;

    public AdService(IDbService db, ILogger<AdService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public bool IsConfigured => true;

    private async Task<(string domain, string ldapPath, string svcUser, string svcPass)> GetConfigAsync()
    {
        var domain   = await _db.GetConfigAsync("AdDomain") ?? "";
        var ldapPath = await _db.GetConfigAsync("AdLdapPath") ?? "";
        var svcUser  = await _db.GetConfigAsync("AdServiceUser") ?? "";
        var svcPass  = await _db.GetConfigAsync("AdServicePassword") ?? "";
        return (domain, ldapPath, svcUser, svcPass);
    }

    // -------------------------------------------------------------------
    // Parse LDAP path - accepts many formats, returns (server, baseDn).
    // See SPEC.md §3.3 for the supported input shapes.
    // -------------------------------------------------------------------
    private (string server, string baseDn) ParseLdap(string ldapPath, string domain)
    {
        var clean = ldapPath
            .Replace("LDAP://", "", StringComparison.OrdinalIgnoreCase)
            .TrimStart('/');

        var slashIdx = clean.IndexOf('/');
        string server, baseDn;

        if (slashIdx > 0)
        {
            server = clean.Substring(0, slashIdx);
            baseDn = clean.Substring(slashIdx + 1);
        }
        else if (clean.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
        {
            server = domain;        // no server -> use domain
            baseDn = clean;
        }
        else
        {
            server = clean;
            baseDn = string.Join(",", domain.Split('.').Select(p => $"DC={p}"));
        }

        // Strip port if present
        var colonIdx = server.LastIndexOf(':');
        if (colonIdx > 0 && int.TryParse(server.Substring(colonIdx + 1), out _))
            server = server.Substring(0, colonIdx);

        return (server, baseDn);
    }

    private string BuildUPN(string username, string domain)
        => username.Contains('@') ? username : $"{username}@{domain}";

    // -------------------------------------------------------------------
    // AUTHENTICATE - bind-only, no LDAP search until AFTER bind succeeds.
    // SPEC.md §2.2 describes the algorithm.
    // -------------------------------------------------------------------
    public async Task<AdUser?> AuthenticateAsync(string username, string password)
    {
        var (domain, ldapPath, _, _) = await GetConfigAsync();
        if (string.IsNullOrWhiteSpace(domain)) return null;

        var (server, _) = ParseLdap(ldapPath, domain);
        var sam = username.Contains('@') ? username.Split('@')[0] : username;
        var upn = BuildUPN(sam, domain);

        _logger.LogInformation("AD auth attempt for {Sam} via server {Server}", sam, server);

        // Try UPN first (most reliable for M365/AD hybrid), then NetBIOS.
        var bindFormats = new[]
        {
            (upn,                                    "UPN"),
            ($"{domain.Split('.')[0]}\\{sam}",       "NetBIOS"),
        };

        foreach (var (bindAs, label) in bindFormats)
        {
            try
            {
                var ldapUrl = $"LDAP://{server}";
                using var entry = new System.DirectoryServices.DirectoryEntry(
                    ldapUrl, bindAs, password,
                    System.DirectoryServices.AuthenticationTypes.Secure);

                // Force authentication - if credentials are wrong, this throws.
                var _ = entry.NativeObject;

                _logger.LogInformation("AD auth SUCCESS for {Sam} via [{Label}]", sam, label);

                // Optional profile fetch - non-critical, login succeeds even if it fails.
                string fullName = sam, email = $"{sam}@{domain}", dept = "";
                try
                {
                    using var searcher = new System.DirectoryServices.DirectorySearcher(entry)
                    {
                        Filter      = $"(sAMAccountName={sam})",
                        SearchScope = System.DirectoryServices.SearchScope.Subtree,
                    };
                    searcher.PropertiesToLoad.AddRange(new[] { "displayName", "cn", "mail", "department" });
                    var r = searcher.FindOne();
                    if (r != null)
                    {
                        fullName = GetProp(r, "displayName") ?? GetProp(r, "cn") ?? sam;
                        email    = GetProp(r, "mail") ?? email;
                        dept     = GetProp(r, "department") ?? "";
                    }
                }
                catch (Exception searchEx)
                {
                    _logger.LogWarning("AD profile search failed (login still OK): {Msg}", searchEx.Message);
                }

                return new AdUser
                {
                    Username   = sam,
                    FullName   = string.IsNullOrWhiteSpace(fullName) ? sam : fullName,
                    Email      = email,
                    Department = dept,
                    IsActive   = true
                };
            }
            // Definitive wrong password - DO NOT try other bind formats.
            catch (System.Runtime.InteropServices.COMException ex)
                when (ex.ErrorCode == unchecked((int)0x8007052E)   // wrong password
                   || ex.ErrorCode == unchecked((int)0x80070005))  // access denied
            {
                _logger.LogWarning("AD auth WRONG PASSWORD for {Sam} [{Label}]", sam, label);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("AD auth [{Label}] failed ({Type}): {Msg}",
                    label, ex.GetType().Name, ex.Message);
                // Try next bind format
            }
        }

        _logger.LogError("All AD auth formats failed for {Sam} - check AD server connectivity", sam);
        return null;
    }

    // -------------------------------------------------------------------
    // GET ALL ACTIVE DIRECTORY USERS - browse (used by AD Browse + Mass Create)
    // SPEC.md §3.1 - uses a strategy ladder so a single AD setup quirk
    // doesn't kill the feature.
    // -------------------------------------------------------------------
    public async Task<List<AdUser>> GetAllActiveUsersAsync()
    {
        var (domain, ldapPath, svcUser, svcPass) = await GetConfigAsync();
        if (string.IsNullOrWhiteSpace(domain)) return new();

        var (server, baseDn) = ParseLdap(ldapPath, domain);

        _logger.LogInformation("AD GetAllUsers: server={S} baseDn={B} svcUser={U}",
            server, baseDn, svcUser);

        var strategies = new List<(string ldapUrl, string? bindUser, string? bindPass, string label)>();

        if (!string.IsNullOrEmpty(svcUser))
        {
            var upn      = BuildUPN(svcUser, domain);
            var netbios  = $"{domain.Split('.')[0]}\\{svcUser}";
            var ldapFull = $"LDAP://{server}/{baseDn}";
            var ldapRoot = $"LDAP://{server}";

            strategies.Add((ldapFull, upn,     svcPass, "FullPath+UPN"));
            strategies.Add((ldapRoot, upn,     svcPass, "Root+UPN"));
            strategies.Add((ldapFull, netbios, svcPass, "FullPath+NetBIOS"));
            strategies.Add((ldapRoot, netbios, svcPass, "Root+NetBIOS"));
            strategies.Add(($"LDAP://{domain}", upn, svcPass, "Domain+UPN"));
        }

        strategies.Add(($"LDAP://{server}/{baseDn}", null, null, "Anonymous"));

        foreach (var (ldapUrl, bindUser, bindPass, label) in strategies)
        {
            try
            {
                _logger.LogInformation("AD trying strategy: {Label} url={Url}", label, ldapUrl);

                System.DirectoryServices.DirectoryEntry root;
                if (bindUser != null)
                    root = new System.DirectoryServices.DirectoryEntry(ldapUrl, bindUser, bindPass,
                        System.DirectoryServices.AuthenticationTypes.Secure);
                else
                    root = new System.DirectoryServices.DirectoryEntry(ldapUrl);

                using (root)
                {
                    var _ = root.NativeObject; // force connection

                    using var searcher = new System.DirectoryServices.DirectorySearcher(root)
                    {
                        // userAccountControl 512 / 514 / 66048 / 66050 = enabled accounts
                        Filter      = "(&(objectCategory=person)(objectClass=user)" +
                                      "(|(userAccountControl=512)(userAccountControl=514)" +
                                      "(userAccountControl=66050)(userAccountControl=66048)))",
                        SearchScope = System.DirectoryServices.SearchScope.Subtree,
                        PageSize    = 500
                    };
                    searcher.PropertiesToLoad.AddRange(new[]
                    {
                        "sAMAccountName", "displayName", "cn", "givenName", "sn",
                        "mail", "department", "userAccountControl"
                    });

                    var results = searcher.FindAll();
                    _logger.LogInformation("Strategy [{Label}] returned {Count} results", label, results.Count);

                    if (results.Count == 0) { results.Dispose(); continue; }

                    var users = new List<AdUser>();
                    foreach (System.DirectoryServices.SearchResult r in results)
                    {
                        try
                        {
                            var sam = GetProp(r, "sAMAccountName");
                            if (string.IsNullOrEmpty(sam) || sam.EndsWith("$")) continue; // skip machine accounts

                            int.TryParse(GetProp(r, "userAccountControl") ?? "0", out var uac);
                            var isEnabled = uac == 512 || uac == 66048;

                            var fn = GetProp(r, "displayName")
                                  ?? GetProp(r, "cn")
                                  ?? $"{GetProp(r, "givenName")} {GetProp(r, "sn")}".Trim();

                            users.Add(new AdUser
                            {
                                Username   = sam,
                                FullName   = string.IsNullOrWhiteSpace(fn) ? sam : fn,
                                Email      = GetProp(r, "mail") ?? "",
                                Department = GetProp(r, "department") ?? "",
                                IsActive   = isEnabled
                            });
                        }
                        catch { }
                    }
                    results.Dispose();

                    _logger.LogInformation("AD returning {Count} users via [{Label}]", users.Count, label);
                    return users.OrderBy(u => u.FullName).ToList();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Strategy [{Label}] failed: {Msg}", label, ex.Message);
            }
        }

        _logger.LogError("All AD strategies failed for GetAllUsers");
        return new();
    }

    // -------------------------------------------------------------------
    // GET SINGLE USER (admin lookup; not used for login)
    // -------------------------------------------------------------------
    public async Task<AdUser?> GetUserInfoAsync(string username)
    {
        var (domain, ldapPath, svcUser, svcPass) = await GetConfigAsync();
        if (string.IsNullOrWhiteSpace(domain)) return null;

        var (server, baseDn) = ParseLdap(ldapPath, domain);
        var ldapUrl = $"LDAP://{server}/{baseDn}";
        var upn     = string.IsNullOrEmpty(svcUser) ? null : BuildUPN(svcUser, domain);

        try
        {
            var root = upn != null
                ? new System.DirectoryServices.DirectoryEntry(ldapUrl, upn, svcPass)
                : new System.DirectoryServices.DirectoryEntry(ldapUrl);

            using (root)
            using (var searcher = new System.DirectoryServices.DirectorySearcher(root)
            {
                Filter = $"(&(objectCategory=person)(objectClass=user)(sAMAccountName={username}))"
            })
            {
                searcher.PropertiesToLoad.AddRange(new[] { "displayName", "cn", "mail", "department" });
                var r = searcher.FindOne();
                if (r == null) return null;
                return new AdUser
                {
                    Username   = username,
                    FullName   = GetProp(r, "displayName") ?? GetProp(r, "cn") ?? username,
                    Email      = GetProp(r, "mail") ?? $"{username}@{domain}",
                    Department = GetProp(r, "department") ?? "",
                    IsActive   = true
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetUserInfo failed for {User}", username);
            return null;
        }
    }

    // -------------------------------------------------------------------
    // TEST CONNECTION - admin button on AD Management page.
    // -------------------------------------------------------------------
    public async Task<(bool ok, string message)> TestConnectionAsync()
    {
        var (domain, ldapPath, svcUser, svcPass) = await GetConfigAsync();
        if (string.IsNullOrWhiteSpace(domain))
            return (false, "AD Domain is not configured.");

        var (server, baseDn) = ParseLdap(ldapPath, domain);
        var upn = string.IsNullOrEmpty(svcUser) ? null : BuildUPN(svcUser, domain);

        var urlsToTry = new[]
        {
            $"LDAP://{server}",            // server root - most reliable, avoids referrals
            $"LDAP://{domain}",            // domain root
            $"LDAP://{server}/{baseDn}",   // full path fallback
        };

        foreach (var url in urlsToTry)
        {
            try
            {
                var entry = upn != null
                    ? new System.DirectoryServices.DirectoryEntry(url, upn, svcPass,
                        System.DirectoryServices.AuthenticationTypes.Secure)
                    : new System.DirectoryServices.DirectoryEntry(url);

                using (entry)
                {
                    var _ = entry.NativeObject; // force authentication

                    using var searcher = new System.DirectoryServices.DirectorySearcher(entry)
                    {
                        Filter      = "(objectClass=user)",
                        SearchScope = System.DirectoryServices.SearchScope.Subtree,
                        SizeLimit   = 1
                    };
                    searcher.PropertiesToLoad.Add("sAMAccountName");
                    var result = searcher.FindOne();

                    return (true, $"Connected to {url} as {upn ?? "anonymous"} - " +
                        $"BaseDN: {baseDn} - {(result != null ? "User search OK" : "Connected (0 users visible)")}");
                }
            }
            catch (System.Runtime.InteropServices.COMException ex)
                when (ex.ErrorCode == unchecked((int)0x8007052E))
            {
                return (false, $"Invalid credentials for '{upn}'. Check username and password.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Test connection [{Url}] failed: {Msg}", url, ex.Message);
            }
        }

        return (false, $"Could not connect to AD. Server: {server}, BaseDN: {baseDn}, User: {upn ?? "none"}. " +
            "Check that the server IP is reachable and credentials are correct.");
    }

    private static string? GetProp(System.DirectoryServices.SearchResult r, string name)
    {
        try
        {
            var c = r.Properties[name];
            return c?.Count > 0 ? c[0]?.ToString() : null;
        }
        catch { return null; }
    }
}
