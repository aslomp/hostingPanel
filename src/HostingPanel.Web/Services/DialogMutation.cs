using System.Text.Json;

namespace HostingPanel.Web.Services;

// Component-owned, renderer-context state. Never cancel or replay a dispatched write.
public sealed class DialogMutation : IDisposable
{
    private bool disposed;
    public bool Busy { get; private set; }
    public string? Error { get; private set; }

    public async Task RunAsync(PanelSession session, string? openedChannel,
        Func<Func<bool>, Task> action)
    {
        if (Busy || disposed || !session.Connected || session.TicketChannel != openedChannel) return;
        Busy = true;
        Error = null;
        bool Current() => !disposed && session.Connected && session.TicketChannel == openedChannel;
        try
        {
            await action(Current);
        }
        catch (Exception ex) when (ex is HttpRequestException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or JsonException)
        {
            if (Current()) Error = ex.Message;
        }
        finally { Busy = false; }
    }

    public void SetError(string message) => Error = message;
    public void Dispose() => disposed = true;
}
