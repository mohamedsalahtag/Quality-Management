using Dapper;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models.Security;

namespace SharbatlyQMS.Web.Security;

/// <summary>One permission as declared by an attribute on a controller action.</summary>
public sealed record DiscoveredPermission(
    string Code, string ScreenKey, string Kind, string DisplayName, int SortOrder, Seed SeedFor);

/// <summary>
/// Discovers the permission catalogue by reflecting over every controller
/// action, and reconciles it with <c>qms_permission</c>.
///
/// This is what makes the user's requirement — "any future addition must also be
/// created as a permission" — hold without anybody remembering to do anything:
/// add a button, give its action an attribute, and the row appears the first
/// time the application starts.
///
/// Discovery is pure reflection and cannot fail. Validation throws, because an
/// action with no decision attribute is a developer mistake that a unit test
/// catches long before a deployment. Reconciliation never throws: a database
/// blip must not stop the application from starting.
/// </summary>
public sealed class PermissionCatalog
{
    private readonly string _cs;
    private readonly ILogger<PermissionCatalog> _log;
    private readonly IActionDescriptorCollectionProvider _actions;
    private readonly IPermissionResolver _resolver;

    public PermissionCatalog(IConfiguration cfg, ILogger<PermissionCatalog> log,
        IActionDescriptorCollectionProvider actions, IPermissionResolver resolver)
    {
        _cs = cfg.GetConnectionString("Default")
              ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
        _log = log;
        _actions = actions;
        _resolver = resolver;
    }

