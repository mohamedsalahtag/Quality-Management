using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

// SharbatlyQMS migration tool.
// Usage:
//   dotnet run -- probe   "<connectionString>"
//   dotnet run -- apply   "<connectionString>" "<scriptPath>"
//   dotnet run -- create-db "<masterConnectionString>" <dbName>
// Scripts may use GO as a batch separator. Connection string is read once;
// each batch runs in its own SqlCommand for clear error reporting.

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  probe     <connectionString>");
    Console.Error.WriteLine("  apply     <connectionString> <scriptPath>");
    Console.Error.WriteLine("  create-db <masterConnectionString> <dbName>");
    return 1;
}

var verb = args[0];
var cs   = args[1];

try
{
    return verb.ToLowerInvariant() switch
    {
        "probe"     => await Probe(cs),
        "apply"     => args.Length >= 3 ? await Apply(cs, args[2]) : Usage(),
        "create-db" => args.Length >= 3 ? await CreateDb(cs, args[2]) : Usage(),
        _           => Usage()
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
    if (ex.InnerException != null) Console.Error.WriteLine($"  inner: {ex.InnerException.Message}");
    return 1;
}

static int Usage() { Console.Error.WriteLine("Bad arguments."); return 2; }

static async Task<int> Probe(string cs)
{
    Console.WriteLine("=== Connectivity ===");
    using var c = new SqlConnection(cs);
    await c.OpenAsync();
    Console.WriteLine($"Server     : {c.DataSource}");
    Console.WriteLine($"Database   : {c.Database}");
    Console.WriteLine($"State      : {c.State}");

    var (ver, login, isSysAdmin, canCreateDb) = await Scalar4Async(c, @"
        SELECT @@VERSION,
               SUSER_NAME(),
               IS_SRVROLEMEMBER('sysadmin'),
               IS_SRVROLEMEMBER('dbcreator')");
    Console.WriteLine($"Login      : {login}");
    Console.WriteLine($"sysadmin?  : {isSysAdmin}");
    Console.WriteLine($"dbcreator? : {canCreateDb}");
    Console.WriteLine($"Version    : {ver?.ToString()?.Split('\n')[0].Trim()}");

    Console.WriteLine();
    Console.WriteLine("=== Tables already in current database ===");
    var existing = new List<string>();
    using (var cmd = new SqlCommand("SELECT name FROM sys.tables ORDER BY name", c))
    using (var rd = await cmd.ExecuteReaderAsync())
        while (await rd.ReadAsync()) existing.Add(rd.GetString(0));
    foreach (var t in existing) Console.WriteLine($"  - {t}");
    Console.WriteLine($"  ({existing.Count} tables)");

    Console.WriteLine();
    Console.WriteLine("=== Schema collision check ===");
    var qmsTables = new[]
    {
        "SiteConfiguration","Users","EmailGroups","EmailGroupMembers","EmailGroupAddresses",
        "GroupMailConfig","AlertRules",
        "qms_arrival","qms_arrival_sap_snapshot","qms_arrival_item","qms_arrival_checklist",
        "qms_shipment_snapshot","qms_quality_order","qms_quality_order_material",
        "qms_sample","qms_sample_reading","qms_sample_observation","qms_defect_catalog",
        "qms_material_group_defect","qms_sample_defect","qms_image_asset","qms_image_link",
        "qms_reading_type","qms_material_group_reading","qms_status_history",
        "qms_audit_log","qms_report_log"
    };
    var collisions = qmsTables.Where(t => existing.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
    if (collisions.Count > 0)
    {
        Console.WriteLine("  COLLIDES:");
        foreach (var t in collisions) Console.WriteLine($"    * {t}");
    }
    else Console.WriteLine("  No collisions.");

    Console.WriteLine();
    Console.WriteLine("=== Other databases on this server ===");
    using (var cmd2 = new SqlCommand(
        "SELECT name FROM sys.databases WHERE database_id > 4 ORDER BY name", c))
    using (var rd = await cmd2.ExecuteReaderAsync())
        while (await rd.ReadAsync()) Console.WriteLine($"  - {rd.GetString(0)}");

    return 0;
}

static async Task<int> CreateDb(string cs, string dbName)
{
    if (!Regex.IsMatch(dbName, @"^[A-Za-z_][A-Za-z0-9_]{0,63}$"))
        throw new ArgumentException("Database name must be a plain SQL identifier.");

    using var c = new SqlConnection(cs);
    await c.OpenAsync();
    using var cmd = new SqlCommand(
        $"IF DB_ID(N'{dbName}') IS NULL CREATE DATABASE [{dbName}]; SELECT DB_ID(N'{dbName}');", c);
    var id = await cmd.ExecuteScalarAsync();
    Console.WriteLine($"Database '{dbName}' ready (database_id = {id}).");
    return 0;
}

static async Task<int> Apply(string cs, string scriptPath)
{
    if (!File.Exists(scriptPath))
        throw new FileNotFoundException("Script not found", scriptPath);

    var sql = await File.ReadAllTextAsync(scriptPath);
    // Split on GO (line by itself, case-insensitive). Our scripts don't use GO,
    // but supporting it keeps this tool useful for any future migration.
    var batches = Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)
        .Where(b => !string.IsNullOrWhiteSpace(b))
        .ToList();

    Console.WriteLine($"Applying {Path.GetFileName(scriptPath)} ({batches.Count} batch(es))...");
    using var c = new SqlConnection(cs);
    await c.OpenAsync();

    var batchIndex = 0;
    foreach (var batch in batches)
    {
        batchIndex++;
        try
        {
            using var cmd = new SqlCommand(batch, c) { CommandTimeout = 120 };
            // If the script returns rows (SELECT-style scripts like verify.sql),
            // print them; otherwise just count affected rows.
            var trimmed = batch.TrimStart();
            if (trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("--", StringComparison.OrdinalIgnoreCase) && trimmed.Contains("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                {
                    var parts = new List<string>();
                    for (int i = 0; i < rd.FieldCount; i++)
                        parts.Add($"{rd.GetName(i)}={rd.GetValue(i)}");
                    Console.WriteLine("  > " + string.Join("  ", parts));
                }
            }
            else
            {
                await cmd.ExecuteNonQueryAsync();
            }
        }
        catch (SqlException ex)
        {
            Console.Error.WriteLine($"  ✗ batch {batchIndex} failed at line {ex.LineNumber}: {ex.Message}");
            throw;
        }
    }
    Console.WriteLine($"  ✓ {Path.GetFileName(scriptPath)} applied.");
    return 0;
}

static async Task<(object? a, object? b, object? c, object? d)> Scalar4Async(SqlConnection conn, string sql)
{
    using var cmd = new SqlCommand(sql, conn);
    using var rd = await cmd.ExecuteReaderAsync();
    if (!await rd.ReadAsync()) return (null, null, null, null);
    return (rd.GetValue(0), rd.GetValue(1), rd.GetValue(2), rd.GetValue(3));
}
