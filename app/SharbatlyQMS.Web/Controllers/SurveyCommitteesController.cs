using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SharbatlyQMS.Web.Models;
using SharbatlyQMS.Web.Models.Security;
using SharbatlyQMS.Web.Security;
using SharbatlyQMS.Web.Services;

namespace SharbatlyQMS.Web.Controllers;

/// <summary>
/// Parameters -> Survey committees (M46): who sits on the committee for each
/// branch. One active committee per branch, plus one default that serves any
/// branch without its own.
///
/// A member is either an application user (internal) or somebody outside
/// (external: a typed name, organisation, e-mail and phone). Putting an
/// external person here gives them no account and no access -- they are only
/// somebody an appointment can be e-mailed to and whose attendance can be
/// recorded.
///
/// Changing a committee never changes a survey already confirmed: each
/// survey keeps its own copy of the members from the moment it was confirmed.
/// </summary>
[Authorize]
public class SurveyCommitteesController : Controller
{
    private readonly ISurveyCommitteeService _committee;
    public SurveyCommitteesController(ISurveyCommitteeService committee) => _committee = committee;

    private string CurrentUser => User.FindFirst(ClaimTypes.Name)?.Value ?? "system";

    [RequireScreen(Screens.SurveyCommittees, Seed.ManagerOrClaimManagerOrAdmin, "Open Survey committees")]
    public async Task<IActionResult> Index()
    {
        ViewBag.Users = await _committee.ListUsersAsync();
        return View(await _committee.ListCommitteesAsync());
    }

    private IActionResult Done((bool ok, string? error) result, string success)
    {
        if (result.ok) TempData["Success"] = success;
        else TempData["Error"] = result.error;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Parameters.SurveyCommitteesEdit, Seed.AdminOnly, "Edit survey committees")]
    public async Task<IActionResult> SaveCommittee(SurveyCommittee committee) =>
        Done(await _committee.SaveCommitteeAsync(committee, CurrentUser), "Committee saved.");

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Parameters.SurveyCommitteesEdit, Seed.AdminOnly, "Edit survey committees")]
    public async Task<IActionResult> DeleteCommittee(int id) =>
        Done(await _committee.DeleteCommitteeAsync(id, CurrentUser),
            "Committee deleted. Surveys already confirmed keep their own copy of its members.");

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Parameters.SurveyCommitteesEdit, Seed.AdminOnly, "Edit survey committees")]
    public async Task<IActionResult> SaveMember(SurveyCommitteeMember member) =>
        Done(await _committee.SaveMemberAsync(member, CurrentUser), "Member saved.");

    [HttpPost, ValidateAntiForgeryToken]
    [RequirePermission(Perm.Parameters.SurveyCommitteesEdit, Seed.AdminOnly, "Edit survey committees")]
    public async Task<IActionResult> DeleteMember(int id) =>
        Done(await _committee.DeleteMemberAsync(id, CurrentUser), "Member removed.");
}
