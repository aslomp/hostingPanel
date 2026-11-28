using System.Net.Http.Headers;
using System.Text;
using HostingPanel.Web.Realtime;

namespace HostingPanel.Web.Services;

// Circuit-scoped identity only. Backend calls must use independent operation scopes.
public sealed class PanelSession(TicketChannelRegistry ticketChannels, PanelSessionStore? sessions = null) : IDisposable
{
    private string? _sessionId;
    private string? _username;
    private string? _loginKey;
    private string? _impersonatedUsername;
    private string? _ticketChannel;
    public string? TicketChannel => _ticketChannel;
    public event Action? Changed;
    public event Action? TicketInboxChanged;
    public void NotifyTicketInboxChanged() => TicketInboxChanged?.Invoke();
    private void NotifyChanged() => Changed?.Invoke();

    public bool Connected => _loginKey is not null &&
        (_sessionId is null || sessions?.Get(_sessionId) is not null);

    public bool Restore(string? sessionId)
    {
        var session = sessions?.Get(sessionId);
        if (session is null) return false;
        SetCredentials(session.Username, session.Password);
        _sessionId = sessionId;
        return true;
    }
    public string Username => _impersonatedUsername ?? _username ?? "";
    public string OriginalUsername => _username ?? "";
    public bool IsImpersonating => _impersonatedUsername is not null;

    public void SetCredentials(string username, string loginKey)
    {
        if (string.IsNullOrWhiteSpace(loginKey) || !IsValidAccountName(username))
            throw new ArgumentException("A valid username and login key are required.");
        ticketChannels.Revoke(_ticketChannel);
        _username = username.Trim();
        _loginKey = loginKey;
        _impersonatedUsername = null;
        _ticketChannel = ticketChannels.Register(_username, () => Connected);
        NotifyChanged();
    }

    public void Disconnect()
    {
        sessions?.Remove(_sessionId);
        _sessionId = null;
        ticketChannels.Revoke(_ticketChannel);
        _ticketChannel = null;
        _username = null;
        _loginKey = null;
        _impersonatedUsername = null;
        NotifyChanged();
    }

    public void Dispose()
    {
        ticketChannels.Revoke(_ticketChannel);
        _ticketChannel = null;
        _username = null;
        _loginKey = null;
        _impersonatedUsername = null;
    }

    internal AuthenticationHeaderValue EffectiveCredentials()
    {
        if (!Connected) throw new InvalidOperationException("Sign in first.");
        var username = _impersonatedUsername is null ? _username : _username + "|" + _impersonatedUsername;
        return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + _loginKey)));
    }

    internal static bool IsValidAccountName(string? username) =>
        !string.IsNullOrWhiteSpace(username) && username.Length <= 64 &&
        username.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    internal void SetImpersonation(string? username)
    {
        ticketChannels.Revoke(_ticketChannel);
        _impersonatedUsername = username;
        _ticketChannel = ticketChannels.Register(Username, () => Connected);
        NotifyChanged();
    }
}
