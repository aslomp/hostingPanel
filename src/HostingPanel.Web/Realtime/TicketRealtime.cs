using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using HostingPanel.Web.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace HostingPanel.Web.Realtime;

/// <summary>
/// Capability-based subscription registry. Only the circuit holding the
/// unguessable channel token can grant a browser access to its notifications.
/// No DirectAdmin login key is ever transmitted through SignalR.
/// </summary>
public sealed class TicketChannelRegistry
{
    private readonly ConcurrentDictionary<string, byte> channels = new(StringComparer.Ordinal);

    public string Register()
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        channels[token] = 0;
        return token;
    }

    public bool IsActive(string token) => !string.IsNullOrEmpty(token) && channels.ContainsKey(token);
    public void Revoke(string? token)
    {
        if (token is not null) channels.TryRemove(token, out _);
    }
}

public sealed class TicketHub(TicketChannelRegistry channels) : Hub
{
    public async Task Join(string channel)
    {
        if (!channels.IsActive(channel))
            throw new HubException("Ticket channel is not available.");
        await Groups.AddToGroupAsync(Context.ConnectionId, channel);
    }
}

public sealed record TicketSignal(string Kind, long Id, string Subject, DateTimeOffset ReceivedAt);

/// <summary>
/// Watches the *effective* DirectAdmin identity per Blazor circuit, then pushes
/// changes over the ASP.NET Core SignalR hub. DirectAdmin itself provides no
/// push endpoint, so bounded server-side polling is the source of detection.
/// </summary>
public sealed class TicketLiveMonitor(
    PanelSession session,
    TicketOperations tickets,
    TicketChannelRegistry channels,
    IHubContext<TicketHub> hub,
    ILogger<TicketLiveMonitor> logger) : IAsyncDisposable
{
    private readonly CancellationTokenSource shutdown = new();
    private Task? work;

    public void Start() => work ??= Task.Run(() => RunAsync(shutdown.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        var known = new Dictionary<string, string>(StringComparer.Ordinal);
        var baselineReady = false;
        string? currentChannel = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            // The layout starts this monitor before the user signs in.
            // Keep polling only while a DirectAdmin session exists.
            do
            {
                var channel = session.TicketChannel;
                if (channel != currentChannel)
                {
                    currentChannel = channel;
                    known.Clear();
                    baselineReady = false;
                }
                if (session.Connected && channel is not null)
                    baselineReady = await ObserveAsync(channel, known, baselineReady, ct);
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            logger.LogWarning(error, "Ticket monitor stopped unexpectedly.");
        }
    }

    private async Task<bool> ObserveAsync(string channel, Dictionary<string, string> known, bool baselineReady, CancellationToken ct)
    {
        try
        {
            var (status, inbox, _) = await tickets.InboxAsync(ct);
            if (status != 200 || inbox is null || !session.Connected ||
                session.TicketChannel != channel || !channels.IsActive(channel)) return baselineReady;
            var observed = new Dictionary<string, (string Signature, string Kind, long Id, string Subject)>();
            foreach (var ticket in inbox.Tickets)
            {
                var replies = ticket.Replies;
                var signature = replies.Length.ToString() + ":" +
                    (replies.Length == 0 ? "" : ReplyTimestamp(replies[^1]));
                observed[ticket.Source + ":" + ticket.Id] = (signature, "ticket", ticket.Id, ticket.Subject);
            }
            foreach (var message in inbox.Messages)
                observed["message:" + message.Id] = ("present", "message", message.Id, message.Subject);

            // Initial snapshot must not announce historical messages as new.
            if (baselineReady)
            {
                foreach (var (key, value) in observed)
                    if (!known.TryGetValue(key, out var before) || before != value.Signature)
                    {
                        if (!session.Connected || session.TicketChannel != channel || !channels.IsActive(channel))
                            return baselineReady;
                        await hub.Clients.Group(channel).SendAsync("InboxChanged",
                            new TicketSignal(value.Kind, value.Id, value.Subject,
                                DateTimeOffset.UtcNow), ct);
                    }
            }
            known.Clear();
            foreach (var (key, value) in observed) known[key] = value.Signature;
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) when (e is HttpRequestException or InvalidOperationException or JsonException)
        {
            logger.LogDebug("Ticket inventory unavailable: {Type}", e.GetType().Name);
        }
        return baselineReady;
    }

    private static string ReplyTimestamp(JsonElement reply) =>
        reply.ValueKind == JsonValueKind.Object && reply.TryGetProperty("timestamp", out var value)
            ? value.ToString() : "";

    public async ValueTask DisposeAsync()
    {
        await shutdown.CancelAsync();
        if (work is not null) await work;
        shutdown.Dispose();
    }
}
