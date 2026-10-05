using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.ViewModels;
using Xunit;

namespace SharbatlyQMS.Tests;

/// <summary>
/// One set of status names on the Time Bar. The filter offered raw codes
/// ("Closed") while the grid printed "Finished", and the stage chip said "Not
/// started" for rows the grid called "Pending". Every label now comes from
/// <see cref="TimeBarStatus"/>, and every row's label is an option the filter
/// can select.
/// </summary>
public class TimeBarStatusTests
{
    [Theory]
    [InlineData(TimeBarStages.Pending, null,        "Not yet inspected")]
    [InlineData(TimeBarStages.Arrival, "Draft",     "Arrival – Draft")]
    [InlineData(TimeBarStages.Arrival, "Completed", "Arrival – Completed")]
    [InlineData(TimeBarStages.Arrival, "Rejected",  "Arrival – Rejected")]
    [InlineData(TimeBarStages.Qc,      "Initial",   "Inspection – Open")]
    [InlineData(TimeBarStages.Qc,      "Open",      "Inspection – Open")]
    [InlineData(TimeBarStages.Qc,      "Reopened",  "Inspection – Open")]
    [InlineData(TimeBarStages.Qc,      "Submitted", "Inspection – Submitted")]
    [InlineData(TimeBarStages.Qc,      "Closed",    "Inspection – Finished")]
    public void The_grid_label(string stage, string? code, string expected)
    {
        var row = new TimeBarRow { Stage = stage, StatusCode = code };
        Assert.Equal(expected, row.StatusLabel);
    }

    [Theory]
    [InlineData(TimeBarStages.Pending, null)]
    [InlineData(TimeBarStages.Arrival, "Draft")]
    [InlineData(TimeBarStages.Qc,      "Initial")]
    [InlineData(TimeBarStages.Qc,      "Closed")]
    public void Every_row_label_is_a_filter_option(string stage, string? code)
    {
        var row = new TimeBarRow { Stage = stage, StatusCode = code };
        Assert.Contains(TimeBarStatus.All, o => o.Label == row.StatusLabel
                                              && o.Value == TimeBarStatus.Normalize(stage, code));
    }

    [Fact]
    public void The_stage_chip_and_the_status_agree_on_rows_with_no_arrival() =>
        Assert.Equal(TimeBarStages.Label(TimeBarStages.Pending),
                     new TimeBarRow { Stage = TimeBarStages.Pending }.StatusLabel);
}
