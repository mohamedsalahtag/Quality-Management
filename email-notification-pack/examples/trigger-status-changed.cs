// HOW TO FIRE: Triggers #3 (picked up) and #5 (status changed)
// ============================================================
// Trigger #3 fires when a technician picks up an unassigned ticket.
// Trigger #5 fires for any other status change the requester should know
// about (Closed / Cancelled / Escalated). Generic copy is used for unknown
// statuses so adding a new status doesn't break the email path.
//
// Both are fire-and-forget.

// ── Trigger #3: ticket picked up ─────────────────────────────────────
public async Task PickUpTicket(int ticketId, int techId)
{
    var ticket = await _db.GetTicketByIdAsync(ticketId);
    var tech   = await _db.GetUserByIdAsync(techId);
    var requester = await _db.GetUserByIdAsync(ticket.RequesterId);
    if (ticket == null || tech == null || requester == null) return;

    // ... existing pickup logic (set status, AssignedTechId, PickedUpAt) ...
    await _db.UpdateTicketAsync(ticket);

    if (!string.IsNullOrEmpty(requester.Email))
    {
        var ticketCopy = ticket;
        var techName   = tech.FullName;
        var email      = requester.Email;
        _ = Task.Run(async () =>
        {
            try { await _email.SendTicketPickedUpAsync(ticketCopy, techName, email); }
            catch (Exception ex) { _logger.LogError(ex, "Pickup email failed to {Email}", email); }
        });
    }
}

// ── Trigger #5: status change (Close, Cancel, Escalate, ...) ─────────
public async Task ChangeTicketStatus(int ticketId, string newStatus)
{
    var ticket = await _db.GetTicketByIdAsync(ticketId);
    if (ticket == null) return;
    var requester = await _db.GetUserByIdAsync(ticket.RequesterId);

    // ... existing status-change logic ...
    ticket.Status = newStatus;
    await _db.UpdateTicketAsync(ticket);

    if (requester != null && !string.IsNullOrEmpty(requester.Email))
    {
        var ticketCopy = ticket;
        var status     = newStatus;
        var email      = requester.Email;
        _ = Task.Run(async () =>
        {
            try { await _email.SendTicketStatusChangedAsync(ticketCopy, status, email); }
            catch (Exception ex) { _logger.LogError(ex, "Status email failed to {Email}", email); }
        });
    }
}
