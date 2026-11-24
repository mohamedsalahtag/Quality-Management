using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// The committee side of survey management (M46): the configured committees,
/// the copy assigned to a survey when it is confirmed, and what the committee
/// then does -- the appointment, who attended, and the QC officer's
/// confirmation that the shipment was inspected.
///
/// Two rules run through all of it:
///   * every committee activity requires the survey to be CONFIRMED, checked
///     here and not only on the page; and
///   * none of it changes the survey's status. A confirmed survey stays
///     confirmed through scheduling, attendance and inspection.
/// </summary>
public interface ISurveyCommitteeService
{
    // ---- Configuration (Parameters -> Survey committees) ----
    Task<IReadOnlyList<SurveyCommittee>> ListCommitteesAsync();
    /// <summary>Creates (id 0) or updates. A new committee's id is set on
    /// <paramref name="committee"/>.</summary>
    Task<(bool ok, string? error)> SaveCommitteeAsync(SurveyCommittee committee, string user);
    /// <summary>The New committee dialog: the committee and the members
    /// picked with it (<see cref="SurveyCommittee.Members"/>). The committee
    /// is saved first and each member is then added on its own, so one bad
    /// row does not lose the rest: <c>notAdded</c> names what was refused and
    /// why. A row left empty is not a member, and a user picked twice is
    /// added once.</summary>
    Task<(bool ok, string? error, int added, IReadOnlyList<string> notAdded)> CreateWithMembersAsync(
        SurveyCommittee committee, string user);
    Task<(bool ok, string? error)> DeleteCommitteeAsync(int committeeId, string user);
    Task<(bool ok, string? error)> SaveMemberAsync(SurveyCommitteeMember member, string user);
    Task<(bool ok, string? error)> DeleteMemberAsync(int memberId, string user);
    /// <summary>Every active application user, those whose role can open the
    /// Surveys page first.</summary>
    Task<IReadOnlyList<CommitteeUserOption>> ListUsersAsync();

    // ---- Assignment ----
    /// <summary>The committee for a branch: its own if it has an active one,
    /// otherwise the active default. Null when neither is configured.</summary>
    Task<SurveyCommittee?> FindForPlantAsync(string? plant);
    /// <summary>Copies the branch's committee onto the survey, inside the
    /// caller's transaction (the Confirm). Returns false when no committee is
    /// configured -- which does not stop the confirmation.</summary>
    Task<bool> AssignAsync(SqlConnection c, SqlTransaction tx, long surveyId, string? plant, string user);
    /// <summary>For a confirmed survey that got no committee because none was
    /// configured at the time: assign the one configured now.</summary>
    Task<(bool ok, string? error)> AssignNowAsync(long surveyId, string user);

    // ---- Per survey ----
    /// <summary>The survey's members, each with their attendance at
    /// <paramref name="appointmentId"/> (or unrecorded when it is null).</summary>
    Task<IReadOnlyList<SurveyMember>> GetMembersAsync(long surveyId, long? appointmentId);
    /// <summary>Every appointment, newest first. The first one that is not
    /// superseded is the one that stands.</summary>
    Task<IReadOnlyList<SurveyAppointment>> GetAppointmentsAsync(long surveyId);

    Task<(bool ok, string? error)> SaveAppointmentAsync(long surveyId, AppointmentInput input, string user);
    Task<(bool ok, string? error)> ConfirmScheduleAsync(long surveyId, string user);
    Task<(bool ok, string? error)> RescheduleAsync(long surveyId, AppointmentInput input, string? reason, string user);
    Task<(bool ok, string? error)> SaveAttendanceAsync(long surveyId, AttendanceInput input, string user);
    Task<(bool ok, string? error)> ConfirmAttendanceAsync(long surveyId, string user);
    Task<(bool ok, string? error)> ConfirmInspectionAsync(long surveyId, InspectionInput input, string user);
}

public class SurveyCommitteeService : ISurveyCommitteeService
{
    public const string NotConfirmed = "Committee activities are only available for a confirmed survey.";

    private readonly string _cs;
    private readonly IAuditService _audit;
    private readonly ISurveyNotifier _notifier;

    public SurveyCommitteeService(IConfiguration config, IAuditService audit, ISurveyNotifier notifier)
    {
        _cs = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
        _audit = audit;
        _notifier = notifier;
    }

    private SqlConnection Open() => new(_cs);

    private static string? Clean(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length > max ? s[..max] : s;
    }

    private static bool? YesNo(string? v) => v?.Trim().ToLowerInvariant() switch
    {
        "true" or "yes"  => true,
        "false" or "no"  => false,
        _                => null
    };

