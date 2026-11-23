using System.Net;
using System.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient("directadmin", client =>
{
    var baseUrl = builder.Configuration["DirectAdmin:BaseUrl"]
        ?? throw new InvalidOperationException("DirectAdmin:BaseUrl is required.");
    var uri = new Uri(baseUrl, UriKind.Absolute);
    if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo))
        throw new InvalidOperationException("DirectAdmin:BaseUrl must be an HTTPS URL without credentials.");
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

// Compatibility boundary: no decoding, remapping, or normalization of DirectAdmin responses.
// The upstream DirectAdmin server remains the authentication and authorization authority.
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

    // Never expose the upstream using an anonymous server-side master credential.
    // Clients supply their own DirectAdmin user credentials or restricted login key.
    if (!AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization.ToString(), out var auth)
        || !auth.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase)
        || string.IsNullOrWhiteSpace(auth.Parameter))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Basic realm=\"HostingPanel DirectAdmin API\"";
        return;
    }

    var client = context.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient("directadmin");
    // Absolute Request.Path cannot override BaseAddress: use a relative request URI.
    var relative = path.TrimStart('/') + context.Request.QueryString;
    using var upstreamRequest = new HttpRequestMessage(new HttpMethod(context.Request.Method), relative);
    upstreamRequest.Headers.Authorization = auth;
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
        {
            // Content headers are copied after the content object is created.
        }
    }

    if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
    {
        upstreamRequest.Content = new StreamContent(context.Request.Body);
        if (!string.IsNullOrEmpty(context.Request.ContentType))
            upstreamRequest.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
    }

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
});
app.Run();
