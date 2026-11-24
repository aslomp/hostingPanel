using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Persistence;
using HostingPanel.Providers.CertManager;
using Microsoft.EntityFrameworkCore;
namespace HostingPanel.Api;

public sealed partial class KubernetesBackend
{
    internal static string AcmeKey(Guid id) => "Acme:Domain:" + id.ToString("N");
    internal static async Task<DomainAcmeConfiguration> DomainAcmeAsync(HostingPanelDbContext db, Guid id, CancellationToken ct)
    {
        var row = await db.Configuration.AsNoTracking().SingleOrDefaultAsync(x => x.Key == AcmeKey(id), ct);
        return row is null ? DomainAcmeConfiguration.Default : JsonSerializer.Deserialize<DomainAcmeConfiguration>(row.ValueJson) ?? DomainAcmeConfiguration.Default;
    }
    internal static AcmeIssuerMapping? ResolveIssuer(KubernetesBackendSettings settings, HostingAccount owner, DomainAcmeConfiguration acme)
    {
        var id = acme.Provider;
        if (string.IsNullOrEmpty(id)) id = settings.AcmeUserProviders.GetValueOrDefault(owner.Username);
        if (string.IsNullOrEmpty(id) && owner.PackageId is { } package) id = settings.AcmePackageProviders.GetValueOrDefault(package.ToString());
        if (string.IsNullOrEmpty(id)) id = settings.AcmeDefaultProvider;
        return settings.AcmeProviders.FirstOrDefault(x => x.Id == id);
    }
    internal static string[] CertificateNames(HostedDomain domain, DomainAcmeConfiguration acme) =>
        (acme.PreferWildcard ? new[] { domain.Name, "*." + domain.Name } : new[] { domain.Name })
        .Where(x => !acme.SkipDNSNames.Contains(x, StringComparer.OrdinalIgnoreCase)).ToArray();
    internal static CertManagerClient CertificateClient(IConfiguration config) => new(config["KubernetesHosting:KubectlPath"] ?? "kubectl", config["KubernetesHosting:Namespace"] ?? "hosting");

