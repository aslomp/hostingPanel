using HostingPanel.Api;

internal static class ApacheAccessAccountingTests
{
    public static void Run()
    {
        var owner = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var domain = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var prefix = $"HP_ACCESS_V1 {owner:N} {domain:N}";
        Require(ApacheAccessAccounting.Directive(owner, domain).Contains(prefix));
        Require(ApacheAccessAccounting.TryParse(prefix + " 1795860000 200 120 400 42", out var row));
        Require(row!.AccountId == owner && row.DomainId == domain && row.ReceivedBytes == 120 && row.SentBytes == 400 && row.Status == 200);
        foreach (var invalid in new[] { "HP_SYSTEM_V1 200 1 2 3", prefix + " 0 200 -1 4 5", prefix + " 0 200 9223372036854775807 1 0", prefix + " 253402300800 200 0 0 0", prefix + " 0 999 0 0 0", prefix + " 0 200 0 0 0 extra" })
            Require(!ApacheAccessAccounting.TryParse(invalid, out _));
    }
    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Apache access accounting contract failed.");
    }
}
