using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HostingPanel.Api.Services;
using HostingPanel.Web.Realtime;

public static class TicketIntegrationTests
{
    public static void Run()
    {
        var handler = new TicketsBackend();
        var service = new TicketService(new Factory(handler));
        var auth = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:valid")));
        var (status, inbox, error) = service.GetInboxAsync(auth, CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert(status == 200 && error is null && inbox is not null, "Ticket inbox must load");
        Assert(inbox.Tickets.Count == 2 && inbox.Messages.Count == 1 && inbox.Complete, "Ticket resources and message count");
        Assert(inbox.Tickets.Any(t => t.Source == "received" && t.LegacyId == "000001234" &&
            t.Replies.Length == 1 && t.Unread), "Ticket replies and unread state");

        var created = service.CreateAsync(new("Subject", "Message body"), auth, CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert(created.Status == 200 && created.Error is null, "Ticket creation accepted");
        Assert(handler.LastPath == "CMD_API_TICKET_CREATE" && handler.LastForm["type"] == "request" &&
            handler.LastForm["subject"] == "Subject" && handler.LastForm["user"] == "creator",
            "Correct DirectAdmin ticket creation form");

        var replied = service.ReplyAsync("000001234", new(
            "Reply", "alice", "Subject", "received"), auth, CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert(replied.Status == 200 && handler.LastPath == "CMD_API_TICKET_MANAGE" &&
            handler.LastForm["number"] == "000001234", "Correct received ticket reply form");

        replied = service.ReplyAsync("000001234", new(
            "Reply", "creator", "Subject", "request"), auth, CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert(replied.Status == 200 && handler.LastPath == "CMD_API_TICKET",
            "Request ticket reply uses user command");

        var changed = service.SetStatusAsync("000001234", new("received", false), auth, CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert(changed.Status == 200 && handler.LastForm["status"] == "closed",
            "Ticket close status forwarded");

        var count = handler.Writes;
        created = service.CreateAsync(new("", ""), auth, CancellationToken.None).GetAwaiter().GetResult();
        Assert(created.Status == 400 && handler.Writes == count,
            "Invalid ticket must not reach upstream");

        handler.Result = "error=1&text=Denied";
        changed = service.SetStatusAsync("000001234", new("received", true), auth, CancellationToken.None)
            .GetAwaiter().GetResult();
        Assert(changed.Status == 422 && handler.Writes == count + 1,
            "Legacy 200 error must not be interpreted as success");

        var registry = new TicketChannelRegistry();
        var first = registry.Register();
        var second = registry.Register();
        Assert(first.Length == 64 && first != second && registry.IsActive(first),
            "SignalR channel tokens must be unguessable and unique");
        registry.Revoke(first);
        Assert(!registry.IsActive(first) && registry.IsActive(second),
            "Revoked channel must not allow new joins");
    }

    private static void Assert(bool good, string message)
    { if (!good) throw new InvalidOperationException(message); }

    private sealed class Factory(TicketsBackend handler) : IHttpClientFactory
    {
        private readonly HttpClient client = new(handler) {
            BaseAddress = new Uri("https://directadmin.invalid:2222/")
        };
        public HttpClient CreateClient(string _) => client;
    }

    private sealed class TicketsBackend : HttpMessageHandler
    {
        public int Writes;
        public string LastPath = "";
        public Dictionary<string, string> LastForm = [];
        public string Result = "error=0&text=OK";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            if (request.Method == HttpMethod.Get)
            {
                var body = path switch {
                    "api/tickets" => """[{"id":1234,"legacyID":"000001234","subject":"Help","from":"alice","fromName":"Alice","message":"Hello","open":true,"priority":20,"timestamp":"2026-11-23T12:00:00Z","unread":true,"replies":[{"from":"admin","fromName":"Admin","timestamp":"2026-11-23T12:01:00Z","message":"Response","subject":"Help","to":"alice"}]}]""",
                    "api/ticket-requests" => """[{"id":789,"legacyID":"000000789","subject":"Request","from":"alice","fromName":"Alice","message":"Request","open":true,"priority":20,"timestamp":"2026-11-23T12:02:00Z","replies":[]}]""",
                    "api/messages" => """[{"id":4,"legacyID":"000000004","subject":"Welcome","from":"admin","fromName":"Admin","message":"Welcome","timestamp":"2026-11-23T12:03:00Z","unread":true}]""",
                    _ => "[]"
                };
                return new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
            }
            Writes++;
            LastPath = path;
            var raw = request.Content is null ? "" :
                await request.Content.ReadAsStringAsync(ct);
            var parsed = System.Web.HttpUtility.ParseQueryString(raw);
            LastForm = parsed.AllKeys.Where(k => k is not null)
                .ToDictionary(k => k!, k => parsed[k!] ?? "", StringComparer.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(Result, Encoding.UTF8, "text/plain")
            };
        }
    }
}
