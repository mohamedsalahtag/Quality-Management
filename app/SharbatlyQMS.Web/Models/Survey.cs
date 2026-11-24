namespace SharbatlyQMS.Web.Models;

/// <summary>
/// The three, and only three, states of a survey. Scheduling, attendance and
/// the surveyor's inspection are facts recorded ABOUT a confirmed survey; they
/// never become a status, so a survey that has been carried out is still
/// "Confirm".
/// </summary>
public static class SurveyStatus
{
    public const string Open    = "Open";
    public const string Confirm = "Confirm";
    public const string Pass    = "Pass";

    public static readonly string[] All = { Open, Confirm, Pass };

    public static string Label(string? s) => s switch
    {
        Open    => "Open",
        Confirm => "Confirmed",
        Pass    => "Passed",
        _       => s ?? ""
    };

    public static string BadgeCss(string? s) => s switch
    {
        Open    => "bg-warning text-dark",
        Confirm => "bg-danger",
        Pass    => "bg-secondary",
        _       => "bg-light text-dark"
    };

    /// <summary>The red UNDER SURVEY alert shows for these two and no others. A
    /// confirmed survey keeps it for good, whatever has happened since.</summary>
    public static bool IsUnderSurvey(string? s) => s == Open || s == Confirm;
}

public static class SurveySource
{
    public const string Arrival      = "Arrival";
    public const string QualityOrder = "QualityOrder";

    public static string Label(string? s) => s switch
    {
        Arrival      => "Arrival",
        QualityOrder => "QC order",
        _            => s ?? ""
    };
}

public static class SurveyMethod
{
    public const string Manual    = "Manual";
    /// <summary>Reserved. Nothing writes it yet.</summary>
    public const string RuleBased = "RuleBased";
}

/// <summary>What the arrival and quality order pages need to show a survey
/// exists: its number and whether the alert is on.</summary>
public class SurveyBrief
{
    public long   SurveyId   { get; set; }
    public string SurveyNo   { get; set; } = "";
    public string StatusCode { get; set; } = "";
    public bool   IsUnderSurvey => SurveyStatus.IsUnderSurvey(StatusCode);
}

/// <summary>One survey with the shipment it is about. Shipment facts are read
/// through the arrival, never copied onto the survey.</summary>
public class Survey
{
    public long      SurveyId         { get; set; }
    public string    SurveyNo         { get; set; } = "";
    public long      ArrivalId        { get; set; }
    public string    SourceCode       { get; set; } = "";
    public long?     SourceQoId       { get; set; }
    public string?   SourceQoNo       { get; set; }
    public string    MethodCode       { get; set; } = SurveyMethod.Manual;
    public string    StatusCode       { get; set; } = SurveyStatus.Open;
    public string    RequestReason    { get; set; } = "";
    public string?   RequestComments  { get; set; }
    public DateTime  RequestedAt      { get; set; }
    public string    RequestedBy      { get; set; } = "";
    public DateTime? DecidedAt        { get; set; }
    public string?   DecidedBy        { get; set; }
    public string?   DecisionComments { get; set; }
    public string?   FollowupError    { get; set; }
    public DateTime? FollowupFailedAt { get; set; }
    public DateTime  UpdatedAt        { get; set; }
    public string    UpdatedBy        { get; set; } = "";

    // ---- The shipment, through the arrival ----
    public string    ArrivalNo        { get; set; } = "";
    public string?   ArrivalStatus    { get; set; }
    public string?   ContainerNo      { get; set; }
    public string?   BolNo            { get; set; }
    public string?   Ebeln            { get; set; }
    public string?   VendorNo         { get; set; }
    public string?   VendorName       { get; set; }
    public string?   Plant            { get; set; }
    public DateTime? ReceiveDate      { get; set; }
    /// <summary>Distinct product descriptions on the arrival, newline-separated.</summary>
    public string?   Products         { get; set; }
    public int       ProductCount     { get; set; }
    /// <summary>Countries of origin from the material master, comma-separated.</summary>
    public string?   Origins          { get; set; }

