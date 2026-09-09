using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Security;
using SharbatlyQMS.Web.Services;

namespace SharbatlyQMS.Web.Controllers;

/// <summary>
/// Admin → Labels. Renames any button, grid header, field label or list
/// caption in the application without a code change or a deployment.
///
/// The list is discovered, not declared: every label the application renders
/// records itself, so this screen shows exactly what the screens really print.
/// A page nobody has opened since the last deployment therefore has no labels
/// here yet — visiting it once is enough.
/// </summary>
[Authorize]
public class LabelsController : Controller
{
    private readonly ILabelService _labels;
    private readonly ILogger<LabelsController> _log;

    public LabelsController(ILabelService labels, ILogger<LabelsController> log)
    {
        _labels = labels;
        _log    = log;
    }

    private string Actor => User.FindFirst(ClaimTypes.Name)?.Value ?? "system";

    [HttpGet]
    [RequireScreen(Screens.AdminLabels, Seed.AdminOnly, "Open the Labels screen")]
    public async Task<IActionResult> Index(string? screen, string? kind, string? q,
        bool changedOnly = false, CancellationToken ct = default)
    {
        // Flush before listing: discovery is written by a 60-second background
        // tick, and an administrator who has just visited a screen to find its
        // labels should not have to wait for that tick to see them.
        // The PDF report's captions are registered here rather than waiting for
        // somebody to generate a report: discovery works by rendering, and a
        // report nobody has printed since the last deployment would contribute
        // nothing to a screen that is supposed to list everything renameable.
        foreach (var caption in Services.Pdf.ReportLabelCatalog.All)
            _labels.Text(caption, Services.Pdf.ReportLabels.ScreenKey);

        await _labels.FlushAsync(ct);

        var all = await _labels.ListAsync(ct);

        var filtered = all.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(screen))
            filtered = filtered.Where(l => string.Equals(l.ScreenKey, screen, StringComparison.OrdinalIgnoreCase));
        // "Other" is the bucket for labels discovered at runtime, which carry no
        // kind at all -- match on the DISPLAYED value so the chip and the rows
        // it filters to always agree.
        if (!string.IsNullOrWhiteSpace(kind))
            filtered = filtered.Where(l => string.Equals(l.KindLabel, kind, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(q))
        {
            // Match on what the code ships AND on what it currently prints — an
            // administrator hunting a label they already renamed will search
            // for the name they gave it, not the original.
            var needle = q.Trim();
            filtered = filtered.Where(l =>
                l.Key.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || (l.CustomText ?? "").Contains(needle, StringComparison.OrdinalIgnoreCase));
        }
        if (changedOnly) filtered = filtered.Where(l => l.IsOverridden);

        ViewBag.Screens = all.Select(l => l.ScreenKey)
                             .Where(s => !string.IsNullOrWhiteSpace(s))
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(s => s)
                             .ToList();
        // Kinds with their counts, so the chips show how much is behind each
        // one before the administrator commits to a click.
        ViewBag.Kinds = all.GroupBy(l => l.KindLabel)
                           .Select(g => new KeyValuePair<string, int>(g.Key, g.Count()))
                           .OrderByDescending(kv => kv.Value)
                           .ToList();
        ViewBag.Screen       = screen;
        ViewBag.Kind         = kind;
        ViewBag.Query        = q;
        ViewBag.ChangedOnly  = changedOnly;
        ViewBag.TotalCount   = all.Count;
        ViewBag.ChangedCount = all.Count(l => l.IsOverridden);
        return View(filtered.ToList());
    }

    /// <summary>
    /// Saves the labels edited on one page of the screen. Only rows whose value
    /// actually differs are written, so opening the screen and pressing Save
    /// does not stamp every label with an update.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    // The page can list over a thousand labels and each row posts a key and a
    // value, so a full save exceeds the framework's default 1,024-value limit
    // and the request is rejected before any action code runs. The view now
    // submits only edited rows; this is the floor under that, for a browser
    // with scripting off or a genuine bulk rename.
    [RequestFormLimits(ValueCountLimit = 16384)]
    [RequirePermission(Perm.Admin.LabelsEdit, Seed.AdminOnly, "Rename a label")]
    public async Task<IActionResult> Save(string[]? key, string[]? value,
        string? screen, string? kind, string? q, bool changedOnly = false,
        CancellationToken ct = default)
    {
        key   ??= Array.Empty<string>();
        value ??= Array.Empty<string>();
        if (key.Length != value.Length)
        {
            TempData["Error"] = "The form was incomplete — nothing was saved.";
            return RedirectToAction(nameof(Index), new { screen, kind, q, changedOnly });
        }

        var existing = (await _labels.ListAsync(ct))
            .ToDictionary(l => l.Key, l => l.CustomText ?? "", StringComparer.Ordinal);

        var changed = 0;
        for (var i = 0; i < key.Length; i++)
        {
            var k = key[i];
            var v = (value[i] ?? "").Trim();
            if (existing.TryGetValue(k, out var current) && string.Equals(current, v, StringComparison.Ordinal))
                continue;
            await _labels.SaveAsync(k, v, Actor, ct);
            changed++;
        }

        if (changed > 0)
            _log.LogInformation("{User} changed {Count} UI label(s).", Actor, changed);

        TempData[changed == 0 ? "Error" : "Success"] = changed == 0
            ? "Nothing changed."
            : $"Saved {changed} label{(changed == 1 ? "" : "s")}. Every screen using them now shows the new text.";
        return RedirectToAction(nameof(Index), new { screen, kind, q, changedOnly });
    }

    /// <summary>Puts a screen's labels — or all of them — back to the English
    /// the code ships.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Admin.LabelsEdit, Seed.AdminOnly, "Rename a label")]
    public async Task<IActionResult> Reset(string? screen, CancellationToken ct = default)
    {
        var n = await _labels.ResetAsync(screen, Actor, ct);
        _log.LogInformation("{User} reset {Count} UI label(s) on {Screen}.", Actor, n, screen ?? "(all screens)");
        TempData[n == 0 ? "Error" : "Success"] = n == 0
            ? "There was nothing to reset."
            : $"Reset {n} label{(n == 1 ? "" : "s")} to the original text.";
        return RedirectToAction(nameof(Index), new { screen });
    }
}
