using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Security;
using Xunit;

namespace SharbatlyQMS.Tests;

/// <summary>
/// The rules the whole permission model rests on, checked against the live
/// snapshot: the administrator floor, the read-only rule on the six record
/// screens, and the fail-closed behaviour for anything unknown.
/// </summary>
[Collection("workflow")]
public class PermissionResolverTests : IClassFixture<QmsAppFactory>
{
    private readonly IPermissionResolver _perms;

    public PermissionResolverTests(QmsAppFactory factory)
    {
        _perms = factory.Services.GetRequiredService<IPermissionResolver>();
        _perms.EnsureLoadedAsync().GetAwaiter().GetResult();
    }

    [Fact]
    public void The_snapshot_actually_loaded()
    {
        Assert.NotNull(_perms.LoadedAtUtc);
        Assert.NotEmpty(_perms.Roles);
        Assert.NotEmpty(_perms.Screens);
        Assert.NotEmpty(_perms.Permissions);
    }

    [Fact]
    public void Administrator_can_always_reach_the_security_screen()
    {
        // The floor is a string constant in code, not a database flag, so no
        // edit to the grant table can take it away.
        Assert.True(_perms.RoleHas(RoleCodes.Admin, Screens.AdminSecurity, AccessLevel.Read, isScreen: true));
        Assert.True(_perms.RoleHas(RoleCodes.Admin, Perm.Admin.SecurityEdit, AccessLevel.Edit, isScreen: false));
    }

    [Fact]
    public void Viewer_cannot_reach_the_security_screen()
    {
        Assert.False(_perms.RoleHas(RoleCodes.Viewer, Screens.AdminSecurity, AccessLevel.Read, isScreen: true));
        Assert.False(_perms.RoleHas(RoleCodes.Viewer, Perm.Admin.SecurityEdit, AccessLevel.Edit, isScreen: false));
    }

    [Fact]
    public void Viewer_can_read_the_record_screens_but_not_change_them()
    {
        Assert.True(_perms.RoleHas(RoleCodes.Viewer, Screens.ArrivalsDetails, AccessLevel.Read, isScreen: true));
        Assert.False(_perms.RoleHas(RoleCodes.Viewer, Screens.ArrivalsDetails, AccessLevel.Edit, isScreen: true));
        Assert.False(_perms.RoleHas(RoleCodes.Viewer, Perm.Arrivals.Complete, AccessLevel.Edit, isScreen: false));
    }

    [Fact]
    public void Viewer_keeps_the_read_only_functions_it_had_before()
    {
        // Downloading a report and opening an attachment were open to every
        // authenticated user before the permission model. Narrowing them would
        // have been a silent regression for the people who use them daily.
        Assert.True(_perms.RoleHas(RoleCodes.Viewer, Perm.Qo.Pdf, AccessLevel.Read, isScreen: false));
        Assert.True(_perms.RoleHas(RoleCodes.Viewer, Perm.Arrivals.ChecklistPdf, AccessLevel.Read, isScreen: false));
        Assert.True(_perms.RoleHas(RoleCodes.Viewer, Perm.Attachments.Download, AccessLevel.Read, isScreen: false));
    }

    [Fact]
    public void Operator_can_record_work_but_not_supervise_or_administer()
    {
        Assert.True(_perms.RoleHas(RoleCodes.Operator, Perm.Qo.Submit, AccessLevel.Edit, isScreen: false));
        Assert.True(_perms.RoleHas(RoleCodes.Operator, Perm.Qo.EditSample, AccessLevel.Edit, isScreen: false));
        Assert.False(_perms.RoleHas(RoleCodes.Operator, Perm.Qo.Finish, AccessLevel.Edit, isScreen: false));
        Assert.False(_perms.RoleHas(RoleCodes.Operator, Perm.Qo.Reopen, AccessLevel.Edit, isScreen: false));
        Assert.False(_perms.RoleHas(RoleCodes.Operator, Screens.AdminUsers, AccessLevel.Read, isScreen: true));
    }

