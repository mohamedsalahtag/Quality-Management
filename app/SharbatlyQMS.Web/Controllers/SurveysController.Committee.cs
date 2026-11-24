using Microsoft.AspNetCore.Mvc;
using SharbatlyQMS.Web.Extensions;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Security;
using SharbatlyQMS.Web.Services;
using SharbatlyQMS.Web.ViewModels;

namespace SharbatlyQMS.Web.Controllers;

/// <summary>
/// What a confirmed survey's committee does (M46): the appointment, who
/// attended, the QC officer's inspection confirmation, and attachments.
///
/// One person or team acts for the whole committee -- nobody is asked to sign
/// in and confirm their own attendance, and an external member could not.
/// Each kind of act is its own permission. Every action re-checks the branch
/// here and the "survey must be confirmed" rule in the service; a hidden
/// button is never the only thing standing in the way.
/// </summary>
public partial class SurveysController
{
    /// <summary>The survey, or the response that ends the request: not found,
    /// or not in the caller's branches.</summary>
    private async Task<(Survey? survey, IActionResult? stop)> LoadAsync(long id)
    {
        var survey = await _surveys.GetAsync(id);
        if (survey == null) return (null, NotFound());
        if (!User.GetPlantScope().Allows(survey.Plant)) return (null, Forbid());
        return (survey, null);
    }

    private IActionResult Back(long id, string section, (bool ok, string? error) result, string success)
    {
        if (result.ok) TempData["Success"] = success;
        else TempData["Error"] = result.error;
        return RedirectToAction(nameof(Details), null, new { id }, section);
    }

