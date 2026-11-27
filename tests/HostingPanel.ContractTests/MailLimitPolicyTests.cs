using HostingPanel.Api;

internal static class MailLimitPolicyTests
{
    internal static void Run()
    {
        Check(MailLimitPolicy.Resolve(null, null) == 0);
        Check(MailLimitPolicy.Resolve("""{"nemails":"10"}""", null) == 10);
        Check(MailLimitPolicy.Resolve("""{"nemails":"10"}""", """{"nemails":"2"}""") == 2);
        Check(MailLimitPolicy.Resolve("""{"nemails":"unlimited"}""", """{"nemails":"0"}""") == 0);
        Check(MailLimitPolicy.Resolve("""{"nemails":"unlimited"}""", null) == long.MaxValue);
        Check(MailLimitPolicy.CanCreate(9, 10));
        Check(!MailLimitPolicy.CanCreate(10, 10));
        Check(!MailLimitPolicy.CanCreate(0, 0));
        Check(!MailLimitPolicy.CanCreate(-1, 10));
        try { MailLimitPolicy.Resolve("""{"nemails":"invalid"}""", null); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("Malformed mailbox entitlement was accepted.");
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Mailbox limit contract failed.");
    }
}