    /// <summary>Every action's declared permission, deduplicated by code.</summary>
    public IReadOnlyList<DiscoveredPermission> Discover()
    {
        var byCode = new Dictionary<string, DiscoveredPermission>(StringComparer.OrdinalIgnoreCase);
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();

        foreach (var d in _actions.ActionDescriptors.Items.OfType<ControllerActionDescriptor>())
        {
            var where = $"{d.ControllerName}.{d.ActionName}";
            var attrs = d.MethodInfo.GetCustomAttributes(inherit: true).OfType<IPermissionDecision>().ToList();
            attrs.AddRange(d.ControllerTypeInfo.GetCustomAttributes(inherit: true)
                            .OfType<IPermissionDecision>()
                            .Where(_ => attrs.Count == 0));   // class-level only when the method declares nothing

            var anonymous = d.EndpointMetadata.OfType<Microsoft.AspNetCore.Authorization.IAllowAnonymous>().Any();
            if (anonymous) continue;

            if (attrs.Count == 0)
            {
                problems.Add($"{where}: no [RequirePermission] / [RequireScreen] / [QmsAlwaysAllowed].");
                continue;
            }
            if (attrs.Count > 1)
            {
                problems.Add($"{where}: {attrs.Count} permission attributes; exactly one is allowed.");
                continue;
            }

            switch (attrs[0])
            {
                case QmsAlwaysAllowedAttribute:
                    continue;

                case RequireScreenAttribute s:
                {
                    if (!Array.Exists(Models.Security.Screens.All,
                            k => string.Equals(k, s.ScreenKey, StringComparison.OrdinalIgnoreCase)))
                    {
                        problems.Add($"{where}: unknown screen key '{s.ScreenKey}'.");
                        continue;
                    }
                    if (s.Inherited) continue;   // rides a screen another action declares

                    Register(byCode, owners, problems, where, new DiscoveredPermission(
                        s.ScreenKey, s.ScreenKey, "Screen", s.DisplayName ?? s.ScreenKey, s.SortOrder, s.SeedFor));
                    break;
                }

                case RequirePermissionAttribute p:
                {
                    var owner = Models.Security.Screens.OwnerOf(p.Code);
                    if (!Array.Exists(Models.Security.Screens.All,
                            k => string.Equals(k, owner, StringComparison.OrdinalIgnoreCase)))
                    {
                        problems.Add($"{where}: '{p.Code}' resolves to unknown screen '{owner}'.");
                        continue;
                    }
                    Register(byCode, owners, problems, where, new DiscoveredPermission(
                        p.Code, owner, "Action", p.DisplayName, p.SortOrder, p.SeedFor));
                    break;
                }
            }
        }

        if (problems.Count > 0)
            throw new InvalidOperationException(
                "The permission catalogue is incomplete, so the application refuses to start. " +
                "Every controller action needs exactly one permission attribute:" +
                Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", problems));

        return byCode.Values.OrderBy(p => p.ScreenKey).ThenBy(p => p.SortOrder).ThenBy(p => p.Code).ToList();
    }

    private static void Register(
        Dictionary<string, DiscoveredPermission> byCode,
        Dictionary<string, string> owners,
        List<string> problems, string where, DiscoveredPermission perm)
    {
        // Several actions may legitimately share one code (the GET that renders a
        // form and the POST that saves it), but they must agree on what it means.
        if (byCode.TryGetValue(perm.Code, out var existing))
        {
            if (existing.Kind != perm.Kind || existing.ScreenKey != perm.ScreenKey)
                problems.Add($"{where}: '{perm.Code}' is already declared by {owners[perm.Code]} " +
                             $"as {existing.Kind} on {existing.ScreenKey}.");
            return;
        }
        byCode[perm.Code] = perm;
        owners[perm.Code] = where;
    }

    /// <summary>
    /// Inserts newly discovered permissions, materialises their day-one grants
    /// from the declared seed, and flags rows whose action has disappeared.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        var discovered = Discover();
        try
        {
            using var c = new SqlConnection(_cs);
            await c.OpenAsync(ct);
            using var tx = c.BeginTransaction();

            var known = (await c.QueryAsync<string>(
                "SELECT permission_code FROM qms_permission", transaction: tx))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var added = new List<DiscoveredPermission>();
            foreach (var p in discovered)
            {
                if (known.Contains(p.Code))
                {
                    await c.ExecuteAsync(@"
                        UPDATE qms_permission
                        SET    screen_key = @ScreenKey, kind = @Kind, display_name = @DisplayName,
                               sort_order = @SortOrder, is_obsolete = 0, last_seen_at = SYSUTCDATETIME()
                        WHERE  permission_code = @Code",
                        new { p.Code, p.ScreenKey, p.Kind, p.DisplayName, p.SortOrder }, tx);
                    continue;
                }

                await c.ExecuteAsync(@"
                    INSERT INTO qms_permission
                        (permission_code, screen_key, kind, display_name, sort_order, seed_roles)
                    VALUES (@Code, @ScreenKey, @Kind, @DisplayName, @SortOrder, @SeedRoles)",
                    new { p.Code, p.ScreenKey, p.Kind, p.DisplayName, p.SortOrder,
                          SeedRoles = Seeds.ToCsv(p.SeedFor) }, tx);
                added.Add(p);
            }

            // Grants for brand-new permissions only. An existing permission is
            // never re-seeded, or every restart would undo an administrator's
            // deliberate revoke. Custom roles are never granted automatically —
            // whoever composed the role decides what it gains.
            foreach (var p in added)
            {
                foreach (var role in Seeds.RolesFor(p.SeedFor))
                {
                    var level = p.Kind == "Screen" && string.Equals(role, RoleCodes.Viewer, StringComparison.OrdinalIgnoreCase)
                        ? (byte)AccessLevel.Read
                        : (byte)AccessLevel.Edit;
                    await c.ExecuteAsync(@"
                        INSERT INTO qms_role_permission (role_code, permission_code, access_level, granted_by)
                        SELECT @role, @code, @level, 'catalog-seed'
                        WHERE  EXISTS (SELECT 1 FROM qms_role WHERE role_code = @role)
                          AND  NOT EXISTS (SELECT 1 FROM qms_role_permission
                                           WHERE role_code = @role AND permission_code = @code)",
                        new { role, code = p.Code, level }, tx);
                }
            }

            // Retire rows whose action is gone. Never DELETE: the grants must
            // survive a hotfix rollback, and a deleted row would silently revoke
            // on the next deploy. Skipped entirely if discovery looks truncated,
            // so a partial descriptor collection can't retire the whole catalogue.
            int retired = 0;
            if (discovered.Count >= 50)
            {
                var live = discovered.Select(p => p.Code).ToList();
                retired = await c.ExecuteAsync(@"
                    UPDATE qms_permission SET is_obsolete = 1
                    WHERE  is_obsolete = 0 AND permission_code NOT IN @live",
                    new { live }, tx);
            }
            else
            {
                _log.LogWarning("Only {Count} permissions discovered; skipping the obsolete pass.", discovered.Count);
            }

            tx.Commit();

            if (added.Count > 0 || retired > 0)
                _log.LogInformation("Permission catalogue reconciled: {Added} added, {Retired} retired, {Total} live.",
                    added.Count, retired, discovered.Count);

            await _resolver.RefreshAsync(ct);
        }
        catch (Exception ex)
        {
            // Never block startup on this. The resolver still loads whatever the
            // catalogue already contains, and the next restart tries again.
            _log.LogError(ex, "Permission catalogue reconciliation failed.");
        }
    }
}

/// <summary>
/// Reconciles the catalogue and warms the resolver during startup.
///
/// An <see cref="IStartupFilter"/> rather than an <c>IHostedService</c> on
/// purpose: the integration-test host removes every hosted service, so anything
/// primed that way would be empty in every test — passing tests that prove
/// nothing about production.
/// </summary>
public sealed class PermissionStartupFilter : IStartupFilter
{
    private readonly PermissionCatalog _catalog;
    private readonly ILogger<PermissionStartupFilter> _log;

    public PermissionStartupFilter(PermissionCatalog catalog, ILogger<PermissionStartupFilter> log)
    {
        _catalog = catalog;
        _log = log;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        // Discovery validation is deliberately allowed to throw here: a missing
        // attribute must stop the build and the deployment, not quietly leave an
        // endpoint unprotected.
        _catalog.ReconcileAsync().GetAwaiter().GetResult();
        _log.LogInformation("Permission catalogue ready.");
        next(app);
    };
}
