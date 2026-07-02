using System.Collections.Concurrent;
using System.DirectoryServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Caching.Memory;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// LDAP bind-only authentication. Adapted from the in-house
/// user-management-pack (reference/AdService.cs). Speaks
/// System.DirectoryServices and is therefore Windows-only -- the host
/// app already runs on Windows / IIS so this is intentional.
/// </summary>
[SupportedOSPlatform("windows")]
public class AdService : IAdService
{
    private static readonly TimeSpan ListUsersTtl = TimeSpan.FromMinutes(30);

    // Caches the URL + bind shape that last succeeded for a given AD config,
    // keyed by Domain|LdapPath|ServiceUser. The strategy ladder has up to
    // six shapes; each failed attempt eats a real TCP/LDAP timeout. Once we
    // know which one works we skip the dud attempts. Cleared automatically
    // when the strategy itself fails (e.g. config changed).
    private static readonly ConcurrentDictionary<string, (string Url, string? BindUser, string? BindPass)> _workingStrategy = new();

    private readonly ILogger<AdService> _log;
    private readonly IMemoryCache _cache;

    public AdService(ILogger<AdService> log, IMemoryCache cache)
    {
        _log = log;
        _cache = cache;
    }

    public Task<(bool ok, AdUserInfo? info, string? error)> AuthenticateAsync(
        string username, string password, AdConfig cfg)
    {
        if (!cfg.IsConfigured)
            return Task.FromResult<(bool, AdUserInfo?, string?)>((false, null, "AD not configured"));

        // System.DirectoryServices has no async API and each bind attempt blocks
        // for the full TCP/LDAP timeout when the DC is slow/unreachable. Offload
        // the blocking bind ladder to a thread-pool thread so it doesn't pin the
        // request thread (a morning login burst against a slow DC could otherwise
        // starve the pool). Bind logic itself is unchanged.
        return Task.Run(() => AuthenticateCore(username, password, cfg));
    }

    private (bool ok, AdUserInfo? info, string? error) AuthenticateCore(
        string username, string password, AdConfig cfg)
    {
        var (server, _) = ParseLdap(cfg.LdapPath, cfg.Domain);
        var sam = username.Contains('@') ? username.Split('@')[0] : username;
        var upn = sam.Contains('@') ? sam : $"{sam}@{cfg.Domain}";
        var netbios = $"{cfg.Domain.Split('.')[0]}\\{sam}";

        _log.LogInformation("AD auth attempt for {Sam} via server {Server}", sam, server);

        // Try UPN first (most reliable for hybrid M365 setups), then NetBIOS.
        foreach (var (bindAs, label) in new[] { (upn, "UPN"), (netbios, "NetBIOS") })
        {
            try
            {
                var url = $"LDAP://{server}";
                using var entry = new DirectoryEntry(url, bindAs, password, AuthenticationTypes.Secure);
                _ = entry.NativeObject; // forces the bind; throws on bad creds

                // Optional profile enrichment. Login is already considered OK.
                string fullName = sam;
                string? email = null, dept = null;
                try
                {
                    using var searcher = new DirectorySearcher(entry)
                    {
                        Filter      = $"(sAMAccountName={LdapEncode(sam)})",
                        SearchScope = SearchScope.Subtree
                    };
                    searcher.PropertiesToLoad.AddRange(new[] { "displayName", "cn", "mail", "department" });
                    var r = searcher.FindOne();
                    if (r != null)
                    {
                        fullName = GetProp(r, "displayName") ?? GetProp(r, "cn") ?? sam;
                        email    = GetProp(r, "mail");
                        dept     = GetProp(r, "department");
                    }
                }
                catch (Exception searchEx)
                {
                    _log.LogWarning("AD profile search failed (login still OK): {Msg}", searchEx.Message);
                }

                _log.LogInformation("AD auth SUCCESS for {Sam} via [{Label}]", sam, label);
                return (true,
                    new AdUserInfo
                    {
                        Username   = sam,
                        FullName   = string.IsNullOrWhiteSpace(fullName) ? sam : fullName,
                        Email      = email,
                        Department = dept
                    }, null);
            }
            catch (COMException ex)
                when (ex.ErrorCode == unchecked((int)0x8007052E)   // wrong password
                   || ex.ErrorCode == unchecked((int)0x80070005))  // access denied
            {
                _log.LogWarning("AD auth WRONG PASSWORD for {Sam} [{Label}]", sam, label);
                return (false, null, "Invalid credentials");
            }
            catch (Exception ex)
            {
                _log.LogWarning("AD auth [{Label}] failed ({Type}): {Msg}",
                    label, ex.GetType().Name, ex.Message);
                // Try next bind format
            }
        }

        return (false, null, "AD bind failed");
    }

