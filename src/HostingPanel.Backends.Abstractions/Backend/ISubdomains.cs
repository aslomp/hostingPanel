using System.Net.Http.Headers;

namespace HostingPanel.Api;

public sealed record SubdomainInventory(int Status, IReadOnlyList<string>? Names, string? Error);
public sealed record SubdomainMutation(int Status, string? Error);

public interface ISubdomains
{
    Task<SubdomainInventory> ListAsync(string domain, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<SubdomainMutation> CreateAsync(string domain, string label, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<SubdomainMutation> DeleteAsync(string domain, string label, string confirmation, AuthenticationHeaderValue auth, CancellationToken ct);
}

public static class SubdomainNames
{
    public static bool ValidLabel(string? label) => label is { Length: >= 1 and <= 63 }
        && char.IsAsciiLetterOrDigit(label[0]) && char.IsAsciiLetterOrDigit(label[^1])
        && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    public static bool ValidDomain(string? domain) => domain is { Length: <= 253 }
        && domain.Contains('.') && domain.Split('.').All(ValidLabel);
}
