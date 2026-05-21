// Site Configuration controller bits — extract these into your AdminController.
// Three responsibilities:
//   1. GET  /Admin/Config   — render the form with current SMTP keys
//   2. POST /Admin/Config   — save them (NEVER overwrite SmtpPassword with blank)
//   3. GET  /Admin/TestEmail?to=... — fire a one-shot test email
//
// Adapt the routing to your stack (Express, Django, etc.) but keep the
// "blank password = keep existing" rule on save and the test-email shape.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using HelpDesk.Models;
using HelpDesk.Services;

namespace HelpDesk.Controllers;

[Authorize(Policy = "AdminOnly")]
public partial class AdminController : Controller
{
    private readonly IDbService _db;
    private readonly IEmailService _email;
    public AdminController(IDbService db, IEmailService email) { _db = db; _email = email; }

    [HttpGet]
    public async Task<IActionResult> Config()
    {
        var cfg = await _db.GetAllConfigAsync();
        var vm = new SiteConfigViewModel
        {
            SmtpHost           = cfg.GetValueOrDefault("SmtpHost", ""),
            SmtpPort           = cfg.GetValueOrDefault("SmtpPort", "587"),
            SmtpUser           = cfg.GetValueOrDefault("SmtpUser", ""),
            SmtpPassword       = "",                                     // never echo
            SmtpFromEmail      = cfg.GetValueOrDefault("SmtpFromEmail", ""),
            SmtpEnableSsl      = cfg.GetValueOrDefault("SmtpEnableSsl", "true") == "true",
            GeneralTicketEmail = cfg.GetValueOrDefault("GeneralTicketEmail", ""),
            SiteName           = cfg.GetValueOrDefault("SiteName", "IT HelpDesk"),
            SiteUrl            = cfg.GetValueOrDefault("SiteUrl", "")
        };
        ViewBag.SmtpPasswordSaved = !string.IsNullOrEmpty(cfg.GetValueOrDefault("SmtpPassword", ""));
        ViewBag.TestEmailTo       = cfg.GetValueOrDefault("SmtpUser", "your@email.com");
        return View(vm);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Config(SiteConfigViewModel model)
    {
        var uid = GetCurrentUserId();
        // Only save non-empty values to prevent accidental erasure.
        if (!string.IsNullOrEmpty(model.SmtpHost))
            await _db.SetConfigAsync("SmtpHost", model.SmtpHost, uid);
        if (!string.IsNullOrEmpty(model.SmtpPort))
            await _db.SetConfigAsync("SmtpPort", model.SmtpPort, uid);
        if (!string.IsNullOrEmpty(model.SmtpUser))
            await _db.SetConfigAsync("SmtpUser", model.SmtpUser, uid);
        if (!string.IsNullOrEmpty(model.SmtpPassword))                  // blank = keep
            await _db.SetConfigAsync("SmtpPassword", model.SmtpPassword, uid);
        if (!string.IsNullOrEmpty(model.SmtpFromEmail))
            await _db.SetConfigAsync("SmtpFromEmail", model.SmtpFromEmail, uid);
        await _db.SetConfigAsync("SmtpEnableSsl", model.SmtpEnableSsl ? "true" : "false", uid);
        if (!string.IsNullOrEmpty(model.GeneralTicketEmail))
            await _db.SetConfigAsync("GeneralTicketEmail", model.GeneralTicketEmail, uid);
        if (!string.IsNullOrEmpty(model.SiteName))
            await _db.SetConfigAsync("SiteName", model.SiteName, uid);
        if (!string.IsNullOrEmpty(model.SiteUrl))
            await _db.SetConfigAsync("SiteUrl", model.SiteUrl, uid);

        TempData["Success"] = "Configuration saved.";
        return RedirectToAction("Config");
    }

    // GET /Admin/TestEmail?to=admin@company.com
    [HttpGet]
    public async Task<IActionResult> TestEmail(string to)
    {
        if (string.IsNullOrWhiteSpace(to))
            return Content("Provide a 'to' query parameter.");

        var cfg = await _db.GetAllConfigAsync();
        var siteName = cfg.GetValueOrDefault("SiteName", "IT HelpDesk");

        // Build a fake ticket so we can reuse the same template the real
        // emails use. Only the headline differs.
        var fake = new Ticket
        {
            TicketId      = 0,
            TicketNumber  = "TEST",
            Subject       = "SMTP Test Email",
            CategoryName  = "Test",
            Priority      = "Medium",
            Status        = "Open",
            SubmittedAt   = DateTime.Now,
            RequesterName = "Admin"
        };
        await _email.SendTicketCreatedAsync(fake, to);
        return Content($"Test email dispatched to {to}. Check your inbox in ~30 seconds.");
    }

    private int GetCurrentUserId()
    {
        // Replace with your auth lookup.
        return 0;
    }
}

public class SiteConfigViewModel
{
    public string SmtpHost           { get; set; } = "";
    public string SmtpPort           { get; set; } = "587";
    public string SmtpUser           { get; set; } = "";
    public string SmtpPassword       { get; set; } = "";
    public string SmtpFromEmail      { get; set; } = "";
    public bool   SmtpEnableSsl      { get; set; } = true;
    public string GeneralTicketEmail { get; set; } = "";
    public string SiteName           { get; set; } = "IT HelpDesk";
    public string SiteUrl            { get; set; } = "";
}