    // ---- The quality order that speaks for the container NOW ----
    public long?     QualityOrderId     { get; set; }
    public string?   QualityOrderNo     { get; set; }
    public string?   QualityOrderStatus { get; set; }
    public DateTime? QoArchivedAt       { get; set; }

    // ---- Its claim, when there is one ----
    public long?     ClaimId        { get; set; }
    public string?   ClaimStatus    { get; set; }
    public DateTime? ClaimCreatedAt { get; set; }

    public bool IsOpen        => StatusCode == SurveyStatus.Open;
    public bool IsConfirmed   => StatusCode == SurveyStatus.Confirm;
    public bool IsUnderSurvey => SurveyStatus.IsUnderSurvey(StatusCode);
}

public class SurveyListRow : Survey
{
    /// <summary>Size of the whole filtered set, carried on every row.</summary>
    public int TotalCount { get; set; }
}

public record SurveyPage(IReadOnlyList<SurveyListRow> Rows, int Total, int Page, int PageSize);

/// <summary>Milestone chips on the Survey tab. These are computed from the
/// survey and its linked records; none of them is a status.</summary>
public static class SurveyMilestone
{
    /// <summary>Confirmed, and the arrival has no quality order yet.</summary>
    public const string AwaitingQcOrder = "awaitingqo";
    /// <summary>Confirmed, and a claim request exists on its quality order.</summary>
    public const string WithClaim       = "withclaim";
}

public class SurveyListFilter
{
    public List<string> Status   { get; set; } = new();
    public string?      Source   { get; set; }
    public string?      Milestone{ get; set; }
    public string?      Search   { get; set; }
    public List<string> Plant    { get; set; } = new();
    public List<string> Supplier { get; set; } = new();
    /// <summary>Requested on or after this local day.</summary>
    public DateTime?    From     { get; set; }
    /// <summary>Requested on or before this local day.</summary>
    public DateTime?    To       { get; set; }
    public int          Page     { get; set; } = 1;
    public int          PageSize { get; set; } = 50;

    public int PanelFilterCount =>
        (Plant.Count > 0 ? 1 : 0) + (Supplier.Count > 0 ? 1 : 0) +
        (From.HasValue ? 1 : 0) + (To.HasValue ? 1 : 0) +
        (string.IsNullOrWhiteSpace(Source) ? 0 : 1);
    public bool AnyPanelFilter => PanelFilterCount > 0;
    public bool Any => AnyPanelFilter || Status.Count > 0
        || !string.IsNullOrWhiteSpace(Search) || !string.IsNullOrWhiteSpace(Milestone);
}

public class SurveyFilterOptions
{
    public IReadOnlyList<string> Plants    { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Suppliers { get; set; } = Array.Empty<string>();
}

/// <summary>The indicator tiles above the list, for the caller's branches.</summary>
public class SurveyCounts
{
    public int Total           { get; set; }
    public int Open            { get; set; }
    public int Confirmed       { get; set; }
    public int Passed          { get; set; }
    public int AwaitingQcOrder { get; set; }
    public int WithClaim       { get; set; }
}

/// <summary>
/// Outcome of asking for a survey. <see cref="AlreadyExisted"/> is not a
/// failure: the arrival already has its one survey, and the caller is handed
/// that one so it can show it.
/// </summary>
public record SurveyRequestResult(
    bool Ok, bool AlreadyExisted, long? SurveyId, string? SurveyNo, string? StatusCode, string? Error)
{
    public static SurveyRequestResult Fail(string error) => new(false, false, null, null, null, error);
}

/// <summary>One QC result at or over its tolerance, as the survey page lists it.</summary>
public record SurveyToleranceLine(
    string Group, string Item, bool IsCategoryTotal, decimal Result, decimal Tolerance)
{
    public decimal Over => Result - Tolerance;
}
