using System.Threading.Channels;
using HostingPanel.Providers.CertManager;

internal static class CertificateWatchIntegrationTests
{
    public static async Task RunAsync()
    {
        using var resources = new KubernetesResourceClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = deadline.Token;
        const string ns = "certificate-watch-test";
        var id = Guid.NewGuid();
        var name = CertManagerClient.Name(id);
        var client = new CertManagerClient(resources, ns);
        var events = Channel.CreateUnbounded<string>();
        async Task ExpectAsync(string expected)
        {
            while (await events.Reader.WaitToReadAsync(ct))
                if (events.Reader.TryRead(out var value) && value == expected) return;
            throw new Exception("Watch closed before " + expected);
        }
        async Task WatchAsync(CancellationToken token) =>
            await resources.WatchCustomResourcesAsync("cert-manager.io", "v1", ns,
                "certificates", "hostingpanel/component=certificate",
                () => events.Writer.TryWrite("reset"), value => events.Writer.TryWrite(value), token);
        async Task StopAsync(CancellationTokenSource source, Task task)
        {
            await source.CancelAsync();
            try { await task; }
            catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        }
        using var first = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watcher = WatchAsync(first.Token);
        try
        {
            await ExpectAsync("reset");
            await client.ApplyAsync(id, ["example.test"], "test", "Issuer", "ec256", ct);
            await ExpectAsync(name);
            await client.ApplyAsync(id, ["changed.example.test"], "test", "Issuer", "ec256", ct);
            await ExpectAsync(name);
        }
        finally { await StopAsync(first, watcher); }

        // Change while disconnected; a new initial LIST must observe it.
        await client.ApplyAsync(id, ["offline.example.test"], "test", "Issuer", "ec256", ct);
        using var second = CancellationTokenSource.CreateLinkedTokenSource(ct);
        watcher = WatchAsync(second.Token);
        try
        {
            await ExpectAsync("reset");
            var snapshot = await client.ListAsync(ct);
            if (!snapshot.Contains("offline.example.test", StringComparison.Ordinal))
                throw new Exception("Relist missed disconnected mutation.");
            await client.DeleteAsync(name, ct);
            await ExpectAsync(name);
        }
        finally { await StopAsync(second, watcher); }
        Console.WriteLine("Certificate SDK LIST/WATCH, update, reconnect and deletion: OK");
    }
}
