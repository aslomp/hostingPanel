namespace HostingPanel.Api;

/// <summary>Internal invalidation only; subscribers must re-read through authorized provider contracts.
/// No account data or credentials travel on this notification channel.</summary>
public sealed class HostingOperationNotifications
{
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
