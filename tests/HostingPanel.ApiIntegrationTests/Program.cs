using System.Net;
using System.Text;
using HostingPanel.Api;
using Microsoft.AspNetCore.Http;

await Check(HttpMethod.Post, "/api/server-settings/directadmin-conf/local",
    "?skip-unknown=true", """{"ssl_redirect":1}""", "Bearer test-token",
    HttpStatusCode.OK, "application/json", """{"ok":true}""", "application/json");
await Check(HttpMethod.Post, "/api/login", "", """{"username":"demo"}""",
    null, HttpStatusCode.Unauthorized, "application/json",
    """{"type":"UNAUTHORIZED"}""", "application/json");
await Check(HttpMethod.Get, "/api/system-packages/history/1/sse", "", null,
    "Basic ZGVtbzprZXk=", HttpStatusCode.OK, "text/event-stream",
    "event: progress\ndata: 42\n\n", null);
await Check(HttpMethod.Get, "/api/filemanager/download", "?path=%2Fhome%2Fdemo", null,
    "Basic ZGVtbzprZXk=", HttpStatusCode.OK, "application/octet-stream",
    "binary-data", null);
await Check(HttpMethod.Get, "/api/clamav", "", null,
    "Basic ZGVtbzprZXk=", HttpStatusCode.NotImplemented, "application/json",
    """{"type":"NOT_IMPLEMENTED"}""", null);
Console.WriteLine("PASS: 5 fake backend HTTP forwarding scenarios (auth, body, query, SSE, binary, 501).");

static async Task Check(HttpMethod method, string path, string query,
    string? requestBody, string? authorization, HttpStatusCode expectedStatus,
    string responseContentType, string responseBody, string? requestContentType)
{
    var backend = new CaptureHandler(expectedStatus, responseContentType, responseBody);
    using var client = new HttpClient(backend) {
        BaseAddress = new Uri("https://directadmin.invalid:2222/") };
    var factory = new ClientFactory(client);
    var context = new DefaultHttpContext();
    context.Request.Method = method.Method;
    context.Request.Path = path;
    context.Request.QueryString = new QueryString(query);
    context.Request.Headers.Cookie = "test-cookie=value";
    context.Request.Headers.Accept = "application/json";
    if (authorization is not null) context.Request.Headers.Authorization = authorization;
    if (requestBody is not null)
    {
        var bytes = Encoding.UTF8.GetBytes(requestBody);
        context.Request.Body = new MemoryStream(bytes);
        context.Request.ContentLength = bytes.Length;
        context.Request.ContentType = requestContentType;
    }
    context.Response.Body = new MemoryStream();
    await ForwardProbe.Invoke(context, factory);
    var actual = Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());
    Ensure(context.Response.StatusCode == (int)expectedStatus, "HTTP response status");
    Ensure(actual == responseBody, "Raw response bytes/body");
    Ensure(context.Response.ContentType?.StartsWith(responseContentType, StringComparison.OrdinalIgnoreCase) == true,
        "Response Content-Type");
    Ensure(backend.Path == path + query, "Exact forwarded path+query");
    Ensure(backend.Authorization == authorization, "Authorization forwarded unchanged");
    Ensure(backend.Cookie == "test-cookie=value", "Cookie forwarded");
    Ensure(backend.RequestBody == requestBody, "Request body forwarded unchanged");
    if (requestBody is not null)
        Ensure(backend.RequestContentType?.StartsWith(requestContentType!, StringComparison.OrdinalIgnoreCase) == true,
            "Request Content-Type");
}

static void Ensure(bool value, string description)
{ if (!value) throw new InvalidOperationException(description); }

public sealed class ForwardProbe : BackendController
{
    public static Task Invoke(HttpContext context, IHttpClientFactory factory) =>
        ForwardToBackendAsync(context, factory);
}
public sealed class ClientFactory(HttpClient client) : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        if (name != "legacy-directadmin") throw new InvalidOperationException("Wrong HTTP client");
        return client;
    }
}
public sealed class CaptureHandler(
    HttpStatusCode status, string contentType, string body) : HttpMessageHandler
{
    public string? Path, Authorization, Cookie, RequestBody, RequestContentType;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken ct)
    {
        Path = request.RequestUri!.PathAndQuery;
        Authorization = request.Headers.Authorization?.ToString();
        Cookie = request.Headers.TryGetValues("Cookie", out var cookie) ? string.Join("; ", cookie) : null;
        RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        RequestContentType = request.Content?.Headers.ContentType?.MediaType;
        var response = new HttpResponseMessage(status) {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body))
        };
        response.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return response;
    }
}
