// User + group + member CRUD methods that the controllers call.
// Add to your existing DbService. Reference uses Dapper + Microsoft.Data.SqlClient.

using Dapper;
using Microsoft.Data.SqlClient;
using HelpDesk.Models;

namespace HelpDesk.Services;

public partial interface IDbService
{
    // Config K/V
    Task<string?> GetConfigAsync(string key);
    Task SetConfigAsync(string key, string value, int? updatedBy);

    // Users
    Task<List<User>>     GetAllUsersAsync();
    Task<User?>          GetUserByIdAsync(int userId);
    Task<User?>          GetUserByUsernameAsync(string username);
    Task<int>            CreateUserAsync(User user);
    Task                 UpdateUserAsync(User user);
    Task                 UpdateUserProfileAsync(int userId, string employeeId, string fullName, string email, string department);
    Task                 UpdateProfilePictureAsync(int userId, string picturePath);
    Task                 SetUserOnlineAsync(int userId, bool online);
    Task                 UpdateLastSeenAsync(int userId);
    Task                 DeleteUserAsync(int userId);
    Task<List<AdUser>>   GetAdUsersWithRegistrationStatusAsync(List<AdUser> adUsers);

    // Groups + memberships
    Task<List<TechnicianGroup>>       GetGroupsAsync(bool activeOnly = true);
    Task<List<TechnicianGroupMember>> GetGroupMembersAsync(int groupId);
    Task                              UpsertGroupMemberAsync(TechnicianGroupMember m);
    Task                              RemoveGroupMemberAsync(int userId, int groupId);
}

public partial class DbService : IDbService
{
    public async Task<List<User>> GetAllUsersAsync()
    {
        using var c = Conn();
        return (await c.QueryAsync<User>(
            "SELECT * FROM Users ORDER BY FullName")).ToList();
    }

    public async Task<User?> GetUserByIdAsync(int userId)
    {
        using var c = Conn();
        return await c.QueryFirstOrDefaultAsync<User>(
            "SELECT * FROM Users WHERE UserId=@userId", new { userId });
    }

    public async Task<User?> GetUserByUsernameAsync(string username)
    {
        using var c = Conn();
        return await c.QueryFirstOrDefaultAsync<User>(
            "SELECT * FROM Users WHERE AdUsername=@username", new { username });
    }

