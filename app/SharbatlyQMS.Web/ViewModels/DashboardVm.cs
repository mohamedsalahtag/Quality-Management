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
    public IReadOnlyList<Arrival> RecentArrivals    { get; set; } = Array.Empty<Arrival>();
    public int OnlineUserCount                      { get; set; }
    public IReadOnlyList<User> OnlineUsers          { get; set; } = Array.Empty<User>();
    public bool IsAdmin                             { get; set; }
}

public class DashboardCounts
{
    public int ArrivalsDraft             { get; set; }
    public int ArrivalsCompletedLast30   { get; set; }
    public int QoOpen                    { get; set; }
    public int StaleArrivals             { get; set; }
    public int AgedOpenQos               { get; set; }
}

public class TrendPoint
{
    public DateTime WeekStart { get; set; }
    public int Created        { get; set; }
    public int Completed      { get; set; }
}
