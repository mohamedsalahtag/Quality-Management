namespace SharbatlyQMS.Web.Services.Pdf;

/// <summary>
/// Makes the printed report's captions renameable from Admin → Labels, the same
/// as every caption on screen.
///
/// The screens go through <c>@Loc["…"]</c>, which is a scoped service the views
/// can inject. The PDF renderers are static and several layers deep in helper
/// methods that were never given a service to call, so the lookup travels in an
/// <see cref="AsyncLocal{T}"/> instead of being threaded through forty
/// signatures. AsyncLocal — not a plain static — because two users can be
/// generating reports at the same moment and a shared field would hand one of
/// them the other's language.
///
/// Outside a render, and in any test that renders without setting one up, the
/// lookup is absent and every caption prints the English the code ships.
/// </summary>
public static class ReportLabels
{
    private static readonly AsyncLocal<Func<string, string>?> Current = new();

    /// <summary>The screen the report's labels are filed under on Admin → Labels.</summary>
    public const string ScreenKey = "Report";

    /// <summary>
    /// Installs a lookup for the duration of one render. Dispose restores what
    /// was there before rather than clearing, so a nested render — the image
    /// appendix builds inside the main one — cannot switch the outer one off.
    /// </summary>
    public static IDisposable Use(Func<string, string>? lookup) => new Scope(lookup);

    /// <summary>The text to print. The English is both the default and the key,
    /// exactly as on screen, so nothing needs a translation file to work.</summary>
    public static string T(string english) => Current.Value?.Invoke(english) ?? english;

    private sealed class Scope : IDisposable
    {
        private readonly Func<string, string>? _previous;
        private bool _done;

        public Scope(Func<string, string>? lookup)
        {
            _previous = Current.Value;
            Current.Value = lookup;
        }

        public void Dispose()
        {
            if (_done) return;
            Current.Value = _previous;
            _done = true;
        }
    }
}

/// <summary>
/// Every caption the quality report prints.
///
/// The list exists so an administrator can find and rename a report label
/// WITHOUT having to generate a report first: the label screen shows what the
/// application has been seen to use, and a report that has not been rendered
/// since the last deployment would otherwise contribute nothing. Opening
/// Admin → Labels registers the whole set in one go.
///
/// It is checked against the renderer by a test, so a caption added to the PDF
/// and forgotten here fails the build rather than going quietly missing from
/// the screen that is supposed to list everything.
/// </summary>
public static class ReportLabelCatalog
{
    public static readonly string[] All =
    {
        "Additional Fields",
        "Arrival Photos",
        "Bill of Lading No.",
        "Branch",
        "Brand",
        "Container",
        "Count of Materials",
        "Country Of Origin",
        "Created by",
        "Date",
        "Defect",
        "Defects",
        "Discharge Date",
        "Every sample and every reading, material by material. A defect shown in red has reached or passed the tolerance set for it.",
        "External damage to container",
        "Grade",
        "Inspection Date",
        "Joint Survey",
        "Loading Date",
        "Loading Port",
        "Logger Serial",
        "Logger active & data available",
        "Material",
        "Material Group",
        "Material description",
        "Materials",
        "No defects configured for this category.",
        "No inspection was carried out: the container was refused on arrival.",
        "Origin",
        "PO Quantity",
        "Photo Appendix",
        "Port Of Arrival",
        "Procurement Type",
        "Product",
        "Pullout Date",
        "Pulp Temperature",
        "Purch.Doc.",
        "QC No.",
        "Quality Control Report",
        "Quantity",
        "REINSPECTION",
        "Readings",
        "Replaced by",
        "Replaces",
        "Report Location",
        "Results rolled up per material group — the overall picture.",
        "SUPERSEDED BY A REINSPECTION",
        "Sample Details",
        "Sample Readings",
        "Sample Size",
        "Samples",
        "Seal Intact?",
        "Seal No",
        "Shipment Details",
        "Shipper",
        "Size",
        "Summary",
        "Tara Weight",
        "Temperature",
        "This container was inspected before. That inspection was judged unsound and the container was inspected again; this report is the second inspection and carries the result that stands.",
        "This inspection was judged unsound and the container was inspected again. It is kept and printed for reference only - it is not the standing result for this container.",
        "REFERENCE COPY - SUPERSEDED BY A REINSPECTION",
        "Time Bar",
        "Total",
        "Transit Days",
        "Unit",
        "Unloading Date",
        "Variety",
        "Vessel Arrival Date",
        "Vessel Name",
        "Visual cargo condition acceptable",
        "Weight",
        "finished",
        "on",
    };
}
