using HostingPanel.Providers.PowerDNS;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HostingPanel.Api.Adapters;
using HostingPanel.Api.Persistence;
using HostingPanel.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace HostingPanel.Api;

public sealed class KubernetesWebHosting(HostingPanelDbContext db, IDomains domains) : IWebHosting
{
    public async Task<WebsiteListResult> ListWebsitesAsync(string? owner, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var result = await domains.ListAsync(owner, auth, ct);
        return new(result.Status, result.Domains, result.Error);
    }

    public async Task<WebsiteResult> GetWebsiteAsync(string domain, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var result = await domains.GetDetailsAsync(domain, auth, ct);
        if (result.Settings is null) return new(result.Status, null, result.Error);
        var name = domain.ToLowerInvariant();
        var id = await db.Domains.AsNoTracking().Where(x => x.Name == name).Select(x => x.Id).SingleOrDefaultAsync(ct);
        var pending = await db.ReconciliationOperations.AsNoTracking().AnyAsync(x =>
            x.ResourceType == "website" && x.ResourceId == id && x.Status == "pending", ct);
        return new(result.Status, new WebsiteInfo(name, result.Settings,
            pending ? WebsiteProvisioningState.Pending : WebsiteProvisioningState.Unknown), result.Error);
    }

    public Task<WebsiteMutationResult> CreateWebsiteAsync(WebsiteConfiguration website, AuthenticationHeaderValue auth, CancellationToken ct) =>
        ApplyWebsiteAsync(website, "create", website.Domain, auth, ct);

    public Task<WebsiteMutationResult> UpdateWebsiteAsync(WebsiteConfiguration website, AuthenticationHeaderValue auth, CancellationToken ct) =>
        ApplyWebsiteAsync(website, "modify", website.Domain, auth, ct);

    public Task<WebsiteMutationResult> DeleteWebsiteAsync(string domain, string confirmation, AuthenticationHeaderValue auth, CancellationToken ct) =>
        ApplyWebsiteAsync(new WebsiteConfiguration(domain), "delete", confirmation, auth, ct);

    private async Task<WebsiteMutationResult> ApplyWebsiteAsync(WebsiteConfiguration website, string action, string confirmation,
        AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var result = await domains.MutateAsync(new DomainMutationRequest(website.Domain, action, confirmation,
            website.Php, website.Tls, website.Cgi), auth, ct);
        return new(result.Status, result.Error);
    }
}
