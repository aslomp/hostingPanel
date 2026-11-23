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
    [AcceptVerbs("GET", "POST", "PUT", "PATCH", "DELETE", Route = "/api/{**path}")]
    public async Task api_path_Methods_22()
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


}