    public Task<(bool ok, string message)> TestConnectionAsync(AdConfig cfg)
    {
        if (!cfg.IsConfigured)
            return Task.FromResult((false, "AD Domain is not configured."));

        var (server, baseDn) = ParseLdap(cfg.LdapPath, cfg.Domain);
        string? bindUser = null;
        if (!string.IsNullOrWhiteSpace(cfg.ServiceUser))
            bindUser = cfg.ServiceUser.Contains('@')
                ? cfg.ServiceUser
                : $"{cfg.ServiceUser}@{cfg.Domain}";

        var urls = new[]
        {
            $"LDAP://{server}",
            $"LDAP://{cfg.Domain}",
            $"LDAP://{server}/{baseDn}"
        };

        foreach (var url in urls)
        {
            try
            {
                var entry = bindUser != null
                    ? new DirectoryEntry(url, bindUser, cfg.ServicePassword, AuthenticationTypes.Secure)
                    : new DirectoryEntry(url);
                using (entry)
                {
                    _ = entry.NativeObject;
                    using var searcher = new DirectorySearcher(entry)
                    {
                        Filter      = "(objectClass=user)",
                        SearchScope = SearchScope.Subtree,
                        SizeLimit   = 1
                    };
                    searcher.PropertiesToLoad.Add("sAMAccountName");
                    var r = searcher.FindOne();
                    return Task.FromResult((true,
                        $"Connected to {url} as {bindUser ?? "anonymous"}. " +
                        $"BaseDN: {baseDn}. {(r != null ? "User search OK." : "Connected (0 users visible).")}"));
                }
            }
            catch (COMException ex) when (ex.ErrorCode == unchecked((int)0x8007052E))
            {
                return Task.FromResult((false, $"Invalid credentials for '{bindUser}'. Check service account and password."));
            }
            catch (Exception ex)
            {
                _log.LogWarning("Test connection [{Url}] failed: {Msg}", url, ex.Message);
            }
        }

        return Task.FromResult((false,
            $"Could not connect to AD. Server: {server}, BaseDN: {baseDn}, User: {bindUser ?? "none"}. " +
            "Check the server is reachable and the credentials are correct."));
    }

    public async Task<List<AdUserInfo>> ListUsersAsync(AdConfig cfg, string? filter, int max = 200)
    {
        if (!cfg.IsConfigured) return new List<AdUserInfo>();

        // Cache the unfiltered fetch -- filtering is cheap in memory and lets
        // every keystroke in the picker reuse the same 5-min snapshot.
        var cacheKey = $"ad:users:{cfg.Domain}|{cfg.LdapPath}|{cfg.ServiceUser}";
        var all = await _cache.GetOrCreateAsync(cacheKey, entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = ListUsersTtl;
            return Task.FromResult(FetchAllUsers(cfg));
        }) ?? new List<AdUserInfo>();

