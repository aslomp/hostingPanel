using HostingPanel.Api;
using HostingPanel.Api.Services;

namespace HostingPanel.Web.Services;

public sealed class TicketOperations(BackendOperationScope scope)
{
    public Task<(int Status, TicketInbox? Inbox, string? Error)> InboxAsync(CancellationToken ct = default) =>
        scope.RunAsync<ITickets, (int Status, TicketInbox? Inbox, string? Error)>((p, auth, token) => p.GetInboxAsync(auth, token), ct);

    public async Task<(int Status, TicketEntry? Ticket)> GetAsync(long id, string source, CancellationToken ct = default)
    {
        if (id < 0 || source is not ("received" or "request")) return (400, null);
        var result = await InboxAsync(ct);
        if (result.Inbox is null) return (result.Status, null);
        var ticket = result.Inbox.Tickets.FirstOrDefault(t => t.Id == id && t.Source == source);
        return ticket is not null ? (200, ticket) : (result.Inbox.Complete ? 404 : 503, null);
    }

    public Task<TicketActionResult> CreateAsync(TicketCreateRequest input, CancellationToken ct = default) =>
        scope.RunAsync<ITickets, TicketActionResult>((p, auth, token) => p.CreateAsync(input, auth, token), ct);

    public Task<TicketActionResult> ReplyAsync(string id, TicketReplyRequest input, CancellationToken ct = default) =>
        scope.RunAsync<ITickets, TicketActionResult>((p, auth, token) => p.ReplyAsync(id, input, auth, token), ct);

    public Task<TicketActionResult> SetStatusAsync(string id, TicketStatusChange input, CancellationToken ct = default) =>
        scope.RunAsync<ITickets, TicketActionResult>((p, auth, token) => p.SetStatusAsync(id, input, auth, token), ct);
}
