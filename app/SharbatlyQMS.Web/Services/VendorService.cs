using Dapper;
using Microsoft.Data.SqlClient;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Read-only lookup over qms_sap_vendor_cache. The cache still stores
/// records as raw JSON (unlike the material cache which V07 flattened),
/// so the email is extracted via JSON_VALUE with multiple candidate field
/// names to match whatever the running vendor sync put there.
///
/// If the vendor cache is empty or the email field is named something we
/// don't probe, GetEmailAsync returns null and the QO send-report dialog
/// opens with an empty To field so the operator can type the address.
/// </summary>
public interface IVendorService
{
    Task<VendorInfo?> GetAsync(string vendorNo);

    /// <summary>Saves (or clears) the QMS-side e-mail for a supplier. Passing a
    /// blank address deletes the row rather than storing an empty string, so
    /// "no address on file" stays a single state.</summary>
    Task SaveEmailAsync(string vendorNo, string? email, string user);
}

public class VendorInfo
{
    public string  VendorNo { get; set; } = "";
    public string? Name     { get; set; }
    public string? Email    { get; set; }

    /// <summary>Where <see cref="Email"/> came from: "sap" when the vendor
    /// cache carried one, "qms" when it came from qms_vendor_contact, null
    /// when there is no address at all. Drives what the send dialog tells the
    /// user, so it must never be guessed from Email alone.</summary>
    public string? EmailSource { get; set; }
}

public class VendorService : IVendorService
{
    private readonly string _cs;
    private readonly ILogger<VendorService> _log;

    public VendorService(IConfiguration cfg, ILogger<VendorService> log)
    {
        _cs = cfg.GetConnectionString("Default")
              ?? throw new InvalidOperationException("Missing Default connection string.");
        _log = log;
    }

    public async Task<VendorInfo?> GetAsync(string vendorNo)
    {
        if (string.IsNullOrWhiteSpace(vendorNo)) return null;
        try
        {
            using var c = new SqlConnection(_cs);
            return await c.QuerySingleOrDefaultAsync<VendorInfo>(@"
                SELECT vendor_no AS VendorNo,
                       COALESCE(
                           JSON_VALUE(payload_json, '$.Vendor_Name'),
                           JSON_VALUE(payload_json, '$.VendorName'),
                           JSON_VALUE(payload_json, '$.Name1'),
                           JSON_VALUE(payload_json, '$.NAME1'),
                           JSON_VALUE(payload_json, '$.Name')
                       ) AS Name,
                       -- SAP first, QMS store second. dbo.SAP_Vendors carries no
                       -- e-mail column today so the JSON probes all return NULL and
                       -- vc.email wins; the order still matters for the day a real
                       -- vendor-master feed is configured.
                       COALESCE(
                           JSON_VALUE(payload_json, '$.Email'),
                           JSON_VALUE(payload_json, '$.EMAIL'),
                           JSON_VALUE(payload_json, '$.Email_Address'),
                           JSON_VALUE(payload_json, '$.EmailAddress'),
                           JSON_VALUE(payload_json, '$.Smtp_Addr'),
                           JSON_VALUE(payload_json, '$.SmtpAddr'),
                           JSON_VALUE(payload_json, '$.SMTP_ADDR'),
                           JSON_VALUE(payload_json, '$.Mail'),
                           JSON_VALUE(payload_json, '$.Email_Id'),
                           JSON_VALUE(payload_json, '$.EmailId'),
                           vc.email
                       ) AS Email,
                       CASE
                           WHEN COALESCE(
                                  JSON_VALUE(payload_json, '$.Email'),
                                  JSON_VALUE(payload_json, '$.EMAIL'),
                                  JSON_VALUE(payload_json, '$.Email_Address'),
                                  JSON_VALUE(payload_json, '$.EmailAddress'),
                                  JSON_VALUE(payload_json, '$.Smtp_Addr'),
                                  JSON_VALUE(payload_json, '$.SmtpAddr'),
                                  JSON_VALUE(payload_json, '$.SMTP_ADDR'),
                                  JSON_VALUE(payload_json, '$.Mail'),
                                  JSON_VALUE(payload_json, '$.Email_Id'),
                                  JSON_VALUE(payload_json, '$.EmailId')) IS NOT NULL THEN 'sap'
                           WHEN vc.email IS NOT NULL THEN 'qms'
                           ELSE NULL
                       END AS EmailSource
                FROM qms_sap_vendor_cache v
                LEFT JOIN qms_vendor_contact vc ON vc.vendor_no = v.vendor_no
                WHERE v.vendor_no = @vendorNo", new { vendorNo });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Vendor lookup failed for {VendorNo}", vendorNo);
            return null;
        }
    }

    public async Task SaveEmailAsync(string vendorNo, string? email, string user)
    {
        if (string.IsNullOrWhiteSpace(vendorNo)) return;
        using var c = new SqlConnection(_cs);
        var trimmed = (email ?? "").Trim();

        if (trimmed.Length == 0)
        {
            await c.ExecuteAsync(
                "DELETE FROM qms_vendor_contact WHERE vendor_no = @vendorNo",
                new { vendorNo });
            return;
        }

        // HOLDLOCK closes the upsert race -- two operators saving an address for
        // the same supplier at once would otherwise both take the INSERT branch
        // and one would hit the primary key.
        await c.ExecuteAsync(@"
            MERGE qms_vendor_contact WITH (HOLDLOCK) AS tgt
            USING (SELECT @vendorNo AS vendor_no) AS src
              ON  tgt.vendor_no = src.vendor_no
            WHEN MATCHED THEN
                UPDATE SET email = @email, updated_at = SYSUTCDATETIME(), updated_by = @user
            WHEN NOT MATCHED THEN
                INSERT (vendor_no, email, updated_at, updated_by)
                VALUES (@vendorNo, @email, SYSUTCDATETIME(), @user);",
            new { vendorNo, email = trimmed, user });
    }
}
