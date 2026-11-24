using SharbatlyQMS.Web.Models;

namespace SharbatlyQMS.Web.Services;

/// <summary>
/// Survey management: the internal record of a container that may need a
/// formal survey. One survey per arrival, requested by hand from the arrival
/// or its quality order, then Confirmed or Passed by an authorised user.
///
/// Every rule lives here, not in the pages: the same request method serves
/// both entry points, and both decisions go through one guarded update.
/// Callers are responsible for the permission and branch checks.
/// </summary>
public interface ISurveyService
{
    /// <summary>The arrival's survey, or null. Cheap enough for the arrival
    /// and quality order pages to call on every render.</summary>
    Task<SurveyBrief?> GetBriefForArrivalAsync(long arrivalId);

    Task<Survey?> GetAsync(long surveyId);

    /// <summary>
    /// Asks for a survey on an arrival. Creates it Open, links it to the
    /// arrival, and sets the arrival's Joint Survey answer to Yes -- all in one
    /// transaction. If the arrival already has a survey (whatever its status,
    /// including one created a moment ago by somebody else) nothing is created
    /// and that survey is returned with AlreadyExisted = true.
    /// </summary>
    /// <param name="source"><see cref="SurveySource"/>: where the request was made.</param>
    /// <param name="sourceQoId">The quality order it was made from, when source is QualityOrder.</param>
    Task<SurveyRequestResult> RequestAsync(long arrivalId, string source, long? sourceQoId,
        string? reason, string? comments, string user);

    /// <summary>
    /// Confirms or Passes an Open survey. Passing requires a reason. Returns
    /// (false, why) when the survey is not Open any more -- two people deciding
    /// at once get one decision and one clear message.
    /// </summary>
    Task<(bool ok, string? error)> DecideAsync(long surveyId, bool confirm, string? comments, string user);

    Task<SurveyPage> ListAsync(SurveyListFilter filter, PlantScope scope);

    /// <summary>Every row matching the filter, for the Excel export (capped).</summary>
    Task<IReadOnlyList<SurveyListRow>> ListForExportAsync(SurveyListFilter filter, PlantScope scope, int max);

    Task<SurveyCounts> CountsAsync(PlantScope scope);

    Task<SurveyFilterOptions> GetFilterOptionsAsync(PlantScope scope);
}
