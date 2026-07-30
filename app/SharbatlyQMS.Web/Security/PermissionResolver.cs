using Dapper;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models.Security;

namespace SharbatlyQMS.Web.Security;

/// <summary>One requirement instance per attribute; see PermissionAttributes.</summary>
public sealed class PermissionRequirement : IAuthorizationRequirement
{
    public PermissionRequirement(string code, AccessLevel minimum, bool isScreen)
    {
        Code     = code;
        Minimum  = minimum;
        IsScreen = isScreen;
    }

    public string      Code     { get; }
    public AccessLevel Minimum  { get; }
    public bool        IsScreen { get; }
}

public sealed record RoleInfo(
    string RoleCode, string DisplayName, string? LegacyName, int Rank,
    bool IsBuiltIn, bool IsSuper, bool IsPlantScoped, bool IsActive, string? Description);

public sealed record ScreenInfo(
    string ScreenKey, string DisplayName, string GroupName, int SortOrder, bool SupportsAccessLevel);

public sealed record PermissionInfo(
    string Code, string ScreenKey, string Kind, string DisplayName, int SortOrder,
    string? SeedRoles, bool IsObsolete);

public interface IPermissionResolver
{
    /// <summary>Grant level a role holds on a code, before the screen-level rule.</summary>
    AccessLevel RawLevel(string? roleCode, string permissionCode);

    /// <summary>The real decision, including the administrator floor and the
    /// screen Read/Edit rule.</summary>
    bool RoleHas(string? roleCode, string permissionCode, AccessLevel minimum, bool isScreen);

    string? RoleCodeOf(int userId);

    /// <summary>Shorthand for "this role can manage security", used by the
    /// last-administrator guards on the Users screen.</summary>
    bool RoleHasSecurityAdmin(string? roleCode);

    IReadOnlyList<RoleInfo>       Roles       { get; }
    IReadOnlyList<ScreenInfo>     Screens     { get; }
    IReadOnlyList<PermissionInfo> Permissions { get; }
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, AccessLevel>> Grants { get; }

    /// <summary>Reload from the database and swap the snapshot. Called at
    /// startup and after every save on the Security screen.</summary>
    Task RefreshAsync(CancellationToken ct = default);

    /// <summary>First-use guard, so a request never authorises against an
    /// unloaded snapshot even if the startup filter was bypassed.</summary>
    Task EnsureLoadedAsync(CancellationToken ct = default);

    DateTimeOffset? LoadedAtUtc { get; }
}

/// <summary>
/// Singleton holding the whole authorisation model as immutable dictionaries
/// behind one <c>volatile</c> reference, swapped in a single assignment. Readers
/// are lock-free, which matters: this is consulted on every request and roughly
/// fifty times per page render.
///
/// The snapshot is keyed on <b>user id and role code</b>, never on the role
/// claim in the cookie. Claims live in a 30-day cookie refreshed at most once a
/// minute; resolving from the database-backed snapshot instead means a deleted
/// role, a renamed role, a stale claim and an impersonation are all irrelevant
/// to the access decision, and a permission change takes effect on the very next
/// request for everyone.
/// </summary>
public sealed class PermissionResolver : IPermissionResolver
{
    private sealed class Snapshot
    {
        public required IReadOnlyDictionary<int, string> UserRole { get; init; }
        public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, AccessLevel>> Grants { get; init; }
        public required IReadOnlyDictionary<string, ScreenInfo> ScreenByKey { get; init; }
        public required IReadOnlyDictionary<string, PermissionInfo> PermByCode { get; init; }
        public required IReadOnlyList<RoleInfo> Roles { get; init; }
        public required IReadOnlyList<ScreenInfo> Screens { get; init; }
        public required IReadOnlyList<PermissionInfo> Permissions { get; init; }
        public DateTimeOffset? LoadedAtUtc { get; init; }
    }

