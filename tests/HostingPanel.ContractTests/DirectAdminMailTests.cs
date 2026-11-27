using System.Net;
using System.Net.Http.Headers;
using HostingPanel.Api;
using HostingPanel.Api.Services;

internal static class DirectAdminMailTests
{
    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("DirectAdmin mailbox contract failed.");
    }

    private static string Inventory(string name, string quota) =>
        Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString("quota=" + quota + "&usage_bytes=12345");

    private static async Task RunAsync()
    {
        var parsed = DirectAdminMailProvider.ParseInventory(Inventory("info", "10"), "example.test").Single();
        Require(parsed.Name == "info" && parsed.Address == "info@example.test" && parsed.QuotaBytes == 10485760 && parsed.UsedBytes == 12345);

        var auth = new AuthenticationHeaderValue("Basic", "dGVzdDp0ZXN0");
        var handler = new Fake();
        var provider = new DirectAdminMailProvider(new Factory(handler));
        handler.Responses.Enqueue((HttpStatusCode.OK, ""));
        handler.Responses.Enqueue((HttpStatusCode.OK, "error=0"));
        handler.Responses.Enqueue((HttpStatusCode.OK, Inventory("info", "10")));
        var created = await provider.CreateAsync("example.test", new MailCreateRequest("info", "fixture-password", 10485760), auth, default);
        Require(created.Status == 201 && created.Error is null);
        Require(handler.Requests.Count == 3 && handler.Requests[1].Method == HttpMethod.Post);
        Require(handler.Requests[1].Body.Contains("quota=10") && handler.Requests.All(x => x.Auth == auth.ToString()));

        handler.Responses.Enqueue((HttpStatusCode.OK, Inventory("info", "10")));
        handler.Responses.Enqueue((HttpStatusCode.OK, "error=1&text=denied"));
        var rejected = await provider.DeleteAsync("example.test", "info", auth, default);
        Require(rejected.Error is not null && handler.Requests.Count == 5);

        handler.Responses.Enqueue((HttpStatusCode.OK, Inventory("info", "10")));
        handler.Responses.Enqueue((HttpStatusCode.OK, "error=0"));
        handler.Responses.Enqueue((HttpStatusCode.OK, Inventory("info", "10")));
        var unchanged = await provider.DeleteAsync("example.test", "info", auth, default);
        Require(unchanged.Status == 409 && unchanged.Error == "CHANGE_NOT_OBSERVED");

        handler.Responses.Enqueue((HttpStatusCode.Forbidden, ""));
        var denied = await provider.ListAsync("example.test", auth, default);
        Require(denied.Status == 403 && denied.Accounts is null);
    }

    private sealed class Factory(Fake handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://example.test/") };
    }

    private sealed class Fake : HttpMessageHandler
    {
        public Queue<(HttpStatusCode Status, string Body)> Responses { get; } = new();
        public List<(HttpMethod Method, string Body, string? Auth)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct), request.Headers.Authorization?.ToString()));
            var response = Responses.Dequeue();
            return new HttpResponseMessage(response.Status) { Content = new StringContent(response.Body) };
        }
    }
}
