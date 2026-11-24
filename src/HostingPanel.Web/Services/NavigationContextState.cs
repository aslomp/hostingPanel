namespace HostingPanel.Web.Services;

/// <summary>
/// Purely presentational DirectAdmin Evolution navigation level.
/// Never implies permissions; the backend remains the only authority.
/// Scoped per interactive Blazor circuit so it persists across page navigation.
/// </summary>
public sealed class NavigationContextState
{
    public string Mode { get; private set; } = "admin";
    public event Action? Changed;

    public void Select(string mode)
    {
        var normalized = mode == "user" ? "user" : "admin";
        if (Mode == normalized) return;
        Mode = normalized;
        Changed?.Invoke();
    }
}
