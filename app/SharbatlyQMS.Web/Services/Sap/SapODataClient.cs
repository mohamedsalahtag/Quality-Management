using System.Net.Http.Headers;
using System.Text.Json;

namespace SharbatlyQMS.Web.Services.Sap;

/// <summary>
/// Real SAP OData consumer (HTTP + Basic Auth). Adapted from
/// ProductionControl.Services.SapODataService -- same patterns:
///   * Self-signed cert acceptance for SAP test/CI hosts.
///   * Parse OData v2 ({"d":{"results":[...]}}) and v4 ({"value":[...]}) responses.
///   * Property-name flattening so we tolerate any casing returned by the gateway.
/// </summary>
public interface ISapODataClient
{
    /// <summary>
    /// Probe the endpoint with <c>$top=1</c>. <paramref name="user"/> /
    /// <paramref name="password"/> override the per-call credentials; pass null
    /// to fall back to the global SAP user from <see cref="ISettingsService"/>.
    /// This lets each URL point at a different SAP server (e.g. dev vs prod)
    /// with its own credentials.
    /// </summary>
    Task<(bool ok, string message)> TestEndpointAsync(string url,
        string? user = null, string? password = null, CancellationToken ct = default);

    Task<(bool ok, string body)> PreviewEndpointAsync(string url,
        string? user = null, string? password = null, CancellationToken ct = default);

    /// <summary>
    /// Pages through the endpoint with $top/$skip and yields each batch as a list
    /// of property-flattened dictionaries. Per-call credentials follow the same
    /// override rule as TestEndpointAsync. Returns the total row count and any
    /// error encountered.
    /// </summary>
    Task<(bool ok, int totalRows, string message)> FetchAllAsync(
        string url, int pageSize, Func<IReadOnlyList<IReadOnlyDictionary<string, string?>>, Task> onBatch,
        string? user = null, string? password = null, CancellationToken ct = default);
}

public class SapODataClient : ISapODataClient
{
    private readonly ISettingsService _settings;
    private readonly ILogger<SapODataClient> _log;

    public SapODataClient(ISettingsService settings, ILogger<SapODataClient> log)
    {
        _settings = settings; _log = log;
    }

