namespace HostingPanel.Api;

internal static class DnsRetryPolicy
{
    // These failures need a committed desired-state change, not repeated calls.
    // Unknown/upstream failures remain retryable; never lose durable pending work.
    internal static bool RequiresRetry(string? error) => error is not null && error is not (
        "NO_ASSIGNED_IP" or "INVALID_HOSTING_IP" or "DNS_NAMESERVERS_NOT_CONFIGURED" or
        "DNS_ZONE_EXISTS" or "MAIL_DOMAIN_SUSPENDED" or "MAIL_DNS_ZONE_NOT_READY" or
        "MAIL_DNS_ZONE_OWNERSHIP_CONFLICT" or "MAIL_DNS_RECORD_CONFLICT" or
        "MAIL_DNS_MANAGEMENT_CONFLICT" or "MAIL_DNS_DISABLED" or "MAIL_NOT_CONFIGURED");
}
