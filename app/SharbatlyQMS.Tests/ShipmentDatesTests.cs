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
    public void An_end_stored_in_utc_counts_as_its_local_day()
    {
        // Opened at 22:30 UTC. In Riyadh (UTC+3) that is 01:30 the NEXT day,
        // and the day the inspector opened the order is the day that counts.
        // Computed against the machine's own zone so the test says the same
        // thing wherever it runs; on a UTC machine both readings coincide.
        var start     = new DateTime(2026, 9, 20);
        var openedUtc = new DateTime(2026, 9, 24, 22, 30, 0, DateTimeKind.Utc);
        var expected  = (openedUtc.ToLocalTime().Date - start).Days;

        Assert.Equal(expected, ShipmentDates.TimeBarDays(start, openedUtc));

        // The value Dapper hands back from datetime2 is Kind=Unspecified and
        // still means UTC; it must not be taken as local.
        var unspecified = DateTime.SpecifyKind(openedUtc, DateTimeKind.Unspecified);
        Assert.Equal(expected, ShipmentDates.TimeBarDays(start, unspecified));
    }

    [Fact]
    public void Days_to_inspection_is_null_without_both_dates_and_never_negative()
    {
        Assert.Null(ShipmentDates.DaysToInspection(null, DateTime.Today));
        Assert.Null(ShipmentDates.DaysToInspection(new DateTime(2026, 9, 20), null));
        Assert.Equal(0, ShipmentDates.DaysToInspection(new DateTime(2026, 9, 30), new DateTime(2026, 9, 20)));
        Assert.Equal(4, ShipmentDates.DaysToInspection(new DateTime(2026, 9, 20), new DateTime(2026, 9, 24, 15, 0, 0)));
    }

    /// <summary>
    /// The report's Time Bar (defined 2026-09-27) is inspection date minus
    /// vessel discharge date. It counts to the INSPECTION date, not to when QC
    /// finished, and the receive date does not move it.
    /// </summary>
    [Fact]
    public void The_report_time_bar_is_inspection_minus_discharge()
    {
        var d = new QualityReportData
        {
            Shipment = new ShipmentSnapshot
            {
                DischargeDate = new DateTime(2026, 9, 18),
                ReceiveDate   = new DateTime(2026, 9, 24),
            },
            InspectionDate = new DateTime(2026, 9, 25, 10, 0, 0),
            // Finished much later -- must not move the figure.
            QualityOrder   = new QualityOrder { ClosedAt = new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc) }
        };

        Assert.Equal(7, d.TimeBarDays);            // 25 Sep - 18 Sep discharge
    }

    [Fact]
    public void A_missing_date_leaves_the_time_bar_blank_not_zero()
    {
        var d = new QualityReportData
        {
            Shipment       = new ShipmentSnapshot { ReceiveDate = new DateTime(2026, 9, 24) },
            InspectionDate = new DateTime(2026, 9, 26)
        };
        Assert.Null(d.TimeBarDays);                // no discharge date entered

        d.Shipment.DischargeDate = new DateTime(2026, 9, 20);
        d.InspectionDate = null;
        Assert.Null(d.TimeBarDays);                // no inspection yet
    }

    // ---- Date checks on saving the arrival ------------------------------------

    private static readonly DateTime Today = new(2026, 11, 3);
    private static readonly DateTime Loading = new(2026, 9, 16);
    private static readonly EnteredDates Nothing = new(null, null, null);

    private static IReadOnlyList<string> Check(EnteredDates entered, EnteredDates? stored = null, int daysBeforeLoading = 1) =>
        ShipmentDates.EnteredDateProblems(Loading, entered, stored ?? Nothing, Today, daysBeforeLoading);

    [Fact]
    public void The_mistyped_month_that_printed_68_transit_days_is_refused()
    {
        // QO-2026-004723: 23/11 and 25/11 typed for 23/10 and 25/10, on 1 Nov.
        var problems = ShipmentDates.EnteredDateProblems(Loading,
            new EnteredDates(new DateTime(2026, 11, 23), new DateTime(2026, 10, 31), new DateTime(2026, 11, 25)),
            Nothing, new DateTime(2026, 11, 1), 1);
        Assert.Contains(problems, p => p.Contains("Vessel Discharge Date 23/11/2026 is in the future"));
        Assert.Contains(problems, p => p.Contains("Pull-out Date 25/11/2026 is in the future"));
        Assert.Contains(problems, p => p.Contains("after the Unloading Date 31/10/2026"));

        // And the corrected dates are accepted, giving the expected 37 days.
        var discharge = new DateTime(2026, 10, 23);
        Assert.Empty(ShipmentDates.EnteredDateProblems(Loading,
            new EnteredDates(discharge, new DateTime(2026, 10, 31), new DateTime(2026, 10, 25)),
            Nothing, new DateTime(2026, 11, 1), 1));
        Assert.Equal((short)37, ShipmentDates.TransitDays(Loading, discharge, null));
    }

    [Fact]
    public void Discharge_before_loading_follows_the_configured_days()
    {
        var oneDayBefore = new EnteredDates(new DateTime(2026, 9, 15), null, null);
        var twoDaysBefore = new EnteredDates(new DateTime(2026, 9, 14), null, null);

        // The default: SAP's sailing date can run a day behind the real discharge.
        Assert.Empty(Check(oneDayBefore, daysBeforeLoading: 1));
        Assert.Contains(Check(twoDaysBefore, daysBeforeLoading: 1),
            p => p.Contains("more than 1 day(s) before the Loading Date 16/09/2026"));

        // 0 is the strict setting; a wider one lets the two days through.
        Assert.Contains(Check(oneDayBefore, daysBeforeLoading: 0),
            p => p.Contains("is before the Loading Date 16/09/2026"));
        Assert.Empty(Check(twoDaysBefore, daysBeforeLoading: 2));
    }

    [Fact]
    public void Pull_out_before_discharge_is_refused()
        => Assert.Contains(Check(new EnteredDates(new DateTime(2026, 10, 23), null, new DateTime(2026, 10, 22))),
            p => p.Contains("before the Vessel Discharge Date"));

    [Fact]
    public void The_same_day_is_allowed_on_every_rule()
    {
        Assert.Empty(Check(new EnteredDates(Today, Today, Today)));
        Assert.Empty(Check(new EnteredDates(Loading, null, null)));
    }

    [Fact]
    public void Blank_dates_are_not_problems()
    {
        Assert.Empty(Check(Nothing));
        Assert.Empty(Check(new EnteredDates(new DateTime(2026, 10, 23), null, null)));
    }

    [Fact]
    public void A_bad_date_already_on_the_row_does_not_block_an_unrelated_save()
    {
        var old = new EnteredDates(new DateTime(2026, 11, 23), new DateTime(2026, 10, 31), new DateTime(2026, 11, 25));
        Assert.Empty(Check(old, old));
    }

    [Fact]
    public void Changing_unloading_is_checked_against_the_stored_discharge()
    {
        var old = new EnteredDates(new DateTime(2026, 10, 23), new DateTime(2026, 10, 31), null);
        var entered = old with { Unloading = new DateTime(2026, 10, 20) };
        Assert.Contains(Check(entered, old), p => p.Contains("after the Unloading Date 20/10/2026"));
    }
}
