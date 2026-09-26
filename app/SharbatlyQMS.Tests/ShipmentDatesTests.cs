using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.Services.Pdf;
using Xunit;

namespace SharbatlyQMS.Tests;

/// <summary>
/// The two derived shipment figures -- transit days and the inspection time
/// bar -- have one definition each, in ShipmentDates, and every screen reads
/// it. These pin the definition itself; no database.
///
///   * Transit = loading (SAP Sailing_Date) to the inspector's discharge date.
///     SAP's own Transit_Days is an ETA-based figure and only the fallback.
///   * Time Bar = receive date (SAP's branch goods receipt) to the QO's LOCAL
///     finish date. It used to be computed in UTC on the report and in local
///     time on the Time Bar page, which is one of the ways the two disagreed.
/// </summary>
public class ShipmentDatesTests
{
    [Fact]
    public void Transit_is_loading_to_discharge_when_both_are_known()
    {
        // ARR-2026-002930: sailed 17 Aug, discharged 18 Sep, SAP said 32.
        var days = ShipmentDates.TransitDays(
            new DateTime(2026, 8, 17), new DateTime(2026, 9, 18), sapTransitDays: 99);
        Assert.Equal((short)32, days);
    }

    [Fact]
    public void Transit_falls_back_to_sap_until_the_discharge_date_is_entered()
    {
        Assert.Equal((short)29, ShipmentDates.TransitDays(new DateTime(2026, 7, 30), null, 29));
        Assert.Equal((short)29, ShipmentDates.TransitDays(null, new DateTime(2026, 9, 1), 29));
        Assert.Null(ShipmentDates.TransitDays(null, null, null));
    }

    [Fact]
    public void Transit_is_never_negative()
    {
        // A discharge typed before the loading date is a data error, not a
        // negative voyage.
        Assert.Equal((short)0, ShipmentDates.TransitDays(new DateTime(2026, 9, 18), new DateTime(2026, 9, 10), 5));
    }

    [Fact]
    public void The_snapshot_exposes_the_same_transit_the_report_prints()
    {
        var s = new ShipmentSnapshot
        {
            SailingDate = new DateTime(2026, 8, 13), DischargeDate = new DateTime(2026, 9, 10), TransitDays = 19
        };
        Assert.Equal((short)28, s.EffectiveTransitDays);

        s.DischargeDate = null;
        Assert.Equal((short)19, s.EffectiveTransitDays);
    }

    [Fact]
    public void Time_bar_counts_to_the_local_finish_date()
    {
        // Finished at 22:30 UTC. In Riyadh (UTC+3) that is 01:30 the NEXT day,
        // and the day the inspector finished is the day that counts. Computed
        // against the machine's own zone so the test says the same thing
        // wherever it runs; on a UTC machine both readings coincide.
        var start       = new DateTime(2026, 9, 20);
        var finishedUtc = new DateTime(2026, 9, 24, 22, 30, 0, DateTimeKind.Utc);
        var expected    = (finishedUtc.ToLocalTime().Date - start).Days;

        Assert.Equal(expected, ShipmentDates.TimeBarDays(start, finishedUtc));

        // The value Dapper hands back from datetime2 is Kind=Unspecified and
        // still means UTC; it must not be taken as local.
        var unspecified = DateTime.SpecifyKind(finishedUtc, DateTimeKind.Unspecified);
        Assert.Equal(expected, ShipmentDates.TimeBarDays(start, unspecified));
    }

    [Fact]
    public void Time_bar_is_null_without_a_start_and_never_negative()
    {
        Assert.Null(ShipmentDates.TimeBarDays(null, DateTime.UtcNow));
        Assert.Null(ShipmentDates.TimeBarDays(new DateTime(2026, 9, 20), null));
        Assert.Equal(0, ShipmentDates.TimeBarDays(new DateTime(2026, 9, 30),
                                                  new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc)));
    }

    /// <summary>
    /// The report reads the same basis setting as the Time Bar page and counts
    /// from the receive date by default -- not from the discharge date, which
    /// is what made "Time Bar 8 days" appear on a container inspected the day
    /// it was received.
    /// </summary>
    [Fact]
    public void The_report_time_bar_counts_from_the_receive_date_by_default()
    {
        var d = new QualityReportData
        {
            Shipment = new ShipmentSnapshot
            {
                DischargeDate   = new DateTime(2026, 9, 18),
                ReceiveDate     = new DateTime(2026, 9, 26),
                PortArrivalDate = new DateTime(2026, 9, 18),
            },
            QualityOrder = new QualityOrder { ClosedAt = new DateTime(2026, 9, 26, 9, 0, 0, DateTimeKind.Utc) }
        };

        Assert.Equal(TimeBarArrivalBases.GoodsReceipt, d.TimeBarBasis);
        Assert.Equal(new DateTime(2026, 9, 26), d.TimeBarStartDate);
        Assert.Equal("Receipt", d.TimeBarCaption);
        Assert.Equal(0, d.TimeBarDays);

        d.TimeBarBasis = TimeBarArrivalBases.PortArrival;
        Assert.Equal(new DateTime(2026, 9, 18), d.TimeBarStartDate);
        Assert.Equal("Port arrival", d.TimeBarCaption);
        Assert.Equal(8, d.TimeBarDays);
    }

    [Fact]
    public void An_open_order_counts_to_the_report_date()
    {
        var d = new QualityReportData
        {
            Shipment     = new ShipmentSnapshot { ReceiveDate = DateTime.Today.AddDays(-3) },
            QualityOrder = new QualityOrder { ClosedAt = null },
            GeneratedAt  = DateTime.UtcNow
        };
        Assert.Equal(3, d.TimeBarDays);
    }
}
