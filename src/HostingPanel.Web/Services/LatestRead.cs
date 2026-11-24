namespace HostingPanel.Web.Services;

// Owned by a component on its renderer synchronization context.
// A new read supersedes the previous one; writes must not use this policy.
public sealed class LatestRead : IDisposable
{
    private CancellationTokenSource? active;
    private bool disposed;
    public bool Loading => active is not null;

    public async Task RunAsync<T>(Func<CancellationToken, Task<T>> read,
        Func<bool> contextIsCurrent, Action<T> accept, Action<Exception> fail)
    {
        if (disposed) return;
        var previous = active;
        using var request = new CancellationTokenSource();
        active = request;
        previous?.Cancel();
        try
        {
            var result = await read(request.Token);
            if (Current()) accept(result);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or FormatException or OperationCanceledException)
        {
            if (Current()) fail(error);
        }
        finally
        {
            if (ReferenceEquals(active, request)) active = null;
        }

        bool Current() => !disposed && ReferenceEquals(active, request) &&
            !request.IsCancellationRequested && contextIsCurrent();
    }

    public void Cancel()
    {
        var request = active;
        active = null;
        request?.Cancel();
    }

    public void Dispose()
    {
        disposed = true;
        Cancel();
    }
}
