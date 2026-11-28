using System.Threading.Channels;

namespace HostingPanel.Api;

// In-process invalidations only. Ticket contents and credentials stay in the
// authoritative backend. A single consumer fans out to authorized circuits.
public sealed record TicketChange(string? Account, string? Channel);

public sealed class TicketEvents
{
    private readonly Channel<TicketChange> queue = Channel.CreateUnbounded<TicketChange>(
        new UnboundedChannelOptions { SingleReader = true, AllowSynchronousContinuations = false });

    public ChannelReader<TicketChange> Reader => queue.Reader;
    public void AccountChanged(string account) => queue.Writer.TryWrite(new(account, null));
    public void ChannelChanged(string channel) => queue.Writer.TryWrite(new(null, channel));
}
