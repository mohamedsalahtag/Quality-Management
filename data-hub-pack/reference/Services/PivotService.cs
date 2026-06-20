using System.Data;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using SharbatlyQMS.Web.Models.Reports;

namespace SharbatlyQMS.Web.Services.Reports;

public class PivotService : IPivotService
{
    private const string NullSentinel = "(null)";

    private readonly string _cs;
    private readonly ILogger<PivotService> _log;
    private readonly IMemoryCache _cache;

    public PivotService(IConfiguration config, ILogger<PivotService> log, IMemoryCache cache)
    {
        _cs = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default missing");
        _log = log;
        _cache = cache;
    }

    public async Task<PivotResult> RunAsync(PivotRequest request, CancellationToken ct)
    {
        // ---- 1. Validate against the registry ----------------------------
        if (string.IsNullOrWhiteSpace(request.ReportKey)
            || !PivotRegistry.All.TryGetValue(request.ReportKey, out var report))
            throw new ArgumentException($"Unknown report '{request.ReportKey}'");

        var rowDims = ResolveDims(report, request.Rows, "row");
        var colDims = ResolveDims(report, request.Cols, "col");
        if (rowDims.Count == 0 && colDims.Count == 0)
            throw new ArgumentException("Pick at least one row or column dimension.");

        // ---- 1b. Resolve the requested measures ---------------------------
        // V34.3: multi-measure. The legacy single-measure request shape
        // (Measure + Agg scalars) is auto-promoted into a one-element list
        // here so older saved perspectives still load. Every measure resolves
        // to a (PivotMeasure, agg, format, label) tuple.
        var measureSpecs = new List<MeasureSpec>();
        if (request.Measures != null && request.Measures.Count > 0)
        {
            foreach (var m in request.Measures)
            {
                measureSpecs.Add(ResolveMeasure(report, m.Key, m.Agg, m.Format, m.Label));
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.Measure))
        {
            measureSpecs.Add(ResolveMeasure(report, request.Measure, request.Agg, null, null));
        }
        else
        {
            throw new ArgumentException("At least one measure is required.");
        }
        // Dedupe (key, agg) so we don't compute the same SQL expression twice.
        var seenSig = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        measureSpecs = measureSpecs
            .Where(ms => seenSig.Add(ms.Measure.Key + "|" + ms.Agg))
            .ToList();

        // ---- 2. Build SQL -------------------------------------------------
        // dimN expressions live in the registry (constant strings); rowN /
        // colN / vM are positional aliases the rest of this method indexes on.
        var sb = new StringBuilder();
        sb.Append("SELECT ");
        for (int i = 0; i < rowDims.Count; i++)
            sb.Append("CAST(").Append(rowDims[i].SqlExpression).Append(" AS NVARCHAR(200)) AS row").Append(i).Append(", ");
        for (int i = 0; i < colDims.Count; i++)
            sb.Append("CAST(").Append(colDims[i].SqlExpression).Append(" AS NVARCHAR(200)) AS col").Append(i).Append(", ");

        for (int i = 0; i < measureSpecs.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(BuildAggExpr(measureSpecs[i])).Append(" AS v").Append(i);
        }

        sb.Append(" FROM ").Append(report.ViewName);

        // ---- 3. Filter WHERE ---------------------------------------------
        // Two layers: (a) page filter from the host data hub above the analyzer
        // (skipped when IgnorePageFilter), and (b) analyzer-only drill filters
        // that always apply on top. All identifiers come from the registry --
        // drill values bind as parameters.
        var p = new DynamicParameters();
        sb.Append(" WHERE 1 = 1");
        if (!request.IgnorePageFilter)
            AppendFilterWhere(sb, p, request.Filter ?? new FlatDefectFilter());
        AppendDrillWhere(sb, p, report, request.Drills);

        // ---- 4. GROUP BY -------------------------------------------------
        if (rowDims.Count + colDims.Count > 0)
        {
            sb.Append(" GROUP BY ");
            for (int i = 0; i < rowDims.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(rowDims[i].SqlExpression);
            }
            for (int i = 0; i < colDims.Count; i++)
            {
                if (rowDims.Count + i > 0) sb.Append(", ");
                sb.Append(colDims[i].SqlExpression);
            }
            sb.Append(" ORDER BY ");
            for (int i = 0; i < rowDims.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(rowDims[i].SqlExpression);
            }
            for (int i = 0; i < colDims.Count; i++)
            {
                if (rowDims.Count + i > 0) sb.Append(", ");
                sb.Append(colDims[i].SqlExpression);
            }
        }

        var sql = sb.ToString();
        _log.LogDebug("PivotService SQL: {Sql}", sql);

        // ---- 5. Execute ---------------------------------------------------
        // Dapper's ExecuteReaderAsync returns IDataReader, but the concrete
        // type (Dapper.DbWrappedReader) inherits from System.Data.Common.DbDataReader,
        // which is the one with ReadAsync/IsDBNullAsync etc.
        int measureCount = measureSpecs.Count;
        var rows = new List<RawRow>();
        using (var c = new SqlConnection(_cs))
        {
            await c.OpenAsync(ct);
            using var reader = await c.ExecuteReaderAsync(new CommandDefinition(
                sql, p, commandTimeout: 120, cancellationToken: ct));
            var dbReader = (System.Data.Common.DbDataReader)reader;
            int firstMeasureOrdinal = rowDims.Count + colDims.Count;
            while (await dbReader.ReadAsync(ct))
            {
                var rowKey = new string[rowDims.Count];
                for (int i = 0; i < rowDims.Count; i++)
                    rowKey[i] = dbReader.IsDBNull(i) ? "(null)" : dbReader.GetString(i);
                var colKey = new string[colDims.Count];
                for (int i = 0; i < colDims.Count; i++)
                    colKey[i] = dbReader.IsDBNull(rowDims.Count + i)
                        ? "(null)" : dbReader.GetString(rowDims.Count + i);
                var values = new decimal?[measureCount];
                for (int m = 0; m < measureCount; m++)
                {
                    int o = firstMeasureOrdinal + m;
                    values[m] = dbReader.IsDBNull(o)
                        ? (decimal?)null
                        : Convert.ToDecimal(dbReader.GetValue(o));
                }
                rows.Add(new RawRow(rowKey, colKey, values));
            }
        }

        // ---- 6. Materialise into matrix coordinates -----------------------
        var rowKeyList = rows.Select(r => r.RowKey)
            .Distinct(StringArrayComparer.Instance)
            .ToList();
        var colKeyList = rows.Select(r => r.ColKey)
            .Distinct(StringArrayComparer.Instance)
            .ToList();

        // Optional Top-N by row total of the FIRST measure. (Multi-measure
        // ranking by anything other than measure[0] would need a UI control;
        // defer until asked.)
        bool truncated = false;
        if (request.TopN.HasValue && request.TopN.Value > 0 && rowKeyList.Count > request.TopN.Value)
        {
            var rowTotals = new Dictionary<string[], decimal>(StringArrayComparer.Instance);
            foreach (var rc in rows)
            {
                rowTotals.TryGetValue(rc.RowKey, out var t);
                rowTotals[rc.RowKey] = t + (rc.Values[0] ?? 0m);
            }
            rowKeyList = rowKeyList
                .OrderByDescending(rk => rowTotals.TryGetValue(rk, out var t) ? t : 0m)
                .Take(request.TopN.Value)
                .ToList();
            var keep = new HashSet<string[]>(rowKeyList, StringArrayComparer.Instance);
            rows = rows.Where(r => keep.Contains(r.RowKey)).ToList();
            truncated = true;
        }

        var rowIndex = rowKeyList
            .Select((k, i) => (k, i))
            .ToDictionary(t => t.k, t => t.i, StringArrayComparer.Instance);
        var colIndex = colKeyList
            .Select((k, i) => (k, i))
            .ToDictionary(t => t.k, t => t.i, StringArrayComparer.Instance);

        var cells = new List<PivotCell>(rows.Count);
        // Per-(axis,measure) totals.
        var rowTotalsArr = new decimal[rowKeyList.Count][];
        for (int i = 0; i < rowKeyList.Count; i++) rowTotalsArr[i] = new decimal[measureCount];
        var colTotalsArr = new decimal[Math.Max(colKeyList.Count, 1)][];
        for (int i = 0; i < colTotalsArr.Length; i++) colTotalsArr[i] = new decimal[measureCount];
        var grandTotals  = new decimal[measureCount];

        foreach (var rc in rows)
        {
            int ri = rowIndex[rc.RowKey];
            int ci = colKeyList.Count == 0 ? 0 : colIndex[rc.ColKey];
            cells.Add(new PivotCell { RowIndex = ri, ColIndex = ci, Values = rc.Values });
            for (int m = 0; m < measureCount; m++)
            {
                var v = rc.Values[m] ?? 0m;
                rowTotalsArr[ri][m] += v;
                colTotalsArr[ci][m] += v;
                grandTotals[m]      += v;
            }
        }

        // When colKeyList is empty the colTotalsArr we created (length 1) is
        // logical noise -- match the public contract by emitting an empty
        // array so the JS rendering is unambiguous.
        if (colKeyList.Count == 0) colTotalsArr = Array.Empty<decimal[]>();

        return new PivotResult
        {
            RowDimensions = rowDims.Select(d => d.Display).ToArray(),
            ColDimensions = colDims.Select(d => d.Display).ToArray(),
            Measures      = measureSpecs.Select(ms => new PivotMeasureInfo
            {
                Key    = ms.Measure.Key,
                Label  = ms.Label  ?? ms.Measure.Display,
                Agg    = ms.Agg,
                Format = ms.Format,
            }).ToArray(),
            RowKeys     = rowKeyList.ToArray(),
            ColKeys     = colKeyList.ToArray(),
            Cells       = cells.ToArray(),
            RowTotals   = rowTotalsArr,
            ColTotals   = colTotalsArr,
            GrandTotals = grandTotals,
            RowsScanned = rows.Count,
            Truncated   = truncated,
        };
    }

