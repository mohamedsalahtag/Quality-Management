using System.Text;
using Dapper;

namespace YourApp.Services.Reports;

/// <summary>
/// data-hub-pack reference. Emits no filter constraints. Useful as a stand-in
/// while you are bringing the analyzer up end-to-end before adding the
/// real filter slots. Replace this with your own implementation that
/// understands your filter type.
/// </summary>
public sealed class DefaultDataHubFilterAdapter : IDataHubFilterAdapter
{
    public void AppendFilterWhere(StringBuilder sb, DynamicParameters p, object? filter)
    {
        // No-op: the analyzer runs over the entire view.
        // Drill filters still apply on top (the pack appends those after this).
    }
}