    private static TimeSpan? ParseTime(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        return TimeSpan.TryParse(v.Trim(), CultureInfo.InvariantCulture, out var t)
               && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1)
            ? t : null;
    }

    // ================= Configuration =================

    private const string MemberColumns = @"
        participant_type AS ParticipantType, user_id AS UserId, member_name AS MemberName,
        department AS Department, organisation AS Organisation, committee_role AS CommitteeRole,
        email AS Email, phone AS Phone, notify_in_app AS NotifyInApp, notify_email AS NotifyEmail,
        expected_attendance AS ExpectedAttendance, sort_order AS SortOrder";

    public async Task<IReadOnlyList<SurveyCommittee>> ListCommitteesAsync()
    {
        using var c = Open();
        var committees = (await c.QueryAsync<SurveyCommittee>(@"
            SELECT committee_id AS CommitteeId, committee_name AS CommitteeName, description AS Description,
                   plant AS Plant, is_active AS IsActive, updated_at AS UpdatedAt, updated_by AS UpdatedBy
            FROM   qms_survey_committee
            ORDER  BY is_active DESC, CASE WHEN plant IS NULL THEN 0 ELSE 1 END, plant, committee_name")).ToList();
        var members = (await c.QueryAsync<SurveyCommitteeMember>(
            "SELECT member_id AS MemberId, committee_id AS CommitteeId, is_active AS IsActive," + MemberColumns + @"
             FROM   qms_survey_committee_member
             ORDER  BY sort_order, member_id")).ToLookup(m => m.CommitteeId);
        foreach (var k in committees) k.Members = members[k.CommitteeId].ToList();
        return committees;
    }

    public async Task<(bool ok, string? error)> SaveCommitteeAsync(SurveyCommittee k, string user)
    {
        var name = Clean(k.CommitteeName, 120);
        if (name == null) return (false, "Give the committee a name.");
        var plant = Clean(k.Plant, 4);
        var description = Clean(k.Description, 500);

        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();
        try
        {
            if (k.CommitteeId == 0)
            {
                var id = await c.ExecuteScalarAsync<int>(@"
                    INSERT INTO qms_survey_committee (committee_name, description, plant, is_active, created_by, updated_by)
                    VALUES (@name, @description, @plant, @active, @user, @user);
                    SELECT CAST(SCOPE_IDENTITY() AS INT);",
                    new { name, description, plant, active = k.IsActive, user }, tx);
                await _audit.WriteAsync(c, tx, EntityTypes.SurveyCommittee, id, ActionCodes.Created,
                    null, new { committee_name = name, plant, is_active = k.IsActive }, user);
                // The caller gets the new id back on the object, so the page
                // can lead straight into adding the first member.
                k.CommitteeId = id;
            }
            else
            {
                var old = await c.QuerySingleOrDefaultAsync(@"
                    SELECT committee_name, description, plant, is_active
                    FROM   qms_survey_committee WHERE committee_id = @id", new { id = k.CommitteeId }, tx);
                if (old == null) { tx.Rollback(); return (false, "Committee not found."); }
                await c.ExecuteAsync(@"
                    UPDATE qms_survey_committee
                    SET    committee_name = @name, description = @description, plant = @plant,
                           is_active = @active, updated_at = SYSUTCDATETIME(), updated_by = @user
                    WHERE  committee_id = @id",
                    new { name, description, plant, active = k.IsActive, user, id = k.CommitteeId }, tx);
                await _audit.WriteAsync(c, tx, EntityTypes.SurveyCommittee, k.CommitteeId, ActionCodes.Updated,
                    old, new { committee_name = name, description, plant, is_active = k.IsActive }, user);
            }
            tx.Commit();
            return (true, null);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // UX_qms_survey_committee_plant: one active committee per branch,
            // and one active default.
            tx.Rollback();
            return (false, plant == null
                ? "There is already an active default committee. Deactivate it first, or give this one a branch."
                : $"Branch {plant} already has an active committee. Deactivate it first.");
        }
    }

    public async Task<(bool ok, string? error, int added, IReadOnlyList<string> notAdded)> CreateWithMembersAsync(
        SurveyCommittee k, string user)
    {
        var members = k.Members ?? new List<SurveyCommitteeMember>();
        k.CommitteeId = 0;
        var saved = await SaveCommitteeAsync(k, user);
        if (!saved.ok) return (false, saved.error, 0, Array.Empty<string>());

        var notAdded = new List<string>();
        var picked = new HashSet<int>();
        int added = 0, sort = 0;
        foreach (var m in members)
        {
            var isInternal = m.ParticipantType == ParticipantType.Internal;
            // A row left empty is not a member; a user picked twice is one member.
            if (isInternal ? m.UserId == null : string.IsNullOrWhiteSpace(m.MemberName)) continue;
            if (isInternal && !picked.Add(m.UserId!.Value)) continue;

            m.MemberId = 0;
            m.CommitteeId = k.CommitteeId;
            m.SortOrder = sort += 10;
            var (ok, error) = await SaveMemberAsync(m, user);
            if (ok) added++;
            else notAdded.Add((isInternal ? "user " + m.UserId : m.MemberName.Trim()) + " (" + error + ")");
        }
        return (true, null, added, notAdded);
    }

    public async Task<(bool ok, string? error)> DeleteCommitteeAsync(int committeeId, string user)
    {
        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();
        var old = await c.QuerySingleOrDefaultAsync(@"
            SELECT committee_name, plant, is_active FROM qms_survey_committee WHERE committee_id = @committeeId",
            new { committeeId }, tx);
        if (old == null) { tx.Rollback(); return (false, "Committee not found."); }
        // Surveys already assigned keep their own copy of the members, so
        // deleting the configuration loses nothing that happened.
        await c.ExecuteAsync("DELETE FROM qms_survey_committee WHERE committee_id = @committeeId", new { committeeId }, tx);
        await _audit.WriteAsync(c, tx, EntityTypes.SurveyCommittee, committeeId, ActionCodes.Deleted, old, null, user);
        tx.Commit();
        return (true, null);
    }

    public async Task<(bool ok, string? error)> SaveMemberAsync(SurveyCommitteeMember m, string user)
    {
        if (m.ParticipantType != ParticipantType.Internal && m.ParticipantType != ParticipantType.External)
            return (false, "Choose whether the member is internal or external.");
        if (!CommitteeRole.All.Contains(m.CommitteeRole))
            return (false, "Choose the member's role on the committee.");

        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();

        if (await c.ExecuteScalarAsync<int>(
                "SELECT COUNT(1) FROM qms_survey_committee WHERE committee_id = @id", new { id = m.CommitteeId }, tx) == 0)
        { tx.Rollback(); return (false, "Committee not found."); }

        string? name = Clean(m.MemberName, 120), email = Clean(m.Email, 256), department = Clean(m.Department, 120);
        int? userId = null;
        if (m.ParticipantType == ParticipantType.Internal)
        {
            // An internal member IS an application user: name, e-mail and
            // department come from the account, so they follow it.
            if (m.UserId is not int uid) { tx.Rollback(); return (false, "Choose the user."); }
            var u = await c.QuerySingleOrDefaultAsync<CommitteeUserOption>(@"
                SELECT UserId, Username, FullName, Email, Department
                FROM   qms.AppUser WHERE UserId = @uid AND IsActive = 1", new { uid }, tx);
            if (u == null) { tx.Rollback(); return (false, "That user is not an active QMS user."); }
            userId = uid;
            name = Clean(string.IsNullOrWhiteSpace(u.FullName) ? u.Username : u.FullName, 120);
            email = Clean(u.Email, 256);
            department ??= Clean(u.Department, 120);
        }
        else
        {
            if (name == null) { tx.Rollback(); return (false, "Enter the member's name."); }
        }
        if (email != null && !email.Contains('@')) { tx.Rollback(); return (false, "That e-mail address does not look right."); }

        var values = new
        {
            id = m.MemberId, committeeId = m.CommitteeId, type = m.ParticipantType, userId, name, department,
            organisation = Clean(m.Organisation, 120), role = m.CommitteeRole, email, phone = Clean(m.Phone, 40),
            bell = m.ParticipantType == ParticipantType.Internal && m.NotifyInApp,   // the bell needs an account
            mail = m.NotifyEmail, expected = m.ExpectedAttendance, active = m.IsActive, sort = m.SortOrder
        };
        if (m.MemberId == 0)
        {
            await c.ExecuteAsync(@"
                INSERT INTO qms_survey_committee_member
                    (committee_id, participant_type, user_id, member_name, department, organisation, committee_role,
                     email, phone, notify_in_app, notify_email, expected_attendance, is_active, sort_order)
                VALUES (@committeeId, @type, @userId, @name, @department, @organisation, @role,
                        @email, @phone, @bell, @mail, @expected, @active, @sort)", values, tx);
        }
        else
        {
            var n = await c.ExecuteAsync(@"
                UPDATE qms_survey_committee_member
                SET    participant_type = @type, user_id = @userId, member_name = @name, department = @department,
                       organisation = @organisation, committee_role = @role, email = @email, phone = @phone,
                       notify_in_app = @bell, notify_email = @mail, expected_attendance = @expected,
                       is_active = @active, sort_order = @sort
                WHERE  member_id = @id AND committee_id = @committeeId", values, tx);
            if (n == 0) { tx.Rollback(); return (false, "Member not found."); }
        }
        await _audit.WriteAsync(c, tx, EntityTypes.SurveyCommittee, m.CommitteeId, ActionCodes.Updated,
            null, new { member = name, participant_type = m.ParticipantType, committee_role = m.CommitteeRole, is_active = m.IsActive }, user);
        tx.Commit();
        return (true, null);
    }

    public async Task<(bool ok, string? error)> DeleteMemberAsync(int memberId, string user)
    {
        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();
        var old = await c.QuerySingleOrDefaultAsync<SurveyCommitteeMember>(@"
            SELECT member_id AS MemberId, committee_id AS CommitteeId, member_name AS MemberName
            FROM   qms_survey_committee_member WHERE member_id = @memberId", new { memberId }, tx);
        if (old == null) { tx.Rollback(); return (false, "Member not found."); }
        await c.ExecuteAsync("DELETE FROM qms_survey_committee_member WHERE member_id = @memberId", new { memberId }, tx);
        await _audit.WriteAsync(c, tx, EntityTypes.SurveyCommittee, old.CommitteeId, ActionCodes.Updated,
            new { member = old.MemberName }, new { member = (string?)null }, user);
        tx.Commit();
        return (true, null);
    }

    public async Task<IReadOnlyList<CommitteeUserOption>> ListUsersAsync()
    {
        using var c = Open();
        // "Can open Surveys" as the notifier reads it: the administrator
        // role, or a role holding the Surveys screen.
        return (await c.QueryAsync<CommitteeUserOption>(@"
            SELECT u.UserId, u.Username, u.FullName, u.Email, u.Department, u.RoleName,
                   CAST(CASE WHEN r.is_super = 1 OR EXISTS (
                                 SELECT 1 FROM qms_role_permission rp
                                 WHERE  rp.role_code = u.RoleCode AND rp.permission_code = @screen)
                             THEN 1 ELSE 0 END AS BIT) AS CanOpenSurveys
            FROM   qms.AppUser u
            LEFT   JOIN qms_role r ON r.role_code = u.RoleCode
            WHERE  u.IsActive = 1
            ORDER  BY CanOpenSurveys DESC, u.FullName, u.Username",
            new { screen = Models.Security.Screens.Surveys })).ToList();
    }

    // ================= Assignment =================

    private sealed class SurveyState
    {
        public string    StatusCode            { get; set; } = "";
        public int?      CommitteeId           { get; set; }
        public string?   Plant                 { get; set; }
        public DateTime? InspectionConfirmedAt { get; set; }
    }

    private static Task<SurveyCommittee?> FindAsync(SqlConnection c, SqlTransaction? tx, string? plant) =>
        // The branch's own committee wins over the default.
        c.QueryFirstOrDefaultAsync<SurveyCommittee>(@"
            SELECT TOP 1 committee_id AS CommitteeId, committee_name AS CommitteeName,
                   description AS Description, plant AS Plant, is_active AS IsActive
            FROM   qms_survey_committee
            WHERE  is_active = 1 AND (plant = @plant OR plant IS NULL)
            ORDER  BY CASE WHEN plant IS NULL THEN 1 ELSE 0 END", new { plant }, tx)!;

    public async Task<SurveyCommittee?> FindForPlantAsync(string? plant)
    {
        using var c = Open();
        return await FindAsync(c, null, plant);
    }

    public async Task<bool> AssignAsync(SqlConnection c, SqlTransaction tx, long surveyId, string? plant, string user)
    {
        var k = await FindAsync(c, tx, plant);
        if (k == null) return false;

        // Guarded on committee_id IS NULL: a survey is assigned once. What is
        // copied below is then the survey's own, whatever the configuration
        // becomes later.
        var changed = await c.ExecuteAsync(@"
            UPDATE qms_survey
            SET    committee_id = @committeeId, committee_name = @name,
                   committee_assigned_at = SYSUTCDATETIME(), committee_assigned_by = @user,
                   updated_at = SYSUTCDATETIME(), updated_by = @user
            WHERE  survey_id = @surveyId AND committee_id IS NULL AND status_code = 'Confirm'",
            new { committeeId = k.CommitteeId, name = k.CommitteeName, user, surveyId }, tx);
        if (changed == 0) return false;

        var copied = await c.ExecuteAsync(@"
            INSERT INTO qms_survey_member
                (survey_id, source_member_id, participant_type, user_id, member_name, department, organisation,
                 committee_role, email, phone, notify_in_app, notify_email, expected_attendance, sort_order)
            SELECT @surveyId, member_id, participant_type, user_id, member_name, department, organisation,
                   committee_role, email, phone, notify_in_app, notify_email, expected_attendance, sort_order
            FROM   qms_survey_committee_member
            WHERE  committee_id = @committeeId AND is_active = 1",
            new { surveyId, committeeId = k.CommitteeId }, tx);

        await _audit.WriteAsync(c, tx, EntityTypes.Survey, surveyId, ActionCodes.SurveyCommitteeAssigned,
            null, new { committee = k.CommitteeName, members = copied }, user);
        await _notifier.CommitteeAssignedAsync(c, tx, surveyId, user);
        return true;
    }

    public async Task<(bool ok, string? error)> AssignNowAsync(long surveyId, string user)
    {
        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();
        var s = await c.QuerySingleOrDefaultAsync<SurveyState>(@"
            SELECT s.status_code AS StatusCode, s.committee_id AS CommitteeId, a.plant AS Plant,
                   s.inspection_confirmed_at AS InspectionConfirmedAt
            FROM   qms_survey s JOIN qms_arrival a ON a.arrival_id = s.arrival_id
            WHERE  s.survey_id = @surveyId", new { surveyId }, tx);
        if (s == null) { tx.Rollback(); return (false, "Survey not found."); }
        if (s.StatusCode != SurveyStatus.Confirm) { tx.Rollback(); return (false, NotConfirmed); }
        if (s.CommitteeId.HasValue) { tx.Rollback(); return (false, "This survey already has its committee."); }
        if (!await AssignAsync(c, tx, surveyId, s.Plant, user))
        {
            tx.Rollback();
            return (false, "No committee is configured for this branch, and there is no default committee. " +
                           "Set one up under Parameters, Survey committees, then assign again.");
        }
        tx.Commit();
        return (true, null);
    }

    // ================= Per survey: reads =================

    public async Task<IReadOnlyList<SurveyMember>> GetMembersAsync(long surveyId, long? appointmentId)
    {
        using var c = Open();
        return (await c.QueryAsync<SurveyMember>(
            "SELECT m.survey_member_id AS SurveyMemberId, m.survey_id AS SurveyId, m.source_member_id AS SourceMemberId," +
            @"      m.participant_type AS ParticipantType, m.user_id AS UserId, m.member_name AS MemberName,
                    m.department AS Department, m.organisation AS Organisation, m.committee_role AS CommitteeRole,
                    m.email AS Email, m.phone AS Phone, m.notify_in_app AS NotifyInApp, m.notify_email AS NotifyEmail,
                    m.expected_attendance AS ExpectedAttendance, m.sort_order AS SortOrder,
                    t.attended AS Attended, t.attended_at AS AttendedAt, t.comments AS AttendanceComments
             FROM   qms_survey_member m
             LEFT   JOIN qms_survey_attendance t
                    ON t.survey_member_id = m.survey_member_id AND t.appointment_id = @appointmentId
             WHERE  m.survey_id = @surveyId
             ORDER  BY m.sort_order, m.survey_member_id", new { surveyId, appointmentId })).ToList();
    }

    private const string AppointmentColumns = @"
        appointment_id AS AppointmentId, survey_id AS SurveyId, seq_no AS SeqNo, planned_at AS PlannedAt,
        location AS Location, survey_company AS SurveyCompany, surveyor_name AS SurveyorName,
        surveyor_phone AS SurveyorPhone, surveyor_email AS SurveyorEmail, insurance_ref AS InsuranceRef,
        instructions AS Instructions, internal_comments AS InternalComments,
        schedule_confirmed_at AS ScheduleConfirmedAt, schedule_confirmed_by AS ScheduleConfirmedBy,
        superseded_at AS SupersededAt, superseded_by AS SupersededBy, supersede_reason AS SupersedeReason,
        actual_at AS ActualAt, actual_location AS ActualLocation, surveyor_attended AS SurveyorAttended,
        actual_surveyor_name AS ActualSurveyorName, attendance_details AS AttendanceDetails,
        attendance_comments AS AttendanceComments, attendance_entered_at AS AttendanceEnteredAt,
        attendance_entered_by AS AttendanceEnteredBy, attendance_confirmed_at AS AttendanceConfirmedAt,
        attendance_confirmed_by AS AttendanceConfirmedBy, created_at AS CreatedAt, created_by AS CreatedBy";

    public async Task<IReadOnlyList<SurveyAppointment>> GetAppointmentsAsync(long surveyId)
    {
        using var c = Open();
        return (await c.QueryAsync<SurveyAppointment>(
            "SELECT " + AppointmentColumns + @"
             FROM   qms_survey_appointment WHERE survey_id = @surveyId
             ORDER  BY seq_no DESC", new { surveyId })).ToList();
    }

    /// <summary>The appointment that stands, locked for the rest of the
    /// transaction so two people cannot change it past each other.</summary>
    private static Task<SurveyAppointment?> CurrentAsync(SqlConnection c, SqlTransaction tx, long surveyId) =>
        c.QuerySingleOrDefaultAsync<SurveyAppointment>(
            "SELECT " + AppointmentColumns + @"
             FROM   qms_survey_appointment WITH (UPDLOCK, HOLDLOCK)
             WHERE  survey_id = @surveyId AND superseded_at IS NULL", new { surveyId }, tx)!;

    private static async Task<bool> IsConfirmedAsync(SqlConnection c, SqlTransaction tx, long surveyId) =>
        await c.ExecuteScalarAsync<string?>(
            "SELECT status_code FROM qms_survey WITH (UPDLOCK) WHERE survey_id = @surveyId",
            new { surveyId }, tx) == SurveyStatus.Confirm;

    private static Task TouchAsync(SqlConnection c, SqlTransaction tx, long surveyId, string user) =>
        c.ExecuteAsync(
            "UPDATE qms_survey SET updated_at = SYSUTCDATETIME(), updated_by = @user WHERE survey_id = @surveyId",
            new { user, surveyId }, tx);

    // ================= Appointments =================

    private sealed record Planned(DateTime At, string? Location, string? Company, string? Surveyor, string? Phone,
        string? Email, string? InsuranceRef, string? Instructions, string? InternalComments);

    private static (Planned? value, string? error) Read(AppointmentInput i)
    {
        if (!i.PlannedDate.HasValue) return (null, "Enter the planned date.");
        var time = ParseTime(i.PlannedTime);
        if (time == null) return (null, "Enter the planned time.");
        var email = Clean(i.SurveyorEmail, 256);
        if (email != null && !email.Contains('@')) return (null, "The surveyor's e-mail address does not look right.");
        return (new Planned(i.PlannedDate.Value.Date + time.Value, Clean(i.Location, 200), Clean(i.SurveyCompany, 120),
            Clean(i.SurveyorName, 120), Clean(i.SurveyorPhone, 40), email, Clean(i.InsuranceRef, 80),
            Clean(i.Instructions, 1000), Clean(i.InternalComments, 1000)), null);
    }

    private static Task<long> InsertAppointmentAsync(SqlConnection c, SqlTransaction tx, long surveyId, Planned p,
        bool confirmed, string user) =>
        c.ExecuteScalarAsync<long>(@"
            INSERT INTO qms_survey_appointment
                (survey_id, seq_no, planned_at, location, survey_company, surveyor_name, surveyor_phone,
                 surveyor_email, insurance_ref, instructions, internal_comments,
                 schedule_confirmed_at, schedule_confirmed_by, created_by, updated_by)
            VALUES (@surveyId,
                    (SELECT ISNULL(MAX(seq_no), 0) + 1 FROM qms_survey_appointment WHERE survey_id = @surveyId),
                    @At, @Location, @Company, @Surveyor, @Phone, @Email, @InsuranceRef, @Instructions, @InternalComments,
                    CASE WHEN @confirmed = 1 THEN SYSUTCDATETIME() END, CASE WHEN @confirmed = 1 THEN @user END,
                    @user, @user);
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
            new
            {
                surveyId, p.At, p.Location, p.Company, p.Surveyor, p.Phone, p.Email, p.InsuranceRef,
                p.Instructions, p.InternalComments, confirmed, user
            }, tx);

    public async Task<(bool ok, string? error)> SaveAppointmentAsync(long surveyId, AppointmentInput input, string user)
    {
        var (p, error) = Read(input);
        if (p == null) return (false, error);

        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();
        if (!await IsConfirmedAsync(c, tx, surveyId)) { tx.Rollback(); return (false, NotConfirmed); }

        var cur = await CurrentAsync(c, tx, surveyId);
        if (cur == null)
        {
            var id = await InsertAppointmentAsync(c, tx, surveyId, p, confirmed: false, user);
            await _audit.WriteAsync(c, tx, EntityTypes.Survey, surveyId, ActionCodes.SurveyScheduled,
                null, new { appointment = id, planned_at = p.At, location = p.Location, surveyor = p.Surveyor }, user);
        }
        else
        {
            if (cur.AttendanceEntered)
            { tx.Rollback(); return (false, "Attendance has been recorded for this appointment. Use Reschedule to plan another visit."); }
            // Once people have been told a time, changing it is a reschedule,
            // so that they are told again and the old time stays on record.
            if (cur.ScheduleConfirmed && cur.PlannedAt != p.At)
            { tx.Rollback(); return (false, "The schedule is confirmed. Use Reschedule to change the date or time."); }
            await c.ExecuteAsync(@"
                UPDATE qms_survey_appointment
                SET    planned_at = @At, location = @Location, survey_company = @Company, surveyor_name = @Surveyor,
                       surveyor_phone = @Phone, surveyor_email = @Email, insurance_ref = @InsuranceRef,
                       instructions = @Instructions, internal_comments = @InternalComments,
                       updated_at = SYSUTCDATETIME(), updated_by = @user
                WHERE  appointment_id = @id",
                new
                {
                    p.At, p.Location, p.Company, p.Surveyor, p.Phone, p.Email, p.InsuranceRef,
                    p.Instructions, p.InternalComments, user, id = cur.AppointmentId
                }, tx);
            await _audit.WriteAsync(c, tx, EntityTypes.Survey, surveyId, ActionCodes.Updated,
                new { planned_at = cur.PlannedAt, location = cur.Location, surveyor = cur.SurveyorName },
                new { planned_at = p.At, location = p.Location, surveyor = p.Surveyor }, user);
        }
        await TouchAsync(c, tx, surveyId, user);
        tx.Commit();
        return (true, null);
    }

    public async Task<(bool ok, string? error)> ConfirmScheduleAsync(long surveyId, string user)
    {
        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();
        if (!await IsConfirmedAsync(c, tx, surveyId)) { tx.Rollback(); return (false, NotConfirmed); }
        var cur = await CurrentAsync(c, tx, surveyId);
        if (cur == null) { tx.Rollback(); return (false, "Save the appointment first."); }
        if (cur.ScheduleConfirmed) { tx.Rollback(); return (false, "This schedule is already confirmed."); }

        await c.ExecuteAsync(@"
            UPDATE qms_survey_appointment
            SET    schedule_confirmed_at = SYSUTCDATETIME(), schedule_confirmed_by = @user,
                   updated_at = SYSUTCDATETIME(), updated_by = @user
            WHERE  appointment_id = @id", new { user, id = cur.AppointmentId }, tx);
        await _audit.WriteAsync(c, tx, EntityTypes.Survey, surveyId, ActionCodes.SurveyScheduleConfirmed,
            null, new { appointment = cur.AppointmentId, planned_at = cur.PlannedAt }, user);
        // Confirming is what tells the participants.
        await _notifier.ScheduledAsync(c, tx, surveyId, cur.AppointmentId, rescheduled: false, user);
        await TouchAsync(c, tx, surveyId, user);
        tx.Commit();
        return (true, null);
    }

    public async Task<(bool ok, string? error)> RescheduleAsync(long surveyId, AppointmentInput input, string? reason, string user)
    {
        var (p, error) = Read(input);
        if (p == null) return (false, error);
        reason = Clean(reason, 500);
        if (reason == null || reason.Length < SurveyService.MinReasonLength)
            return (false, $"Say why the survey is being rescheduled (at least {SurveyService.MinReasonLength} characters).");

        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();
        if (!await IsConfirmedAsync(c, tx, surveyId)) { tx.Rollback(); return (false, NotConfirmed); }
        var cur = await CurrentAsync(c, tx, surveyId);
        if (cur == null) { tx.Rollback(); return (false, "There is no appointment to reschedule. Save one first."); }

        // The old appointment is kept, with why it was replaced and whatever
        // attendance was recorded against it: the rows are the history.
        await c.ExecuteAsync(@"
            UPDATE qms_survey_appointment
            SET    superseded_at = SYSUTCDATETIME(), superseded_by = @user, supersede_reason = @reason,
                   updated_at = SYSUTCDATETIME(), updated_by = @user
            WHERE  appointment_id = @id", new { user, reason, id = cur.AppointmentId }, tx);
        var newId = await InsertAppointmentAsync(c, tx, surveyId, p, confirmed: true, user);

        await _audit.WriteAsync(c, tx, EntityTypes.Survey, surveyId, ActionCodes.SurveyRescheduled,
            new { appointment = cur.AppointmentId, planned_at = cur.PlannedAt },
            new { appointment = newId, planned_at = p.At, reason }, user);
        await _notifier.ScheduledAsync(c, tx, surveyId, newId, rescheduled: true, user);
        await TouchAsync(c, tx, surveyId, user);
        tx.Commit();
        return (true, null);
    }

    // ================= Attendance =================

    public async Task<(bool ok, string? error)> SaveAttendanceAsync(long surveyId, AttendanceInput input, string user)
    {
        var surveyorAttended = YesNo(input.SurveyorAttended);
        if (surveyorAttended == null) return (false, "Say whether the surveyor attended.");
        if (!input.ActualDate.HasValue) return (false, "Enter the date of the visit.");
        var time = ParseTime(input.ActualTime);
        if (time == null) return (false, "Enter the time of the visit.");
        var actualAt = input.ActualDate.Value.Date + time.Value;

        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();
        if (!await IsConfirmedAsync(c, tx, surveyId)) { tx.Rollback(); return (false, NotConfirmed); }
        var cur = await CurrentAsync(c, tx, surveyId);
        if (cur == null) { tx.Rollback(); return (false, "There is no appointment to record attendance for. Schedule one first."); }
        if (cur.AttendanceConfirmed)
        { tx.Rollback(); return (false, "This attendance record is confirmed and can no longer be changed."); }

        var first = !cur.AttendanceEntered;
        await c.ExecuteAsync(@"
            UPDATE qms_survey_appointment
            SET    actual_at = @actualAt, actual_location = @location, surveyor_attended = @surveyorAttended,
                   actual_surveyor_name = @surveyor, attendance_details = @details, attendance_comments = @comments,
                   attendance_entered_at = COALESCE(attendance_entered_at, SYSUTCDATETIME()),
                   attendance_entered_by = COALESCE(attendance_entered_by, @user),
                   updated_at = SYSUTCDATETIME(), updated_by = @user
            WHERE  appointment_id = @id",
            new
            {
                actualAt, location = Clean(input.ActualLocation, 200), surveyorAttended,
                surveyor = Clean(input.ActualSurveyorName, 120), details = Clean(input.AttendanceDetails, 1000),
                comments = Clean(input.AttendanceComments, 1000), user, id = cur.AppointmentId
            }, tx);

        // One row per member of THIS survey. A member left unanswered stays
        // "not recorded" (NULL), which is not the same as absent.
        var memberIds = (await c.QueryAsync<long>(
            "SELECT survey_member_id FROM qms_survey_member WHERE survey_id = @surveyId", new { surveyId }, tx)).ToHashSet();
        var present = 0; var absent = 0;
        foreach (var m in input.Members.Where(m => memberIds.Contains(m.SurveyMemberId))
                                       .GroupBy(m => m.SurveyMemberId).Select(g => g.Last()))
        {
            var attended = YesNo(m.Attended);
            if (attended == true) present++; else if (attended == false) absent++;
            await c.ExecuteAsync(@"
                MERGE qms_survey_attendance AS t
                USING (SELECT @appointmentId AS appointment_id, @memberId AS survey_member_id) AS s
                   ON t.appointment_id = s.appointment_id AND t.survey_member_id = s.survey_member_id
                WHEN MATCHED THEN
                    UPDATE SET attended = @attended, comments = @comments,
                               attended_at = CASE WHEN @attended = 1 THEN @actualAt END
                WHEN NOT MATCHED THEN
                    INSERT (appointment_id, survey_member_id, attended, attended_at, comments)
                    VALUES (@appointmentId, @memberId, @attended, CASE WHEN @attended = 1 THEN @actualAt END, @comments);",
                new
                {
                    appointmentId = cur.AppointmentId, memberId = m.SurveyMemberId, attended,
                    comments = Clean(m.Comments, 500), actualAt
                }, tx);
        }

        await _audit.WriteAsync(c, tx, EntityTypes.Survey, surveyId,
            first ? ActionCodes.SurveyAttendanceRecorded : ActionCodes.Updated,
            first ? null : new { actual_at = cur.ActualAt, surveyor_attended = cur.SurveyorAttended },
            new { appointment = cur.AppointmentId, actual_at = actualAt, surveyor_attended = surveyorAttended, present, absent },
            user);
        await _notifier.AttendanceRecordedAsync(c, tx, surveyId, cur.AppointmentId, user);
        await TouchAsync(c, tx, surveyId, user);
        tx.Commit();
        return (true, null);
    }

    public async Task<(bool ok, string? error)> ConfirmAttendanceAsync(long surveyId, string user)
    {
        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();
        if (!await IsConfirmedAsync(c, tx, surveyId)) { tx.Rollback(); return (false, NotConfirmed); }
        var cur = await CurrentAsync(c, tx, surveyId);
        if (cur == null || !cur.AttendanceEntered) { tx.Rollback(); return (false, "Record the attendance first."); }
        if (cur.AttendanceConfirmed) { tx.Rollback(); return (false, "This attendance record is already confirmed."); }

        await c.ExecuteAsync(@"
            UPDATE qms_survey_appointment
            SET    attendance_confirmed_at = SYSUTCDATETIME(), attendance_confirmed_by = @user,
                   updated_at = SYSUTCDATETIME(), updated_by = @user
            WHERE  appointment_id = @id", new { user, id = cur.AppointmentId }, tx);
        await _audit.WriteAsync(c, tx, EntityTypes.Survey, surveyId, ActionCodes.SurveyAttendanceConfirmed,
            null, new { appointment = cur.AppointmentId }, user);
        await TouchAsync(c, tx, surveyId, user);
        tx.Commit();
        return (true, null);
    }

    // ================= Inspection confirmation =================

    public async Task<(bool ok, string? error)> ConfirmInspectionAsync(long surveyId, InspectionInput input, string user)
    {
        // This is a confirmation that the inspection HAPPENED, so both answers
        // must be yes. A surveyor who did not come is recorded under Attendance.
        if (YesNo(input.SurveyorAttended) != true || YesNo(input.ShipmentInspected) != true)
            return (false, "Confirm the inspection only once the surveyor has attended and inspected the shipment. " +
                           "If the surveyor did not come, record that under Attendance and reschedule.");
        if (!input.InspectionDate.HasValue) return (false, "Enter the date of the inspection.");
        var start = ParseTime(input.StartTime);
        if (start == null) return (false, "Enter the time the inspection started.");
        var end = ParseTime(input.EndTime);
        if (!string.IsNullOrWhiteSpace(input.EndTime) && end == null) return (false, "The end time does not look right.");
        if (end.HasValue && end < start) return (false, "The inspection cannot end before it starts.");

        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();
        // Confirmed survey, and not confirmed before: the WHERE is the rule.
        var changed = await c.ExecuteAsync(@"
            UPDATE qms_survey
            SET    surveyor_attended = 1, shipment_inspected = 1,
                   inspection_date = @date, inspection_start_time = @start, inspection_end_time = @end,
                   inspection_observations = @observations, inspection_findings = @findings,
                   inspection_damage = @damage, inspection_comments = @comments,
                   inspection_confirmed_at = SYSUTCDATETIME(), inspection_confirmed_by = @user,
                   updated_at = SYSUTCDATETIME(), updated_by = @user
            WHERE  survey_id = @surveyId AND status_code = 'Confirm' AND inspection_confirmed_at IS NULL",
            new
            {
                date = input.InspectionDate.Value.Date, start, end,
                observations = Clean(input.Observations, 2000), findings = Clean(input.Findings, 2000),
                damage = Clean(input.Damage, 2000), comments = Clean(input.Comments, 2000), user, surveyId
            }, tx);
        if (changed == 0)
        {
            var s = await c.QuerySingleOrDefaultAsync<SurveyState>(@"
                SELECT status_code AS StatusCode, inspection_confirmed_at AS InspectionConfirmedAt
                FROM   qms_survey WHERE survey_id = @surveyId", new { surveyId }, tx);
            tx.Rollback();
            if (s == null) return (false, "Survey not found.");
            return (false, s.StatusCode != SurveyStatus.Confirm
                ? NotConfirmed
                : "The inspection has already been confirmed for this survey.");
        }

        await _audit.WriteAsync(c, tx, EntityTypes.Survey, surveyId, ActionCodes.SurveyInspectionConfirmed,
            null, new { inspection_date = input.InspectionDate.Value.Date, findings = Clean(input.Findings, 2000) }, user);
        await _notifier.InspectionConfirmedAsync(c, tx, surveyId, user);
        tx.Commit();
        return (true, null);
    }
}