    public async Task HandleCertificateRequestAsync(HttpContext context)
    {
        var ct = context.RequestAborted;
        if (!AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization.ToString(), out var auth) ||
            await AuthenticateAsync(auth, ct) is not { } principal)
        { await Results.Unauthorized().ExecuteAsync(context); return; }
        var path = context.Request.Path.Value ?? "";
        var method = context.Request.Method;
        var settings = await GetEffectiveSettingsAsync(ct);
        var ids = await VisibleAccountsAsync(principal, ct);
        if (path == "/api/session/acme-data" && method == "GET")
        {
            await Results.Ok(new { certProviders = settings.AcmeProviders.Select(x => new { autoRegistration = true, id = x.Id, name = x.Name, staging = x.Staging }),
                creatorDNSProviderID = "", defaultCertProviderID = ResolveIssuer(settings, principal, DomainAcmeConfiguration.Default)?.Id ?? "",
                dnsProviders = Array.Empty<object>(), globalDNSProviderID = "" }).ExecuteAsync(context);
            return;
        }
        if (path == "/api/domain-tls/domains" && method == "GET")
        {
            var domains = await db.Domains.AsNoTracking().Where(x => ids.Contains(x.OwnerId)).ToListAsync(ct);
            var owners = await db.Accounts.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
            var result = new List<object>();
            foreach (var domain in domains)
            {
                var acme = await DomainAcmeAsync(db, domain.Id, ct);
                result.Add(new { domain = domain.Name, username = owners[domain.OwnerId].Username,
                    acmeSettings = new { enabled = acme.Enabled, provider = acme.Provider, skippedCerts = acme.SkipDNSNames.Length },
                    certs = new { invalidAuto = 0, invalidManual = 0, selfSigned = 0, validAuto = 0, validManual = 0 },
                    dnsNames = new { missing = CertificateNames(domain, acme).Length, trusted = 0, untrusted = 0 } });
            }
            await Results.Ok(result).ExecuteAsync(context); return;
        }
        if (path.StartsWith("/api/domain-tls/domains/", StringComparison.Ordinal))
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) { await Results.StatusCode(501).ExecuteAsync(context); return; }
            var domain = await db.Domains.AsNoTracking().SingleOrDefaultAsync(x => x.Name == parts[3], ct);
            if (domain is null) { await Results.NotFound().ExecuteAsync(context); return; }
            if (!ids.Contains(domain.OwnerId)) { await Results.StatusCode(403).ExecuteAsync(context); return; }
            var owner = await db.Accounts.AsNoTracking().SingleAsync(x => x.Id == domain.OwnerId, ct);
            var acme = await DomainAcmeAsync(db, domain.Id, ct);
            if (parts.Length == 5 && parts[4] == "acme-config")
            {
                if (method == "GET") { await Results.Ok(acme).ExecuteAsync(context); return; }
                if (method == "PUT")
                {
                    DomainAcmeConfiguration? input;
                    try { input = await context.Request.ReadFromJsonAsync<DomainAcmeConfiguration>(cancellationToken: ct); }
                    catch (JsonException) { await Results.BadRequest().ExecuteAsync(context); return; }
                    if (input is null || input.DnsEnvironment is null || input.DnsEnvironment.Count != 0 ||
                        input.DnsProvider != "" || input.ExternalAccountHMAC != "" || input.ExternalAccountKeyID != "" ||
                        input.Provider is null || input.SkipDNSNames is null ||
                        input.KeyType is not ("ec256" or "ec384" or "rsa2048" or "rsa3072" or "rsa4096" or "rsa8192") ||
                        (input.Provider.Length > 0 && !settings.AcmeProviders.Any(x => x.Id == input.Provider)))
                    { await Results.BadRequest(new { type = "INVALID_ACME_CONFIG", message = "Configure DNS solvers and EAB on the issuer; select a registered provider." }).ExecuteAsync(context); return; }
                    var issuer = ResolveIssuer(settings, owner, input);
                    if (input.Enabled && (issuer is null || (input.PreferWildcard && !issuer.SupportsWildcard) || CertificateNames(domain, input).Length == 0))
                    { await Results.BadRequest(new { type = "INVALID_ACME_PROVIDER" }).ExecuteAsync(context); return; }
                    var key = AcmeKey(domain.Id);
                    var row = await db.Configuration.SingleOrDefaultAsync(x => x.Key == key, ct);
                    if (row is null) { row = new PanelConfiguration { Key = key }; db.Configuration.Add(row); }
                    row.ValueJson = JsonSerializer.Serialize(input);
                    await db.SaveChangesAsync(ct);
                    await Results.NoContent().ExecuteAsync(context); return;
                }
            }
            if (parts.Length == 5 && parts[4] == "provision-certs" && method == "POST")
            {
                if (!acme.Enabled) { await Results.Conflict(new { type = "DOMAIN_ACME_IS_DISABLED" }).ExecuteAsync(context); return; }
                var issuer = ResolveIssuer(settings, owner, acme);
                if (configuration?.GetValue<bool>("CertManager:Enabled") != true || issuer is null)
                { await Results.StatusCode(503).ExecuteAsync(context); return; }
                try
                {
                    await CertificateClient(configuration).ApplyAsync(domain.Id, CertificateNames(domain, acme), issuer.IssuerName, issuer.IssuerKind, acme.KeyType, ct);
                    await Results.Ok(new { logIDs = Array.Empty<int>() }).ExecuteAsync(context);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                { await Results.Json(new { type = "INTERNAL_ERROR", message = "Certificate request could not be submitted." }, statusCode: 500).ExecuteAsync(context); }
                return;
            }
        }
        await Results.StatusCode(501).ExecuteAsync(context);
    }
}
