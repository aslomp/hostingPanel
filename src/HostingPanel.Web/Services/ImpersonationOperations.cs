using HostingPanel.Api;
using HostingPanel.Api.Services;

namespace HostingPanel.Web.Services;

// A provider must authorize delegation before the effective identity changes.
public sealed class ImpersonationOperations(BackendOperationScope scope, PanelSession session)
{
    public async Task StartAsync(string username, CancellationToken ct = default)
    {
        if (!session.Connected || session.IsImpersonating)
            throw new InvalidOperationException("An active original session is required.");
        if (!PanelSession.IsValidAccountName(username) || username.Equals(session.OriginalUsername, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid target account.", nameof(username));
        var channel = session.TicketChannel;
        var result = await scope.RunAsync<IImpersonation, ImpersonationCheck>(
            (p, auth, token) => p.CheckAsync(auth, username, token), ct);
        if (result.Status != 200 || result.Error is not null)
            throw new UnauthorizedAccessException("The backend did not authorize impersonation.");
        ct.ThrowIfCancellationRequested();
        if (!session.Connected || session.IsImpersonating || session.TicketChannel != channel)
            throw new InvalidOperationException("The active session changed during validation.");
        session.SetImpersonation(username);
    }

    public void End()
    {
        if (session.Connected && session.IsImpersonating) session.SetImpersonation(null);
    }
}
