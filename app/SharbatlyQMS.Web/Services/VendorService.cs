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
}

public class VendorInfo
{
    public string  VendorNo { get; set; } = "";
    public string? Name     { get; set; }
    public string? Email    { get; set; }
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
                           JSON_VALUE(payload_json, '$.EmailId')
                       ) AS Email
                FROM qms_sap_vendor_cache
                WHERE vendor_no = @vendorNo", new { vendorNo });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Vendor lookup failed for {VendorNo}", vendorNo);
            return null;
        }
    }
}
