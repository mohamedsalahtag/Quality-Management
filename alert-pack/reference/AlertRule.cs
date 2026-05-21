// Domain models for the alert-pack. Drop into your project's Models/ folder
// (or merge with your existing model files). Field names are intentionally
// neutral so the same shape covers vehicles / contracts / certificates / etc.

namespace YourApp.Models;

public class AlertRule
{
    public int       AlertId         { get; set; }
    public string    Name            { get; set; } = "";
    public int       GroupId         { get; set; }

    // Three CSV "axes". The IAlertDataSource consumes these to filter records.
    // For Fleet:    PrimaryFilter = "Passenger,Trucks"
    //              SecondaryFilter = "MVPI,Registration,OperationCards"
    // For HR:       PrimaryFilter = "Employee,Contractor"
    //              SecondaryFilter = "VisaExpiry,ContractEnd,InsuranceRenewal"
    public string    PrimaryFilter   { get; set; } = "";
    public string    SecondaryFilter { get; set; } = "";
    public string    Severities      { get; set; } = "";   // "Red,Yellow"

    public string    Subject         { get; set; } = "Expiry alert";
    public string    Schedule        { get; set; } = AlertSchedules.Daily;
    public int       ScheduleN       { get; set; } = 7;
    public bool      IsActive        { get; set; } = true;
    public DateTime? LastSentAt      { get; set; }
    public DateTime? LastSentDate    { get; set; }
    public bool      OnceSent        { get; set; }
    public DateTime  CreatedAt       { get; set; }
    public int?      CreatedBy       { get; set; }

    // Populated by JOIN on the groups table.
    public string?   GroupName       { get; set; }
}

public class AlertMatch
{
    public string    PrimaryType   { get; set; } = "";   // e.g. "Passenger" / "Contractor"
    public string    SecondaryType { get; set; } = "";   // e.g. "MVPI" / "VisaExpiry"
    public string    Identifier    { get; set; } = "";   // e.g. plate # / contract code
    public string    Label         { get; set; } = "";   // human-readable display
    public string    DueRaw        { get; set; } = "";   // original date string
    public DateTime? DueDate       { get; set; }         // parsed; null if can't parse
    public string    Severity      { get; set; } = "";   // AlertSeverities.Red or .Yellow
    public int       DaysFromToday { get; set; }
    public string    Owner         { get; set; } = "";
    public string    Branch        { get; set; } = "";
}

public class EmailGroupAddress
{
    public int       AddressId    { get; set; }
    public int       GroupId      { get; set; }
    public string    EmailAddress { get; set; } = "";
    public string    Recipient    { get; set; } = "To";  // "To" or "CC"
    public DateTime  AddedAt      { get; set; }
    public int?      AddedBy      { get; set; }
}

public static class AlertSeverities { public const string Red = "Red"; public const string Yellow = "Yellow"; }
public static class AlertSchedules  { public const string Daily = "Daily"; public const string EveryN = "EveryN"; public const string Once = "Once"; }
