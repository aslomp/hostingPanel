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


}
