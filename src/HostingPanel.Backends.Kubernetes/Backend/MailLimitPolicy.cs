using System.Globalization;
using System.Text.Json;

namespace HostingPanel.Api;

internal static class MailLimitPolicy
{
    // Missing limits deny new capacity; unlimited must be an explicit entitlement.
    internal static long Resolve(string? package, string? account)
    {
        static Dictionary<string, string> Fields(string? json) =>
            JsonSerializer.Deserialize<Dictionary<string, string>>(json ?? "{}")
                ?? throw new InvalidOperationException("Invalid hosting limits.");
        var defaults = Fields(package);
        var overrides = Fields(account);
        var raw = overrides.GetValueOrDefault("nemails") ?? defaults.GetValueOrDefault("nemails");
        if (raw is null) return 0;
        if (raw == "unlimited") return long.MaxValue;
        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 0)
            throw new InvalidOperationException("Invalid mailbox limit.");
        return value;
    }

    internal static bool CanCreate(long current, long limit) => current >= 0 && current < limit;
}

// One shared gate across request scopes. The Helm panel runs one replica.
// External writers and multiple panel instances are not covered by this gate.
internal sealed class MailMutationGate
{
    internal SemaphoreSlim Semaphore { get; } = new(1, 1);
}