    public async Task<int> CreateUserAsync(User user)
    {
        using var c = Conn();
        return await c.ExecuteScalarAsync<int>(@"
            INSERT INTO Users
                (EmployeeId, AdUsername, FullName, Email, Department,
                 PasswordHash, Role, IsActive, CreatedAt)
            VALUES
                (@EmployeeId, @AdUsername, @FullName, @Email, @Department,
                 @PasswordHash, @Role, @IsActive, GETDATE());
            SELECT SCOPE_IDENTITY();", user);
    }

    public async Task UpdateUserAsync(User user)
    {
        using var c = Conn();
        await c.ExecuteAsync(@"
            UPDATE Users SET
                FullName=@FullName, Email=@Email, Department=@Department,
                Role=@Role, IsActive=@IsActive,
                DisabledAt=@DisabledAt, DisabledBy=@DisabledBy,
                LastLogin=@LastLogin, PasswordHash=@PasswordHash
            WHERE UserId=@UserId", user);
    }

    public async Task UpdateUserProfileAsync(int userId, string employeeId,
        string fullName, string email, string department)
    {
        using var c = Conn();
        await c.ExecuteAsync(@"
            UPDATE Users SET
                EmployeeId=@employeeId, FullName=@fullName,
                Email=@email, Department=@department
            WHERE UserId=@userId",
            new { userId, employeeId, fullName, email, department });
    }

    public async Task UpdateProfilePictureAsync(int userId, string picturePath)
    {
        using var c = Conn();
        await c.ExecuteAsync(
            "UPDATE Users SET ProfilePicture=@picturePath WHERE UserId=@userId",
            new { userId, picturePath });
    }

    public async Task SetUserOnlineAsync(int userId, bool online)
    {
        using var c = Conn();
        await c.ExecuteAsync(
            "UPDATE Users SET IsOnline=@online, LastSeen=GETDATE() WHERE UserId=@userId",
            new { userId, online });
    }

    public async Task UpdateLastSeenAsync(int userId)
    {
        using var c = Conn();
        await c.ExecuteAsync(
            "UPDATE Users SET LastSeen=GETDATE(), LastLogin=GETDATE() WHERE UserId=@userId",
            new { userId });
    }

    // Per SPEC.md §6 the admin can only delete users that have no FK references.
    // The controller catches SqlException 547 and surfaces a friendly message.
    public async Task DeleteUserAsync(int userId)
    {
        using var c = Conn();
        // Clean up rows that we DO want to cascade (filters / notes / memberships)
        await c.ExecuteAsync("DELETE FROM SavedTicketFilters WHERE UserId=@userId", new { userId });
        await c.ExecuteAsync("DELETE FROM TechnicianNotes WHERE UserId=@userId", new { userId });
        await c.ExecuteAsync("DELETE FROM TechnicianGroupMembers WHERE UserId=@userId", new { userId });
        // Real delete - throws SqlException 547 if any ticket/audit row still refs us
        await c.ExecuteAsync("DELETE FROM Users WHERE UserId=@userId", new { userId });
    }

    public async Task<List<AdUser>> GetAdUsersWithRegistrationStatusAsync(List<AdUser> adUsers)
    {
        if (!adUsers.Any()) return adUsers;
        using var c = Conn();
        var registered = (await c.QueryAsync<User>(
            "SELECT UserId, AdUsername, Role, IsActive FROM Users")).ToList();
        var byUsername = registered.ToDictionary(
            u => u.AdUsername.ToLowerInvariant(),
            u => u);
        foreach (var ad in adUsers)
        {
            if (byUsername.TryGetValue(ad.Username.ToLowerInvariant(), out var sys))
            {
                ad.IsRegistered   = true;
                ad.UserId         = sys.UserId;
                ad.SystemRole     = sys.Role;
                ad.SystemIsActive = sys.IsActive;
            }
        }
        return adUsers;
    }

    // -- Groups + members ------------------------------------------------
    public async Task<List<TechnicianGroup>> GetGroupsAsync(bool activeOnly = true)
    {
        using var c = Conn();
        var sql = activeOnly
            ? "SELECT * FROM TechnicianGroups WHERE IsActive=1 ORDER BY GroupName"
            : "SELECT * FROM TechnicianGroups ORDER BY GroupName";
        return (await c.QueryAsync<TechnicianGroup>(sql)).ToList();
    }

    public async Task<List<TechnicianGroupMember>> GetGroupMembersAsync(int groupId)
    {
        using var c = Conn();
        return (await c.QueryAsync<TechnicianGroupMember>(@"
            SELECT m.*, u.FullName, g.GroupName
            FROM TechnicianGroupMembers m
            JOIN Users u             ON u.UserId  = m.UserId
            JOIN TechnicianGroups g  ON g.GroupId = m.GroupId
            WHERE m.GroupId = @groupId", new { groupId })).ToList();
    }

    public async Task UpsertGroupMemberAsync(TechnicianGroupMember m)
    {
        using var c = Conn();
        await c.ExecuteAsync(@"
            MERGE TechnicianGroupMembers AS t
            USING (SELECT @UserId AS UserId, @GroupId AS GroupId) AS s
              ON (t.UserId = s.UserId AND t.GroupId = s.GroupId)
            WHEN MATCHED THEN UPDATE SET
                CanDelete=@CanDelete, CanAssign=@CanAssign, CanPickupOthers=@CanPickupOthers
            WHEN NOT MATCHED THEN INSERT
                (UserId,GroupId,CanDelete,CanAssign,CanPickupOthers,AssignedAt)
            VALUES
                (@UserId,@GroupId,@CanDelete,@CanAssign,@CanPickupOthers,GETDATE());", m);
    }

    public async Task RemoveGroupMemberAsync(int userId, int groupId)
    {
        using var c = Conn();
        await c.ExecuteAsync(
            "DELETE FROM TechnicianGroupMembers WHERE UserId=@userId AND GroupId=@groupId",
            new { userId, groupId });
    }

    private SqlConnection Conn() => new SqlConnection(/* your connection string */);
}
