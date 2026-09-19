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

/// <summary>
/// Claim workflow statuses. The CODES below are what the database stores (and
/// what qms_claim's CHECK constraint allows) -- they are deliberately NOT
/// renamed. Only the LABELS changed (2026-08-30), to match how the business
/// actually describes the process: this workflow notifies a supplier of a
/// problem, it does not by itself lodge or approve a claim.
///
///   ClaimRequest         -> "Claim Notification Request"   (was "Claim Request")
///   ClaimRequestApproved -> "Claim Notification Reviewed"  (was "Claim Request Approved")
///   HoldClaim            -> "Hold Notification"            (was "Hold Claim")
///   PassedQC             -> "Passed QC"                    (unchanged)
///
/// Renaming the codes would mean an UPDATE across qms_claim +
/// qms_claim_note.status_at_post and a new CHECK constraint, for no gain: no
/// user ever sees a code.
/// </summary>
public static class ClaimStatus
{
    public const string ClaimRequest         = "ClaimRequest";
    public const string PassedQC             = "PassedQC";
    public const string ClaimRequestApproved = "ClaimRequestApproved";
    public const string HoldClaim            = "HoldClaim";

    /// <summary>
    /// An administrator judged the inspection unsound and had the container
    /// inspected again.
    ///
    /// The claim MOVES to the reinspection and carries this status: one
    /// container, one conversation. The superseded original keeps its report
    /// but ends up with no claim of its own, because it is a reference document
    /// rather than a live commercial position.
    ///
    /// It means two different things depending on whether the second inspection
    /// has finished -- see <see cref="LabelFor"/>.
    /// </summary>
    public const string Reinspection         = "Reinspection";

    // There is deliberately NO "Archived" status. Archiving is a flag on the
    // quality order (qms_quality_order.archived_at, M20), not a claim decision
    // -- an order carrying a real decision can be archived without losing it,
    // and an order that was never Closed can be archived at all. M17's
    // status-based approach could do neither.

    /// <summary>UI-only sentinel for a Closed QO that has no qms_claim row yet.</summary>
    public const string Pending              = "Pending";

    public static string Label(string? s) => s switch
    {
        ClaimRequest         => "Claim Notification Request",
        PassedQC             => "Passed QC",
        ClaimRequestApproved => "Claim Notification Reviewed",
        HoldClaim            => "Hold Notification",
        Reinspection         => "Sent for Reinspection",
        Pending              => "Pending",
        null or ""           => "Pending",
        _                    => s
    };

    /// <summary>Compact form for the Claims GRID only — the full names run to 26
    /// characters ("Claim Notification Request"), which is a column's worth of
    /// width on a table that already scrolls sideways. Everywhere a status is
    /// read on its own (chat panel, chips, badges on the claim page) keeps
    /// <see cref="Label"/>; the grid puts the full wording in the cell tooltip.</summary>
    public static string ShortLabel(string? s) => s switch
    {
        ClaimRequest         => "Notification Req.",
        PassedQC             => "Passed QC",
        ClaimRequestApproved => "Reviewed",
        HoldClaim            => "Hold",
        Reinspection         => "Reinspection",
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
        // Its own colour: this is not a claim verdict at all, it says the
        // inspection behind the verdict is being redone.
        Reinspection         => "bg-info text-dark",
        PassedQC             => "bg-success text-white",
        _                    => "bg-secondary text-white"
    };

    // ---- 'Reinspection' describes two different situations ----------------
    //
    // The code is one value, but the row it labels is in one of two states, and
    // telling them apart is the whole question a claim desk asks of a worklist:
    // is this waiting on the warehouse, or on me?
    //
    //   Reinspection + order not yet Closed -> the second inspection is still
    //       being carried out. Nobody can act. "Under reinspection".
    //   Reinspection + order Closed         -> the second inspection is
    //       finished and nobody has judged it yet. Actionable.
    //
    // Derived from (claim_status, qo.status_code) rather than stored. A sixth
    // code would have to be written by whoever CLOSES a quality order -- a claim
    // write on a path that has nothing to do with claims -- and it would be
    // wrong again the moment a reinspection is reopened.
    //
    // The single-argument Label / ShortLabel / BadgeCss above are deliberately
    // left alone: a chat bubble's status pill records what the status WAS when
    // the note was posted, and re-labelling history by today's order status
    // would be a lie.

    public static bool IsUnderReinspection(string? claimStatus, string? qoStatusCode) =>
        claimStatus == Reinspection && qoStatusCode != QualityOrderStatus.Closed;

    public static string LabelFor(string? claimStatus, string? qoStatusCode) => claimStatus switch
    {
        Reinspection when IsUnderReinspection(claimStatus, qoStatusCode) => "Under reinspection",
        Reinspection                                                     => "Reinspected — awaiting QC decision",
        _                                                                => Label(claimStatus)
    };

