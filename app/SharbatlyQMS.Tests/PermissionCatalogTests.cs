using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Configuration;
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

    /// <summary>
    /// Every screen constant must have a qms_screen row.
    ///
    /// qms_permission.screen_key carries a FOREIGN KEY to qms_screen, and
    /// permission discovery cannot invent the screen. A constant without its
    /// migration row makes the whole reconciliation transaction roll back at
    /// startup -- and because the resolver is refreshed at the END of that same
    /// transaction, the application then authorises against an EMPTY snapshot:
    /// every screen denies for every user. It looks like a catastrophic
    /// permission bug and it is a missing row.
    ///
    /// Fail here, where the message names the file to write, rather than there.
    /// </summary>
    [Fact]
    public async Task Every_declared_screen_has_a_qms_screen_row()
    {
        using var scope = _factory.Services.CreateScope();
        var cfg = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        using var c = new Microsoft.Data.SqlClient.SqlConnection(cfg.GetConnectionString("Default"));
        var known = (await Dapper.SqlMapper.QueryAsync<string>(c,
            "SELECT screen_key FROM qms_screen")).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = Screens.All.Where(k => !known.Contains(k)).ToList();
        Assert.True(missing.Count == 0,
            "These screen constants have no qms_screen row. Add the migration pair " +
            "(app/db/mis/Mnn__*.sql + app/db/Vnn__*.sql, modelled on M28__labels_screen.sql) " +
            "and apply it BEFORE deploying: " + string.Join(", ", missing));
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
    public void Read_only_actions_are_flagged_so_they_survive_a_read_only_screen()
    {
        // The Security editor keeps these grantable when their screen is set to
        // read-only, and the resolver only asks for screen-Read to run them.
        // Both depend on the ReadOnly flag reaching the catalogue.
        var readOnly = Catalog().ReadOnlyActionCodes();

        Assert.Contains(Perm.Qo.Pdf, readOnly);
        Assert.Contains(Perm.Arrivals.ChecklistPdf, readOnly);
        Assert.Contains(Perm.Attachments.Download, readOnly);

        // An action that changes data must never be flagged read-only, or the
        // editor would leave an edit button pressable on a read-only screen.
        Assert.DoesNotContain(Perm.Qo.Delete, readOnly);
        Assert.DoesNotContain(Perm.Qo.Submit, readOnly);
        Assert.DoesNotContain(Perm.Arrivals.Complete, readOnly);
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
