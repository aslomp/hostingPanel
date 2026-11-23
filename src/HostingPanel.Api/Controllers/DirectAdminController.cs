using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Adapters;
using HostingPanel.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

[ApiController]
public sealed class DirectAdminController(IHostingBackend backend) : BackendController
{
    [HttpGet("/api/search/users")]
    public async Task<IResult> api_search_users_Get_3()
    {
        var context = HttpContext;
        var catalog = backend.AccountCatalog;
        if (!TryGetBasic(context, out var authentication))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
            var query = context.Request.Query["q"].ToString();
            var rawLimit = context.Request.Query["limit"].ToString();
            var limit = 50;
            if (rawLimit.Length > 0 && (!int.TryParse(rawLimit, out limit) || limit is < 1 or > 1000))
                return Results.Json(new { type = "BAD_REQUEST" }, statusCode: 400);
            if (query.Length > 256)
                return Results.Json(new { type = "BAD_REQUEST" }, statusCode: 400);
        
            var listing = await catalog.ListAsync(authentication, context.RequestAborted);
            return listing.Accounts is null
                ? Results.Json(new { type = listing.Error ?? "UPSTREAM_ERROR" }, statusCode: listing.Status)
                : Results.Json(listing.Accounts.Select(x => x.Username)
                    .Where(x => string.IsNullOrEmpty(query) ||
                        x.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .Take(limit).ToArray());
    }

    [HttpGet("/api/users/{username}/config")]
    public Task<IResult> api_users_username_config_Get_6([FromRoute] string username)
    {
        var context = HttpContext;
        var service = backend.LegacyUserDetails;
        return ReadUserDetail(context, username, "config", service);
    }

    [HttpGet("/api/users/{username}/usage")]
    public Task<IResult> api_users_username_usage_Get_7([FromRoute] string username)
    {
        var context = HttpContext;
        var service = backend.LegacyUserDetails;
        return ReadUserDetail(context, username, "usage", service);
    }

    [AcceptVerbs("GET", "POST", "PUT", "PATCH", "DELETE", Route = "/api/{**path}")]
    public async Task Forward_22()
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
                    new HttpMethod(context.Request.Method), rawPath.TrimStart('/') + context.Request.QueryString);
                upstream.Headers.Authorization = auth;
                if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
                {
                    upstream.Content = new StreamContent(context.Request.Body);
                    if (!string.IsNullOrWhiteSpace(context.Request.ContentType))
                        upstream.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
                }
        
                try
                {
                    using var response = await clients.CreateClient("legacy-directadmin")
                        .SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                    context.Response.StatusCode = (int)response.StatusCode;
                    if (response.Content.Headers.ContentType is { } contentType)
                        context.Response.ContentType = contentType.ToString();
                    await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
                }
                catch (TaskCanceledException) when (!context.RequestAborted.IsCancellationRequested)
                {
                    context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
                }
                catch (HttpRequestException)
                {
                    context.Response.StatusCode = StatusCodes.Status502BadGateway;
                }
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
