using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Adapters;

namespace HostingPanel.Api.Services;

public sealed record TicketEntry(
    long Id, string LegacyId, string Source, string Subject, string From,
    string FromName, string Message, bool Open, bool Unread, int Priority,
    DateTimeOffset? Timestamp, JsonElement[] Replies);

public sealed record MessageEntry(
    long Id, string Subject, string From, string FromName,
    bool Unread, DateTimeOffset? Timestamp);

public sealed record TicketInbox(
    IReadOnlyList<TicketEntry> Tickets, IReadOnlyList<MessageEntry> Messages,
    IReadOnlyList<string> Warnings, bool Complete);

public sealed record TicketCreateRequest(string Subject, string Message,
    string Recipient = "creator", int Priority = 20);

public sealed record TicketReplyRequest(string Message, string Recipient,
    string Subject, string Source, bool Close = false);

public sealed record TicketStatusChange(string Source, bool Open);

public sealed record TicketActionResult(int Status, string? Error);