    private async Task<HttpClient> BuildAsync(string? overrideUser, string? overridePassword)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };

        // Per-call override wins; otherwise fall back to the global default.
        string user = overrideUser ?? "";
        string password = overridePassword ?? "";
        if (string.IsNullOrWhiteSpace(user))
        {
            var sap = await _settings.GetSapConfigAsync();
            user     = sap.User;
            password = sap.Password;
        }

        if (!string.IsNullOrEmpty(user))
        {
            var token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{password}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        }
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    public async Task<(bool ok, string message)> TestEndpointAsync(string url,
        string? user = null, string? password = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return (false, "URL is empty");
        try
        {
            using var client = await BuildAsync(user, password);
            var probe = AppendQuery(url, BuildProbeQuery(url, top: 1));
            var resp = await client.GetAsync(probe, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                return (false, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}\n{Truncate(body, 400)}");
            }
            if (LooksLikeServiceRoot(url))
                return (true,
                    $"OK ({(int)resp.StatusCode}). This URL points at the OData service root, not an entity set -- " +
                    "no rows can be fetched until you append an entity set name. Click Preview to see the list of entity sets the service exposes.");
            return (true, $"OK ({(int)resp.StatusCode}).");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SAP probe failed: {Url}", url);
            return (false, ex.Message);
        }
    }

    public async Task<(bool ok, string body)> PreviewEndpointAsync(string url,
        string? user = null, string? password = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return (false, "URL is empty");
        try
        {
            using var client = await BuildAsync(user, password);
            var probe = AppendQuery(url, BuildProbeQuery(url, top: 1));
            var resp = await client.GetAsync(probe, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) return (false, $"HTTP {(int)resp.StatusCode}\n{body}");
            try
            {
                using var doc = JsonDocument.Parse(body);
                body = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
            }
            catch { /* not JSON; show as-is */ }
            return (true, body);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<(bool ok, int totalRows, string message)> FetchAllAsync(
        string url, int pageSize,
        Func<IReadOnlyList<IReadOnlyDictionary<string, string?>>, Task> onBatch,
        string? user = null, string? password = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return (false, 0, "URL is empty");
        if (LooksLikeServiceRoot(url))
            return (false, 0,
                "URL points at the OData service root, not an entity set. " +
                "Sync needs an entity set URL -- click Preview to see which entity sets the service exposes, " +
                "then update the URL with the appropriate entity set name appended.");
        try
        {
            using var client = await BuildAsync(user, password);
            int total = 0, skip = 0;
            const int safetyCap = 500;   // 500 pages * pageSize -- ample for any single CDS view.
            for (int page = 0; page < safetyCap; page++)
            {
                var pageUrl = AppendQuery(url, BuildPageQuery(url, pageSize, skip));
                var resp = await client.GetAsync(pageUrl, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    return (false, total, $"HTTP {(int)resp.StatusCode} at skip={skip}: {Truncate(body, 400)}");
                }
                var json = await resp.Content.ReadAsStringAsync(ct);
                var batch = ParseODataBatch(json);
                if (batch.Count == 0) break;
                await onBatch(batch);
                total += batch.Count;
                if (batch.Count < pageSize) break;
                skip += pageSize;
            }
            return (true, total, $"Synced {total} row(s).");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "SAP fetch failed for {Url}", url);
            return (false, 0, ex.Message);
        }
    }

    // ---- helpers ---------------------------------------------------------

    // Preserves the URL as-supplied (no TrimEnd('/')); SAP v4 service-root
    // paths typically end with a slash before the query string and stripping
    // that slash can change the semantic from "service root" to a 404.
    private static string AppendQuery(string url, string query)
    {
        if (string.IsNullOrEmpty(query)) return url;
        return url + (url.Contains('?') ? "&" : "?") + query;
    }

    /// <summary>
    /// OData v4 services are reached via paths containing <c>/odata4/</c>
    /// (SAP S/4HANA convention) and do <em>not</em> need <c>$format=json</c>
    /// in the query string -- the <c>Accept: application/json</c> header
    /// handles content negotiation. Sending <c>$format=json</c> against
    /// some v4 services returns 400 Bad Request, so we omit it for v4 and
    /// keep it for v2 paths (<c>/odata/</c>) where it's expected.
    /// </summary>
    private static bool IsODataV4(string url)
        => url.Contains("/odata4/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A "service root" URL has the version number (digits) as the last path
    /// segment, e.g. <c>.../zqc_data_sd/0001/</c>. SAP v4 service roots return
    /// the service document (a list of entity sets) and do <em>not</em> accept
    /// <c>$top</c>/<c>$skip</c> query options -- adding them returns
    /// <c>CX_OD_URI_SYNTAX_ERROR</c> from SAP gateway. We detect service-root
    /// URLs and skip the query options so Test/Preview still work against
    /// them (and Sync can refuse with a helpful message).
    /// </summary>
    private static bool LooksLikeServiceRoot(string url)
    {
        try
        {
            var u = new Uri(url);
            var path = u.AbsolutePath.TrimEnd('/');
            var lastSlash = path.LastIndexOf('/');
            if (lastSlash < 0) return false;
            var lastSegment = path.Substring(lastSlash + 1);
            return lastSegment.Length > 0 && lastSegment.All(char.IsDigit);
        }
        catch { return false; }
    }

    private static string BuildProbeQuery(string url, int top)
    {
        if (LooksLikeServiceRoot(url)) return "";
        return IsODataV4(url) ? $"$top={top}" : $"$top={top}&$format=json";
    }

    private static string BuildPageQuery(string url, int top, int skip)
    {
        if (LooksLikeServiceRoot(url)) return "";
        return IsODataV4(url) ? $"$top={top}&$skip={skip}" : $"$format=json&$top={top}&$skip={skip}";
    }

    /// <summary>
    /// Parses one OData page (v2 {"d":{"results":[...]}} or v4 {"value":[...]}).
    /// Each row is flattened to a case-insensitive dictionary so callers can
    /// pick fields by any casing the gateway returns.
    /// </summary>
    public static List<IReadOnlyDictionary<string, string?>> ParseODataBatch(string json)
    {
        var list = new List<IReadOnlyDictionary<string, string?>>();
        using var doc = JsonDocument.Parse(json);
        if (!TryFindArray(doc.RootElement, out var arr)) return list;

        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object) continue;
            list.Add(FlattenObject(el));
        }
        return list;
    }

    private static bool TryFindArray(JsonElement root, out JsonElement arr)
    {
        arr = default;
        if (root.TryGetProperty("d", out var d))
        {
            if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("results", out var r)) { arr = r; return true; }
            if (d.ValueKind == JsonValueKind.Array) { arr = d; return true; }
        }
        if (root.TryGetProperty("value", out var v)) { arr = v; return true; }
        return false;
    }

    private static Dictionary<string, string?> FlattenObject(JsonElement obj)
    {
        var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Name.StartsWith("__", StringComparison.Ordinal)) continue;
            switch (prop.Value.ValueKind)
            {
                case JsonValueKind.String: dict[prop.Name] = prop.Value.GetString(); break;
                case JsonValueKind.Number: dict[prop.Name] = prop.Value.ToString(); break;
                case JsonValueKind.True:   dict[prop.Name] = "true"; break;
                case JsonValueKind.False:  dict[prop.Name] = "false"; break;
                case JsonValueKind.Null:   dict[prop.Name] = null; break;
                // skip object/array (typically nav-prop expansion or metadata)
            }
        }
        return dict;
    }

    public static string? Pick(IReadOnlyDictionary<string, string?> dict, params string[] names)
    {
        foreach (var n in names)
            if (dict.TryGetValue(n, out var v) && !string.IsNullOrEmpty(v)) return v;
        return null;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "...";
}