    /// <summary>V34.3 -- resolve one measure-spec triple against the registry.
    /// Used by both the new <c>Measures[]</c> path and the legacy single-measure
    /// fallback so validation stays in one place.</summary>
    private static MeasureSpec ResolveMeasure(PivotReport report, string? key, string? agg,
        string? format, string? label)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Measure key is required.");
        var measure = report.Measures.FirstOrDefault(m =>
            string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown measure '{key}'");
        var aggNorm = (agg ?? "").Trim().ToUpperInvariant();
        if (!PivotAggregations.IsValid(aggNorm))
            throw new ArgumentException($"Unknown aggregation '{agg}'");
        if (Array.IndexOf(measure.AllowedAggs, aggNorm) < 0)
            throw new ArgumentException($"{aggNorm} is not allowed for measure '{measure.Key}'.");
        return new MeasureSpec(measure, aggNorm, format, label);
    }

    private static string BuildAggExpr(MeasureSpec ms) => ms.Agg switch
    {
        PivotAggregations.Count when ms.Measure.SqlInner == "*" => "COUNT(*)",
        PivotAggregations.Count                                 => $"COUNT({ms.Measure.SqlInner})",
        PivotAggregations.CountDistinct                         => $"COUNT(DISTINCT {ms.Measure.SqlInner})",
        _                                                       => $"{ms.Agg}({ms.Measure.SqlInner})",
    };

