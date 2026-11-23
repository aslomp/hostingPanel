using System.Net.Http.Headers;
using HostingPanel.Api.Adapters;

namespace HostingPanel.Api.Services;

/// <summary>Queries each account exposed by DirectAdmin; no local role or owner filter.</summary>
public sealed class AccessibleDomainsService(
    AccountCatalogService accounts, DomainManagementService domains)
{
    public async Task<VisibleDomainsResult> ListAsync(AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var listed = await accounts.ListAsync(auth, ct);
        if (listed.Accounts is null)
            return new(listed.Status, null, listed.Error);

        var discovered = new Dictionary<string, VisibleDomain>(StringComparer.OrdinalIgnoreCase);
        var failedOwners = new List<string>();
        foreach (var account in listed.Accounts)
        {
            var result = await domains.ListAsync(account.Username, auth, ct);
            if (result.Domains is null)
            {
                // DirectAdmin might reject a specific owner, including a peer admin.
                // Do not reimplement that policy in HostingPanel.
                if (result.Status is not (401 or 403 or 404))
                    failedOwners.Add(account.Username);
                continue;
            }
            foreach (var domain in result.Domains)
                discovered.TryAdd(domain, new VisibleDomain(domain, account.Username, account.Role));
        }

        // Also include domains directly owned by the effective account, which may be
        // a reseller or admin operating at its own user level.
        var own = await domains.ListAsync(null, auth, ct);
        if (own.Domains is not null)
        {
            // ListAsync(null) represents the authenticated effective user's own domains.
            // Owner is taken from the first account only when it can be established.
            // Avoid inventing ownership; caller can still see the domain name.
            foreach (var domain in own.Domains)
                discovered.TryAdd(domain, new VisibleDomain(domain, "", ""));
        }
        else if (own.Status is not (401 or 403 or 404))
            failedOwners.Add("current");

        return new(200, discovered.Values.OrderBy(x => x.Domain,
                StringComparer.OrdinalIgnoreCase).ToArray(),
            failedOwners.Count == 0 ? null : "PARTIAL_UPSTREAM_RESULTS");
    }
}

public sealed record VisibleDomainsResult(int Status,
    IReadOnlyList<VisibleDomain>? Items, string? Warning);
