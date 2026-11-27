using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace HostingPanel.Api;

internal static class ReconciliationLoop
{
    internal static async Task RunAsync(ChannelReader<bool> reader,
        Func<CancellationToken, Task<bool>> reconcile, TimeSpan retry, TimeSpan maintenance,
        ILogger logger, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Drain before processing, never after: writes during reconciliation
                // must cause a subsequent pass.
                while (reader.TryRead(out _)) { }
                var pending = true;
                try { pending = await reconcile(ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { logger.LogWarning(ex, "Reconciliation failed; will retry"); }

                using var delay = CancellationTokenSource.CreateLinkedTokenSource(ct);
                delay.CancelAfter(pending ? retry : maintenance);
                try { await reader.WaitToReadAsync(delay.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
