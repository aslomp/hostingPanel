using System.Net;
using System.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("directadmin", client =>
{
    var configured = builder.Configuration["DirectAdmin:BaseUrl"]
        ?? throw new InvalidOperationException("DirectAdmin:BaseUrl ontbreekt.");
    var uri = new Uri(configured, UriKind.Absolute);
    if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)
        || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        throw new InvalidOperationException("DirectAdmin:BaseUrl moet een HTTPS-origin zonder credentials of pad zijn.");

    client.BaseAddress = uri;
    client.Timeout = TimeSpan.FromMinutes(3);
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    UseCookies = false,
    AutomaticDecompression = DecompressionMethods.None
});

var app = builder.Build();
app.UseHttpsRedirection();
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// Auth, authorization, and DirectAdmin API semantics are enforced by the fixed HTTPS upstream.
// No shared administrator credentials exist in this application.
app.Run(async context =>
{
    var path = context.Request.Path.Value ?? "";
    if (!(path.StartsWith("/CMD_API_", StringComparison.Ordinal)
          || path.StartsWith("/api/", StringComparison.Ordinal)
          || path.StartsWith("/CMD_", StringComparison.Ordinal)
          || path.Equals("/static/swagger.json", StringComparison.Ordinal)))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    if (!AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization.ToString(), out var auth)
        || !auth.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase)
        || string.IsNullOrWhiteSpace(auth.Parameter))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"DirectAdmin API\"";
        return;
    }

    var client = context.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient("directadmin");
    // A relative URI is resolved against the configured upstream origin; request headers
    // cannot override the target server. Query and form bodies are passed through verbatim.
    using var upstreamRequest = new HttpRequestMessage(new HttpMethod(context.Request.Method),
        path.TrimStart('/') + context.Request.QueryString);
    upstreamRequest.Headers.Authorization = auth;

    if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
    {
        upstreamRequest.Content = new StreamContent(context.Request.Body);
        if (!string.IsNullOrEmpty(context.Request.ContentType))
            upstreamRequest.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
    }

    foreach (var (name, values) in context.Request.Headers)
    {
        if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Host", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            continue;
        if (!upstreamRequest.Headers.TryAddWithoutValidation(name, values.ToArray()))
            upstreamRequest.Content?.Headers.TryAddWithoutValidation(name, values.ToArray());
    }

    try
    {
        using var response = await client.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead,
            context.RequestAborted);
        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var (name, values) in response.Headers)
        {
            if (!name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                && !name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
                context.Response.Headers[name] = values.ToArray();
        }
        foreach (var (name, values) in response.Content.Headers)
        {
            if (!name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                context.Response.Headers[name] = values.ToArray();
        }
        context.Response.Headers.Remove("transfer-encoding");
        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
    catch (HttpRequestException)
    {
        if (!context.Response.HasStarted)
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
    }
    catch (TaskCanceledException) when (!context.RequestAborted.IsCancellationRequested)
    {
        if (!context.Response.HasStarted)
            context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
    }
});
app.Run();
