namespace SharbatlyQMS.Web.Models;

/// <summary>What a member is on the committee FOR. There are no teams or
/// departments in QMS, so this is how "the insurance people" are told apart
/// from "the claims people" when a notice is addressed to one of them.</summary>
public static class CommitteeRole
{
    public const string Insurance = "Insurance";
    public const string Claims    = "Claims";
    public const string QC        = "QC";
    public const string Surveyor  = "Surveyor";
    public const string Other     = "Other";

    public static readonly string[] All = { Insurance, Claims, QC, Surveyor, Other };

    public static string Label(string? r) => r switch
    {
        Insurance => "Insurance",
        Claims    => "Claims",
        QC        => "QC",
        Surveyor  => "Surveyor",
        Other     => "Other",
        _         => r ?? ""
    };
}

public static class ParticipantType
{
    /// <summary>An application user. Reached through their account.</summary>
    public const string Internal = "Internal";
    /// <summary>Somebody outside: no account, no access, a typed name and address.</summary>
    public const string External = "External";
}

/// <summary>A configured committee (Parameters -> Survey committees).</summary>
public class SurveyCommittee
{
    public int      CommitteeId   { get; set; }
    public string   CommitteeName { get; set; } = "";
    public string?  Description   { get; set; }
    /// <summary>Null = the default committee, for any branch without its own.</summary>
    public string?  Plant         { get; set; }
    public bool     IsActive      { get; set; } = true;
    public DateTime UpdatedAt     { get; set; }
    public string   UpdatedBy     { get; set; } = "";
    public List<SurveyCommitteeMember> Members { get; set; } = new();
}

/// <summary>The fields a configured member and an assigned member share.</summary>
public class CommitteePerson
{
    public string  ParticipantType    { get; set; } = Models.ParticipantType.Internal;
    public int?    UserId             { get; set; }
    public string  MemberName         { get; set; } = "";
    public string? Department         { get; set; }
    public string? Organisation       { get; set; }
    public string  CommitteeRole      { get; set; } = Models.CommitteeRole.Other;
    public string? Email              { get; set; }
    public string? Phone              { get; set; }
    public bool    NotifyInApp        { get; set; } = true;
    public bool    NotifyEmail        { get; set; } = true;
    public bool    ExpectedAttendance { get; set; } = true;
    public int     SortOrder          { get; set; }

    public bool IsInternal => ParticipantType == Models.ParticipantType.Internal;
}

public class SurveyCommitteeMember : CommitteePerson
{
    public int  MemberId    { get; set; }
    public int  CommitteeId { get; set; }
    public bool IsActive    { get; set; } = true;
}

/// <summary>
/// A member as COPIED onto a survey when it was confirmed. Editing or
/// removing the configured member afterwards does not change this row.
/// The attendance fields are that member's record for one appointment.
/// </summary>
public class SurveyMember : CommitteePerson
{
    public long      SurveyMemberId { get; set; }
    public long      SurveyId       { get; set; }
    public int?      SourceMemberId { get; set; }

    /// <summary>Null = not recorded; false = recorded as absent.</summary>
    public bool?     Attended           { get; set; }
    public DateTime? AttendedAt         { get; set; }
    public string?   AttendanceComments { get; set; }
}

public class SurveyAppointment
{
    public long      AppointmentId         { get; set; }
    public long      SurveyId              { get; set; }
    public int       SeqNo                 { get; set; }
    /// <summary>Local wall-clock time, as typed.</summary>
    public DateTime  PlannedAt             { get; set; }
    public string?   Location              { get; set; }
    public string?   SurveyCompany         { get; set; }
    public string?   SurveyorName          { get; set; }
    public string?   SurveyorPhone         { get; set; }
    public string?   SurveyorEmail         { get; set; }
    public string?   InsuranceRef          { get; set; }
    public string?   Instructions          { get; set; }
    public string?   InternalComments      { get; set; }
    public DateTime? ScheduleConfirmedAt   { get; set; }
    public string?   ScheduleConfirmedBy   { get; set; }
    public DateTime? SupersededAt          { get; set; }
    public string?   SupersededBy          { get; set; }
    public string?   SupersedeReason       { get; set; }
    public DateTime? ActualAt              { get; set; }
    public string?   ActualLocation        { get; set; }
    public bool?     SurveyorAttended      { get; set; }
    public string?   ActualSurveyorName    { get; set; }
    public string?   AttendanceDetails     { get; set; }
    public string?   AttendanceComments    { get; set; }
    public DateTime? AttendanceEnteredAt   { get; set; }
    public string?   AttendanceEnteredBy   { get; set; }
    public DateTime? AttendanceConfirmedAt { get; set; }
    public string?   AttendanceConfirmedBy { get; set; }
    public DateTime  CreatedAt             { get; set; }
    public string    CreatedBy             { get; set; } = "";

