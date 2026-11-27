namespace HostingPanel.Api;

// One coalescing mailbox per controller. A full invalidation supersedes keys.
public sealed class CertificateWork
{
    private readonly object gate = new();
    private readonly HashSet<Guid> domains = new();
    private bool full = true;
    internal HostingOperationNotifications Wake { get; } = new();

    public void DomainChanged(Guid id)
    {
        lock (gate) { if (!full) domains.Add(id); }
        Wake.Publish();
    }

    public void AllChanged()
    {
        lock (gate) { full = true; domains.Clear(); }
        Wake.Publish();
    }

    internal Guid[]? Take()
    {
        lock (gate)
        {
            var result = full ? null : domains.ToArray();
            full = false;
            domains.Clear();
            return result;
        }
    }

    internal void Recover()
    {
        // Do not publish from inside a failed pass: the shared loop backs off.
        lock (gate) { full = true; domains.Clear(); }
    }
}
