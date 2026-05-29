using System.Net;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Caching.Memory;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Captures the client IP + user-agent + device (host) name from the
/// HttpContext into the scoped IAuditContext before each MVC action
/// runs. Registered as a global filter in Program.cs.
///
/// The device-name lookup is a reverse-DNS query of the remote IP,
/// cached in IMemoryCache to amortise across requests (and to keep the
/// hot path fast when many requests come from the same workstation).
/// A 500 ms timeout protects against slow/unreachable DNS servers --
/// if the lookup hangs we fall through with DeviceName = null and the
/// UI falls back to the User-Agent-derived browser/OS label.
///
/// Truncates user-agent to the column width (500) to avoid surprises
/// from pathologically long UA strings.
/// </summary>
public class AuditContextActionFilter : IAsyncActionFilter
{
    private const int UserAgentMaxLength = 500;
    private const int DnsTimeoutMs       = 500;
    private static readonly TimeSpan DnsCacheTtl       = TimeSpan.FromHours(1);
    private static readonly TimeSpan DnsNegativeCacheTtl = TimeSpan.FromMinutes(5);

    private readonly IAuditContext _audit;
    private readonly IMemoryCache  _cache;

    public AuditContextActionFilter(IAuditContext audit, IMemoryCache cache)
    {
        _audit = audit;
        _cache = cache;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var ctx = context.HttpContext;

        var ipAddress = ctx.Connection.RemoteIpAddress;
        _audit.RemoteIp = ipAddress?.ToString();

        var ua = ctx.Request.Headers["User-Agent"].ToString();
        if (!string.IsNullOrEmpty(ua) && ua.Length > UserAgentMaxLength)
            ua = ua.Substring(0, UserAgentMaxLength);
        _audit.UserAgent = string.IsNullOrEmpty(ua) ? null : ua;

        _audit.DeviceName = ipAddress == null ? null : await ResolveDeviceNameAsync(ipAddress);

        await next();
    }

    private async Task<string?> ResolveDeviceNameAsync(IPAddress ip)
    {
        var key = "audit-device:" + ip.ToString();
        if (_cache.TryGetValue<string?>(key, out var cached)) return cached;

        // Loopback / link-local don't have meaningful PTR records on most LANs.
        if (IPAddress.IsLoopback(ip)) { _cache.Set(key, (string?)"localhost", DnsCacheTtl); return "localhost"; }

        string? hostname = null;
        try
        {
            var lookupTask = Dns.GetHostEntryAsync(ip);
            var done = await Task.WhenAny(lookupTask, Task.Delay(DnsTimeoutMs));
            if (done == lookupTask)
            {
                var entry = await lookupTask;
                hostname = string.IsNullOrWhiteSpace(entry.HostName) ? null : entry.HostName;
                // Strip the trailing dot some resolvers include.
                if (hostname != null && hostname.EndsWith(".")) hostname = hostname[..^1];
                // If the resolver just echoes the IP back (no PTR record), treat as no result.
                if (hostname == ip.ToString()) hostname = null;
            }
            // else: timed out -- leave hostname null; cache the miss for a short window.
        }
        catch
        {
            // Resolution failure (no PTR, network blip, etc.) -- treat as null.
        }

        _cache.Set(key, hostname,
            hostname == null ? DnsNegativeCacheTtl : DnsCacheTtl);
        return hostname;
    }
}
