using System.Net.Http.Headers;

namespace HostingPanel.Web.Services;

// Blazor circuits outlive EF Core units of work. Resolve and dispose providers per call.
public sealed class BackendOperationScope(IServiceScopeFactory scopes, PanelSession session)
{
    public async Task<TResult> RunOptionalAsync<TProvider, TResult>(
        Func<TProvider, AuthenticationHeaderValue, CancellationToken, Task<TResult>> operation, TResult unsupported,
        CancellationToken ct = default) where TProvider : class
    {
        var credentials = session.EffectiveCredentials();
        await using var scope = scopes.CreateAsyncScope();
        var provider = scope.ServiceProvider.GetService<TProvider>();
        return provider is null ? unsupported : await operation(provider, credentials, ct);
    }

    public async Task<TResult> RunAsync<TProvider, TResult>(
        Func<TProvider, AuthenticationHeaderValue, CancellationToken, Task<TResult>> operation,
        CancellationToken ct = default) where TProvider : notnull
    {
        var credentials = session.EffectiveCredentials();
        await using var scope = scopes.CreateAsyncScope();
        return await operation(scope.ServiceProvider.GetRequiredService<TProvider>(), credentials, ct);
    }
}
