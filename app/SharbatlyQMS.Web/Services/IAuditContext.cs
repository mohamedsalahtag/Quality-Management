namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Per-request carrier for the audit-trail HTTP context. Populated by
/// AuditContextActionFilter before each MVC action runs; consumed by
/// AuditService when writing an audit entry. Keeping these two pieces
/// of HTTP context behind a typed interface avoids spreading
/// IHttpContextAccessor across the service layer.
///
/// Background hosted services (AutoSyncService, AdCachePrimingService)
/// never go through the ActionFilter, so the values remain null for
/// non-user-initiated work -- exactly matching FR-001's "user-initiated
/// mutations" boundary.
/// </summary>
public interface IAuditContext
{
    string? RemoteIp   { get; set; }
    string? UserAgent  { get; set; }
    /// <summary>Reverse-DNS host name of <see cref="RemoteIp"/> at request time
    /// (e.g. <c>STATION-12.sharbatlyfruit.com</c>). Null when the lookup
    /// failed, timed out, or no IP was captured.</summary>
    string? DeviceName { get; set; }
}

public class AuditContext : IAuditContext
{
    public string? RemoteIp   { get; set; }
    public string? UserAgent  { get; set; }
    public string? DeviceName { get; set; }
}
