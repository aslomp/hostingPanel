using HostingPanel.Api;
using HostingPanel.Api.Persistence;

internal static class StalwartDnsTests
{
    internal static void Run()
    {
        const string domain = "example.test";
        const string source = """
            $ORIGIN example.test.
            @ 3600 IN MX 10 mail.example.net.
            @ 3600 IN TXT "v=spf1 mx -all"
            _dmarc 3600 IN TXT "v=DMARC1; p=none"
            mail._domainkey 3600 IN TXT "v=DKIM1; p=abc" "def"
            @ 3600 IN TXT "unrelated verification"
            """;
        var records = StalwartDnsRecords.Parse(domain, source);
        Check(records.Count == 4);
        Check(records.Any(x => x.Name == "mail._domainkey.example.test."));
        var existing = new List<DnsRecordEntity> {
            new() { Name = "@", Type = "A", Value = "192.0.2.1" },
            new() { Name = "@", Type = "TXT", Value = "site-verification=example" }
        };
        Check(StalwartDnsProvisioning.Plan(domain, existing, records)?.Count == 4);
        existing.AddRange(records.Select(x => new DnsRecordEntity { Name = x.Name, Type = x.Type, Value = x.Value }));
        Check(StalwartDnsProvisioning.Plan(domain, existing, records)?.Count == 0);
        existing.Add(new() { Name = "@", Type = "MX", Value = "20 other.example.net." });
        Check(StalwartDnsProvisioning.Plan(domain, existing, records) is null);
        existing.RemoveAt(existing.Count - 1);
        existing.Add(new() { Name = "@", Type = "TXT", Value = "v=spf1 -all" });
        Check(StalwartDnsProvisioning.Plan(domain, existing, records) is null);
        Reject(source.Replace("mail._domainkey", "mail._domainkey.other.test."));
        Reject("@ 3600 IN MX 10 mail.example.net.");
        void Reject(string text)
        {
            try { StalwartDnsRecords.Parse(domain, text); }
            catch (FormatException) { return; }
            throw new InvalidOperationException("Invalid mail DNS accepted.");
        }
    }

    private static void Check(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Mail DNS contract failed.");
    }
}
