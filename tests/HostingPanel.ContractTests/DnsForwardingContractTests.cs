using System.Net;
using System.Net.Http.Headers;
using System.Text;
using HostingPanel.Api.Adapters;
using HostingPanel.Api.Services;

public static class DnsForwardingContractTests
{
    public static void Run()
    {
        var backend = new Backend();
        var factory = new Factory(backend);
        var service = new DnsZoneService(factory);
        var auth = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("sample:token")));
        var zone = service.ReadAsync("example.com", auth, CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert(zone.Status == 200 && zone.Records?.Count == 1,
            "A failed admin read must not prevent a successful DNS control read");
        Assert(backend.Paths.SequenceEqual(["GET CMD_DNS_ADMIN", "GET CMD_DNS_CONTROL"]),
            "DNS reads should fall back after a definitive backend denial");

        backend.Paths.Clear();
        backend.AdminReadStatus = HttpStatusCode.InternalServerError;
        zone = service.ReadAsync("example.com", auth, CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert(zone.Status == 200 && zone.Records?.Count == 1,
            "An admin HTTP 500 must permit a read-only fallback");

        backend.AdminReadStatus = HttpStatusCode.OK;
        backend.AdminReadBody = """{"records":[{"type":"A","value":"192.0.2.8"}]}""";
        zone = service.ReadAsync("example.com", auth, CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert(zone.Status == 200 && zone.Records?.Count == 1,
            "Missing admin DNS fields must permit a read-only fallback");

        backend.ControlReadBody = """{"records":[{"type":"A","value":"192.0.2.8"}]}""";
        zone = service.ReadAsync("example.com", auth, CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert(zone.Status == 502 && zone.Error == "INVALID_LEGACY_RESPONSE",
            "Malformed DNS records must not escape as HTTP 500");

        var extraTypes = LegacyDnsAdapter.ParseRecords(
            """{"records":[{"type":"HTTPS","index":"example.com.","value":"1 ."}]}""");
        Assert(extraTypes.Count == 1 && extraTypes[0].Type == "HTTPS",
            "Read-only DNS inventory must preserve additional record types");

        backend.Paths.Clear();
        var change = new DnsChange("add", new DnsRecord("A", "www", "192.0.2.8"));
        var written = service.ChangeAsync("example.com", change, auth, CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert(written.Status == 200, "DirectAdmin must decide whether the write is allowed");
        Assert(backend.Paths.SequenceEqual(["POST CMD_API_DNS_ADMIN", "POST CMD_API_DNS_CONTROL"]),
            "Writes must not perform a preliminary read or require a selected role");

        backend.Paths.Clear();
        backend.AmbiguousWriteFailure = true;
        written = service.ChangeAsync("example.com", change, auth, CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert(written.Status == 422, "A legacy error must remain an upstream denial");
        Assert(backend.Paths.SequenceEqual(["POST CMD_API_DNS_ADMIN"]),
            "An ambiguous or application-level write failure must never be retried");
    }

    private static void Assert(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        private readonly HttpClient client = new(handler)
        {
            BaseAddress = new Uri("https://directadmin.invalid:2222/")
        };
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Backend : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public bool AmbiguousWriteFailure { get; set; }
        public HttpStatusCode AdminReadStatus { get; set; } = HttpStatusCode.Forbidden;
        public string AdminReadBody { get; set; } = "";
        public string ControlReadBody { get; set; } =
            """{"records":[{"type":"A","index":"www","value":"192.0.2.8","ttl":300}]}""";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            Paths.Add(request.Method.Method + " " + path);
            HttpResponseMessage response;
            if (path == "CMD_DNS_ADMIN")
                response = Reply(AdminReadStatus, AdminReadBody);
            else if (path == "CMD_API_DNS_ADMIN")
                response = AmbiguousWriteFailure && request.Method == HttpMethod.Post
                    ? Reply(HttpStatusCode.OK, "error=1&text=Denied")
                    : Reply(HttpStatusCode.Forbidden);
            else if (path == "CMD_DNS_CONTROL")
                response = Reply(HttpStatusCode.OK, ControlReadBody);
            else if (path == "CMD_API_DNS_CONTROL")
                response = Reply(HttpStatusCode.OK, "error=0&text=Success");
            else
                response = Reply(HttpStatusCode.NotFound);
            return Task.FromResult(response);
        }

        private static HttpResponseMessage Reply(HttpStatusCode status, string body = "") =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
    }
}
