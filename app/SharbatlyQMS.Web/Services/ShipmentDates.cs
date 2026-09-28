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
///   * TIME BAR          = inspection date - vessel discharge date. The
///                         SUPPLIER-CLAIM figure: how long after discharge the
///                         container was inspected. Printed on the QC report,
///                         the supplier's copy included.
///   * INSPECTION TIME BAR = inspection date - branch receive date. The INTERNAL
///                         performance figure the Inspection Time Bar page
///                         monitors; the QC report prints it on the internal copy
///                         only.
///   Both count to the INSPECTION DATE -- the local day the Quality Order was
///   opened -- not to the day QC was finished (2026-09-27, on request).
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
    /// Whole days from a start date (a business date with no time part) to the
    /// inspection date, both as LOCAL days. Used for both the Time Bar (start =
    /// vessel discharge) and the Inspection Time Bar (start = branch receipt).
    /// Null when either date is missing. Clamped at zero: an inspection dated
    /// before its discharge or receipt is a data condition, and printing "-2"
    /// on a report reads as a bug in the report.
    /// </summary>
    public static int? DaysToInspection(DateTime? startDate, DateTime? inspectionLocal)
    {
        if (!startDate.HasValue || !inspectionLocal.HasValue) return null;
        return Math.Max(0, (inspectionLocal.Value.Date - startDate.Value.Date).Days);
    }

    /// <summary>
    /// Same as <see cref="DaysToInspection"/> for an end moment stored in UTC
    /// (<c>opened_at</c>). It is shifted into local time before the day is
    /// taken, because an order opened at 01:00 Riyadh time is 22:00 UTC the
    /// previous day and would otherwise count a day short.
    /// </summary>
    public static int? TimeBarDays(DateTime? startDate, DateTime? endUtc) =>
        endUtc.HasValue ? DaysToInspection(startDate, ToLocal(endUtc.Value)) : null;

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
