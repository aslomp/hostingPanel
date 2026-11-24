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

/// <summary>
/// Resource-owned ticket and messaging operations. No local role hierarchy or
/// permission decisions: all requests use the caller's effective DirectAdmin identity.
/// </summary>
public sealed class TicketService(IHttpClientFactory clients) : ITickets
{
    public async Task<(int Status, TicketInbox? Inbox, string? Error)> GetInboxAsync(
        AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var tickets = new List<TicketEntry>();
        var messages = new List<MessageEntry>();
        var warnings = new List<string>();
        var success = 0;
        var firstError = 502;

        foreach (var (source, endpoint) in new[] {
            ("received", "api/tickets?limit=500"),
            ("request", "api/ticket-requests?limit=500"),
            ("message", "api/messages?limit=500")
        })
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
                req.Headers.Authorization = auth;
                using var response = await clients.CreateClient("legacy-directadmin")
                    .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!response.IsSuccessStatusCode)
                {
                    if (success == 0) firstError = (int)response.StatusCode;
                    warnings.Add(source + ":HTTP_" + (int)response.StatusCode);
                    continue;
                }
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    throw new JsonException("Invalid ticket list response.");
                if (source == "message")
                    messages.AddRange(ParseMessages(doc.RootElement));
                else
                    tickets.AddRange(ParseTickets(doc.RootElement, source));
                success++;
            }
            catch (HttpRequestException)
            { warnings.Add(source + ":UPSTREAM_UNAVAILABLE"); }
            catch (JsonException)
            { warnings.Add(source + ":INVALID_RESPONSE"); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            { warnings.Add(source + ":UPSTREAM_TIMEOUT"); }
        }

        if (success == 0)
            return (firstError, null, string.Join(",", warnings));
        return (200, new TicketInbox(
            tickets.OrderByDescending(t => t.Timestamp).ThenByDescending(t => t.Id).ToArray(),
            messages.OrderByDescending(m => m.Timestamp).ThenByDescending(m => m.Id).ToArray(),
            warnings, warnings.Count == 0), null);
    }

    public static IReadOnlyList<TicketEntry> ParseTickets(JsonElement root, string source)
    {
        if (root.ValueKind != JsonValueKind.Array) throw new JsonException("Expected tickets array.");
        var results = new List<TicketEntry>();
        foreach (var t in root.EnumerateArray())
        {
            if (!t.TryGetProperty("id", out var id) || !id.TryGetInt64(out var number))
                throw new JsonException("Ticket has no numeric id.");
            var replies = t.TryGetProperty("replies", out var r) && r.ValueKind == JsonValueKind.Array
                ? r.EnumerateArray().Select(x => x.Clone()).ToArray() : [];
            results.Add(new TicketEntry(number, String(t, "legacyID") ?? number.ToString("D9"),
                source, String(t, "subject") ?? "", String(t, "from") ?? "",
                String(t, "fromName") ?? "", String(t, "message") ?? "",
                Bool(t, "open"), Bool(t, "unread"), Int(t, "priority"), Date(t, "timestamp"), replies));
        }
        return results;
    }

    public static IReadOnlyList<MessageEntry> ParseMessages(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) throw new JsonException("Expected messages array.");
        var results = new List<MessageEntry>();
        foreach (var m in root.EnumerateArray())
        {
            if (!m.TryGetProperty("id", out var id) || !id.TryGetInt64(out var number))
                throw new JsonException("Message has no numeric id.");
            results.Add(new MessageEntry(number, String(m, "subject") ?? "",
                String(m, "from") ?? "", String(m, "fromName") ?? "",
                Bool(m, "unread"), Date(m, "timestamp")));
        }
        return results;
    }

    private static string? String(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool Bool(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
    private static int Int(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
    private static DateTimeOffset? Date(JsonElement e, string key) =>
        DateTimeOffset.TryParse(String(e, key), out var dt) ? dt : null;

    private static bool ValidRecipient(string? user) =>
        !string.IsNullOrWhiteSpace(user) && user.Length <= 64 &&
        (user == "creator" || user.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'));
    private static bool ValidMessage(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 65_536;
    private static bool ValidSubject(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 200;
    private static bool ValidPriority(int p) => p is 10 or 20 or 30;
    private static bool ValidLegacyId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 16 && value.All(char.IsAsciiDigit);

    public Task<TicketActionResult> CreateAsync(
        TicketCreateRequest input, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        if (!ValidSubject(input.Subject) || !ValidMessage(input.Message) ||
            !ValidRecipient(input.Recipient) || !ValidPriority(input.Priority))
            return Task.FromResult(new TicketActionResult(400, "INVALID_TICKET"));
        return PostAsync("CMD_API_TICKET_CREATE", new Dictionary<string, string> {
            ["action"] = "create", ["type"] = "request", ["user"] = input.Recipient,
            ["priority"] = input.Priority.ToString(), ["subject"] = input.Subject,
            ["message"] = input.Message
        }, auth, ct);
    }

    public Task<TicketActionResult> ReplyAsync(string legacyId, TicketReplyRequest input,
        AuthenticationHeaderValue auth, CancellationToken ct)
    {
        if (!ValidLegacyId(legacyId) || !ValidMessage(input.Message) ||
            !ValidRecipient(input.Recipient) || !ValidSubject(input.Subject) ||
            input.Source is not ("received" or "request"))
            return Task.FromResult(new TicketActionResult(400, "INVALID_REPLY"));
        var fields = new Dictionary<string, string> {
            ["action"] = "reply", ["type"] = "reply", ["number"] = legacyId,
            ["user"] = input.Recipient, ["subject"] = input.Subject,
            ["priority"] = "20", ["message"] = input.Message
        };
        if (input.Source == "received") fields["status"] = input.Close ? "closed" : "open";
        return PostAsync(input.Source == "received" ? "CMD_API_TICKET_MANAGE" : "CMD_API_TICKET",
            fields, auth, ct);
    }

    public Task<TicketActionResult> SetStatusAsync(string legacyId, TicketStatusChange change,
        AuthenticationHeaderValue auth, CancellationToken ct)
    {
        if (!ValidLegacyId(legacyId) || change.Source != "received")
            return Task.FromResult(new TicketActionResult(400, "INVALID_STATUS_CHANGE"));
        return PostAsync("CMD_API_TICKET_MANAGE", new Dictionary<string, string> {
            ["action"] = "status", ["number"] = legacyId,
            ["status"] = change.Open ? "open" : "closed"
        }, auth, ct);
    }

    private async Task<TicketActionResult> PostAsync(string endpoint,
        Dictionary<string, string> form, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = auth;
            request.Content = new FormUrlEncodedContent(form);
            using var response = await clients.CreateClient("legacy-directadmin")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is 301 or 302 or 303)
                return new(202, "BACKEND_ACCEPTED_UNVERIFIED");
            if (!response.IsSuccessStatusCode)
                return new((int)response.StatusCode, "UPSTREAM_REJECTED");
            var body = await response.Content.ReadAsStringAsync(ct);
            LegacyDomainsAdapter.AssertMutationSucceeded(body);
            return new(200, null);
        }
        catch (LegacyApiException) { return new(422, "BACKEND_REJECTED"); }
        catch (FormatException) { return new(502, "INVALID_BACKEND_CONFIRMATION"); }
        catch (HttpRequestException) { return new(502, "UPSTREAM_UNAVAILABLE"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        { return new(504, "UPSTREAM_TIMEOUT"); }
    }
}
