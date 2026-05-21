// HR-style example IAlertDataSource implementation. Shows how the same alert
// engine works for a totally different domain (employee contract / visa /
// insurance expiry instead of vehicle documents).
//
// PrimaryFilter   = employee types ("Employee,Contractor,Vendor")
// SecondaryFilter = renewal types ("Contract,Visa,Insurance,BackgroundCheck")
// Severities      = "Red" / "Yellow"

using YourApp.Models;
using YourApp.Services;

namespace YourApp.Services;

public class ContractAlertDataSource : IAlertDataSource
{
    private readonly IDbService _db;
    private const int YellowWindowDays = 30;   // configurable in your app's site config

    public ContractAlertDataSource(IDbService db) { _db = db; }

    public async Task<List<AlertMatch>> CollectMatchesAsync(AlertRule rule, CancellationToken ct = default)
    {
        var primary   = Split(rule.PrimaryFilter);
        var secondary = Split(rule.SecondaryFilter);
        var sev       = Split(rule.Severities);

        bool WantPrimary  (string v) => primary.Count   == 0 || primary  .Contains(v);
        bool WantSecondary(string v) => secondary.Count == 0 || secondary.Contains(v);
        bool WantSev      (string v) => sev.Count       == 0 || sev      .Contains(v);

        var matches = new List<AlertMatch>();
        var people  = await _db.GetPeopleAsync();   // your project's data layer

        foreach (var p in people)
        {
            if (!WantPrimary(p.Type)) continue;     // p.Type ∈ {"Employee","Contractor","Vendor"}

            void Bump(string aspect, DateTime? when)
            {
                if (when == null || !WantSecondary(aspect)) return;
                var days = (when.Value.Date - DateTime.Today).Days;
                string severity =
                    days <  0                 ? "Red"    :
                    days <= YellowWindowDays  ? "Yellow" :
                                                "";
                if (string.IsNullOrEmpty(severity) || !WantSev(severity)) return;
                matches.Add(new AlertMatch
                {
                    PrimaryType   = p.Type,
                    SecondaryType = aspect,
                    Identifier    = p.EmployeeId,
                    Label         = p.FullName,
                    DueRaw        = when.Value.ToString("yyyy-MM-dd"),
                    DueDate       = when.Value.Date,
                    Severity      = severity,
                    DaysFromToday = days,
                    Owner         = p.Manager,
                    Branch        = p.OfficeLocation
                });
            }

            Bump("Contract",         p.ContractEndDate);
            Bump("Visa",             p.VisaExpiry);
            Bump("Insurance",        p.InsuranceRenewal);
            Bump("BackgroundCheck",  p.BackgroundCheckExpiry);
        }
        return matches.OrderBy(m => m.Severity == "Red" ? 0 : 1).ThenBy(m => m.DaysFromToday).ToList();
    }

    private static List<string> Split(string csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
