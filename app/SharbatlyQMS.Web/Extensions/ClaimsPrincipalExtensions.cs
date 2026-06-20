using System.Security.Claims;
using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Extensions;

/// <summary>
/// Plant-scoping rule centralized so every controller asks the same question.
/// Only Operator users carry a non-empty PlantCode claim (issued in
/// AccountController.IssueCookieAsync); for every other role the claim is
/// either absent or empty and <see cref="GetScopedPlant"/> returns null —
/// the caller treats null as "unrestricted, sees everything".
/// </summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>The plant code the user is restricted to, or null if unrestricted.</summary>
    public static string? GetScopedPlant(this ClaimsPrincipal user)
    {
        var p = user.FindFirst("PlantCode")?.Value;
        return string.IsNullOrWhiteSpace(p) ? null : p;
    }

    /// <summary>True when the user is in the Operator role.</summary>
    public static bool IsOperator(this ClaimsPrincipal user) =>
        user.IsInRole(UserRoles.Operator);
}
