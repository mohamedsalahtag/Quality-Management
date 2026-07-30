using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Services;

namespace SharbatlyQMS.Web.Security;

/// <summary>
/// Turns a <see cref="PermissionRequirement"/> into a yes or no.
///
/// Reads the role from the resolver by USER ID, so the answer never depends on
/// how fresh the auth cookie is, and honours an active impersonation the same
/// way the views do — one code path, so a preview shows exactly what the role
/// would really get.
/// </summary>
public sealed class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly IPermissionResolver _resolver;
    private readonly ILogger<PermissionHandler> _log;

    public PermissionHandler(IPermissionResolver resolver, ILogger<PermissionHandler> log)
    {
        _resolver = resolver;
        _log = log;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User?.Identity?.IsAuthenticated != true) return;   // 401, not 403

        await _resolver.EnsureLoadedAsync();

        var actual = UserPermissions.ResolveActualRole(_resolver, context.User);

        // Same impersonation rule as IUserPermissions: only a role that may
        // preview gets to be evaluated as someone else.
        var impersonated = context.User.FindFirst(ViewAsClaimsTransformer.OriginalRoleClaim) != null
            ? context.User.FindFirst(ClaimTypes.Role)?.Value
            : null;
        var effective =
            impersonated != null
            && !string.Equals(impersonated, actual, StringComparison.OrdinalIgnoreCase)
            && _resolver.RoleHas(actual, Perm.Admin.ViewAs, AccessLevel.Edit, isScreen: false)
                ? impersonated
                : actual;

        if (_resolver.RoleHas(effective, requirement.Code, requirement.Minimum, requirement.IsScreen))
        {
            context.Succeed(requirement);
            return;
        }

        // Left at Debug on purpose: a denial is a normal outcome (hidden buttons
        // are still reachable by URL), and logging every one at Warning would
        // bury the genuinely interesting entries from the decision filter.
        _log.LogDebug("Denied {Role} -> {Code} (needs {Level}).",
            effective ?? "(no role)", requirement.Code, requirement.Minimum);
    }
}

/// <summary>
/// The backstop that makes a FORGOTTEN attribute fail closed.
///
/// Every action must carry one of the permission attributes. A startup check
/// refuses to boot if one is missing, and a unit test catches it earlier still —
/// but if both are somehow bypassed, an endpoint with no decision is denied here
/// rather than silently served to everyone. That inverts the usual failure mode:
/// forgetting to protect something breaks it loudly instead of exposing it.
/// </summary>
public sealed class PermissionDecisionFilter : IAsyncAuthorizationFilter
{
    private readonly ILogger<PermissionDecisionFilter> _log;
    public PermissionDecisionFilter(ILogger<PermissionDecisionFilter> log) => _log = log;

    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var endpoint = context.HttpContext.GetEndpoint();
        if (endpoint == null) return Task.CompletedTask;

        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() != null) return Task.CompletedTask;
        if (endpoint.Metadata.GetMetadata<IPermissionDecision>() != null) return Task.CompletedTask;

        _log.LogError(
            "Endpoint {Endpoint} has no permission decision attribute and was denied. " +
            "Add [RequirePermission], [RequireScreen] or [QmsAlwaysAllowed].",
            endpoint.DisplayName);
        context.Result = new Microsoft.AspNetCore.Mvc.ForbidResult();
        return Task.CompletedTask;
    }
}
