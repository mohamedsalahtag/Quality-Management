using System.Text;
using Dapper;

namespace YourApp.Services.Reports;

/// <summary>
/// data-hub-pack — the plug-in seam.
///
/// The pack does not know what filter shape your host uses. You implement
/// this interface to translate your host's filter object into parameterised
/// SQL <c>AND ...</c> fragments that <see cref="PivotService"/> and
/// <see cref="PerspectiveService"/> reuse.
///
/// HARD RULE: every value MUST bind through <see cref="DynamicParameters"/>.
/// Never concatenate user input into the SQL string.
///
/// EXAMPLE (taken from the Sharbatly QMS instantiation):
///
/// <code>
/// public void AppendFilterWhere(StringBuilder sb, DynamicParameters p, object? filter) {
///     if (filter is not FlatDefectFilter f) return;
///     if (f.QualityOrderId.HasValue) {
///         sb.Append(" AND QualityOrderId = @qoId");
///         p.Add("qoId", f.QualityOrderId.Value, DbType.Int64);
///     }
///     if (!string.IsNullOrWhiteSpace(f.Plant)) {
///         sb.Append(" AND Plant = @plant");
///         p.Add("plant", f.Plant, DbType.AnsiString);
///     }
///     // ...one block per filter slot...
/// }
/// </code>
/// </summary>
public interface IDataHubFilterAdapter
{
    void AppendFilterWhere(StringBuilder sb, DynamicParameters p, object? filter);
}
