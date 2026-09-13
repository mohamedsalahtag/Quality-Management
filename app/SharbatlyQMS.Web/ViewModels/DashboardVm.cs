using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;

namespace SharbatlyQMS.Web.ViewModels;

/// <summary>
/// What the dashboard is currently showing: one plant (or all the user may
/// see) over one period. Bound straight from the query string so every portlet,
/// every drill-through link and a bookmarked URL all describe the same view.
///
/// Dates are the user's LOCAL dates. Everything in the database is stored UTC,
/// so the service converts once, at the edge -- comparing a local date against a
/// UTC column silently drops rows either side of the +03:00 day boundary, which
/// on a "today" default would be most of the morning's work.
/// </summary>
public class DashboardFilter
{
    /// <summary>today | 7d | 30d | 90d | custom. Defaults to today: the
    /// received-vs-committed portlet is a "did we keep up today" question.</summary>
    public string    Period { get; set; } = Periods.Today;
    public string?   Plant  { get; set; }
    public DateTime? From   { get; set; }
    public DateTime? To     { get; set; }

    public static class Periods
    {
        public const string Today  = "today";
        public const string Week   = "7d";
        public const string Month  = "30d";
        public const string Quarter= "90d";
        public const string Custom = "custom";
    }

    /// <summary>The period as concrete local dates, inclusive both ends. A
    /// custom period with only one bound set is closed off with today.</summary>
    public (DateTime FromLocal, DateTime ToLocal) Resolve()
    {
        var today = DateTime.Now.Date;
        return Period switch
        {
            Periods.Week    => (today.AddDays(-6),  today),
            Periods.Month   => (today.AddDays(-29), today),
            Periods.Quarter => (today.AddDays(-89), today),
            Periods.Custom  => ((From ?? To ?? today).Date, (To ?? From ?? today).Date),
            _               => (today, today),
        };
    }

    /// <summary>How many days the period covers, inclusive.</summary>
    public int DayCount
    {
        get { var (f, t) = Resolve(); return Math.Max(1, (int)(t - f).TotalDays + 1); }
    }

    /// <summary>Trend buckets follow the period. A single day bucketed BY day
    /// is one point, which draws as an empty chart with a dot in it -- so a
    /// one-day view is bucketed by HOUR and actually shows the shape of the
    /// day's work.</summary>
    public bool BucketByHour => DayCount == 1;
    public bool BucketByDay  => DayCount > 1 && DayCount <= 31;
    /// <summary>The SQL DATEPART the trends group on.</summary>
    public string BucketPart => BucketByHour ? "hour" : (BucketByDay ? "day" : "week");

    public string Label
    {
        get
        {
            var (f, t) = Resolve();
            return Period switch
            {
                Periods.Week    => "Last 7 days",
                Periods.Month   => "Last 30 days",
                Periods.Quarter => "Last 90 days",
                Periods.Custom  => f == t ? f.ToString("yyyy-MM-dd")
                                          : $"{f:yyyy-MM-dd} → {t:yyyy-MM-dd}",
                _               => "Today",
            };
        }
    }

    /// <summary>Query-string values that reproduce this exact view, for the
    /// drill-through links and the plant chips.</summary>
    public Dictionary<string, string?> RouteValues(string? plantOverride = null)
    {
        var (f, t) = Resolve();
        var d = new Dictionary<string, string?> { ["period"] = Period };
        var plant = plantOverride ?? Plant;
        if (!string.IsNullOrWhiteSpace(plant)) d["plant"] = plant;
        if (Period == Periods.Custom)
        {
            d["from"] = f.ToString("yyyy-MM-dd");
            d["to"]   = t.ToString("yyyy-MM-dd");
        }
        return d;
    }
}

public class DashboardVm
{
    public DashboardFilter Filter           { get; set; } = new();
    /// <summary>Plants the signed-in user may choose between. A single-plant
    /// operator gets exactly one and the picker renders as a locked badge.</summary>
    public IReadOnlyList<string> PlantOptions { get; set; } = Array.Empty<string>();
    /// <summary>Received vs QC-created, one row per plant, over the period.</summary>
    public List<PlantCommitmentRow> Commitment { get; set; } = new();

