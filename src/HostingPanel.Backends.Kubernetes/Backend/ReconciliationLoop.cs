using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace HostingPanel.Api;

internal static class ReconciliationLoop
{
    internal static async Task RunAsync(HostingOperationNotifications notifications,
        Func<Action, CancellationToken, Task> watch, Func<CancellationToken, Task<bool>> reconcile,
        ILogger logger, TimeSpan idleRecovery, CancellationToken ct)
    {
        var wake = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
        void Notify() => wake.Writer.TryWrite(true);
        notifications.Changed += Notify;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watcher = WatchAsync(watch, Notify, logger, cancellation.Token);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Drain before reconciliation, never after it: changes during a pass
                // must cause another pass.
                while (wake.Reader.TryRead(out _)) { }
                var retry = true;
                try { retry = await reconcile(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { logger.LogWarning(ex, "Reconciliation failed"); }
                using var delay = CancellationTokenSource.CreateLinkedTokenSource(ct);
                delay.CancelAfter(retry ? TimeSpan.FromSeconds(5) : idleRecovery);
                try { await wake.Reader.WaitToReadAsync(delay.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            notifications.Changed -= Notify;
            await cancellation.CancelAsync();
            await watcher;
        }
    }

    private static async Task WatchAsync(Func<Action, CancellationToken, Task> watch,
        Action notify, ILogger logger, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await watch(notify, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Kubernetes watch disconnected"); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        }
    }
}
