// HOW IT FIRES: Bulk-delete server-side pattern.
// =============================================
// Pairs with the front-end JS in reference/Users.snippet.cshtml.
// Hard rules from SPEC.md §6:
//   - Distinct the IDs (the form may post duplicates).
//   - Defensively skip self even if the form posted the admin's ID.
//   - On FK violation (SqlException 547) report "Blocked", never crash.
//   - Group result into Deleted / Blocked / Skipped-self for the toast.

[HttpPost, ValidateAntiForgeryToken]
public async Task<IActionResult> BulkDeleteUsers(int[] userIds)
{
    if (userIds == null || userIds.Length == 0)
    {
        TempData["Error"] = "No users selected.";
        return RedirectToAction("Users");
    }

    var selfId      = GetUserId();
    var deleted     = new List<string>();
    var blocked     = new List<string>();
    var skippedSelf = false;

    foreach (var id in userIds.Distinct())
    {
        if (id == selfId) { skippedSelf = true; continue; }   // never delete yourself

        var user = await _db.GetUserByIdAsync(id);
        if (user == null) continue;

        try
        {
            await _db.DeleteUserAsync(id);
            deleted.Add(user.FullName);
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 547)
        {
            blocked.Add(user.FullName);    // still referenced - admin should disable
        }
        catch
        {
            blocked.Add(user.FullName);
        }
    }

    var parts = new List<string>();
    if (deleted.Count > 0)
        parts.Add($"Deleted {deleted.Count}: {string.Join(", ", deleted)}");
    if (blocked.Count > 0)
        parts.Add($"Blocked {blocked.Count} (still referenced - disable instead): {string.Join(", ", blocked)}");
    if (skippedSelf)
        parts.Add("Skipped your own account.");

    TempData[deleted.Count > 0 ? "Success" : "Error"] =
        parts.Count > 0 ? string.Join(" | ", parts) : "No users were deleted.";

    return RedirectToAction("Users");
}
