using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SharbatlyQMS.Web.Security;
using SharbatlyQMS.Web.Services;

namespace SharbatlyQMS.Web.Controllers;

/// <summary>
/// The bell (M45): a signed-in user's own notifications.
///
/// Always allowed, like the profile page -- there is nothing to grant, because
/// every query is bound to the caller's own user id and nobody can be shown,
/// or mark, another person's notification. A notification is only a pointer:
/// the page it links to applies its own permission and branch checks.
/// </summary>
[Authorize]
public class InboxController : Controller
{
    private readonly INotificationService _bell;
    public InboxController(INotificationService bell) => _bell = bell;

    private int? UserId =>
        int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    [QmsAlwaysAllowed]
    public async Task<IActionResult> Index()
    {
        var rows = UserId is int id
            ? await _bell.ListAsync(id, 200)
            : (IReadOnlyList<NotificationRow>)Array.Empty<NotificationRow>();
        return View(rows);
    }

    /// <summary>The newest few, for the dropdown under the bell.</summary>
    [HttpGet]
    [QmsAlwaysAllowed]
    public async Task<IActionResult> Recent()
    {
        if (UserId is not int id) return Json(new { count = 0, items = Array.Empty<object>() });
        var rows = await _bell.ListAsync(id, 8);
        return Json(new
        {
            count = await _bell.UnreadCountAsync(id),
            items = rows.Select(r => new
            {
                id     = r.NotificationId,
                title  = r.Title,
                body   = r.Body,
                unread = r.IsUnread,
                at     = DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                url    = Url.Action(nameof(Open), new { id = r.NotificationId })
            })
        });
    }

    /// <summary>Marks one notification read and goes where it points.</summary>
    [HttpGet]
    [QmsAlwaysAllowed]
    public async Task<IActionResult> Open(long id)
    {
        var row = UserId is int uid ? await _bell.MarkReadAsync(uid, id) : null;
        // Only ever a path inside this application.
        if (row?.LinkUrl is { Length: > 0 } link && Url.IsLocalUrl(link)) return LocalRedirect(link);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    [QmsAlwaysAllowed]
    public async Task<IActionResult> MarkAllRead()
    {
        if (UserId is int id) await _bell.MarkAllReadAsync(id);
        return RedirectToAction(nameof(Index));
    }
}
