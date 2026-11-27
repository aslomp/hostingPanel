using System.Threading.Channels;

namespace HostingPanel.Api;

[Flags]
public enum ReconciliationTarget
{
    Website = 1,
    Dns = 2,
    Certificate = 4,
    All = Website | Dns | Certificate
}

/// <summary>Separate coalescing invalidation channels; SQLite remains authoritative.</summary>
public sealed class HostingOperationNotifications
{
    private readonly Channel<bool> website = Create();
    private readonly Channel<bool> dns = Create();
    private readonly Channel<bool> certificate = Create();

    private static Channel<bool> Create() => Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite
    });

    public ChannelReader<bool> Reader(ReconciliationTarget target) => target switch
    {
        ReconciliationTarget.Website => website.Reader,
        ReconciliationTarget.Dns => dns.Reader,
        ReconciliationTarget.Certificate => certificate.Reader,
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };

    public void Signal(ReconciliationTarget targets)
    {
        if ((targets & ReconciliationTarget.Website) != 0) website.Writer.TryWrite(true);
        if ((targets & ReconciliationTarget.Dns) != 0) dns.Writer.TryWrite(true);
        if ((targets & ReconciliationTarget.Certificate) != 0) certificate.Writer.TryWrite(true);
    }

    // Call only after durable mutation commit. Status-only publications must not
    // wake their own reconciler and bypass retry delays.
    public void PublishChange(ReconciliationTarget targets)
    {
        Signal(targets);
        Publish();
    }

    public event Action? Changed;

    public void Publish()
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try { ((Action)handler)(); }
            catch (ObjectDisposedException) { }
        }
    }
}
