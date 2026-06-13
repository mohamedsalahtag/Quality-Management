using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;

namespace SharbatlyQMS.Web.ViewModels;

public class DashboardVm
{
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
    public int[]                   OpenQoAgeBuckets { get; set; } = new int[4];   // 0-3 / 3-7 / 7-14 / 14+
    public List<DefectGroupPoint>  DefectByGroup    { get; set; } = new();
}

public class DashboardCounts
{
    public int ArrivalsDraft             { get; set; }
    public int ArrivalsCompletedLast30   { get; set; }
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

public class DefectGroupPoint
{
    public string  MaterialGroup     { get; set; } = "";
    public string? MaterialGroupDesc { get; set; }
    public decimal AvgDefectPct      { get; set; }
}
