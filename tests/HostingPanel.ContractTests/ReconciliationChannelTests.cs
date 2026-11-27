using HostingPanel.Api;

internal static class ReconciliationChannelTests
{
    internal static void Run()
    {
        var notifications = new HostingOperationNotifications();
        var website = notifications.Reader(ReconciliationTarget.Website);
        var dns = notifications.Reader(ReconciliationTarget.Dns);
        var certificate = notifications.Reader(ReconciliationTarget.Certificate);
        int ui = 0;
        notifications.Changed += () => ui++;
        notifications.Publish();
        Check(ui == 1 && !website.TryRead(out _) && !dns.TryRead(out _) && !certificate.TryRead(out _));
        notifications.PublishChange(ReconciliationTarget.Website | ReconciliationTarget.Dns);
        notifications.Signal(ReconciliationTarget.Website);
        Check(ui == 2 && website.TryRead(out _) && !website.TryRead(out _) && dns.TryRead(out _) && !certificate.TryRead(out _));
        notifications.Signal(ReconciliationTarget.All);
        Check(website.TryRead(out _) && dns.TryRead(out _) && certificate.TryRead(out _));
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new Exception("Reconciliation channel invariant failed.");
    }
}
