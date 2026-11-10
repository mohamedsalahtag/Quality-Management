using Dapper;
using Microsoft.Data.SqlClient;

namespace SharbatlyQMS.Web.Services;

/// <summary>A supplier on Parameters -> Supplier contacts.</summary>
public class SupplierContact
{
    public string    VendorNo      { get; set; } = "";
    public string    Name          { get; set; } = "";
    public string?   Origin        { get; set; }
    public string?   ContactPerson { get; set; }
    public bool      IsActive      { get; set; }
    public string    Source        { get; set; } = "";
    public DateTime  UpdatedAt     { get; set; }
    public string?   UpdatedBy     { get; set; }
    /// <summary>Containers received from this supplier (cancelled arrivals excluded).</summary>
    public int       Containers    { get; set; }
    public DateTime? LastArrival   { get; set; }
    public List<string> Emails     { get; set; } = new();
}

public sealed record SupplierImportResult(
    int SuppliersAdded, int SuppliersUpdated, int EmailsAdded, int RowsApplied,
    IReadOnlyList<SupplierImportSkip> Skipped);

/// <summary>
/// The supplier list and its report addresses (M42). The send-report dialog
/// reads it to fill To; Parameters -> Supplier contacts maintains it.
///
/// SAP has no supplier e-mail (dbo.SAP_Vendors is VendorId + VendorName and
/// the ZQC_Data feed carries none), so this is the only source of addresses.
/// </summary>
public interface ISupplierContactService
{
    /// <summary>Adds every vendor we have received a container from that is not
    /// on the list yet. Run from the screen and the send dialog rather than
    /// from each place an arrival is created, so no arrival path can miss it.</summary>
    Task<int> EnsureFromArrivalsAsync();

    Task<List<SupplierContact>> ListAsync(string? status, string? show, string? q);
    Task<SupplierContact?> GetAsync(string vendorNo);

    /// <summary>The supplier's report addresses, oldest first.</summary>
    Task<IReadOnlyList<string>> GetEmailsAsync(string vendorNo);

    /// <summary>The name SAP has for a vendor number, or null when SAP does not know it.</summary>
    Task<string?> SapNameAsync(string vendorNo);

    /// <summary>Creates or updates a supplier and REPLACES its address list.</summary>
    Task<bool> SaveAsync(SupplierContact s, IReadOnlyList<string> emails, string user);

    /// <summary>Deletes a supplier with no containers; returns why not otherwise.</summary>
    Task<string?> DeleteAsync(string vendorNo);

    /// <summary>Adds addresses, never removes any. Creates the supplier when it
    /// is missing. Returns how many were new.</summary>
    Task<int> AddEmailsAsync(string vendorNo, string? name, IEnumerable<string> emails, string user);

    Task<SupplierImportResult> ImportAsync(IReadOnlyList<SupplierImportRow> rows, string user);
}

public class SupplierContactService : ISupplierContactService
{
    private readonly string _cs;

    public SupplierContactService(IConfiguration cfg)
    {
        _cs = cfg.GetConnectionString("Default")
              ?? throw new InvalidOperationException("Missing Default connection string.");
    }

    private const string SapName =
        "(SELECT TOP 1 JSON_VALUE(v.payload_json, '$.Vendor_Name') FROM qms_sap_vendor_cache v WHERE v.vendor_no = {0})";

