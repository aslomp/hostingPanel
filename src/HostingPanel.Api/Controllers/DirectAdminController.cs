using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Adapters;
using HostingPanel.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

// Explicit modern routes take precedence over the catch-all forwarding endpoint.
// All other documented /api routes retain their upstream HTTP behavior.
[ApiController]
public sealed class DirectAdminController(IHostingBackend backend) : BackendController
{
    [HttpGet("/api/search/users")]
    public async Task<IResult> SearchUsers0()
    {
        var context = HttpContext;
        var catalog = backend.AccountCatalog;
        if (!TryGetBasic(context, out var authentication))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 40
    }

    [HttpGet("/api/users/{username}/config")]
    public Task<IResult> ReadUserConfig3([FromRoute] string username)
    {
        var context = HttpContext;
        var service = backend.LegacyUserDetails;
        return ReadUserDetail(context, username, "config", service);
    }

    [HttpGet("/api/users/{username}/usage")]
    public Task<IResult> ReadUserUsage4([FromRoute] string username)
    {
        var context = HttpContext;
        var service = backend.LegacyUserDetails;
        return ReadUserDetail(context, username, "usage", service);
    }

    [AcceptVerbs("GET", "POST", "PUT", "PATCH", "DELETE", Route = "/api/{**path}")]
    public async Task ForwardDirectAdmin19()
    {
        var context = HttpContext;
        var clients = backend.Clients;
        if (!TryGetBasic(context, out var auth))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
        
                var rawPath = context.Request.Path.Value ?? "";
                if (!rawPath.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
                    || rawPath.Contains("..", StringComparison.Ordinal)
                    || rawPath.Contains('\\') || rawPath.Any(char.IsControl))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
        
                using var upstream = new HttpRequestMessage(
                    new HttpMethod(context.Request.Method), rawPath.TrimStart('/') + context.Request.QueryStrin
    }

    static async Task<IResult> ReadUserDetail(HttpContext context, string username,
        string kind, LegacyUserDetailsService service)
    {
        if (!TryGetBasic(context, out var auth))
            return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
        if (username.Length is < 1 or > 64 || !username.All(c =>
            char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            return Results.Json(new { type = "BAD_REQUEST" }, statusCode: 400);
        var result = await service.GetAsync(username, kind, auth, context.RequestAborted);
        return result.Result is not null
            ? Results.Json(result.Result)
            : Results.Json(new { type = result.Error ?? "UPSTREAM_ERROR" }, statusCode: result.Status);
    }
}
