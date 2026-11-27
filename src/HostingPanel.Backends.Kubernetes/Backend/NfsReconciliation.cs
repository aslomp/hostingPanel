using HostingPanel.Api.Persistence;

namespace HostingPanel.Api;

// Successful directory provisioning is cached for the worker lifetime.
// A restart deliberately validates existing DirectAdmin ownership again.
internal sealed class NfsReconciliation(NfsDirectoryProvisioner storage)
{
    private readonly Dictionary<Guid, (string User, string Domain, int Uid, int Gid)> provisioned = new();

    internal async Task<(List<HostedDomain> Usable, Dictionary<Guid, Exception> Errors)> EnsureAsync(
        IReadOnlyList<HostedDomain> domains, IReadOnlyDictionary<Guid, HostingAccount> accounts,
        IReadOnlyDictionary<Guid, PhpAccountIdentity> identities, CancellationToken ct)
    {
        var current = domains.Select(x => x.Id).ToHashSet();
        foreach (var id in provisioned.Keys.Where(id => !current.Contains(id)).ToArray()) provisioned.Remove(id);
        var usable = new List<HostedDomain>();
        var errors = new Dictionary<Guid, Exception>();
        foreach (var domain in domains)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var owner = accounts[domain.OwnerId];
                var identity = identities[domain.OwnerId];
                if (!provisioned.TryGetValue(domain.Id, out var cached) ||
                    cached.User != owner.Username || cached.Domain != domain.Name)
                {
                    var (uid, gid) = await storage.EnsureAsync(owner.Username, domain.Name, identity, ct);
                    cached = (owner.Username, domain.Name, uid, gid);
                }
                if (identity.ExistingUid is { } previous && (previous != cached.Uid || identity.ExistingGid != cached.Gid))
                    throw new InvalidOperationException("Inconsistent ownership of DirectAdmin account directories.");
                identity.ExistingUid = cached.Uid;
                identity.ExistingGid = cached.Gid;
                provisioned[domain.Id] = cached;
                usable.Add(domain);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                provisioned.Remove(domain.Id);
                errors[domain.Id] = ex;
            }
        }
        return (usable, errors);
    }

    internal async Task DeleteAsync(Guid id, string username, string domain, CancellationToken ct)
    {
        provisioned.Remove(id);
        await storage.DeleteAsync(username, domain, ct);
    }
}
