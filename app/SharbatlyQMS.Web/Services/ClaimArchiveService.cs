using Dapper;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Files finished orders in the Claims archive (or takes them back out) by
/// FINISHED date -- the same date the Claims page's From / To filter uses, so
/// a range filtered on screen is the range archived.
///
/// Archive is the qms_quality_order.archived_at flag (M20), never a claim
/// status: each order keeps its claim decision and stays printable. Which
/// claim statuses go is chosen by the person archiving, from a preview that
/// counts each one, so open work is never swept away unseen.
///
/// Only orders that speak for their container are touched: Closed and not
/// superseded. A superseded original holds no claim of its own.
/// </summary>
public interface IClaimArchiveService
{
    /// <summary>Orders finished in the range, per claim status, that an
    /// archive (or, with <paramref name="restore"/>, a restore) would move.</summary>
    Task<IReadOnlyList<ClaimArchiveBucket>> PreviewAsync(DateTime from, DateTime to, bool restore, PlantScope scope);

    /// <summary>Moves the orders of the chosen statuses; returns how many.
    /// Throws InvalidOperationException with a user-facing message.</summary>
    Task<int> ApplyAsync(DateTime from, DateTime to, IReadOnlyCollection<string> statuses, bool restore,
                         string reason, string user, PlantScope scope);
}

public sealed class ClaimArchiveBucket
{
    public string Status { get; set; } = "";
    public string Label { get; set; } = "";
    public int Count { get; set; }
}

public sealed class ClaimArchiveService : IClaimArchiveService
{
    /// <summary>A year of finished orders is a few thousand rows; anything
    /// wider is more likely a typo in the year than a real request.</summary>
    public const int MaxDays = 400;

    private readonly string _cs;
    private readonly IAuditService _audit;

    public ClaimArchiveService(IConfiguration config, IAuditService audit)
    {
        _cs = config.GetConnectionString("Default")
              ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
        _audit = audit;
    }

    // closed_at is UTC; the dates picked are local, so the range is converted
    // the same way ClaimService does for the list filter, upper bound exclusive.
    private static (DateTime FromUtc, DateTime ToUtc) Range(DateTime from, DateTime to)
    {
        if (to < from) (from, to) = (to, from);
        if ((to.Date - from.Date).TotalDays > MaxDays)
            throw new InvalidOperationException($"Choose a range of at most {MaxDays} days.");
        return (DateTime.SpecifyKind(from.Date, DateTimeKind.Local).ToUniversalTime(),
                DateTime.SpecifyKind(to.Date.AddDays(1), DateTimeKind.Local).ToUniversalTime());
    }

    private const string Scope = @"
        FROM   qms_quality_order qo
        JOIN   qms_arrival a      ON a.arrival_id = qo.arrival_id
        LEFT   JOIN qms_claim cl  ON cl.quality_order_id = qo.quality_order_id
        WHERE  qo.status_code = 'Closed' AND qo.superseded_at IS NULL
          AND  qo.closed_at >= @fromUtc AND qo.closed_at < @toUtc
          AND  ((@restore = 0 AND qo.archived_at IS NULL) OR (@restore = 1 AND qo.archived_at IS NOT NULL))
          AND  (@sUnrestricted = 1 OR a.plant IN @sPlants)";

    public async Task<IReadOnlyList<ClaimArchiveBucket>> PreviewAsync(DateTime from, DateTime to, bool restore, PlantScope scope)
    {
        var (fromUtc, toUtc) = Range(from, to);
        using var c = new SqlConnection(_cs);
        var rows = await c.QueryAsync<(string Status, int Count)>(@"
            SELECT ISNULL(cl.claim_status, 'Pending') AS Status, COUNT(*) AS Count" + Scope + @"
            GROUP  BY ISNULL(cl.claim_status, 'Pending')",
            new { fromUtc, toUtc, restore, sUnrestricted = scope.Unrestricted, sPlants = scope.QueryPlants });
        return rows.Select(r => new ClaimArchiveBucket { Status = r.Status, Label = ClaimStatus.Label(r.Status), Count = r.Count })
                   .OrderBy(b => Order(b.Status)).ToList();
    }

    private static int Order(string s) => s switch
    {
        ClaimStatus.Pending => 0, ClaimStatus.ClaimRequest => 1, ClaimStatus.HoldClaim => 2,
        ClaimStatus.Reinspection => 3, ClaimStatus.ClaimRequestApproved => 4, ClaimStatus.PassedQC => 5, _ => 9
    };

    public async Task<int> ApplyAsync(DateTime from, DateTime to, IReadOnlyCollection<string> statuses, bool restore,
                                      string reason, string user, PlantScope scope)
    {
        if (statuses.Count == 0) throw new InvalidOperationException("Tick at least one claim status.");
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 5)
            throw new InvalidOperationException("Say why these orders are being " + (restore ? "restored" : "archived") + "; the reason goes into the audit log.");
        reason = reason.Trim();
        var (fromUtc, toUtc) = Range(from, to);

        using var c = new SqlConnection(_cs);
        await c.OpenAsync();
        using var tx = c.BeginTransaction();
        try
        {
            var picked = (await c.QueryAsync<(long Id, string Status)>(@"
                SELECT qo.quality_order_id AS Id, ISNULL(cl.claim_status, 'Pending') AS Status" +
                Scope.Replace("FROM   qms_quality_order qo", "FROM   qms_quality_order qo WITH (UPDLOCK, HOLDLOCK)") + @"
                  AND  ISNULL(cl.claim_status, 'Pending') IN @statuses",
                new { fromUtc, toUtc, restore, statuses, sUnrestricted = scope.Unrestricted, sPlants = scope.QueryPlants }, tx))
                .ToList();
            if (picked.Count == 0) throw new InvalidOperationException("No orders match any more; nothing was changed.");

            foreach (var chunk in picked.Chunk(1000))
                await c.ExecuteAsync(restore
                    ? "UPDATE qms_quality_order SET archived_at = NULL, archived_by = NULL WHERE quality_order_id IN @ids"
                    : "UPDATE qms_quality_order SET archived_at = SYSUTCDATETIME(), archived_by = @user WHERE quality_order_id IN @ids",
                    new { ids = chunk.Select(x => x.Id).ToArray(), user }, tx);

            var range = $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd}";
            foreach (var (id, status) in picked)
                await _audit.WriteAsync(c, tx, EntityTypes.QualityOrder, id,
                    restore ? ActionCodes.Unarchived : ActionCodes.Archived,
                    oldValues: new { archived = restore },
                    newValues: new { archived = !restore, claim_status = status, finished_range = range, reason },
                    actor: user);
            tx.Commit();
            return picked.Count;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }
}
