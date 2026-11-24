using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Adapters;
using HostingPanel.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace HostingPanel.Api.Controllers;

[ApiController]
public sealed class DatabaseController(IHostingBackend backend) : BackendController
{
    [HttpGet("/hosting/databases/accessible")]
    public async Task<IResult> ListAccessibleDatabases(CancellationToken ct)
    {
        if (!TryGetBasic(HttpContext, out var auth))
            return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);

        var inventory = await backend.AccessibleDatabases.ListAsync(auth, ct);
        return inventory.Items is null
            ? Results.Json(new { type = inventory.Error ?? "UPSTREAM_ERROR" }, statusCode: inventory.Status)
            : Results.Json(new {
                items = inventory.Items,
                warnings = inventory.Warnings,
                complete = inventory.Warnings?.Count == 0
            });
    }

    [HttpGet("/hosting/databases")]
    public async Task<IResult> hosting_databases_Get_10()
    {
        var context = HttpContext;
        var service = backend.Databases;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
            var result = await service.ListAsync(auth, context.RequestAborted);
            return result.Databases is null
                ? Results.Json(new { type = result.Error ?? "UPSTREAM_ERROR" }, statusCode: result.Status)
                : Results.Json(new { databases = result.Databases });
    }

    [HttpGet("/hosting/databases/{database}/users")]
    public async Task<IResult> hosting_databases_database_users_Get_11([FromRoute] string database)
    {
        var context = HttpContext;
        var service = backend.Databases;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
            var result = await service.UsersAsync(database, auth, context.RequestAborted);
            return result.Users is null
                ? Results.Json(new { type = result.Error ?? "UPSTREAM_ERROR" }, statusCode: result.Status)
                : Results.Json(new { database, users = result.Users });
    }

    [HttpPost("/hosting/databases")]
    public async Task<IResult> hosting_databases_Post_12([FromBody] DatabaseCreateRequest input)
    {
        var context = HttpContext;
        var service = backend.Databases;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
            var result = await service.CreateAsync(input, auth, context.RequestAborted);
            return result.Error is null
                ? Results.Json(new { created = true })
                : Results.Json(new { type = result.Error }, statusCode: result.Status);
    }

    [HttpDelete("/hosting/databases/{database}")]
    public async Task<IResult> hosting_databases_database_Delete_13([FromRoute] string database, [FromBody] DatabaseDeleteRequest input)
    {
        var context = HttpContext;
        var service = backend.Databases;
        if (!TryGetBasic(context, out var auth))
                return Results.Json(new { type = "UNAUTHORIZED" }, statusCode: 401);
            var result = await service.DeleteAsync(database, input.Confirmation, auth,
                context.RequestAborted);
            return result.Error is null
                ? Results.Json(new { database, deleted = true })
                : Results.Json(new { type = result.Error }, statusCode: result.Status);
    }

    [HttpPost("/api/db-manage/clone-db")]
    [HttpPost("/api/db-manage/clone-dbuser")]
    [HttpPost("/api/db-manage/create-db")]
    [HttpPost("/api/db-manage/create-db-with-user")]
    [HttpPost("/api/db-manage/create-user")]
    [HttpDelete("/api/db-manage/databases/{database}")]
    [HttpPost("/api/db-manage/databases/{database}/check")]
    [HttpGet("/api/db-manage/databases/{database}/export")]
    [HttpGet("/api/db-manage/databases/{database}/export-definition")]
    [HttpPost("/api/db-manage/databases/{database}/fix-definers")]
    [HttpPost("/api/db-manage/databases/{database}/import")]
    [HttpPost("/api/db-manage/databases/{database}/import-definition")]
    [HttpPost("/api/db-manage/databases/{database}/optimize")]
    [HttpPost("/api/db-manage/databases/{database}/repair")]
    [HttpDelete("/api/db-manage/users/{dbuser}")]
    [HttpPost("/api/db-manage/users/{dbuser}/change-hosts")]
    [HttpPost("/api/db-manage/users/{dbuser}/change-password")]
    [HttpPut("/api/db-manage/users/{dbuser}/databases/{database}/change-privs")]
    [HttpGet("/api/db-monitor/processes")]
    [HttpPost("/api/db-monitor/processes/{id}/kill")]
    [HttpGet("/api/db-show/databases")]
    [HttpGet("/api/db-show/databases/{database}")]
    [HttpGet("/api/db-show/databases/{database}/users")]
    [HttpGet("/api/db-show/info")]
    [HttpGet("/api/db-show/users")]
    [HttpGet("/api/db-show/users/{dbuser}")]
    [HttpGet("/api/db-show/users/{dbuser}/databases")]
    public Task ForwardDatabaseAsync() => backend.ForwardAsync(HttpContext);


}
