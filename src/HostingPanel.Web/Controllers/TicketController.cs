using HostingPanel.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

/// <summary>HostingPanel ticket resource. Official Swagger paths stay with the generic proxy.</summary>
[ApiController]
public sealed class TicketController(IHostingBackend backend) : BackendController
{
    [HttpGet("/hosting/tickets/inbox")]
    public async Task<IResult> Inbox(CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth))
            return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
        var (status, inbox, error) = await backend.Tickets.GetInboxAsync(auth, ct);
        return inbox is null ? Results.Json(new { type = error ?? "UPSTREAM_ERROR" }, statusCode: status)
            : Results.Json(inbox);
    }

    [HttpGet("/hosting/tickets/{id:long}")]
    public async Task<IResult> Details(long id, [FromQuery] string source, CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth))
            return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
        if (id < 0 || source is not ("received" or "request"))
            return Results.Json(new { type = "INVALID_TICKET" }, statusCode: 400);
        var (status, inbox, error) = await backend.Tickets.GetInboxAsync(auth, ct);
        if (inbox is null)
            return Results.Json(new { type = error ?? "UPSTREAM_ERROR" }, statusCode: status);
        var ticket = inbox.Tickets.FirstOrDefault(t => t.Id == id && t.Source == source);
        return ticket is not null ? Results.Json(ticket)
            : Results.Json(new { type = inbox.Complete ? "NOT_FOUND" : "INCOMPLETE_INBOX" },
                statusCode: inbox.Complete ? 404 : 503);
    }

    [HttpPost("/hosting/tickets")]
    public async Task<IResult> Create([FromBody] TicketCreateRequest request, CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth))
            return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
        var result = await backend.Tickets.CreateAsync(request, auth, ct);
        return Respond(result);
    }

    [HttpPost("/hosting/tickets/{legacyId}/replies")]
    public async Task<IResult> Reply(string legacyId, [FromBody] TicketReplyRequest request, CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth))
            return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
        return Respond(await backend.Tickets.ReplyAsync(legacyId, request, auth, ct));
    }

    [HttpPut("/hosting/tickets/{legacyId}/status")]
    public async Task<IResult> SetStatus(string legacyId, [FromBody] TicketStatusChange request, CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth))
            return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
        return Respond(await backend.Tickets.SetStatusAsync(legacyId, request, auth, ct));
    }

    private static IResult Respond(TicketActionResult outcome) =>
        outcome.Error is null
            ? Results.Json(new { success = true })
            : Results.Json(new { type = outcome.Error }, statusCode: outcome.Status);
}
