// HOW TO FIRE: Triggers #1, #2, and #6 (ticket created)
// =============================================================
// Place this block at the END of your "create ticket" service method,
// AFTER the ticket has been written to the database. The pattern is:
//   1. Reload the freshly-saved ticket so navigation properties (CategoryName,
//      GroupName) are populated for the email template.
//   2. Fire-and-forget the requester confirmation (Trigger #1).
//   3. Fire-and-forget the general-inbox copy (Trigger #6).
//   4. Resolve the technician recipient list and fire the fan-out (Trigger #2).
//
// The two fire-and-forget calls are wrapped in Task.Run so a slow SMTP
// server cannot block the user-facing request.

var fullTicket = await _db.GetTicketByIdAsync(ticketId);
if (fullTicket != null)
{
    // ── Trigger #1: confirmation to the requester ──────────
    _ = Task.Run(async () =>
    {
        try { await _email.SendTicketCreatedAsync(fullTicket, requesterEmail); }
        catch { /* logged inside SendAsync */ }
    });

    // ── Trigger #6: copy to the General Ticket Inbox ───────
    _ = Task.Run(async () =>
    {
        try { await _email.SendToGeneralInboxAsync(fullTicket); }
        catch { }
    });

    // ── Trigger #2: fan-out to all technicians + SiteAdmins
    var cfg     = await _db.GetAllConfigAsync();
    var siteUrl = cfg.GetValueOrDefault("SiteUrl", "http://localhost:5000");

    var allUsers     = await _db.GetAllUsersAsync();
    var groupMembers = await _db.GetGroupMembersAsync(fullTicket.GroupId);
    var memberIds    = groupMembers.Select(m => m.UserId).ToHashSet();

    var techEmails = allUsers
        .Where(u => u.IsActive &&
                    (u.Role == UserRoles.SiteAdmin ||
                     ((u.Role == UserRoles.Technician || u.Role == UserRoles.FirstLevelSupport)
                      && memberIds.Contains(u.UserId))))
        .Select(u => u.Email)
        .Where(e => !string.IsNullOrEmpty(e))
        .Distinct()
        .ToList();

    // SendNewTicketToTechniciansAsync internally Task.Runs each recipient.
    await _email.SendNewTicketToTechniciansAsync(fullTicket, techEmails, siteUrl);
}