    private static readonly Snapshot Empty = new()
    {
        UserRole    = new Dictionary<int, string>(),
        Grants      = new Dictionary<string, IReadOnlyDictionary<string, AccessLevel>>(StringComparer.OrdinalIgnoreCase),
        ScreenByKey = new Dictionary<string, ScreenInfo>(StringComparer.OrdinalIgnoreCase),
        PermByCode  = new Dictionary<string, PermissionInfo>(StringComparer.OrdinalIgnoreCase),
        Roles       = Array.Empty<RoleInfo>(),
        Screens     = Array.Empty<ScreenInfo>(),
        Permissions = Array.Empty<PermissionInfo>(),
    };

    private readonly string _cs;
    private readonly ILogger<PermissionResolver> _log;
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    // volatile: the swap must be visible to reader threads immediately, without
    // taking a lock on a path this hot.
    private volatile Snapshot _snap = Empty;
    private volatile bool _loaded;

    public PermissionResolver(IConfiguration cfg, ILogger<PermissionResolver> log)
    {
        _cs = cfg.GetConnectionString("Default")
              ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
        _log = log;
    }

    public IReadOnlyList<RoleInfo>       Roles       => _snap.Roles;
    public IReadOnlyList<ScreenInfo>     Screens     => _snap.Screens;
    public IReadOnlyList<PermissionInfo> Permissions => _snap.Permissions;
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, AccessLevel>> Grants => _snap.Grants;
    public DateTimeOffset? LoadedAtUtc => _snap.LoadedAtUtc;

    public string? RoleCodeOf(int userId) =>
        _snap.UserRole.TryGetValue(userId, out var r) ? r : null;

    public bool RoleHasSecurityAdmin(string? roleCode) =>
        RoleHas(roleCode, Perm.Admin.SecurityEdit, AccessLevel.Edit, isScreen: false);

    public AccessLevel RawLevel(string? roleCode, string permissionCode)
    {
        if (string.IsNullOrEmpty(roleCode)) return AccessLevel.None;
        return _snap.Grants.TryGetValue(roleCode, out var byPerm)
               && byPerm.TryGetValue(permissionCode, out var lvl)
            ? lvl
            : AccessLevel.None;
    }

