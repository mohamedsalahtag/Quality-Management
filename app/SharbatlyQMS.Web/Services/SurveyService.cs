using Dapper;
using Microsoft.Data.SqlClient;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.ViewModels;

namespace SharbatlyQMS.Web.Services;

public class SurveyService : ISurveyService
{
    /// <summary>Shortest reason accepted for a request, and for passing one.</summary>
    public const int MinReasonLength = 5;
    public const int MaxReasonLength = 500;
    public const int MaxCommentsLength = 2000;

    private readonly string _cs;
    private readonly IAuditService _audit;

    public SurveyService(IConfiguration config, IAuditService audit)
    {
        _cs = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
        _audit = audit;
    }

    private SqlConnection Open() => new(_cs);

    // ---- Read paths -------------------------------------------------

    /// <summary>
    /// One definition of "a survey with its shipment", shared by the list, the
    /// details page and the export so they cannot disagree.
    ///
    /// The quality order is the arrival's LIVE one (not cancelled, not
    /// superseded) -- the same filter as UX_qms_quality_order_active_per_arrival
    /// -- so a reinspected container shows the inspection that counts, and its
    /// claim, without the survey ever having been re-pointed.
    /// </summary>
    private const string SurveySelect = @"
        SELECT s.survey_id          AS SurveyId,
               s.survey_no          AS SurveyNo,
               s.arrival_id         AS ArrivalId,
               s.source_code        AS SourceCode,
               s.source_qo_id       AS SourceQoId,
               sq.quality_order_no  AS SourceQoNo,
               s.method_code        AS MethodCode,
               s.status_code        AS StatusCode,
               s.request_reason     AS RequestReason,
               s.request_comments   AS RequestComments,
               s.requested_at       AS RequestedAt,
               s.requested_by       AS RequestedBy,
               s.decided_at         AS DecidedAt,
               s.decided_by         AS DecidedBy,
               s.decision_comments  AS DecisionComments,
               s.followup_error     AS FollowupError,
               s.followup_failed_at AS FollowupFailedAt,
               s.updated_at         AS UpdatedAt,
               s.updated_by         AS UpdatedBy,
               a.arrival_no         AS ArrivalNo,
               a.status_code        AS ArrivalStatus,
               a.container_no       AS ContainerNo,
               a.bol_no             AS BolNo,
               a.ebeln              AS Ebeln,
               a.vendor_no          AS VendorNo,
               a.vendor_name        AS VendorName,
               a.plant              AS Plant,
               -- The branch goods receipt; arrival_date is its legacy twin.
               -- Same expression as the Claims list next door.
               COALESCE(sh.receive_date, sh.arrival_date) AS ReceiveDate,
               it.products          AS Products,
               ISNULL(it.product_count, 0) AS ProductCount,
               it.origins           AS Origins,
               qo.quality_order_id  AS QualityOrderId,
               qo.quality_order_no  AS QualityOrderNo,
               qo.status_code       AS QualityOrderStatus,
               qo.archived_at       AS QoArchivedAt,
               cl.claim_id          AS ClaimId,
               cl.claim_status      AS ClaimStatus,
               cl.created_at        AS ClaimCreatedAt,
               COUNT(*) OVER ()     AS TotalCount
        FROM   qms_survey s
        JOIN   qms_arrival a ON a.arrival_id = s.arrival_id
        LEFT   JOIN qms_shipment_snapshot sh ON sh.arrival_id = a.arrival_id
        LEFT   JOIN qms_quality_order sq ON sq.quality_order_id = s.source_qo_id
        OUTER  APPLY (
            SELECT TOP 1 q.quality_order_id, q.quality_order_no, q.status_code, q.archived_at
            FROM   qms_quality_order q
            WHERE  q.arrival_id = a.arrival_id AND q.status_code <> 'Cancelled'
              AND  q.superseded_at IS NULL
            ORDER  BY q.quality_order_id DESC
        ) qo
        LEFT   JOIN qms_claim cl ON cl.quality_order_id = qo.quality_order_id
        -- Products and origins come from the ARRIVAL's lines, so they are
        -- there before any quality order exists. SQL Server 2016: FOR XML PATH
        -- + STUFF, not STRING_AGG. Products are newline-separated because a
        -- description can contain a comma.
        OUTER  APPLY (
            SELECT product_count = COUNT(DISTINCT i3.material_desc),
                   products = STUFF((
                        SELECT CHAR(10) + d.material_desc
                        FROM  (SELECT DISTINCT i2.material_desc
                               FROM   qms_arrival_item i2
                               WHERE  i2.arrival_id = a.arrival_id
                                 AND  i2.material_desc IS NOT NULL) d
                        ORDER BY d.material_desc
                        FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 1, ''),
                   origins = STUFF((
                        SELECT ', ' + o.origin_name
                        FROM  (SELECT DISTINCT mc.origin_name
                               FROM   qms_arrival_item i4
                               JOIN   qms_sap_material_cache mc ON mc.material_no = i4.material_no
                               WHERE  i4.arrival_id = a.arrival_id
                                 AND  NULLIF(mc.origin_name, N'') IS NOT NULL) o
                        ORDER BY o.origin_name
                        FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, '')
            FROM   qms_arrival_item i3
            WHERE  i3.arrival_id = a.arrival_id
        ) it";

    private const string ClaimRequestStatuses = "('ClaimRequest', 'ClaimRequestApproved', 'HoldClaim')";

    public async Task<SurveyBrief?> GetBriefForArrivalAsync(long arrivalId)
    {
        using var c = Open();
        return await c.QuerySingleOrDefaultAsync<SurveyBrief>(@"
            SELECT survey_id AS SurveyId, survey_no AS SurveyNo, status_code AS StatusCode
            FROM   qms_survey WHERE arrival_id = @arrivalId", new { arrivalId });
    }

    public async Task<Survey?> GetAsync(long surveyId)
    {
        using var c = Open();
        return await c.QuerySingleOrDefaultAsync<Survey>(
            SurveySelect + " WHERE s.survey_id = @surveyId", new { surveyId });
    }

    public async Task<SurveyPage> ListAsync(SurveyListFilter f, PlantScope scope)
    {
        // Clamped to the sizes the pager offers, as on the Claims list.
        var pageSize = f.PageSize is 25 or 50 or 100 ? f.PageSize : 50;
        var page     = f.Page < 1 ? 1 : f.Page;
        var rows     = await QueryAsync(f, scope, (page - 1) * pageSize, pageSize);
        return new SurveyPage(rows, rows.Count > 0 ? rows[0].TotalCount : 0, page, pageSize);
    }

    public async Task<IReadOnlyList<SurveyListRow>> ListForExportAsync(
        SurveyListFilter f, PlantScope scope, int max) =>
        await QueryAsync(f, scope, 0, Math.Max(1, max));

    private async Task<List<SurveyListRow>> QueryAsync(
        SurveyListFilter f, PlantScope scope, int offset, int take)
    {
        var statuses  = FilterValues.Many(f.Status).Where(s => SurveyStatus.All.Contains(s)).ToList();
        var plants    = FilterValues.Many(f.Plant);
        var suppliers = FilterValues.Many(f.Supplier);
        var source    = f.Source is SurveySource.Arrival or SurveySource.QualityOrder ? f.Source : null;
        var milestone = f.Milestone is SurveyMilestone.AwaitingQcOrder or SurveyMilestone.WithClaim
            ? f.Milestone : null;

        // The date boxes are the reader's LOCAL days; requested_at is UTC.
        DateTime? fromUtc = f.From.HasValue
            ? DateTime.SpecifyKind(f.From.Value.Date, DateTimeKind.Local).ToUniversalTime()
            : null;
        DateTime? toUtc = f.To.HasValue
            ? DateTime.SpecifyKind(f.To.Value.Date.AddDays(1), DateTimeKind.Local).ToUniversalTime()
            : null;

        const string where = @"
            WHERE (@sUnrestricted = 1 OR a.plant IN @sPlants)
              AND (@statusAny   = 0 OR s.status_code IN @statuses)
              AND (@source IS NULL  OR s.source_code = @source)
              AND (@plantAny    = 0 OR a.plant       IN @plants)
              AND (@supplierAny = 0 OR a.vendor_name IN @suppliers)
              AND (@fromUtc IS NULL OR s.requested_at >= @fromUtc)
              AND (@toUtc   IS NULL OR s.requested_at <  @toUtc)
              AND (@milestone IS NULL
                   OR (@milestone = 'awaitingqo' AND s.status_code = 'Confirm'
                       AND qo.quality_order_id IS NULL)
                   OR (@milestone = 'withclaim'  AND s.status_code = 'Confirm'
                       AND cl.claim_status IN " + ClaimRequestStatuses + @"))
              AND (@search IS NULL OR
                   s.survey_no         LIKE '%' + @search + '%' OR
                   a.arrival_no        LIKE '%' + @search + '%' OR
                   a.container_no      LIKE '%' + @search + '%' OR
                   a.bol_no            LIKE '%' + @search + '%' OR
                   a.ebeln             LIKE '%' + @search + '%' OR
                   a.vendor_name       LIKE '%' + @search + '%' OR
                   qo.quality_order_no LIKE '%' + @search + '%' OR
                   s.requested_by      LIKE '%' + @search + '%')
            ORDER BY s.requested_at DESC, s.survey_id DESC
            OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY";

        using var c = Open();
        var rows = await c.QueryAsync<SurveyListRow>(SurveySelect + where, new
        {
            offset, take,
            sUnrestricted = scope.Unrestricted,
            sPlants       = scope.QueryPlants,
            statuses  = FilterValues.ForIn(statuses),  statusAny   = statuses.Count  > 0,
            plants    = FilterValues.ForIn(plants),    plantAny    = plants.Count    > 0,
            suppliers = FilterValues.ForIn(suppliers), supplierAny = suppliers.Count > 0,
            source, milestone, fromUtc, toUtc,
            search = string.IsNullOrWhiteSpace(f.Search) ? null : f.Search.Trim()
        });
        return rows.ToList();
    }

    public async Task<SurveyCounts> CountsAsync(PlantScope scope)
    {
        using var c = Open();
        return await c.QuerySingleAsync<SurveyCounts>(@"
            SELECT COUNT(*)                                                       AS Total,
                   ISNULL(SUM(CASE WHEN s.status_code = 'Open'    THEN 1 ELSE 0 END), 0) AS [Open],
                   ISNULL(SUM(CASE WHEN s.status_code = 'Confirm' THEN 1 ELSE 0 END), 0) AS Confirmed,
                   ISNULL(SUM(CASE WHEN s.status_code = 'Pass'    THEN 1 ELSE 0 END), 0) AS Passed,
                   ISNULL(SUM(CASE WHEN s.status_code = 'Confirm' AND qo.quality_order_id IS NULL
                                   THEN 1 ELSE 0 END), 0)                          AS AwaitingQcOrder,
                   ISNULL(SUM(CASE WHEN s.status_code = 'Confirm'
                                    AND cl.claim_status IN " + ClaimRequestStatuses + @"
                                   THEN 1 ELSE 0 END), 0)                          AS WithClaim
            FROM   qms_survey s
            JOIN   qms_arrival a ON a.arrival_id = s.arrival_id
            OUTER  APPLY (
                SELECT TOP 1 q.quality_order_id
                FROM   qms_quality_order q
                WHERE  q.arrival_id = a.arrival_id AND q.status_code <> 'Cancelled'
                  AND  q.superseded_at IS NULL
                ORDER  BY q.quality_order_id DESC
            ) qo
            LEFT   JOIN qms_claim cl ON cl.quality_order_id = qo.quality_order_id
            WHERE  (@sUnrestricted = 1 OR a.plant IN @sPlants)",
            new { sUnrestricted = scope.Unrestricted, sPlants = scope.QueryPlants });
    }

    public async Task<SurveyFilterOptions> GetFilterOptionsAsync(PlantScope scope)
    {
        using var c = Open();
        var rows = (await c.QueryAsync<(string? Plant, string? Supplier)>(@"
            SELECT DISTINCT a.plant AS Plant, a.vendor_name AS Supplier
            FROM   qms_survey s
            JOIN   qms_arrival a ON a.arrival_id = s.arrival_id
            WHERE  (@sUnrestricted = 1 OR a.plant IN @sPlants)",
            new { sUnrestricted = scope.Unrestricted, sPlants = scope.QueryPlants })).ToList();
        return new SurveyFilterOptions
        {
            Plants = rows.Select(r => r.Plant).Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p).ToList(),
            Suppliers = rows.Select(r => r.Supplier).Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p).ToList(),
        };
    }

    // ---- Request ----------------------------------------------------

    private sealed class ArrivalForRequest
    {
        public string    StatusCode  { get; set; } = "";
        public bool?     JointSurvey { get; set; }
        public bool      HasSnapshot { get; set; }
        public DateTime? ArchivedAt  { get; set; }
    }

    public async Task<SurveyRequestResult> RequestAsync(long arrivalId, string source, long? sourceQoId,
        string? reason, string? comments, string user)
    {
        reason   = (reason ?? "").Trim();
        comments = string.IsNullOrWhiteSpace(comments) ? null : comments.Trim();
        if (reason.Length < MinReasonLength)
            return SurveyRequestResult.Fail($"Say why a survey is needed (at least {MinReasonLength} characters).");
        if (reason.Length > MaxReasonLength)
            return SurveyRequestResult.Fail($"The reason is too long (limit {MaxReasonLength} characters). Put the detail in the comments.");
        if (comments is { Length: > MaxCommentsLength })
            return SurveyRequestResult.Fail($"The comments are too long (limit {MaxCommentsLength} characters).");
        if (source != SurveySource.Arrival && source != SurveySource.QualityOrder)
            return SurveyRequestResult.Fail("Unknown request source.");
        if (source == SurveySource.Arrival) sourceQoId = null;

        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();

        // Take the arrival's slot before anything else. UPDLOCK + HOLDLOCK on
        // the unique key makes a second request for the SAME arrival wait
        // here until this one commits, and then find the survey -- so two
        // people asking at once get one survey, not an error. The unique
        // constraint is still the backstop (caught below).
        var existing = await c.QuerySingleOrDefaultAsync<SurveyBrief>(@"
            SELECT survey_id AS SurveyId, survey_no AS SurveyNo, status_code AS StatusCode
            FROM   qms_survey WITH (UPDLOCK, HOLDLOCK)
            WHERE  arrival_id = @arrivalId", new { arrivalId }, tx);
        if (existing != null)
        {
            tx.Rollback();
            return Existing(existing);
        }

        var a = await c.QuerySingleOrDefaultAsync<ArrivalForRequest>(@"
            SELECT a.status_code   AS StatusCode,
                   sh.joint_survey AS JointSurvey,
                   CAST(CASE WHEN sh.arrival_id IS NULL THEN 0 ELSE 1 END AS bit) AS HasSnapshot,
                   qo.archived_at  AS ArchivedAt
            FROM   qms_arrival a
            LEFT   JOIN qms_shipment_snapshot sh ON sh.arrival_id = a.arrival_id
            OUTER  APPLY (
                SELECT TOP 1 q.archived_at
                FROM   qms_quality_order q
                WHERE  q.arrival_id = a.arrival_id AND q.status_code <> 'Cancelled'
                  AND  q.superseded_at IS NULL
                ORDER  BY q.quality_order_id DESC
            ) qo
            WHERE  a.arrival_id = @arrivalId", new { arrivalId }, tx);
        if (a == null)
        {
            tx.Rollback();
            return SurveyRequestResult.Fail("Arrival not found.");
        }
        if (a.StatusCode == ArrivalStatus.Cancelled)
        {
            tx.Rollback();
            return SurveyRequestResult.Fail("This arrival is cancelled, so a survey cannot be requested for it.");
        }
        if (a.ArchivedAt.HasValue)
        {
            tx.Rollback();
            return SurveyRequestResult.Fail("This container's quality order is archived, so a survey cannot be requested for it.");
        }
        if (source == SurveySource.QualityOrder)
        {
            var belongs = sourceQoId.HasValue && await c.ExecuteScalarAsync<int>(@"
                SELECT COUNT(1) FROM qms_quality_order
                WHERE  quality_order_id = @sourceQoId AND arrival_id = @arrivalId",
                new { sourceQoId, arrivalId }, tx) > 0;
            if (!belongs)
            {
                tx.Rollback();
                return SurveyRequestResult.Fail("That quality order does not belong to this arrival.");
            }
        }

        // Same shape as arrival and quality order numbers: the year is a
        // label, the sequence never restarts.
        var seq = await c.ExecuteScalarAsync<int>(
            "SELECT NEXT VALUE FOR seq_qms_survey_no", transaction: tx);
        var surveyNo = $"SRV-{DateTime.UtcNow:yyyy}-{seq:D6}";

        long surveyId;
        try
        {
            surveyId = await c.ExecuteScalarAsync<long>(@"
                INSERT INTO qms_survey
                    (survey_no, arrival_id, source_code, source_qo_id, method_code, status_code,
                     request_reason, request_comments, requested_at, requested_by, updated_at, updated_by)
                VALUES (@surveyNo, @arrivalId, @source, @sourceQoId, @method, @status,
                        @reason, @comments, SYSUTCDATETIME(), @user, SYSUTCDATETIME(), @user);
                SELECT CAST(SCOPE_IDENTITY() AS BIGINT);",
                new
                {
                    surveyNo, arrivalId, source, sourceQoId,
                    method = SurveyMethod.Manual, status = SurveyStatus.Open,
                    reason, comments, user
                }, tx);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // Somebody else's request won the unique key. Theirs is the survey.
            tx.Rollback();
            var theirs = await GetBriefForArrivalAsync(arrivalId);
            return theirs != null
                ? Existing(theirs)
                : SurveyRequestResult.Fail("The survey could not be created. Please try again.");
        }

        // Joint Survey on the arrival IS the request, so the two never
        // disagree: asking from the quality order answers it too.
        if (a.HasSnapshot && a.JointSurvey != true)
        {
            await c.ExecuteAsync(
                "UPDATE qms_shipment_snapshot SET joint_survey = 1 WHERE arrival_id = @arrivalId",
                new { arrivalId }, tx);
            await _audit.WriteAsync(c, tx,
                EntityTypes.Arrival, arrivalId, ActionCodes.Updated,
                oldValues: new { JointSurvey = a.JointSurvey },
                newValues: new { JointSurvey = (bool?)true },
                actor: user);
        }

        await c.ExecuteAsync(@"
            INSERT INTO qms_status_history (entity_type, entity_id, old_status, new_status, reason, changed_at, changed_by)
            VALUES ('Survey', @surveyId, NULL, @status, @reason, SYSUTCDATETIME(), @user)",
            new { surveyId, status = SurveyStatus.Open, reason, user }, tx);

        await _audit.WriteAsync(c, tx,
            EntityTypes.Survey, surveyId, ActionCodes.Created,
            oldValues: null,
            newValues: new
            {
                survey_no = surveyNo, arrival_id = arrivalId, source_code = source,
                source_qo_id = sourceQoId, method_code = SurveyMethod.Manual,
                status_code = SurveyStatus.Open, request_reason = reason, request_comments = comments
            },
            actor: user);

        tx.Commit();
        return new SurveyRequestResult(true, false, surveyId, surveyNo, SurveyStatus.Open, null);
    }

    private static SurveyRequestResult Existing(SurveyBrief s) =>
        new(true, true, s.SurveyId, s.SurveyNo, s.StatusCode, null);

    // ---- Decision ---------------------------------------------------

    private sealed class DecisionState
    {
        public string  StatusCode { get; set; } = "";
        public string? DecidedBy  { get; set; }
    }

    public async Task<(bool ok, string? error)> DecideAsync(long surveyId, bool confirm, string? comments, string user)
    {
        comments = string.IsNullOrWhiteSpace(comments) ? null : comments.Trim();
        if (comments is { Length: > MaxCommentsLength })
            return (false, $"The comments are too long (limit {MaxCommentsLength} characters).");
        // Passing says "no survey is needed": that needs a reason on record.
        if (!confirm && (comments?.Length ?? 0) < MinReasonLength)
            return (false, $"Say why the survey is passed (at least {MinReasonLength} characters).");

        var newStatus = confirm ? SurveyStatus.Confirm : SurveyStatus.Pass;

        using var c = Open();
        await c.OpenAsync();
        using var tx = c.BeginTransaction();

        // The WHERE is the rule: only an Open survey can be decided, and only
        // once. Whoever's update lands second changes no row.
        var changed = await c.ExecuteAsync(@"
            UPDATE qms_survey
            SET    status_code = @newStatus, decided_at = SYSUTCDATETIME(), decided_by = @user,
                   decision_comments = @comments, updated_at = SYSUTCDATETIME(), updated_by = @user
            WHERE  survey_id = @surveyId AND status_code = @open",
            new { newStatus, user, comments, surveyId, open = SurveyStatus.Open }, tx);
        if (changed == 0)
        {
            var cur = await c.QuerySingleOrDefaultAsync<DecisionState>(@"
                SELECT status_code AS StatusCode, decided_by AS DecidedBy
                FROM   qms_survey WHERE survey_id = @surveyId", new { surveyId }, tx);
            tx.Rollback();
            if (cur == null) return (false, "Survey not found.");
            var by = string.IsNullOrWhiteSpace(cur.DecidedBy) ? "" : $" by {cur.DecidedBy}";
            return (false, $"This survey was already {SurveyStatus.Label(cur.StatusCode).ToLowerInvariant()}{by}. A decision cannot be changed.");
        }

        var historyReason = comments is { Length: > 500 } ? comments[..500] : comments;
        await c.ExecuteAsync(@"
            INSERT INTO qms_status_history (entity_type, entity_id, old_status, new_status, reason, changed_at, changed_by)
            VALUES ('Survey', @surveyId, @open, @newStatus, @reason, SYSUTCDATETIME(), @user)",
            new { surveyId, open = SurveyStatus.Open, newStatus, reason = historyReason, user }, tx);

        await _audit.WriteAsync(c, tx,
            EntityTypes.Survey, surveyId,
            confirm ? ActionCodes.SurveyConfirmed : ActionCodes.SurveyPassed,
            oldValues: new { status_code = SurveyStatus.Open },
            newValues: new { status_code = newStatus, decision_comments = comments },
            actor: user);

        tx.Commit();
        return (true, null);
    }
}
