using Dapper;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models.Security;

namespace SharbatlyQMS.Web.Services;

/// <summary>An application user who can be told about something.</summary>
public record Recipient(int UserId, string Username, string? DisplayName, string? Email)
{
    public bool CanBeMailed => !string.IsNullOrWhiteSpace(Email);
}

/// <summary>
/// Who is told about a survey event. There are no teams or departments in
/// QMS -- access is a role's permissions plus a user's branches -- so "the
/// people who decide surveys" means exactly that: active users whose role
/// holds the permission, for the branch the container is in.
///
/// Its own interface so the test host can substitute it: the tests run
/// against the live database and must not put a notice in a real person's
/// bell or outbox.
/// </summary>
public interface ISurveyRecipients
{
    /// <summary>
    /// Active users whose role holds <paramref name="permissionCode"/> and who
    /// are assigned to <paramref name="plant"/>.
    ///
    /// The Administrator role is left out on purpose: it holds every
    /// permission, so including it would send every notice about every branch
    /// to the people who run the system rather than the people who do the work.
    /// </summary>
    Task<IReadOnlyList<Recipient>> WithPermissionAsync(
        SqlConnection c, SqlTransaction tx, string permissionCode, string? plant);

    Task<Recipient?> ByUsernameAsync(SqlConnection c, SqlTransaction tx, string? username);
}

public class SurveyRecipients : ISurveyRecipients
{
    public async Task<IReadOnlyList<Recipient>> WithPermissionAsync(
        SqlConnection c, SqlTransaction tx, string permissionCode, string? plant)
    {
        // The two-switch rule, as the permission resolver applies it: an
        // action on a screen with a read-only / edit level also needs that
        // screen at Edit. A role with the action but a read-only screen cannot
        // act, so it is not told to.
        var owner = Screens.OwnerOf(permissionCode);
        var rows = await c.QueryAsync<Recipient>(@"
            SELECT u.UserId, u.Username, u.FullName AS DisplayName, u.Email
            FROM   qms.AppUser u
            JOIN   qms_role r ON r.role_code = u.RoleCode
            WHERE  u.IsActive = 1
              AND  r.is_super = 0
              AND  EXISTS (SELECT 1 FROM qms_role_permission rp
                           WHERE rp.role_code = u.RoleCode AND rp.permission_code = @permissionCode)
              AND  NOT EXISTS (
                       SELECT 1 FROM qms_screen sc
                       WHERE  sc.screen_key = @owner AND sc.screen_key <> @permissionCode
                         AND  sc.supports_access_level = 1
                         AND  NOT EXISTS (SELECT 1 FROM qms_role_permission rp2
                                          WHERE rp2.role_code = u.RoleCode
                                            AND rp2.permission_code = @owner AND rp2.access_level >= 2))
              AND  (@plant IS NULL OR EXISTS (SELECT 1 FROM portal.UserPlant up
                                              WHERE up.UserId = u.UserId AND up.Plant = @plant))",
            new { permissionCode, owner, plant }, tx);
        return rows.ToList();
    }

    public async Task<Recipient?> ByUsernameAsync(SqlConnection c, SqlTransaction tx, string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        return await c.QueryFirstOrDefaultAsync<Recipient>(@"
            SELECT u.UserId, u.Username, u.FullName AS DisplayName, u.Email
            FROM   qms.AppUser u
            WHERE  u.Username = @username AND u.IsActive = 1", new { username }, tx);
    }
}
