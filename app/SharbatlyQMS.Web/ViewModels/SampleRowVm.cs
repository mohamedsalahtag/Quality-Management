using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.ViewModels;

/// <summary>One <c>&lt;tr&gt;</c> in the samples table of QO Details. Rendered by the <c>_SampleRow</c> partial and used both server-side (initial render) and via the <c>SaveSampleAjax</c> JSON <c>rowHtml</c> field so the same markup ships from one source.</summary>
public class SampleRowVm
{
    public Sample Sample   { get; set; } = new();
    public bool   Editable { get; set; }
}