    public static string ShortLabelFor(string? claimStatus, string? qoStatusCode) => claimStatus switch
    {
        Reinspection when IsUnderReinspection(claimStatus, qoStatusCode) => "Reinspecting",
        Reinspection                                                     => "Awaiting QC",
        _                                                                => ShortLabel(claimStatus)
    };

    /// <summary>Muted while the work is somebody else's; the full Reinspection
    /// colour once it is back on the claim desk's own plate.</summary>
    public static string BadgeCssFor(string? claimStatus, string? qoStatusCode) =>
        IsUnderReinspection(claimStatus, qoStatusCode)
            ? "bg-info-subtle text-info-emphasis border border-info-subtle"
            : BadgeCss(claimStatus);
}

/// <summary>QC's finish-time claim assessment (M24) as it appears in the Claims
/// window. Distinct from <see cref="ClaimStatus"/>: this is the inspector's
/// verdict at the end of the report, the claim status is what the Quality /
/// Claim Manager decided afterwards.</summary>
public static class ClaimAssessment
{
    /// <summary>Filter values, also the query-string values.</summary>
    public const string Any        = "";
    public const string Potential  = "Yes";
    public const string NoClaim    = "No";
    public const string Unset      = "Unset";

    public static string Label(bool? v) => v switch
    {
        true  => "Potential Claim",
        false => "No Potential Claim",
        _     => "Not classified"
    };

    /// <summary>Compact form for the Claims grid. The full wording is 18
    /// characters and, multiplied by a column, was a large part of why that
    /// table scrolled sideways; the long label stays in the cell's tooltip.</summary>
    public static string ShortLabel(bool? v) => v switch
    {
        true  => "Potential",
        false => "No claim",
        _     => "—"
    };

    public static string BadgeCss(bool? v) => v switch
    {
        true  => "bg-danger text-white",
        false => "bg-success text-white",
        _     => "bg-light text-muted border"
    };

    public static string Icon(bool? v) => v switch
    {
        true  => "bi-exclamation-octagon-fill",
        false => "bi-check-circle-fill",
        _     => "bi-dash-circle"
    };
}

/// <summary>Row shape for the Claim Management list page.</summary>
public class ClaimListRow
{
    /// <summary>
    /// The size of the whole matching set, not of this page — COUNT(*) OVER ()
    /// repeats it on every row. Carried on the row rather than counted by a
    /// second query because the list's filters live inline in that query, and a
    /// separate count would be a copy of them that could drift.
    /// </summary>
    public int       TotalCount     { get; set; }

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

    /// <summary>QO status — always "Closed" on the active Claims tab, but the
    /// Archived tab also carries Submitted orders, which show it as a column.</summary>
    public string    StatusCode     { get; set; } = "";
    /// <summary>Non-null once the order has been archived off the active list.</summary>
    public DateTime? ArchivedAt     { get; set; }

    // From qms_claim (NULL when no claim row yet -- treat as Pending in UI).
    public string?   ClaimStatus    { get; set; }
    public DateTime? LastActivityAt { get; set; }
    public DateTime? DecidedAt      { get; set; }
    public int       NoteCount      { get; set; }
    public int       UnreadCount    { get; set; }   // notes added after this user's last_seen, authored by someone else

    // ---- M24: QC's finish-time claim assessment (qms_quality_order) ----
    /// <summary>true = Potential Claim, false = No Potential Claim, null =
    /// finished before the question existed.</summary>
    public bool?     PotentialClaim   { get; set; }
    public DateTime? PotentialClaimAt { get; set; }
    public string?   PotentialClaimBy { get; set; }

    // ---- Shipment + inspection detail, shown in the expandable row so a
    //      claim can be worked without opening the order. ----
    public string?   Plant           { get; set; }
    public string?   StorageLocation { get; set; }
    /// <summary>SAP PO type (EKKO.BSART); rendered through PoTypeDisplay.</summary>
    public string?   PoType          { get; set; }
    public string?   VesselName      { get; set; }
    public string?   VoyageNumber    { get; set; }
    public string?   LoadingPort     { get; set; }
    public string?   LoadingCountry  { get; set; }
    public string?   ArrivalPlace    { get; set; }
    public DateTime? SailingDate     { get; set; }
    public DateTime? ArrivalDate     { get; set; }
    public DateTime? DischargeDate   { get; set; }
    /// <summary>When the Quality Order was opened — the date the inspection
    /// actually began. Stored UTC, so render it with SpecifyKind(Utc) rather
    /// than a bare ToLocalTime().</summary>
    public DateTime? InspectionDate  { get; set; }
    /// <summary>Comma-separated material descriptions on the order (the
    /// "Product" column). Capped by the query to keep the list light.</summary>
    public string?   Products        { get; set; }
    public int       MaterialCount   { get; set; }
    public int       SampleCount     { get; set; }
    /// <summary>The finish note typed in the Finish dialog (qo.close_reason).</summary>
    public string?   CloseReason     { get; set; }
}