    /// <summary>
    /// Calendar days in the chosen period, inclusive of both ends. Drives the
    /// daily average: a rate is only comparable between periods if it is
    /// divided by the days those periods actually cover, and "today" is 1 day
    /// while "this month" may be 30.
    /// </summary>
    public int PeriodDays { get; set; } = 1;
    /// <summary>The same comparison over time, for the trend chart: one point
    /// per day (short periods) or per week (long ones).</summary>
    public List<CommitmentPoint> CommitmentTrend { get; set; } = new();
    public DashboardCounts Counts            { get; set; } = new();
    public Dictionary<string, int> ArrivalsByStatus { get; set; } = new();
    public Dictionary<string, int> QoByStatus       { get; set; } = new();
    public List<TrendPoint> ArrivalsTrend           { get; set; } = new();
    public decimal? AvgDefectPctLast30              { get; set; }
    public AlertConfig Thresholds                   { get; set; } = new();
    public IReadOnlyList<Arrival> OpenArrivals      { get; set; } = Array.Empty<Arrival>();
    public IReadOnlyList<QualityOrder> OpenQos      { get; set; } = Array.Empty<QualityOrder>();
    public bool IsAdmin                             { get; set; }
    // New innovative-chart series:
    public List<ThroughputPoint>   ThroughputTrend  { get; set; } = new();
    /// <summary>Open quality orders by age IN DAYS: index 0 = opened today,
    /// 1 = yesterday, … 13 = thirteen days, 14 = fourteen days or more. Ranges
    /// (0-3 / 3-7 / …) hid exactly what the chart is for -- whether a specific
    /// day's work is piling up.</summary>
    public int[]                   OpenQoAgeBuckets { get; set; } = new int[15];
    public List<DefectGroupPoint>  DefectByGroup    { get; set; } = new();
    /// <summary>Provenance for the defect-rate gauge: how much was inspected,
    /// across how many orders, and which defects drive the number.</summary>
    public DefectRateContext       DefectRate       { get; set; } = new();
    /// <summary>SAP endpoints whose most recent sync run failed (shown to admins).</summary>
    public List<SyncFailure>       SyncFailures     { get; set; } = new();
}

public class SyncFailure
{
    public string    EndpointKey { get; set; } = "";
    public DateTime? CompletedAt { get; set; }
    public string?   Message     { get; set; }
}

public class DashboardCounts
{
    public int ArrivalsDraft             { get; set; }
    public int ArrivalsCompletedInPeriod { get; set; }
    public int QoOpen                    { get; set; }
    public int StaleArrivals             { get; set; }
    public int AgedOpenQos               { get; set; }
    public int PendingContainers         { get; set; }
}

public class TrendPoint
{
    public DateTime WeekStart { get; set; }
    public int Created        { get; set; }
    public int Completed      { get; set; }
}

public class ThroughputPoint
{
    public DateTime WeekStart    { get; set; }
    public int      ArrivalsRecv { get; set; }
    public int      QosClosed    { get; set; }
}

/// <summary>
/// One plant's line in the received-vs-committed portlet: how many containers
/// arrived in the period against how many of them were actually taken into QC.
/// The gap is the backlog the plant built up during the period, which is the
/// number the portlet exists to make impossible to miss.
/// </summary>
public class PlantCommitmentRow
{
    public string Plant     { get; set; } = "";

    /// <summary>Arrivals created in the period -- containers received.</summary>
    public int    Received  { get; set; }

    /// <summary>
    /// How many of <see cref="Received"/> now have a quality order. The SAME
    /// containers, not a second count of unrelated work.
    ///
    /// This used to be "quality orders created in the period", which is a
    /// different cohort entirely and produced numbers nobody could read: on
    /// 2026-09-03 Dammam received 3 containers and raised 13 orders, because 10
    /// of them were against the previous day's arrivals. Presented side by side
    /// that said "3 received, 13 committed, 433% covered", which is not a fact
    /// about anything. Coverage only means something when both halves describe
    /// one set of containers.
    /// </summary>
    public int    Committed { get; set; }

    /// <summary>
    /// Quality orders CREATED in the period whatever their container's arrival
    /// date -- the team's throughput, including catching up on a backlog. Kept
    /// as its own column because it is a real and useful number; it is simply
    /// not the other half of a coverage ratio.
    /// </summary>
    public int    QosCreated { get; set; }

    /// <summary>
    /// How many of <see cref="QosCreated"/> were raised against containers that
    /// arrived BEFORE this period. Without it a plant that raised 13 orders on a
    /// day it took 3 containers in looks impossible rather than busy.
    /// </summary>
    public int    QosCatchUp { get; set; }

    /// <summary>The rest of the period's orders: this period's own arrivals.
    /// Equal to <see cref="Committed"/> by construction -- both count orders
    /// OPENED in the period against arrivals received in it.</summary>
    public int    QosOnNewArrivals => Math.Max(0, QosCreated - QosCatchUp);

    /// <summary>Orders opened per day over the period. Null when the period is
    /// somehow zero days, rather than dividing by zero.</summary>
    public double? DailyAverage(int periodDays) =>
        periodDays <= 0 ? null : (double)QosCreated / periodDays;

    /// <summary>Containers received in the period still waiting for a quality
    /// order. Cannot go negative now that both halves are the same cohort.</summary>
    public int    Outstanding => Math.Max(0, Received - Committed);

    /// <summary>Committed as a share of received. Null when nothing arrived --
    /// 0% would read as a failure on a day with no containers.</summary>
    public decimal? CoveragePct => Received == 0 ? null : Math.Round(Committed * 100m / Received, 1);
}

/// <summary>One bucket of the received-vs-committed trend.</summary>
public class CommitmentPoint
{
    public DateTime Bucket     { get; set; }
    /// <summary>Containers received in this bucket.</summary>
    public int      Received   { get; set; }
    /// <summary>How many of THOSE containers have a quality order.</summary>
    public int      Committed  { get; set; }
    /// <summary>Quality orders raised in this bucket, from any arrival date.</summary>
    public int      QosCreated { get; set; }
}

