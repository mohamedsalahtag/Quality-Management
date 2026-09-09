using System.Security.Claims;
using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Extensions;

/// <summary>
/// Plant-scoping rule centralized so every controller asks the same question.
///
/// Plant access is a per-USER setting now, not a per-role flag. At login
/// AccountController issues a single "Plants" claim:
///   * "*"            -> unrestricted: the user sees every plant (Administrators,
///                       and anyone we deliberately mark all-plants).
///   * "RD01,JD02"    -> restricted to exactly those plant codes.
///   * ""  (empty)    -> restricted to NOTHING until plants are assigned.
///
/// <see cref="GetScopedPlants"/> returns null for the unrestricted case so a
/// caller can keep the cheap "null means see everything" check it always had;
/// an empty (but non-null) set means "assigned no plants -> show nothing".
/// </summary>
public static class ClaimsPrincipalExtensions
{
    public const string PlantsClaim = "Plants";
    private const string AllPlantsMarker = "*";

    /// <summary>
    /// The set of plant codes the user is limited to, or null when the user is
    /// unrestricted (sees every plant). An empty set means the user has been
    /// assigned no plants and must see nothing.
    /// </summary>
    public static IReadOnlyCollection<string>? GetScopedPlants(this ClaimsPrincipal user)
    {
        var raw = user.FindFirst(PlantsClaim)?.Value;

        // No claim at all: an older cookie issued before this feature. Treat as
        // unrestricted so a mid-transition session is never locked out; the next
        // sign-in issues the proper claim.
        if (raw == null) return null;
        if (raw == AllPlantsMarker) return null;

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The single plant to force a list filter to, when the user is limited to
    /// exactly one plant; null otherwise (unrestricted, or several plants where
    /// the caller must fall back to the full <see cref="GetScopedPlants"/> set).
    /// Lets the existing single-plant "lock the dropdown" UI keep working for the
    /// common operator-with-one-plant case.
    /// </summary>
    public static string? SinglePlantOrNull(this ClaimsPrincipal user)
    {
        var plants = user.GetScopedPlants();
        return plants is { Count: 1 } ? plants.First() : null;
    }

    /// <summary>The user's plant restriction as a value a data query can apply
    /// directly. Unrestricted users get <see cref="Models.PlantScope.All"/>.</summary>
    public static PlantScope GetPlantScope(this ClaimsPrincipal user)
    {
        var plants = user.GetScopedPlants();
        return plants == null
            ? PlantScope.All
            : new PlantScope(false, plants.ToArray());
    }

}