    public bool RoleHas(string? roleCode, string permissionCode, AccessLevel minimum, bool isScreen)
    {
        if (string.IsNullOrEmpty(roleCode)) return false;

        // ---- The administrator floor. Two codes the built-in administrator can
        //      never be denied, checked before anything reads the grant table so
        //      that no edit, however bad, can make the Security screen
        //      unreachable. Deliberately a string comparison against a constant:
        //      a database flag can be flipped by a bug, a constant cannot.
        if (string.Equals(roleCode, RoleCodes.Admin, StringComparison.OrdinalIgnoreCase)
            && Array.Exists(Perm.AdminFloor, c => string.Equals(c, permissionCode, StringComparison.OrdinalIgnoreCase)))
            return true;

        // An unknown code denies. It never throws: a permission removed by a
        // rollback must lock the button, not break the page.
        if (!_snap.PermByCode.ContainsKey(permissionCode)) return false;

        if (RawLevel(roleCode, permissionCode) < minimum) return false;

        // ---- The two-switch rule. On a levelled screen, performing an action
        //      needs the SCREEN at Edit as well as the action itself. Read-only
        //      actions (downloading a PDF, exporting) ask for Read instead, or
        //      switching a screen to read-only would take its reports away too.
        if (!isScreen)
        {
            var owner = Models.Security.Screens.OwnerOf(permissionCode);
            if (_snap.ScreenByKey.TryGetValue(owner, out var screen) && screen.SupportsAccessLevel)
            {
                var needed = minimum == AccessLevel.Read ? AccessLevel.Read : AccessLevel.Edit;
                if (RawLevel(roleCode, owner) < needed) return false;
            }
        }

        return true;
    }

    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_loaded) return;
        await _loadGate.WaitAsync(ct);
        try
        {
            if (_loaded) return;
            // First load is allowed to throw. Serving requests against an empty
            // snapshot would either 403 the whole application or, worse, be
            // mistaken for "no permissions configured" and opened up.
            await LoadAsync(ct, isFirstLoad: true);
        }
        finally { _loadGate.Release(); }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _loadGate.WaitAsync(ct);
        try { await LoadAsync(ct, isFirstLoad: !_loaded); }
        finally { _loadGate.Release(); }
    }

    private async Task LoadAsync(CancellationToken ct, bool isFirstLoad)
    {
        try
        {
            using var c = new SqlConnection(_cs);
            using var grid = await c.QueryMultipleAsync(new CommandDefinition(@"
                SELECT role_code RoleCode, display_name DisplayName, legacy_name LegacyName,
                       rank Rank, is_builtin IsBuiltIn, is_super IsSuper,
                       is_plant_scoped IsPlantScoped, is_active IsActive, description Description
                FROM   qms_role
                ORDER  BY rank DESC, role_code;

                SELECT screen_key ScreenKey, display_name DisplayName, group_name GroupName,
                       sort_order SortOrder, supports_access_level SupportsAccessLevel
                FROM   qms_screen
                ORDER  BY sort_order, screen_key;

                SELECT permission_code Code, screen_key ScreenKey, kind Kind,
                       display_name DisplayName, sort_order SortOrder,
                       seed_roles SeedRoles, is_obsolete IsObsolete
                FROM   qms_permission
                ORDER  BY screen_key, sort_order, permission_code;

                SELECT role_code RoleCode, permission_code PermissionCode, access_level AccessLevel
                FROM   qms_role_permission;

                SELECT UserId, RoleCode FROM qms.AppUser;", cancellationToken: ct));

            var roles   = (await grid.ReadAsync<RoleInfo>()).ToList();
            var screens = (await grid.ReadAsync<ScreenInfo>()).ToList();
            var perms   = (await grid.ReadAsync<PermissionInfo>()).ToList();
            var grantRows = (await grid.ReadAsync<(string RoleCode, string PermissionCode, byte AccessLevel)>()).ToList();
            var userRows  = (await grid.ReadAsync<(int UserId, string RoleCode)>()).ToList();

            // ---- Zero rows is an ERROR, not an empty configuration.
            //      portal.* carries row-level security, and querying an RLS table
            //      without SESSION_CONTEXT returns zero rows rather than failing.
            //      Accepting an empty result would either lock every user out of
            //      everything or, on a fail-open handler, open everything.
            if (roles.Count == 0 || screens.Count == 0 || userRows.Count == 0)
                throw new InvalidOperationException(
                    $"Security tables returned an implausible result (roles={roles.Count}, " +
                    $"screens={screens.Count}, users={userRows.Count}). Refusing to install this snapshot.");

            var grants = new Dictionary<string, Dictionary<string, AccessLevel>>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in roles) grants[r.RoleCode] = new Dictionary<string, AccessLevel>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in grantRows)
            {
                if (!grants.TryGetValue(g.RoleCode, out var byPerm)) continue;
                byPerm[g.PermissionCode] = AccessLevels.FromDb(g.AccessLevel);
            }

            _snap = new Snapshot
            {
                UserRole = userRows.GroupBy(u => u.UserId)
                                   .ToDictionary(g => g.Key, g => g.First().RoleCode),
                Grants = grants.ToDictionary(
                    kv => kv.Key,
                    kv => (IReadOnlyDictionary<string, AccessLevel>)kv.Value,
                    StringComparer.OrdinalIgnoreCase),
                ScreenByKey = screens.ToDictionary(s => s.ScreenKey, s => s, StringComparer.OrdinalIgnoreCase),
                PermByCode  = perms.ToDictionary(p => p.Code, p => p, StringComparer.OrdinalIgnoreCase),
                Roles = roles, Screens = screens, Permissions = perms,
                LoadedAtUtc = DateTimeOffset.UtcNow,
            };
            _loaded = true;

            _log.LogInformation(
                "Permissions loaded: {Roles} roles, {Screens} screens, {Perms} permissions, {Grants} grants, {Users} users.",
                roles.Count, screens.Count, perms.Count, grantRows.Count, userRows.Count);
        }
        catch (Exception ex) when (!isFirstLoad)
        {
            // A refresh failure keeps the previous snapshot. Slightly stale
            // permissions beat an application that stops authorising.
            _log.LogError(ex, "Permission refresh failed; keeping the snapshot loaded at {LoadedAt}.", _snap.LoadedAtUtc);
        }
    }
}
