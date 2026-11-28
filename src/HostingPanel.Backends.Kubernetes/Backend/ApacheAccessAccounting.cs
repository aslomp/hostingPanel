using System.Globalization;

namespace HostingPanel.Api;

/// <summary>Versioned, privacy-minimized accounting records. IDs are configuration literals, not request headers.</summary>
public sealed record ApacheAccessRecord(Guid AccountId, Guid DomainId, DateTimeOffset CompletedAt, int Status,
    long ReceivedBytes, long SentBytes, long DurationMicroseconds);

public static class ApacheAccessAccounting
{
    public const string Prefix = "HP_ACCESS_V1";

    public static string Directive(Guid accountId, Guid domainId, bool durable = false)
    {
        if (accountId == Guid.Empty || domainId == Guid.Empty) throw new ArgumentException("Missing accounting identity.");
        var target = durable ? "\"|/usr/bin/python3 /etc/hostingpanel-accounting/spool.py\"" : "/proc/self/fd/1";
        return $"CustomLog {target} \"{Prefix} {accountId:N} {domainId:N} %{{end:sec}}t %>s %I %O %D\"";
    }

    public static string ErrorDirective(Guid accountId, Guid domainId, bool durable = false)
    {
        if (accountId == Guid.Empty || domainId == Guid.Empty) throw new ArgumentException("Missing logging identity.");
        var target = durable ? "\"|/usr/bin/python3 /etc/hostingpanel-accounting/spool.py\"" : "/proc/self/fd/2";
        return $"ErrorLog {target}\nErrorLogFormat \"HP_ERROR_V1 {accountId:N} {domainId:N} [%l] %M\"";
    }

    public static bool TryParse(string line, out ApacheAccessRecord? record)
    {
        record = null;
        if (string.IsNullOrEmpty(line) || line.Length > 512) return false;
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 8 || fields[0] != Prefix
            || !Guid.TryParseExact(fields[1], "N", out var account) || account == Guid.Empty
            || !Guid.TryParseExact(fields[2], "N", out var domain) || domain == Guid.Empty
            || !Number(fields[3], out var seconds) || seconds > 253402300799L
            || !Number(fields[4], out var status) || status is < 100 or > 599
            || !Number(fields[5], out var received) || !Number(fields[6], out var sent)
            || !Number(fields[7], out var duration) || received > long.MaxValue - sent) return false;
        record = new(account, domain, DateTimeOffset.FromUnixTimeSeconds(seconds), (int)status, received, sent, duration);
        return true;
    }

    private static bool Number(string input, out long number) =>
        long.TryParse(input, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number >= 0;
}