/// <summary>
/// One material group's defect picture. A bare percentage answers "how bad"
/// but not "how much" or "of what", which is what anyone reading it needs
/// next -- so the units behind the ratio and the single worst defect travel
/// with it.
/// </summary>
public class DefectGroupPoint
{
    public string  MaterialGroup     { get; set; } = "";
    public string? MaterialGroupDesc { get; set; }
    /// <summary>Defective units as a share of inspected units. Weighted by
    /// sample size, so a heavily-sampled group cannot be swamped by a tiny one.</summary>
    public decimal AvgDefectPct      { get; set; }
    /// <summary>Closed quality orders behind the figure.</summary>
    public int     Orders            { get; set; }
    /// <summary>Samples behind the figure -- the credibility of the ratio.</summary>
    public int     Samples           { get; set; }
    public decimal DefectiveUnits    { get; set; }
    public decimal InspectedUnits    { get; set; }
    /// <summary>The defect contributing the most units in this group.</summary>
    public string? TopDefect         { get; set; }
    public decimal TopDefectUnits    { get; set; }
}

/// <summary>
/// What is actually behind the defect-rate gauge. On its own the gauge is a
/// number with no provenance -- "which defect, which order, which material?"
/// was the exact complaint. These are the answers: how much was inspected, how
/// many orders it came from, and which defects drive it.
/// </summary>
public class DefectRateContext
{
    public int     Orders         { get; set; }
    public int     Samples        { get; set; }
    public decimal DefectiveUnits { get; set; }
    public decimal InspectedUnits { get; set; }
    public List<TopDefectRow> TopDefects { get; set; } = new();
}

/// <summary>One defect and how much of the overall rate it accounts for.</summary>
public class TopDefectRow
{
    public string  DefectName     { get; set; } = "";
    public string? DefectCategory { get; set; }
    public string? MaterialGroup  { get; set; }
    public decimal Units          { get; set; }
    /// <summary>This defect's units as a share of everything inspected --
    /// i.e. its own contribution to the headline rate, so the rows add up to it.</summary>
    public decimal PctOfInspected { get; set; }
}

/// <summary>
/// The five ways to slice the Received vs Inspected portlet. The names are the
/// column captions, because a user who clicks a number and lands on a page
/// should see the word they clicked.
/// </summary>
public static class CommitmentBuckets
{
    public const string Received          = "received";
    public const string PeriodInspection  = "period";
    public const string PendingInspection = "pending";
    public const string BacklogInspected  = "backlog";
    public const string TotalInspected    = "total";

    /// <summary>Caption, and the sheet name in the workbook.</summary>
    public static string Caption(string bucket) => bucket switch
    {
        PeriodInspection  => "Period Inspection",
        PendingInspection => "Pending Inspection",
        BacklogInspected  => "Backlog Inspected",
        TotalInspected    => "Total Inspected",
        _                 => "Received"
    };

    /// <summary>What the rows in that bucket actually are, said plainly.</summary>
    public static string Explain(string bucket) => bucket switch
    {
        PeriodInspection  => "Containers received in this period that were inspected in it.",
        PendingInspection => "Containers received in this period with no inspection opened in it.",
        BacklogInspected  => "Inspections opened in this period on containers received BEFORE it.",
        TotalInspected    => "Every inspection opened in this period, whenever its container arrived.",
        _                 => "Containers received in this period."
    };

    public static readonly string[] All =
        { Received, PeriodInspection, PendingInspection, BacklogInspected, TotalInspected };

    public static bool IsValid(string? b) =>
        !string.IsNullOrWhiteSpace(b) && All.Contains(b, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// One container (and its inspection, where there is one) behind a number on
/// the Received vs Inspected portlet. The same rows feed the drill-through page
/// and the workbook, so what is exported is what was on screen.
/// </summary>
public class CommitmentDetailRow
{
    public string    Plant           { get; set; } = "";
    public long      ArrivalId       { get; set; }
    public string?   ArrivalNo       { get; set; }
    public string?   ContainerNo     { get; set; }
    public string?   BolNo           { get; set; }
    public string?   Ebeln           { get; set; }
    public string?   VendorName      { get; set; }
    public string?   ArrivalStatus   { get; set; }
    public DateTime  ArrivalCreatedAt{ get; set; }
    public long?     QualityOrderId  { get; set; }
    public string?   QualityOrderNo  { get; set; }
    public string?   QoStatus        { get; set; }
    public DateTime? QoCreatedAt     { get; set; }
    public DateTime? QoClosedAt      { get; set; }

    /// <summary>Days from the container being received to its inspection being
    /// opened. Null while nothing has been opened.</summary>
    public int? DaysToInspection =>
        QoCreatedAt.HasValue ? Math.Max(0, (int)(QoCreatedAt.Value.Date - ArrivalCreatedAt.Date).TotalDays) : null;
}
