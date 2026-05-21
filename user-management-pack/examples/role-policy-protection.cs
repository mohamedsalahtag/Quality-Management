// HOW IT FIRES: Role-based authorization on controllers + actions.
// ================================================================
// Two policies are registered in Program.cs (see reference/Program.cs.snippet):
//
//   "TechOrAdmin" -> Role in { Technician, FirstLevelSupport, SiteAdmin }
//   "AdminOnly"   -> Role == SiteAdmin
//
// Apply with [Authorize(Policy = "...")] at class or method level.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HelpDesk.Controllers;

// Whole-controller gate - admins only.
[Authorize(Policy = "AdminOnly")]
public class AdminController : Controller
{
    public IActionResult Users() => View();
}

// Anonymous index, gated actions for technicians+.
public class TicketController : Controller
{
    [Authorize]                              // any signed-in user
    public IActionResult Index() => View();

    [Authorize(Policy = "TechOrAdmin")]      // technicians + admins
    public IActionResult Pickup(int id)      => View();

    [Authorize(Policy = "AdminOnly")]        // admins only
    public IActionResult ForceClose(int id)  => View();
}

// In a Razor view, render different UI based on role:
//
//   @if (User.IsInRole("SiteAdmin"))
//   {
//       <a href="/Admin">Admin Panel</a>
//   }
//   @if (User.IsInRole("Technician") || User.IsInRole("FirstLevelSupport") || User.IsInRole("SiteAdmin"))
//   {
//       <a href="/Notes">My Notes</a>
//   }
