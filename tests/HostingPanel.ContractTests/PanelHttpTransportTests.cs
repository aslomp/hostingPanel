using System.Net;
using System.Text;
using HostingPanel.Web.Realtime;
using HostingPanel.Web.Services;

// Fake same-host requests: no live accounts, domains or Kubernetes resources.
public static class PanelHttpTransportTests
{
    public static void Run() => VerifyAsync().GetAwaiter().GetResult();

    private static async Task VerifyAsync()
    {
        using var session = new PanelSession(new TicketChannelRegistry());
        var handler = new CaptureHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5040/") };
        var clientFactory = new FakeFactory(http);
        var transport = new PanelHttpTransport(clientFactory, session);
        var settings = new SettingsOperations(transport);
        var compatibility = new CompatibilityApiClient(transport, session);

        await AssertThrowsAsync<InvalidOperationException>(() => settings.GetBackendSettingsAsync());
        if (handler.Count != 0) throw new Exception("Disconnected session caused HTTP I/O");

        session.SetCredentials("alice", "secret");
        var result = await settings.SaveBackendSettingsAsync("""{"example":true}""");
        if (result.Status != 200 || result.Body != "ok" ||
            handler.Path != "/hosting/backend-settings" || handler.Method != HttpMethod.Put ||
            handler.Content != """{"example":true}""" ||
            handler.ContentType != "application/json" ||
            handler.Authorization != Basic("alice", "secret"))
            throw new Exception("Settings operation lost its route, body or caller credentials");

        result = await settings.GetNetworkInventoryAsync();
        if (result.Status != 200 || handler.Path != "/hosting/network/inventory" ||
            handler.Method != HttpMethod.Get || handler.Content is not null)
            throw new Exception("Network inventory request changed");

        result = await settings.GetProfileNameserversAsync();
        if (result.Status != 200 || handler.Path != "/hosting/profile/nameservers" ||
            handler.Method != HttpMethod.Get || handler.Content is not null)
            throw new Exception("Profile nameserver read changed");

        result = await settings.SaveProfileNameserversAsync("""{"ns1":"ns1.example.com"}""");
        if (result.Status != 200 || handler.Path != "/hosting/profile/nameservers" ||
            handler.Method != HttpMethod.Put || handler.ContentType != "application/json" ||
            handler.Content != """{"ns1":"ns1.example.com"}""" ||
            handler.Authorization != Basic("alice", "secret"))
            throw new Exception("Profile nameserver write changed");

        result = await compatibility.GetRawAsync("api/info");
        if (result.Status != 200 || handler.Path != "/api/info" ||
            handler.Method != HttpMethod.Get || handler.Authorization != Basic("alice", "secret"))
            throw new Exception("Compatibility request changed transport semantics");

        var previousCount = handler.Count;
        await AssertThrowsAsync<ArgumentException>(() => compatibility.GetRawAsync("/hosting/backend-settings"));
        if (handler.Count != previousCount)
            throw new Exception("Compatibility client accepted a non-compatibility route");

        session.Disconnect();
        await AssertThrowsAsync<InvalidOperationException>(() => compatibility.GetRawAsync("api/info"));
        if (handler.Count != previousCount)
            throw new Exception("Disconnected session caused a compatibility request");
    }

    private static string Basic(string username, string password) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password));

    private static async Task AssertThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try { await action(); }
        catch (TException) { return; }
        throw new Exception($"Expected {typeof(TException).Name}");
    }

    private sealed class FakeFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            name == "hosting-api" ? client : throw new Exception("Unexpected HTTP client");
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public int Count { get; private set; }
        public string? Path { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? Content { get; private set; }
        public string? ContentType { get; private set; }
        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Count++;
            Path = request.RequestUri?.AbsolutePath;
            Method = request.Method;
            Content = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            Authorization = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") };
        }
    }
}
