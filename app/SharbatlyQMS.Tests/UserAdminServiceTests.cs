using Microsoft.Extensions.DependencyInjection;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Services;
using Xunit;

namespace SharbatlyQMS.Tests;

/// <summary>
/// The guard rails on user provisioning, checked against the live database.
///
/// These exercise only the REFUSAL paths on purpose: the tests must not mutate
/// the shared Sharbatly_MIS data, and every one of these returns before any
/// write (and therefore before the permission-snapshot refresh) happens.
/// </summary>
[Collection("workflow")]
public class UserAdminServiceTests : IClassFixture<QmsAppFactory>
{
    private readonly QmsAppFactory _factory;
    public UserAdminServiceTests(QmsAppFactory factory) => _factory = factory;

    private IUserAdminService Service(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IUserAdminService>();

    [Fact]
    public async Task Assigning_a_role_to_a_missing_user_fails_cleanly()
    {
        using var scope = _factory.Services.CreateScope();
        var res = await Service(scope).AssignRoleAsync(
            userId: int.MaxValue, roleCode: RoleCodes.Viewer, plantCode: null, actor: "test");

        Assert.False(res.Ok);
        Assert.Equal("That user no longer exists.", res.Error);
    }

    [Fact]
    public async Task Assigning_an_unknown_role_is_refused_before_any_write()
    {
        using var scope = _factory.Services.CreateScope();
        // The user exists, but the role does not -- the call must fail on the
        // role check, never touching portal.UserRole.
        var res = await Service(scope).AssignRoleAsync(
            QmsAppFactory.AdminUserId, "QcRoleThatDoesNotExist", plantCode: null, actor: "test");

        Assert.False(res.Ok);
        Assert.Equal("That role does not exist or is not active.", res.Error);
    }

    [Fact]
    public async Task Importing_without_a_username_is_refused()
    {
        using var scope = _factory.Services.CreateScope();
        var res = await Service(scope).AddFromDirectoryAsync(
            username: "", roleCode: RoleCodes.Viewer, plantCode: null, actorUserId: 0, actor: "test");

        Assert.False(res.Ok);
        Assert.Equal("A username and an active role are required.", res.Error);
    }
}