    public bool IsCurrent           => !SupersededAt.HasValue;
    public bool ScheduleConfirmed   => ScheduleConfirmedAt.HasValue;
    public bool AttendanceEntered   => AttendanceEnteredAt.HasValue;
    public bool AttendanceConfirmed => AttendanceConfirmedAt.HasValue;
}

// ---- What the forms post ----------------------------------------------

public class AppointmentInput
{
    public DateTime? PlannedDate      { get; set; }
    /// <summary>"HH:mm" from a time input.</summary>
    public string?   PlannedTime      { get; set; }
    public string?   Location         { get; set; }
    public string?   SurveyCompany    { get; set; }
    public string?   SurveyorName     { get; set; }
    public string?   SurveyorPhone    { get; set; }
    public string?   SurveyorEmail    { get; set; }
    public string?   InsuranceRef     { get; set; }
    public string?   Instructions     { get; set; }
    public string?   InternalComments { get; set; }
}

public class AttendanceInput
{
    public DateTime? ActualDate         { get; set; }
    public string?   ActualTime         { get; set; }
    public string?   ActualLocation     { get; set; }
    /// <summary>"true" / "false"; anything else is unanswered.</summary>
    public string?   SurveyorAttended   { get; set; }
    public string?   ActualSurveyorName { get; set; }
    public string?   AttendanceDetails  { get; set; }
    public string?   AttendanceComments { get; set; }
    public List<MemberAttendanceInput> Members { get; set; } = new();
}

public class MemberAttendanceInput
{
    public long    SurveyMemberId { get; set; }
    /// <summary>"true" = present, "false" = absent, "" = not recorded.</summary>
    public string? Attended       { get; set; }
    public string? Comments       { get; set; }
}

public class InspectionInput
{
    public string?   SurveyorAttended  { get; set; }
    public string?   ShipmentInspected { get; set; }
    public DateTime? InspectionDate    { get; set; }
    public string?   StartTime         { get; set; }
    public string?   EndTime           { get; set; }
    public string?   Observations      { get; set; }
    public string?   Findings          { get; set; }
    public string?   Damage            { get; set; }
    public string?   Comments          { get; set; }
}

/// <summary>An application user offered as an internal committee member.</summary>
public class CommitteeUserOption
{
    public int     UserId     { get; set; }
    public string  Username   { get; set; } = "";
    public string? FullName   { get; set; }
    public string? Email      { get; set; }
    public string? Department { get; set; }
    public string? RoleName   { get; set; }
}

/// <summary>Document categories offered for a survey's attachments.</summary>
public static class SurveyDocCategory
{
    public const string Request      = "SurveyRequest";
    public const string SurveyReport = "SurveyReport";
    public const string Photo        = "SurveyPhoto";
    public const string Appointment  = "SurveyAppointment";
    public const string Attendance   = "SurveyAttendance";
    public const string Inspection   = "SurveyInspection";
    public const string Other        = "SurveyOther";

    public static readonly (string Code, string Label)[] All =
    {
        (SurveyReport, "Survey report"),
        (Photo,        "Photograph"),
        (Request,      "Request evidence"),
        (Appointment,  "Appointment"),
        (Attendance,   "Attendance"),
        (Inspection,   "Inspection"),
        (Other,        "Other"),
    };

    public static string Label(string? code) =>
        All.FirstOrDefault(c => c.Code == code).Label ?? (string.IsNullOrWhiteSpace(code) ? "Other" : code!);

    public static bool IsValid(string? code) => All.Any(c => c.Code == code);
}
