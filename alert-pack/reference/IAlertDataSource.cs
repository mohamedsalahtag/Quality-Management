// The pack's plug-in seam. Implement this once per project. The pack's
// AlertService calls CollectMatchesAsync(rule) to get the list of records
// matching the rule's filters — schedule logic, HTML rendering, and the
// SMTP fan-out are all handled by the pack.

using YourApp.Models;

namespace YourApp.Services;

public interface IAlertDataSource
{
    // Walk the project's data and return matches that satisfy the rule's
    // PrimaryFilter / SecondaryFilter / Severities CSVs. Severity must be
    // either AlertSeverities.Red (past-due) or AlertSeverities.Yellow
    // (within the warning window). Records outside both windows are dropped
    // from the result.
    //
    // If a CSV filter is empty string, treat as "match all on this axis".
    Task<List<AlertMatch>> CollectMatchesAsync(AlertRule rule, CancellationToken ct = default);
}
