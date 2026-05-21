// HOW TO FIRE: Trigger #4 (reply added)
// =====================================
// Place this at the END of your "add reply" service method, AFTER the reply
// row has been inserted. Hard rules:
//   - Internal replies MUST NOT trigger emails (filter on r.IsInternal).
//   - Empty / "placeholder" requester emails are skipped silently.
//   - Email is fire-and-forget.
//
// SendTicketReplyAsync renders the FULL conversation thread with the new
// reply highlighted in blue. Pass `allReplies` so it has the history.

if (!newReplyIsInternal &&
    !string.IsNullOrEmpty(requesterEmail) &&
    !requesterEmail.Contains("placeholder", StringComparison.OrdinalIgnoreCase))
{
    var allReplies = await _db.GetRepliesAsync(ticketId);
    var newReply   = allReplies.LastOrDefault(r => !r.IsInternal) ?? new TicketReply
    {
        AuthorName = authorName,
        AuthorRole = authorRole,
        Message    = message,
        CreatedAt  = DateTime.UtcNow
    };

    // Capture by value so the closure doesn't see mutated state.
    var emailAddr  = requesterEmail;
    var ticketCopy = ticket;
    var replyCopy  = newReply;
    var allCopy    = allReplies;

    _ = Task.Run(async () =>
    {
        try { await _email.SendTicketReplyAsync(ticketCopy, replyCopy, allCopy, emailAddr); }
        catch (Exception ex) { _logger.LogError(ex, "Reply email failed to {Email}", emailAddr); }
    });
}
else if (newReplyIsInternal)
{
    _logger.LogDebug("Reply email skipped -- internal note for ticket {Id}", ticketId);
}
else
{
    _logger.LogWarning("Reply email skipped -- empty/placeholder email for ticket {Id}", ticketId);
}
