namespace SharbatlyQMS.Web.Services;

/// <summary>
/// The one place that says what each shipment date MEANS and how the two
/// derived figures -- transit days and the inspection time bar -- are computed.
/// Every screen, report and export goes through here so they cannot disagree.
///
/// The vocabulary, measured against SAP's ZQC_Data feed on 2026-09-26:
///
///   * LOADING DATE      = SAP <c>Sailing_Date</c>. SAP's own <c>LoadingDate</c>
///                         column is empty on every row (0 of 25,120), so the
///                         sailing date is the loading date the business works
///                         from and is stored as <c>sailing_date</c>.
///   * VESSEL ARRIVAL    = SAP <c>Arrival_Date</c>, the vessel reaching the port.
///                         It equals SAP's Estimated_Arrival_Date on 99.6% of
///                         rows, so it is an ETA-grade figure. Stored as
///                         <c>port_arrival_date</c>.
///   * DISCHARGE DATE    = entered by the inspector on the arrival: the day the
///                         container was actually discharged from the vessel
///                         ("Vessel Discharge Date" on the Labels screen).
///   * RECEIVE DATE      = SAP <c>Receive_Date</c>: the BRANCH goods receipt
///                         ("Branch Receive Date" on the Labels screen). SAP
///                         moves it -- first set when the container is pulled
///                         out of the port, then advanced to the day the branch
///                         books the goods in, on average four days later and
///                         on more than half of all arrivals. It is therefore
///                         refreshed from the SAP cache on every sweep rather
///                         than frozen at arrival creation. Stored as
///                         <c>receive_date</c>; the legacy <c>arrival_date</c>
///                         column is kept equal to it.
///   * INSPECTION DATE   = when the Quality Order was opened.
///
/// Derived:
///   * TRANSIT DAYS      = discharge date - loading date. SAP's Transit_Days
///                         (= Arrival_Date - Sailing_Date, i.e. ETA-based) is the
///                         fallback until the inspector enters the discharge date.
///   * TIME BAR          = QC finished (local date) - receive date. The same
///                         arithmetic on the Time Bar page, the QC report and the
///                         Data Hub, so the three print the same number.
/// </summary>
public static class ShipmentDates
{
    /// <summary>
    /// Days at sea: from loading (SAP Sailing_Date) to the inspector's discharge
    /// date. Falls back to SAP's own Transit_Days while the discharge date is
    /// not yet entered, and to null when neither can be had. Never negative:
    /// a discharge typed before the loading date is a data error, and printing
    /// "-3" on a supplier report reads as a bug in the report.
    /// </summary>
    public static short? TransitDays(DateTime? loading, DateTime? discharge, short? sapTransitDays)
    {
        if (loading.HasValue && discharge.HasValue)
        {
            var days = (discharge.Value.Date - loading.Value.Date).Days;
            return (short)Math.Max(0, Math.Min(days, short.MaxValue));
        }
        return sapTransitDays;
    }

    /// <summary>
    /// The inspection time bar: whole days from the clock's start date (a
    /// business date with no time part) to the LOCAL date the quality order was
    /// finished. <paramref name="finishedUtc"/> is <c>closed_at</c> (UTC), or the
    /// moment of rendering while the order is still open; it is shifted into
    /// local time before the day is taken, because an order finished at 01:00
    /// Riyadh time is 22:00 UTC the previous day and would otherwise count a
    /// day short. Clamped at zero for the same reason as transit.
    /// </summary>
    public static int? TimeBarDays(DateTime? startDate, DateTime? finishedUtc)
    {
        if (!startDate.HasValue || !finishedUtc.HasValue) return null;
        var finishedLocal = ToLocal(finishedUtc.Value);
        return Math.Max(0, (finishedLocal.Date - startDate.Value.Date).Days);
    }

    /// <summary>
    /// UTC-to-local for values read back from <c>datetime2</c> columns, which
    /// Dapper hands over with Kind = Unspecified. Treating those as local would
    /// silently skip the conversion.
    /// </summary>
    public static DateTime ToLocal(DateTime utc) =>
        utc.Kind == DateTimeKind.Local
            ? utc
            : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
}
