using HostingPanel.Api;

internal static class ApacheConfigurationRevisionTests
{
    public static void Run()
    {
        var a = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var b = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var input = new Dictionary<Guid, string> { [a] = "site a", [b] = "site b" };
        var first = new ApacheConfigurationRevision(input);
        var reordered = new ApacheConfigurationRevision(input.Reverse());
        Require(first.Id == reordered.Id);
        Require(first.Id.Length == 64);
        input[a] = "changed";
        Require(first.VirtualHosts[a] == "site a");
        Require(first.Id != new ApacheConfigurationRevision(input).Id);
        input.Remove(b);
        Require(first.Id != new ApacheConfigurationRevision(input).Id);
        Require(first.Id != new ApacheConfigurationRevision([]).Id);
        try
        {
            _ = new ApacheConfigurationRevision(new[] { KeyValuePair.Create(a, "one"), KeyValuePair.Create(a, "two") });
            throw new Exception("Duplicate domain accepted.");
        }
        catch (ArgumentException) { }
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new Exception("Apache revision invariant failed.");
    }
}
