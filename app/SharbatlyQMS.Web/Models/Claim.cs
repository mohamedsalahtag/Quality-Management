namespace SharbatlyQMS.Web.Models;

/// <summary>
/// Post-QC commercial claim attached to a Closed Quality Order. Named
/// QualityClaim (not Claim) so it does not collide with the auth-system
/// type System.Security.Claims.Claim which is used pervasively.
/// </summary>
public class QualityClaim
{
    public long      ClaimId         { get; set; }
    public long      QualityOrderId  { get; set; }
    public string    ClaimStatus     { get; set; } = "";
    public DateTime  CreatedAt       { get; set; }
    public string    CreatedBy       { get; set; } = "";
    public DateTime  LastChangedAt   { get; set; }
    public string    LastChangedBy   { get; set; } = "";
    public DateTime? DecidedAt       { get; set; }
    public string?   DecidedBy       { get; set; }
}

public class ClaimNote
{
    public long     NoteId        { get; set; }
    public long     ClaimId       { get; set; }
    public string   NoteText      { get; set; } = "";
    public string   NoteKind      { get; set; } = ClaimNoteKind.Comment;
    public string?  StatusAtPost  { get; set; }
    public DateTime CreatedAt     { get; set; }
    public string   CreatedBy     { get; set; } = "";
    public string   AuthorRole    { get; set; } = "";
}

public static class ClaimNoteKind
{
    public const string StatusChange = "StatusChange";
    public const string Comment      = "Comment";
}

public static class ClaimStatus
{
    public const string ClaimRequest         = "ClaimRequest";
    public const string PassedQC             = "PassedQC";
    public const string ClaimRequestApproved = "ClaimRequestApproved";
    public const string HoldClaim            = "HoldClaim";

    /// <summary>UI-only sentinel for a Closed QO that has no qms_claim row yet.</summary>
    public const string Pending              = "Pending";

    public static string Label(string? s) => s switch
    {
        ClaimRequest         => "Claim Request",
        PassedQC             => "Passed QC",
        ClaimRequestApproved => "Claim Request Approved",
        HoldClaim            => "Hold Claim",
        Pending              => "Pending",
        null or ""           => "Pending",
        _                    => s
    };

    // Each status pairs a background and a foreground colour. The custom
    // `.bubble-status-pill` class used in _ClaimChatPanel does NOT
    // auto-pair them (unlike Bootstrap's `.badge`), so omitting the
    // text-* utility leaves the label illegible inside dark bubbles.
    public static string BadgeCss(string? s) => s switch
    {
        ClaimRequest         => "bg-danger text-white",
        ClaimRequestApproved => "bg-dark text-white",
        HoldClaim            => "bg-warning text-dark",
        PassedQC             => "bg-success text-white",
        _                    => "bg-secondary text-white"
    };
}

/// <summary>Row shape for the Claim Management list page.</summary>
public class ClaimListRow
{
    public long      QualityOrderId { get; set; }
    public string    QualityOrderNo { get; set; } = "";
    public long      ArrivalId      { get; set; }
    public string?   ArrivalNo      { get; set; }
    public string?   ContainerNo    { get; set; }
    public string?   BolNo          { get; set; }
    public string?   Ebeln          { get; set; }
    public string?   VendorName     { get; set; }
    public DateTime? ClosedAt       { get; set; }
    public string?   ClosedBy       { get; set; }

    // From qms_claim (NULL when no claim row yet -- treat as Pending in UI).
    public string?   ClaimStatus    { get; set; }
    public DateTime? LastActivityAt { get; set; }
    public DateTime? DecidedAt      { get; set; }
    public int       NoteCount      { get; set; }
    public int       UnreadCount    { get; set; }   // notes added after this user's last_seen, authored by someone else
}