    [Fact]
    public void Supervisor_finishes_but_does_not_reopen()
    {
        Assert.True(_perms.RoleHas(RoleCodes.Supervisor, Perm.Qo.Finish, AccessLevel.Edit, isScreen: false));
        Assert.True(_perms.RoleHas(RoleCodes.Supervisor, Perm.Qo.CancelSubmit, AccessLevel.Edit, isScreen: false));
        Assert.False(_perms.RoleHas(RoleCodes.Supervisor, Perm.Qo.Reopen, AccessLevel.Edit, isScreen: false));
    }

    [Fact]
    public void Claim_manager_approves_but_does_not_run_the_quality_workflow()
    {
        Assert.True(_perms.RoleHas(RoleCodes.ClaimManager, Perm.Claims.Approve, AccessLevel.Edit, isScreen: false));
        Assert.True(_perms.RoleHas(RoleCodes.ClaimManager, Perm.Claims.AddNote, AccessLevel.Edit, isScreen: false));
        Assert.False(_perms.RoleHas(RoleCodes.ClaimManager, Perm.Claims.MarkClaimRequest, AccessLevel.Edit, isScreen: false));
        Assert.False(_perms.RoleHas(RoleCodes.ClaimManager, Perm.Qo.Finish, AccessLevel.Edit, isScreen: false));
    }

    [Fact]
    public void Only_the_administrator_may_delete_a_quality_order()
    {
        Assert.True(_perms.RoleHas(RoleCodes.Admin, Perm.Qo.Delete, AccessLevel.Edit, isScreen: false));
        foreach (var r in new[] { RoleCodes.Manager, RoleCodes.Supervisor, RoleCodes.Operator, RoleCodes.Viewer })
            Assert.False(_perms.RoleHas(r, Perm.Qo.Delete, AccessLevel.Edit, isScreen: false));
    }

    [Fact]
    public void An_unknown_permission_denies_rather_than_throwing()
    {
        Assert.False(_perms.RoleHas(RoleCodes.Admin, "Nonsense.Not.A.Permission", AccessLevel.Read, isScreen: false));
    }

    [Fact]
    public void An_unknown_or_missing_role_denies_everything()
    {
        Assert.False(_perms.RoleHas("QcDoesNotExist", Screens.ArrivalsIndex, AccessLevel.Read, isScreen: true));
        Assert.False(_perms.RoleHas(null, Screens.ArrivalsIndex, AccessLevel.Read, isScreen: true));
        Assert.False(_perms.RoleHas("", Screens.ArrivalsIndex, AccessLevel.Read, isScreen: true));
    }

    [Fact]
    public void Access_levels_are_ordered_none_read_edit()
    {
        Assert.True(AccessLevel.None < AccessLevel.Read);
        Assert.True(AccessLevel.Read < AccessLevel.Edit);
    }

    [Fact]
    public void Exactly_six_screens_offer_a_read_only_setting()
    {
        var levelled = _perms.Screens.Where(s => s.SupportsAccessLevel).Select(s => s.ScreenKey).OrderBy(x => x).ToList();
        Assert.Equal(new[]
        {
            Screens.ArrivalsDetails, Screens.ArrivalsIndex, Screens.ArrivalsPending,
            Screens.Claims, Screens.QoDetails, Screens.QoIndex
        }.OrderBy(x => x), levelled);
    }

    [Fact]
    public void Every_user_who_can_sign_in_resolves_to_a_role()
    {
        // qms.AppUser only lists people holding an active Qc role, so a null
        // here would mean somebody can log in and then be denied everything.
        Assert.NotNull(_perms.RoleCodeOf(QmsAppFactory.AdminUserId));
        Assert.Equal(RoleCodes.Admin, _perms.RoleCodeOf(QmsAppFactory.AdminUserId));
    }
}
