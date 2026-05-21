// Reference IAlertDataSource implementation from the Fleet Management project.
// Walks PassengerVehicles + Trucks + their date columns, classifies each via
// the project's existing ExpiryHelper (Red/Yellow), and emits AlertMatch rows.
//
// PrimaryFilter   = vehicle types ("Passenger,Trucks")
// SecondaryFilter = document types ("MVPI,Registration,OperationCards")
// Severities      = "Red" / "Yellow" / both

using FleetManagement.Helpers;
using FleetManagement.Models;
using FleetManagement.Services;

namespace FleetManagement.Services;

public class FleetAlertDataSource : IAlertDataSource
{
    private readonly IDbService _db;
    public FleetAlertDataSource(IDbService db) { _db = db; }

    public async Task<List<AlertMatch>> CollectMatchesAsync(AlertRule rule, CancellationToken ct = default)
    {
        var th       = ExpiryHelper.ReadThresholds(await _db.GetAllConfigAsync());
        var primary  = SplitCsv(rule.PrimaryFilter);
        var secondary= SplitCsv(rule.SecondaryFilter);
        var sev      = SplitCsv(rule.Severities);

        bool wantPrimary  (string v) => primary.Count   == 0 || primary  .Contains(v);
        bool wantSecondary(string v) => secondary.Count == 0 || secondary.Contains(v);
        bool wantSev      (string v) => sev.Count       == 0 || sev      .Contains(v);

        var matches = new List<AlertMatch>();

        if (wantPrimary("Passenger"))
        {
            foreach (var v in await _db.GetPassengerVehiclesAsync())
            {
                var label = $"{v.PlateNumber} — {v.VehicleMake} {v.Model}".Trim(' ', '—');
                if (wantSecondary("MVPI"))         AddIfHit(matches, "Passenger", v.PlateNumber ?? "", label, "MVPI",         v.MvpiExpiry,         v.AssignedUserName ?? v.AssignedTo, v.Branch, th, wantSev);
                if (wantSecondary("Registration")) AddIfHit(matches, "Passenger", v.PlateNumber ?? "", label, "Registration", v.RegistrationExpiry, v.AssignedUserName ?? v.AssignedTo, v.Branch, th, wantSev);
            }
        }
        if (wantPrimary("Trucks"))
        {
            foreach (var t in await _db.GetTrucksAsync())
            {
                var label = $"{t.PlateNumber} — {t.VehicleMake} {t.Model}".Trim(' ', '—');
                if (wantSecondary("MVPI"))           AddIfHit(matches, "Trucks", t.PlateNumber ?? "", label, "MVPI",           t.MvpiExpiry,         t.Department, t.Branch, th, wantSev);
                if (wantSecondary("Registration"))   AddIfHit(matches, "Trucks", t.PlateNumber ?? "", label, "Registration",   t.RegistrationExpiry, t.Department, t.Branch, th, wantSev);
                if (wantSecondary("OperationCards")) AddIfHit(matches, "Trucks", t.PlateNumber ?? "", label, "OperationCards", t.OperationCards,     t.Department, t.Branch, th, wantSev);
            }
        }
        return matches
            .OrderBy(m => m.Severity == "Red" ? 0 : 1)
            .ThenBy (m => m.DaysFromToday)
            .ToList();
    }

    private static void AddIfHit(List<AlertMatch> dest, string primary, string id, string label,
        string secondary, string? raw, string? owner, string? branch,
        ExpiryHelper.Thresholds th, Func<string, bool> wantSev)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        var b = ExpiryHelper.GetBadge(raw, th.YellowDays, th.BlueDays, th.GreenDays);
        var severity = b.Status switch { "expired" => "Red", "yellow" => "Yellow", _ => "" };
        if (string.IsNullOrEmpty(severity) || !wantSev(severity)) return;

        DateTime? parsed = null; var days = 0;
        if (DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d))
        { parsed = d.Date; days = (int)Math.Round((d.Date - DateTime.Today).TotalDays); }

        dest.Add(new AlertMatch
        {
            PrimaryType   = primary,
            SecondaryType = secondary,
            Identifier    = id,
            Label         = label,
            DueRaw        = raw,
            DueDate       = parsed,
            Severity      = severity,
            DaysFromToday = days,
            Owner         = owner ?? "",
            Branch        = branch ?? ""
        });
    }

    private static List<string> SplitCsv(string csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