    // ---- Committee ----------------------------------------------------

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Surveys.Schedule, Seed.ManagerOrClaimManagerOrAdmin, "Maintain survey appointments")]
    public async Task<IActionResult> AssignCommittee(long id)
    {
        var (_, stop) = await LoadAsync(id);
        if (stop != null) return stop;
        return Back(id, "committee", await _committee.AssignNowAsync(id, CurrentUser), "The committee was assigned.");
    }

    // ---- Appointment --------------------------------------------------

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Surveys.Schedule, Seed.ManagerOrClaimManagerOrAdmin, "Maintain survey appointments")]
    public async Task<IActionResult> SaveAppointment(long id, AppointmentInput input)
    {
        var (_, stop) = await LoadAsync(id);
        if (stop != null) return stop;
        return Back(id, "appointment", await _committee.SaveAppointmentAsync(id, input, CurrentUser),
            "Appointment saved. Confirm the schedule to tell the participants.");
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Surveys.Schedule, Seed.ManagerOrClaimManagerOrAdmin, "Maintain survey appointments")]
    public async Task<IActionResult> ConfirmSchedule(long id)
    {
        var (_, stop) = await LoadAsync(id);
        if (stop != null) return stop;
        return Back(id, "appointment", await _committee.ConfirmScheduleAsync(id, CurrentUser),
            "Schedule confirmed. The participants have been notified.");
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Surveys.Schedule, Seed.ManagerOrClaimManagerOrAdmin, "Maintain survey appointments")]
    public async Task<IActionResult> Reschedule(long id, AppointmentInput input, string? reason)
    {
        var (_, stop) = await LoadAsync(id);
        if (stop != null) return stop;
        return Back(id, "appointment", await _committee.RescheduleAsync(id, input, reason, CurrentUser),
            "Survey rescheduled. The participants have been notified.");
    }

    // ---- Attendance ---------------------------------------------------

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Surveys.Attendance, Seed.ManagerOrClaimManagerOrAdmin, "Record committee attendance")]
    public async Task<IActionResult> SaveAttendance(long id, AttendanceInput input)
    {
        var (_, stop) = await LoadAsync(id);
        if (stop != null) return stop;
        return Back(id, "attendance", await _committee.SaveAttendanceAsync(id, input, CurrentUser), "Attendance recorded.");
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Surveys.ConfirmAttendance, Seed.ManagerOrClaimManagerOrAdmin, "Confirm committee attendance")]
    public async Task<IActionResult> ConfirmAttendance(long id)
    {
        var (_, stop) = await LoadAsync(id);
        if (stop != null) return stop;
        return Back(id, "attendance", await _committee.ConfirmAttendanceAsync(id, CurrentUser),
            "Attendance confirmed. The record is now locked.");
    }

    // ---- Inspection ---------------------------------------------------

    /// <summary>The QC officer's confirmation that the surveyor inspected the
    /// shipment. It is not a claim approval and changes no status.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Surveys.ConfirmInspection, Seed.ManagerOrAdmin, "Confirm the survey inspection")]
    public async Task<IActionResult> ConfirmInspection(long id, InspectionInput input)
    {
        var (_, stop) = await LoadAsync(id);
        if (stop != null) return stop;
        return Back(id, "inspection", await _committee.ConfirmInspectionAsync(id, input, CurrentUser),
            "Survey inspection confirmed.");
    }

    // ---- Attachments --------------------------------------------------
    // A survey's files are confidential. They are stored as documents (never
    // as photos, which are open static files) and served only from here.

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Surveys.Attachments, Seed.ManagerOrClaimManagerOrAdmin, "Upload or remove survey attachments")]
    [RequestSizeLimit(100L * 1024 * 1024)]
    public async Task<IActionResult> UploadDocument(long id, string? category, List<IFormFile>? files)
    {
        var (_, stop) = await LoadAsync(id);
        if (stop != null) return stop;
        if (files == null || files.Count == 0)
            return Back(id, "attachments", (false, "No files were selected."), "");
        var cat = SurveyDocCategory.IsValid(category) ? category! : SurveyDocCategory.Other;

        var (saved, rejected) = await _docs.UploadAsync(DocumentService.SurveyOwner, id, cat, files, CurrentUser);
        if (saved > 0)
            await _audit.WriteAsync(EntityTypes.Survey, id, ActionCodes.SurveyAttachmentAdded,
                null, new { category = cat, files = saved }, CurrentUser);
        if (rejected.Count > 0)
            TempData["Error"] = $"{rejected.Count} file(s) not saved: {string.Join("; ", rejected)}";
        if (saved > 0) TempData["Success"] = $"Attached {saved} file(s).";
        return RedirectToAction(nameof(Details), null, new { id }, "attachments");
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Surveys.Attachments, Seed.ManagerOrClaimManagerOrAdmin, "Upload or remove survey attachments")]
    public async Task<IActionResult> DeleteDocument(long id, long documentId)
    {
        var (_, stop) = await LoadAsync(id);
        if (stop != null) return stop;
        var doc = await OwnDocumentAsync(id, documentId);
        if (doc == null) return NotFound();

        await _docs.SoftDeleteAsync(documentId, CurrentUser);
        await _audit.WriteAsync(EntityTypes.Survey, id, ActionCodes.SurveyAttachmentRemoved,
            new { file = doc.OriginalName, category = doc.Category }, null, CurrentUser);
        TempData["Success"] = "Attachment removed.";
        return RedirectToAction(nameof(Details), null, new { id }, "attachments");
    }

    /// <summary>Reads one attachment. Seeing the survey is what allows it; the
    /// file must belong to THIS survey, so a document number alone opens nothing.</summary>
    [HttpGet]
    [RequireScreen(Screens.Surveys)]
    public async Task<IActionResult> Document(long id, long documentId, bool inline = false)
    {
        var (_, stop) = await LoadAsync(id);
        if (stop != null) return stop;
        var doc = await OwnDocumentAsync(id, documentId);
        if (doc == null) return NotFound();

        var path = _docs.ResolveAbsolutePath(doc);
        if (path == null || !System.IO.File.Exists(path)) return NotFound();
        var stream = System.IO.File.OpenRead(path);
        // The type comes from the extension, never from what the uploader claimed.
        var contentType = _docs.ContentTypeFor(doc.OriginalName);

        // Inline only for what a browser can safely show in place.
        if (inline && (contentType.StartsWith("image/", StringComparison.Ordinal) || contentType == "application/pdf"))
        {
            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return File(stream, contentType);
        }
        return File(stream, contentType, doc.OriginalName);
    }

    private async Task<DocumentInfo?> OwnDocumentAsync(long surveyId, long documentId)
    {
        var doc = await _docs.GetAsync(documentId);
        return doc != null && doc.OwnerType == DocumentService.SurveyOwner && doc.OwnerId == surveyId ? doc : null;
    }
}