    public async Task<int> EnsureFromArrivalsAsync()
    {
        using var c = new SqlConnection(_cs);
        return await c.ExecuteAsync(@"
            INSERT INTO qms_supplier (vendor_no, name, source, created_by, updated_by)
            SELECT a.vendor_no, MAX(ISNULL(NULLIF(a.vendor_name, ''), a.vendor_no)), 'arrival', 'system', 'system'
            FROM   qms_arrival a
            WHERE  ISNULL(a.vendor_no, '') <> ''
              AND  NOT EXISTS (SELECT 1 FROM qms_supplier s WHERE s.vendor_no = a.vendor_no)
            GROUP  BY a.vendor_no");
    }

    public async Task<List<SupplierContact>> ListAsync(string? status, string? show, string? q)
    {
        using var c = new SqlConnection(_cs);
        var like = string.IsNullOrWhiteSpace(q) ? null : $"%{q.Trim()}%";
        var rows = (await c.QueryAsync<SupplierContact>(@"
            SELECT s.vendor_no VendorNo, s.name Name, s.origin Origin, s.contact_person ContactPerson,
                   s.is_active IsActive, s.source Source, s.updated_at UpdatedAt, s.updated_by UpdatedBy,
                   ISNULL(a.n, 0) Containers, a.last_at LastArrival
            FROM   qms_supplier s
            OUTER APPLY (SELECT COUNT(*) n, MAX(x.created_at) last_at
                         FROM qms_arrival x
                         WHERE x.vendor_no = s.vendor_no AND x.status_code <> 'Cancelled') a
            WHERE (@status = 'all' OR (@status = 'inactive' AND s.is_active = 0) OR (@status = 'active' AND s.is_active = 1))
              AND (@show IS NULL
                   OR (@show = 'with' AND ISNULL(a.n, 0) > 0)
                   OR (@show = 'none' AND ISNULL(a.n, 0) = 0)
                   OR (@show = 'noemail' AND NOT EXISTS (SELECT 1 FROM qms_supplier_email e WHERE e.vendor_no = s.vendor_no)))
              AND (@like IS NULL OR s.vendor_no LIKE @like OR s.name LIKE @like OR s.origin LIKE @like
                   OR s.contact_person LIKE @like
                   OR EXISTS (SELECT 1 FROM qms_supplier_email e WHERE e.vendor_no = s.vendor_no AND e.email LIKE @like))
            ORDER  BY s.name, s.vendor_no",
            new { status = status ?? "active", show = string.IsNullOrWhiteSpace(show) ? null : show, like })).ToList();

        var emails = (await c.QueryAsync<(string VendorNo, string Email)>(
                "SELECT vendor_no, email FROM qms_supplier_email ORDER BY email_id"))
            .ToLookup(e => e.VendorNo, e => e.Email, StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) r.Emails = emails[r.VendorNo].ToList();
        return rows;
    }

    public async Task<SupplierContact?> GetAsync(string vendorNo)
    {
        if (string.IsNullOrWhiteSpace(vendorNo)) return null;
        using var c = new SqlConnection(_cs);
        var s = await c.QuerySingleOrDefaultAsync<SupplierContact>(@"
            SELECT vendor_no VendorNo, name Name, origin Origin, contact_person ContactPerson,
                   is_active IsActive, source Source, updated_at UpdatedAt, updated_by UpdatedBy
            FROM   qms_supplier WHERE vendor_no = @vendorNo", new { vendorNo });
        if (s != null) s.Emails = (await GetEmailsAsync(c, vendorNo)).ToList();
        return s;
    }

    public async Task<IReadOnlyList<string>> GetEmailsAsync(string vendorNo)
    {
        if (string.IsNullOrWhiteSpace(vendorNo)) return Array.Empty<string>();
        using var c = new SqlConnection(_cs);
        return await GetEmailsAsync(c, vendorNo);
    }

    private static async Task<IReadOnlyList<string>> GetEmailsAsync(SqlConnection c, string vendorNo) =>
        (await c.QueryAsync<string>(
            "SELECT email FROM qms_supplier_email WHERE vendor_no = @vendorNo ORDER BY email_id",
            new { vendorNo })).ToList();

    public async Task<string?> SapNameAsync(string vendorNo)
    {
        if (string.IsNullOrWhiteSpace(vendorNo)) return null;
        using var c = new SqlConnection(_cs);
        return await c.ExecuteScalarAsync<string?>("SELECT " + string.Format(SapName, "@vendorNo"), new { vendorNo });
    }

    public async Task<bool> SaveAsync(SupplierContact s, IReadOnlyList<string> emails, string user)
    {
        using var c = new SqlConnection(_cs);
        await c.OpenAsync();
        using var tx = c.BeginTransaction();
        var created = await c.ExecuteScalarAsync<int>(@"
            IF EXISTS (SELECT 1 FROM qms_supplier WITH (UPDLOCK, HOLDLOCK) WHERE vendor_no = @VendorNo)
            BEGIN
                UPDATE qms_supplier SET name = @Name, origin = @Origin, contact_person = @ContactPerson,
                       is_active = @IsActive, updated_at = SYSUTCDATETIME(), updated_by = @user
                WHERE  vendor_no = @VendorNo;
                SELECT 0;
            END
            ELSE
            BEGIN
                INSERT INTO qms_supplier (vendor_no, name, origin, contact_person, is_active, source, created_by, updated_by)
                VALUES (@VendorNo, @Name, @Origin, @ContactPerson, @IsActive, 'manual', @user, @user);
                SELECT 1;
            END",
            new { s.VendorNo, s.Name, s.Origin, s.ContactPerson, s.IsActive, user }, tx) == 1;

        // Replace the address set: drop what was removed, add what is new, and
        // leave the rest so their created_at/by still says who added them.
        var keep = emails.ToList();
        if (keep.Count == 0)
            await c.ExecuteAsync("DELETE FROM qms_supplier_email WHERE vendor_no = @vendorNo",
                new { vendorNo = s.VendorNo }, tx);
        else
            await c.ExecuteAsync("DELETE FROM qms_supplier_email WHERE vendor_no = @vendorNo AND email NOT IN @keep",
                new { vendorNo = s.VendorNo, keep }, tx);
        await InsertEmailsAsync(c, tx, s.VendorNo, keep, user);
        tx.Commit();
        return created;
    }

    public async Task<string?> DeleteAsync(string vendorNo)
    {
        using var c = new SqlConnection(_cs);
        var n = await c.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM qms_arrival WHERE vendor_no = @vendorNo", new { vendorNo });
        // It would only come back on the next visit -- EnsureFromArrivalsAsync
        // re-adds every vendor with a container.
        if (n > 0)
            return $"We have {n} container(s) from this supplier, so it stays on the list. Untick Active to hide it instead.";
        var deleted = await c.ExecuteAsync("DELETE FROM qms_supplier WHERE vendor_no = @vendorNo", new { vendorNo });
        return deleted == 0 ? "Supplier not found." : null;
    }

    public async Task<int> AddEmailsAsync(string vendorNo, string? name, IEnumerable<string> emails, string user)
    {
        using var c = new SqlConnection(_cs);
        await c.OpenAsync();
        using var tx = c.BeginTransaction();
        await c.ExecuteAsync(@"
            IF NOT EXISTS (SELECT 1 FROM qms_supplier WITH (UPDLOCK, HOLDLOCK) WHERE vendor_no = @vendorNo)
                INSERT INTO qms_supplier (vendor_no, name, source, created_by, updated_by)
                VALUES (@vendorNo, COALESCE(NULLIF(@name, ''), " + string.Format(SapName, "@vendorNo") + @", @vendorNo),
                        'manual', @user, @user)",
            new { vendorNo, name, user }, tx);
        var added = await InsertEmailsAsync(c, tx, vendorNo, emails, user);
        tx.Commit();
        return added;
    }

    private static async Task<int> InsertEmailsAsync(SqlConnection c, SqlTransaction tx, string vendorNo,
        IEnumerable<string> emails, string user)
    {
        var added = 0;
        // Last line of defence: callers filter internal addresses with a message,
        // but none may reach a supplier's list by any path.
        foreach (var email in emails.Where(e => !SupplierEmails.IsInternal(e)).Distinct(StringComparer.OrdinalIgnoreCase))
            added += await c.ExecuteAsync(@"
                INSERT INTO qms_supplier_email (vendor_no, email, created_by)
                SELECT @vendorNo, @email, @user
                WHERE NOT EXISTS (SELECT 1 FROM qms_supplier_email WHERE vendor_no = @vendorNo AND email = @email)",
                new { vendorNo, email, user }, tx);
        return added;
    }

    public async Task<SupplierImportResult> ImportAsync(IReadOnlyList<SupplierImportRow> rows, string user)
    {
        using var c = new SqlConnection(_cs);
        await c.OpenAsync();
        var sap = (await c.QueryAsync<string>("SELECT vendor_no FROM qms_sap_vendor_cache"))
                  .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var (accepted, skipped) = SupplierImport.Plan(rows, sap.Contains);

        int suppliersAdded = 0, suppliersUpdated = 0, emailsAdded = 0;
        using var tx = c.BeginTransaction();
        foreach (var r in accepted)
        {
            // SAP's name wins, so the list reads the same as the containers do;
            // the workbook's name is used only for a vendor SAP does not know.
            // An existing supplier keeps its name and only has blanks filled.
            var outcome = await c.ExecuteScalarAsync<int>(@"
                IF EXISTS (SELECT 1 FROM qms_supplier WITH (UPDLOCK, HOLDLOCK) WHERE vendor_no = @Code)
                BEGIN
                    UPDATE qms_supplier
                    SET    origin = COALESCE(origin, LEFT(@Origin, 80)),
                           contact_person = COALESCE(contact_person, LEFT(@ContactPerson, 200)),
                           updated_at = SYSUTCDATETIME(), updated_by = @user
                    WHERE  vendor_no = @Code AND (origin IS NULL AND @Origin IS NOT NULL
                                                  OR contact_person IS NULL AND @ContactPerson IS NOT NULL);
                    SELECT CASE WHEN @@ROWCOUNT > 0 THEN 2 ELSE 0 END;
                END
                ELSE
                BEGIN
                    INSERT INTO qms_supplier (vendor_no, name, origin, contact_person, source, created_by, updated_by)
                    VALUES (@Code, LEFT(COALESCE(" + string.Format(SapName, "@Code") + @", @Name, @Code), 200),
                            LEFT(@Origin, 80), LEFT(@ContactPerson, 200), 'excel', @user, @user);
                    SELECT 1;
                END",
                new { r.Code, r.Name, r.Origin, r.ContactPerson, user }, tx);
            if (outcome == 1) suppliersAdded++;
            else if (outcome == 2) suppliersUpdated++;
            emailsAdded += await InsertEmailsAsync(c, tx, r.Code, r.Emails.Where(e => e.Length <= 320), user);
        }
        tx.Commit();
        return new SupplierImportResult(suppliersAdded, suppliersUpdated, emailsAdded, accepted.Count, skipped);
    }
}