    private sealed record MeasureSpec(PivotMeasure Measure, string Agg, string? Format, string? Label);

    // -----------------------------------------------------------------------

    private static List<PivotDimension> ResolveDims(PivotReport report, string[] keys, string slot)
    {
        var result = new List<PivotDimension>(keys.Length);
        foreach (var k in keys ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(k)) continue;
            var d = report.Dimensions.FirstOrDefault(x =>
                string.Equals(x.Key, k, StringComparison.OrdinalIgnoreCase));
            if (d == null) throw new ArgumentException($"Unknown {slot} dimension '{k}'");
            result.Add(d);
        }
        return result;
    }

    /// <summary>
    /// Reproduces the WHERE fragment from QualityOrderService.StreamFlatDefectRowsAsync,
    /// adapted to the flat view's column aliases. Keeps the analyzer's filter
    /// semantics in lockstep with the data hub above it.
    /// </summary>
    private static void AppendFilterWhere(StringBuilder sb, DynamicParameters p, FlatDefectFilter f)
    {
        if (f.QualityOrderId.HasValue)
        {
            sb.Append(" AND QualityOrderId = @qoId");
            p.Add("qoId", f.QualityOrderId.Value, DbType.Int64);
        }
        if (f.ArrivalId.HasValue)
        {
            sb.Append(" AND ArrivalId = @arrivalId");
            p.Add("arrivalId", f.ArrivalId.Value, DbType.Int64);
        }
        if (!string.IsNullOrWhiteSpace(f.Status))
        {
            sb.Append(" AND QoStatus = @status");
            p.Add("status", f.Status, DbType.AnsiString);
        }
        if (!string.IsNullOrWhiteSpace(f.Plant))
        {
            sb.Append(" AND Plant = @plant");
            p.Add("plant", f.Plant, DbType.AnsiString);
        }
        if (!string.IsNullOrWhiteSpace(f.MaterialGroup))
        {
            sb.Append(" AND MaterialGroup = @matGroup");
            p.Add("matGroup", f.MaterialGroup, DbType.AnsiString);
        }
        if (!string.IsNullOrWhiteSpace(f.MajorCategory))
        {
            sb.Append(" AND MajorCategory = @majorCat");
            p.Add("majorCat", f.MajorCategory, DbType.String);
        }
        if (!string.IsNullOrWhiteSpace(f.Variety))
        {
            sb.Append(" AND Variety = @variety");
            p.Add("variety", f.Variety, DbType.String);
        }
        if (!string.IsNullOrWhiteSpace(f.Origin))
        {
            sb.Append(" AND Origin = @origin");
            p.Add("origin", f.Origin, DbType.String);
        }
        if (!string.IsNullOrWhiteSpace(f.MaterialClass))
        {
            sb.Append(" AND MaterialClass = @matClass");
            p.Add("matClass", f.MaterialClass, DbType.String);
        }
        if (!string.IsNullOrWhiteSpace(f.VendorName))
        {
            sb.Append(" AND VendorName LIKE '%' + @vendorName + '%'");
            p.Add("vendorName", f.VendorName, DbType.String);
        }
        if (!string.IsNullOrWhiteSpace(f.VendorNo))
        {
            sb.Append(" AND VendorNo = @vendorNo");
            p.Add("vendorNo", f.VendorNo, DbType.AnsiString);
        }
        if (!string.IsNullOrWhiteSpace(f.StorageLocation))
        {
            sb.Append(" AND StorageLocation = @storageLoc");
            p.Add("storageLoc", f.StorageLocation, DbType.AnsiString);
        }
        if (!string.IsNullOrWhiteSpace(f.ContainerNo))
        {
            sb.Append(" AND ContainerNo = @containerNo");
            p.Add("containerNo", f.ContainerNo, DbType.AnsiString);
        }
        if (!string.IsNullOrWhiteSpace(f.BolNo))
        {
            sb.Append(" AND BolNo = @bolNo");
            p.Add("bolNo", f.BolNo, DbType.AnsiString);
        }
        if (!string.IsNullOrWhiteSpace(f.Ebeln))
        {
            sb.Append(" AND Ebeln = @ebeln");
            p.Add("ebeln", f.Ebeln, DbType.AnsiString);
        }
        if (!string.IsNullOrWhiteSpace(f.SampleScope))
        {
            sb.Append(" AND SampleScope = @sampleScope");
            p.Add("sampleScope", f.SampleScope, DbType.AnsiString);
        }
        // PO date range. Mirror the data-hub semantics: include NULL PoDate
        // rows so arrivals whose SAP cache row is missing still surface in
        // the unfiltered window. End-of-day is rolled forward when the user
        // picks a whole date so 'to = today' includes today's POs.
        if (f.PoFrom.HasValue)
        {
            sb.Append(" AND (PoDate IS NULL OR PoDate >= @poFrom)");
            p.Add("poFrom", f.PoFrom.Value.Date, DbType.Date);
        }
        if (f.PoTo.HasValue)
        {
            var poToExcl = f.PoTo.Value.TimeOfDay == TimeSpan.Zero
                ? f.PoTo.Value.AddDays(1)
                : f.PoTo.Value;
            sb.Append(" AND (PoDate IS NULL OR PoDate < @poToExcl)");
            p.Add("poToExcl", poToExcl.Date, DbType.Date);
        }
    }

    /// <summary>
    /// V34.1 (2026-06-20). Emits a parameterised <c>AND ({expr}) IN (...)</c>
    /// per drill. Dim keys must exist in the registry (matches the same
    /// guardrail as Rows/Cols). "(null)" sentinel maps to IS NULL.
    /// </summary>
    private static void AppendDrillWhere(StringBuilder sb, DynamicParameters p,
        PivotReport report, List<PivotDrillFilter>? drills)
    {
        if (drills == null || drills.Count == 0) return;
        int slot = 0;
        foreach (var drill in drills)
        {
            if (drill?.DimensionKey == null) continue;
            var dim = report.Dimensions.FirstOrDefault(d =>
                string.Equals(d.Key, drill.DimensionKey, StringComparison.OrdinalIgnoreCase));
            if (dim == null)
                throw new ArgumentException($"Unknown drill dimension '{drill.DimensionKey}'");
            var values = drill.Values ?? Array.Empty<string>();
            if (values.Length == 0) continue;

            bool wantsNull = false;
            var concrete = new List<string>(values.Length);
            foreach (var v in values)
            {
                if (string.Equals(v, NullSentinel, StringComparison.Ordinal))
                    wantsNull = true;
                else
                    concrete.Add(v ?? "");
            }

            sb.Append(" AND (");
            bool first = true;
            if (wantsNull)
            {
                sb.Append('(').Append(dim.SqlExpression).Append(") IS NULL");
                first = false;
            }
            if (concrete.Count > 0)
            {
                if (!first) sb.Append(" OR ");
                sb.Append("CAST(").Append(dim.SqlExpression).Append(" AS NVARCHAR(200)) IN (");
                for (int i = 0; i < concrete.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    var name = "drill_" + slot + "_" + i;
                    sb.Append('@').Append(name);
                    p.Add(name, concrete[i], DbType.String);
                }
                sb.Append(')');
            }
            sb.Append(')');
            slot++;
        }
    }

    public async Task<(IReadOnlyList<string> Values, bool Truncated)> GetDistinctValuesAsync(
        string reportKey, string dimKey, FlatDefectFilter filter,
        bool ignorePageFilter, string? search, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reportKey)
            || !PivotRegistry.All.TryGetValue(reportKey, out var report))
            throw new ArgumentException($"Unknown report '{reportKey}'");
        var dim = report.Dimensions.FirstOrDefault(d =>
            string.Equals(d.Key, dimKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown dimension '{dimKey}'");

        var trimmed = (search ?? "").Trim();
        var cacheKey = "pivotvals:" + report.Key + ":" + dim.Key + ":"
            + (ignorePageFilter ? "all" : "scoped") + ":"
            + FilterFingerprint(ignorePageFilter ? null : filter) + ":"
            + trimmed.ToLowerInvariant();
        if (_cache.TryGetValue(cacheKey, out (IReadOnlyList<string> Values, bool Truncated) cached))
            return cached;

        const int Cap = 200;
        var sb = new StringBuilder();
        sb.Append("SELECT DISTINCT TOP ").Append(Cap + 1).Append(' ')
          .Append("CAST(").Append(dim.SqlExpression).Append(" AS NVARCHAR(200)) AS V")
          .Append(" FROM ").Append(report.ViewName)
          .Append(" WHERE 1 = 1");
        var p = new DynamicParameters();
        if (!ignorePageFilter)
            AppendFilterWhere(sb, p, filter ?? new FlatDefectFilter());
        if (trimmed.Length > 0)
        {
            sb.Append(" AND CAST(").Append(dim.SqlExpression).Append(" AS NVARCHAR(200)) LIKE @q + N'%'");
            p.Add("q", trimmed, DbType.String);
        }
        sb.Append(" ORDER BY V");

        var raw = new List<string>();
        bool hasNull = false;
        using (var c = new SqlConnection(_cs))
        {
            await c.OpenAsync(ct);
            using var reader = await c.ExecuteReaderAsync(new CommandDefinition(
                sb.ToString(), p, commandTimeout: 60, cancellationToken: ct));
            var dbReader = (System.Data.Common.DbDataReader)reader;
            while (await dbReader.ReadAsync(ct))
            {
                if (dbReader.IsDBNull(0)) { hasNull = true; continue; }
                raw.Add(dbReader.GetString(0));
            }
        }

        bool truncated = raw.Count > Cap;
        if (truncated) raw.RemoveRange(Cap, raw.Count - Cap);
        if (hasNull) raw.Insert(0, NullSentinel);

        var result = ((IReadOnlyList<string>)raw, truncated);
        _cache.Set(cacheKey, result, TimeSpan.FromSeconds(60));
        return result;
    }

    private static string FilterFingerprint(FlatDefectFilter? f)
    {
        if (f == null) return "none";
        // Stable, allocation-light hash of the 18 filter slots. Used only as
        // a cache key prefix; collisions just mean two scopes share an entry,
        // which a 60-second TTL absorbs.
        var raw = string.Join('|',
            f.QualityOrderId, f.ArrivalId, f.Status, f.Plant, f.MaterialGroup, f.MajorCategory,
            f.Variety, f.Origin, f.MaterialClass, f.VendorName, f.VendorNo, f.StorageLocation,
            f.ContainerNo, f.BolNo, f.Ebeln, f.SampleScope,
            f.PoFrom?.ToString("yyyyMMdd"), f.PoTo?.ToString("yyyyMMdd"));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes, 0, 8);
    }

    private sealed record RawRow(string[] RowKey, string[] ColKey, decimal?[] Values);

    private sealed class StringArrayComparer : IEqualityComparer<string[]>
    {
        public static readonly StringArrayComparer Instance = new();
        public bool Equals(string[]? x, string[]? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null || x.Length != y.Length) return false;
            for (int i = 0; i < x.Length; i++)
                if (!string.Equals(x[i], y[i], StringComparison.Ordinal)) return false;
            return true;
        }
        public int GetHashCode(string[] obj)
        {
            unchecked
            {
                int h = 17;
                foreach (var s in obj)
                    h = h * 31 + (s?.GetHashCode() ?? 0);
                return h;
            }
        }
    }
}
