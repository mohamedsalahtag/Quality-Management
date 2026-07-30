using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Security;
using Xunit;

namespace SharbatlyQMS.Tests;

/// <summary>
/// The regression net behind "any future addition must also be a permission".
///
/// The first test here fails the build the day somebody adds a controller action
/// without deciding what permission guards it — which is the whole point of the
/// feature. The application also refuses to start in that case, but a red test
/// is a much better place to find out than a failed deployment.
/// </summary>
[Collection("workflow")]
public class PermissionCatalogTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    public PermissionCatalogTests(QmsAppFactory factory) => _factory = factory;

    private IReadOnlyList<ControllerActionDescriptor> Actions()
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items.OfType<ControllerActionDescriptor>().ToList();
    }

    private PermissionCatalog Catalog()
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<PermissionCatalog>();
    }

    [Fact]
    public void Every_action_declares_exactly_one_permission_decision()
    {
        var offenders = new List<string>();
        foreach (var d in Actions())
        {
            if (d.EndpointMetadata.OfType<IAllowAnonymous>().Any()) continue;

            var onMethod = d.MethodInfo.GetCustomAttributes(inherit: true).OfType<IPermissionDecision>().ToList();
            var onClass  = d.ControllerTypeInfo.GetCustomAttributes(inherit: true).OfType<IPermissionDecision>().ToList();
            var count    = onMethod.Count > 0 ? onMethod.Count : onClass.Count;

            if (count != 1)
                offenders.Add($"{d.ControllerName}.{d.ActionName} has {count} permission attributes");
        }

        Assert.True(offenders.Count == 0,
            "Every controller action needs exactly one of [RequirePermission], [RequireScreen] or " +
            "[QmsAlwaysAllowed]. Offenders:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Discovery_validates_and_produces_a_plausible_catalogue()
    {
        // Discover() throws on a duplicate code, an unknown screen key or a
        // missing attribute, so simply completing is a meaningful assertion.
        var discovered = Catalog().Discover();

        Assert.InRange(discovered.Count, 50, 200);
        Assert.All(discovered, p => Assert.False(string.IsNullOrWhiteSpace(p.DisplayName)));
    }

    [Fact]
    public void Every_permission_belongs_to_a_known_screen()
    {
        foreach (var p in Catalog().Discover())
            Assert.Contains(p.ScreenKey, Screens.All);
    }

    [Fact]
    public void Screen_permissions_use_their_screen_key_as_the_code()
    {
        foreach (var p in Catalog().Discover().Where(p => p.Kind == "Screen"))
            Assert.Equal(p.ScreenKey, p.Code);
    }

    [Fact]
    public void Action_codes_resolve_back_to_their_owning_screen()
    {
        // The two-switch rule depends on this: an action's screen is derived
        // from its code, so a code that does not resolve would silently escape
        // the read-only gate on a levelled screen.
        foreach (var p in Catalog().Discover().Where(p => p.Kind == "Action"))
            Assert.Equal(p.ScreenKey, Screens.OwnerOf(p.Code));
    }

    [Fact]
    public void No_two_actions_claim_the_same_code_with_a_different_meaning()
    {
        var byCode = Catalog().Discover().GroupBy(p => p.Code, StringComparer.OrdinalIgnoreCase);
        foreach (var g in byCode)
        {
            Assert.Single(g.Select(p => p.Kind).Distinct());
            Assert.Single(g.Select(p => p.ScreenKey).Distinct(StringComparer.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Self_service_actions_stay_outside_the_permission_system()
    {
        // Signing out and editing your own profile must never be revocable, or
        // an administrator could trap somebody in the application.
        var mustBeAlwaysAllowed = new[] { "Logout", "Profile", "ChangePassword", "UploadProfilePicture", "ViewAs" };
        foreach (var name in mustBeAlwaysAllowed)
        {
            var actions = Actions().Where(a => a.ControllerName == "Account" && a.ActionName == name).ToList();
            Assert.NotEmpty(actions);
            Assert.All(actions, a => Assert.Contains(
                a.MethodInfo.GetCustomAttributes(inherit: true).OfType<IPermissionDecision>(),
                p => p is QmsAlwaysAllowedAttribute));
        }
    }
}