        IEnumerable<AdUserInfo> q = all;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            var f = filter.Trim();
            q = q.Where(u =>
                u.Username.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                u.FullName.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                (u.Email?.Contains(f, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        return q.OrderBy(u => u.FullName).Take(Math.Max(1, max)).ToList();
    }

    public Task<AdUserInfo?> GetUserInfoAsync(string username, AdConfig cfg)
    {
        if (!cfg.IsConfigured || string.IsNullOrWhiteSpace(username))
            return Task.FromResult<AdUserInfo?>(null);

        // Re-use the same strategy ladder as ListUsers. Previously this
        // method tried only one shape (FullPath+UPN) and threw "not found"
        // when the working bind was a different shape -- the bug the user
        // reported when adding a picked AD account.
        AdUserInfo? found = null;
        RunWithLadder(cfg, "GetUserInfo", (root, label) =>
        {
            using var searcher = new DirectorySearcher(root)
            {
                Filter      = $"(&(objectCategory=person)(objectClass=user)(sAMAccountName={LdapEncode(username)}))",
                SearchScope = SearchScope.Subtree
            };
            searcher.PropertiesToLoad.AddRange(new[] { "displayName", "cn", "mail", "department", "sAMAccountName" });
            var r = searcher.FindOne();
            if (r == null) return false;       // nothing under this strategy -- try the next shape
            found = new AdUserInfo
            {
                Username   = GetProp(r, "sAMAccountName") ?? username,
                FullName   = GetProp(r, "displayName") ?? GetProp(r, "cn") ?? username,
                Email      = GetProp(r, "mail"),
                Department = GetProp(r, "department")
            };
            return true;
        });
        return Task.FromResult(found);
    }

    // Bulk AD fetch via the pack's strategy ladder. The successful shape
    // is cached in _workingStrategy so subsequent fetches (or
    // GetUserInfo calls) skip the dud attempts.
    private List<AdUserInfo> FetchAllUsers(AdConfig cfg)
    {
        var list = new List<AdUserInfo>();
        RunWithLadder(cfg, "ListUsers", (root, label) =>
        {
            using var searcher = new DirectorySearcher(root)
            {
                // 512 / 66048 = normal enabled; 514 / 66050 = disabled (we
                // include disabled at fetch-time but they won't bind anyway).
                Filter      = "(&(objectCategory=person)(objectClass=user)" +
                              "(|(userAccountControl=512)(userAccountControl=66048)))",
                SearchScope = SearchScope.Subtree,
                PageSize    = 500
            };
            searcher.PropertiesToLoad.AddRange(new[]
            {
                "sAMAccountName", "displayName", "cn", "givenName", "sn",
                "mail", "department"
            });
            var results = searcher.FindAll();
            if (results.Count == 0) { results.Dispose(); return false; }

            list.Capacity = Math.Max(list.Capacity, results.Count);
            foreach (SearchResult r in results)
            {
                var sam = GetProp(r, "sAMAccountName");
                if (string.IsNullOrWhiteSpace(sam) || sam.EndsWith("$")) continue; // skip machine accounts
                var fn = GetProp(r, "displayName")
                      ?? GetProp(r, "cn")
                      ?? $"{GetProp(r, "givenName")} {GetProp(r, "sn")}".Trim();
                list.Add(new AdUserInfo
                {
                    Username   = sam,
                    FullName   = string.IsNullOrWhiteSpace(fn) ? sam : fn,
                    Email      = GetProp(r, "mail"),
                    Department = GetProp(r, "department")
                });
            }
            results.Dispose();
            _log.LogInformation("AD ListUsers OK via [{Label}] -- {Count} users", label, list.Count);
            return true;
        });
        return list;
    }

    // ---- Shared LDAP strategy ladder ----
    //
    // Every AD-touching method binds against one of a few URL + credential
    // shapes; only one works for a given DC and we don't know which
    // ahead of time. After the first success we remember the shape in
    // _workingStrategy and try it first next time so cold-and-warm calls
    // skip the dud attempts (each dud is a real TCP/LDAP timeout, usually
    // several seconds).
    //
    // The action returns true if it succeeded; false means the bind
    // worked but the query produced no useful result (e.g. a baseDn that
    // happens to be valid but doesn't see users) -- we move on to the
    // next strategy. Exceptions are caught and treated the same way, and
    // a cached strategy that throws is evicted so the next call rebuilds
    // from the full ladder.
    private void RunWithLadder(AdConfig cfg, string opName,
        Func<DirectoryEntry, string, bool> action)
    {
        var (server, baseDn) = ParseLdap(cfg.LdapPath, cfg.Domain);
        var key = $"{cfg.Domain}|{cfg.LdapPath}|{cfg.ServiceUser}";
        var ladder = BuildLadder(cfg, server, baseDn).ToList();
        if (_workingStrategy.TryGetValue(key, out var saved))
            ladder.Insert(0, (saved.Url, saved.BindUser, saved.BindPass, "cached"));

        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (url, bindUser, bindPass, label) in ladder)
        {
            var id = $"{url}|{bindUser}";
            if (!tried.Add(id)) continue;          // skip the cached strategy when we hit it again in the ladder
            try
            {
                DirectoryEntry root = bindUser != null
                    ? new DirectoryEntry(url, bindUser, bindPass, AuthenticationTypes.Secure)
                    : new DirectoryEntry(url);
                using (root)
                {
                    _ = root.NativeObject;        // forces the bind; throws on bad creds / unreachable
                    if (action(root, label))
                    {
                        _workingStrategy[key] = (url, bindUser, bindPass);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning("AD {Op} [{Label}] failed: {Msg}", opName, label, ex.Message);
                if (label == "cached")
                    _workingStrategy.TryRemove(key, out _);     // stale -- next call rebuilds from ladder
            }
        }
        _log.LogWarning("AD {Op}: no strategy succeeded", opName);
    }

    private static IEnumerable<(string url, string? bindUser, string? bindPass, string label)>
        BuildLadder(AdConfig cfg, string server, string baseDn)
    {
        if (!string.IsNullOrWhiteSpace(cfg.ServiceUser))
        {
            var upn     = cfg.ServiceUser.Contains('@') ? cfg.ServiceUser : $"{cfg.ServiceUser}@{cfg.Domain}";
            var netbios = $"{cfg.Domain.Split('.')[0]}\\{cfg.ServiceUser}";
            yield return ($"LDAP://{server}/{baseDn}", upn,     cfg.ServicePassword, "FullPath+UPN");
            yield return ($"LDAP://{server}",          upn,     cfg.ServicePassword, "Root+UPN");
            yield return ($"LDAP://{server}/{baseDn}", netbios, cfg.ServicePassword, "FullPath+NetBIOS");
            yield return ($"LDAP://{server}",          netbios, cfg.ServicePassword, "Root+NetBIOS");
            yield return ($"LDAP://{cfg.Domain}",       upn,     cfg.ServicePassword, "Domain+UPN");
        }
        yield return ($"LDAP://{server}/{baseDn}", null, null, "Anonymous");
    }

    // ---- Helpers ----

    private static (string server, string baseDn) ParseLdap(string ldapPath, string domain)
    {
        var clean = (ldapPath ?? "")
            .Replace("LDAP://", "", StringComparison.OrdinalIgnoreCase)
            .TrimStart('/');

        var slashIdx = clean.IndexOf('/');
        string server, baseDn;
        if (slashIdx > 0)
        {
            server = clean[..slashIdx];
            baseDn = clean[(slashIdx + 1)..];
        }
        else if (clean.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
        {
            server = domain;
            baseDn = clean;
        }
        else
        {
            server = string.IsNullOrWhiteSpace(clean) ? domain : clean;
            baseDn = string.Join(",", domain.Split('.').Select(p => $"DC={p}"));
        }

        var colonIdx = server.LastIndexOf(':');
        if (colonIdx > 0 && int.TryParse(server[(colonIdx + 1)..], out _))
            server = server[..colonIdx];

        return (server, baseDn);
    }

    private static string? GetProp(SearchResult r, string name)
    {
        try
        {
            var c = r.Properties[name];
            return c?.Count > 0 ? c[0]?.ToString() : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// RFC 4515 §3 escape for LDAP search filter values. A user-supplied
    /// sAMAccountName must be escaped before interpolation into a filter
    /// string, otherwise a crafted value like <c>*)(uid=*</c> can change
    /// the query semantics. Five characters need escaping: <c>\ * ( )</c>
    /// and NUL. Each is replaced with its three-character \xx hex form.
    /// </summary>
    private static string LdapEncode(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new System.Text.StringBuilder(s.Length + 8);
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\5c"); break;
                case '*':  sb.Append("\\2a"); break;
                case '(':  sb.Append("\\28"); break;
                case ')':  sb.Append("\\29"); break;
                case '\0': sb.Append("\\00"); break;
                default:   sb.Append(ch);     break;
            }
        }
        return sb.ToString();
    }
}
