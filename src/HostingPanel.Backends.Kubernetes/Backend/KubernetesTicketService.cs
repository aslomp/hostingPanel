using HostingPanel.Providers.PowerDNS;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HostingPanel.Api.Adapters;
using HostingPanel.Api.Persistence;
using HostingPanel.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace HostingPanel.Api;

public sealed class KubernetesTicketService(HostingPanelDbContext db, KubernetesIdentityService identity, TicketEvents? events = null) : ITickets
{
    public async Task<(int Status, TicketInbox? Inbox, string? Error)> GetInboxAsync(
        AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return (401, null, "UNAUTHORIZED");
        var tickets = (await db.Tickets.AsNoTracking()
            .Where(x => x.OwnerId == principal.Id).ToListAsync(ct))
            .OrderByDescending(x => x.CreatedAt).ToArray();
        var ids = tickets.Select(x => x.Id).ToArray();
        var replies = (await db.TicketReplies.AsNoTracking()
            .Where(x => ids.Contains(x.TicketId)).ToListAsync(ct))
            .OrderBy(x => x.CreatedAt);
        var byTicket = replies.GroupBy(x => x.TicketId).ToDictionary(g => g.Key,
            g => g.Select(x => JsonSerializer.SerializeToElement(new {
                message = x.Text, timestamp = x.CreatedAt
            })).ToArray());
        var messages = (await db.Messages.AsNoTracking()
            .Where(x => x.RecipientId == principal.Id).ToListAsync(ct))
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new MessageEntry(x.Id, x.Subject, x.From, x.From, x.Unread, x.CreatedAt)).ToArray();
        return (200, new TicketInbox(
            tickets.Select(x => new TicketEntry(x.Id, x.Id.ToString("D9"), "request",
                x.Subject, principal.Username, principal.Username, x.Text, x.Open, x.Unread,
                x.Priority, x.CreatedAt, byTicket.GetValueOrDefault(x.Id) ?? [])).ToArray(),
            messages, [], true), null);
    }

    public async Task<TicketActionResult> CreateAsync(TicketCreateRequest input,
        AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, "UNAUTHORIZED");
        if (string.IsNullOrWhiteSpace(input.Subject) || input.Subject.Length > 200
            || string.IsNullOrWhiteSpace(input.Message) || input.Message.Length > 65536
            || input.Priority is not (10 or 20 or 30))
            return new(400, "INVALID_TICKET");
        db.Tickets.Add(new HostedTicket {
            OwnerId = principal.Id, Subject = input.Subject, Text = input.Message,
            Recipient = input.Recipient, Priority = input.Priority
        });
        await db.SaveChangesAsync(ct);
        events?.AccountChanged(principal.Username);
        return new(200, null);
    }

    public async Task<TicketActionResult> ReplyAsync(string legacyId, TicketReplyRequest input,
        AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, "UNAUTHORIZED");
        if (!long.TryParse(legacyId, out var id) || id <= 0
            || string.IsNullOrWhiteSpace(input.Message) || input.Message.Length > 65536)
            return new(400, "INVALID_REPLY");
        var ticket = await db.Tickets.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (ticket is null) return new(404, "TICKET_NOT_FOUND");
        if (ticket.OwnerId != principal.Id) return new(403, "ACCESS_DENIED");
        db.TicketReplies.Add(new HostedTicketReply {
            TicketId = id, AuthorId = principal.Id, Text = input.Message
        });
        if (input.Close) ticket.Open = false;
        await db.SaveChangesAsync(ct);
        events?.AccountChanged(principal.Username);
        return new(200, null);
    }

    public async Task<TicketActionResult> SetStatusAsync(string legacyId, TicketStatusChange input,
        AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return new(401, "UNAUTHORIZED");
        if (!long.TryParse(legacyId, out var id) || id <= 0)
            return new(400, "INVALID_TICKET");
        var ticket = await db.Tickets.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (ticket is null) return new(404, "TICKET_NOT_FOUND");
        if (ticket.OwnerId != principal.Id) return new(403, "ACCESS_DENIED");
        ticket.Open = input.Open;
        await db.SaveChangesAsync(ct);
        events?.AccountChanged(principal.Username);
        return new(200, null);
    }
}
