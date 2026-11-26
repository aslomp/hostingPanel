namespace HostingPanel.Api;

/// <summary>Serial activation for the single platform publisher. The runtime
/// callbacks must validate the exact candidate and reload the selected revision.</summary>
public sealed class ApacheRevisionActivator(ApacheCandidateStore store)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task ActivateAsync(ApacheConfigurationRevision revision,
        Func<string, CancellationToken, Task> validate,
        Func<string, CancellationToken, Task> reload,
        CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await store.StageAsync(revision, ct);
            var previous = await store.ReadActiveAsync(ct);
            if (previous is null)
            {
                // Initial rollback target is the empty, fail-closed baseline.
                var baseline = new ApacheConfigurationRevision([]);
                await store.StageAsync(baseline, ct);
                await validate(baseline.Id, ct);
                previous = baseline.Id;
            }

            await validate(revision.Id, ct);
            await store.SelectAsync(revision.Id, ct);
            try
            {
                await reload(revision.Id, ct);
            }
            catch (Exception activationError)
            {
                // Cancellation must not prevent restoration of the previous revision.
                using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                try
                {
                    await store.SelectAsync(previous, recovery.Token);
                    await reload(previous, recovery.Token);
                }
                catch (Exception rollbackError)
                {
                    throw new AggregateException("Apache activation and rollback failed.",
                        activationError, rollbackError);
                }
                throw;
            }
        }
        finally { gate.Release(); }
    }
}
