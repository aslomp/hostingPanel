using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace HostingPanel.Api;

internal static class ReconciliationLoop
{
    internal static async Task RunAsync(ChannelReader<bool> reader,
        Func<Action, CancellationToken, Task>? watch, Func<CancellationToken, Task<bool>> reconcile,
        ILogger logger, TimeSpan idleRecovery, CancellationToken ct, Action? onTimeout = null, Action? signal = null, TimeSpan? retryDelay = null)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watcher = watch is null ? Task.CompletedTask : WatchAsync(watch, signal ?? throw new ArgumentNullException(nameof(signal)), logger, cancellation.Token);
        var failures = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Drain before reconciliation, never after it: changes during a pass
                // must cause another pass.
                while (reader.TryRead(out _)) { }
                var retry = true;
                try { retry = await reconcile(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { logger.LogWarning(ex, "Reconciliation failed"); }
                using var delay = CancellationTokenSource.CreateLinkedTokenSource(ct);
                failures = retry ? Math.Min(failures + 1, 10) : 0;
                delay.CancelAfter(retry ? RetryDelay(retryDelay ?? TimeSpan.FromSeconds(5), failures) : idleRecovery);
                try { if (!await reader.WaitToReadAsync(delay.Token)) return; }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { onTimeout?.Invoke(); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            await cancellation.CancelAsync();
            await watcher;
        }
    }

    internal static Task RunAsync(ChannelReader<bool> reader, Func<CancellationToken, Task<bool>> reconcile,
        TimeSpan retry, TimeSpan maintenance, ILogger logger, CancellationToken ct)
        => RunAsync(reader, null, reconcile, logger, maintenance, ct, retryDelay: retry);

    internal static TimeSpan RetryDelay(TimeSpan initial, int failures) =>
        TimeSpan.FromMilliseconds(Math.Min(300_000, initial.TotalMilliseconds * Math.Pow(2, Math.Clamp(failures - 1, 0, 10))));

    internal static async Task WatchAsync(Func<Action, CancellationToken, Task> watch,
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
