using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HostingPanel.Api;

/// <summary>Owns persisted Kubernetes settings and network inventory.</summary>
public sealed class KubernetesSettingsService(HostingPanelDbContext db, KubernetesBackendSettings configuredSettings, KubernetesIdentityService identity, IConfiguration? configuration = null) : IBackendSettingsAccess, IHostingNetworkAccess, IKubernetesSettingsReader
{
    private const string SettingsKey = "BackendSettings:Kubernetes";


    public async Task<KubernetesBackendSettings> GetEffectiveSettingsAsync(CancellationToken ct)
    {
        var row = await db.Configuration.AsNoTracking().SingleOrDefaultAsync(x => x.Key == SettingsKey, ct);
        if (row is null) return configuredSettings;
        using var document = JsonDocument.Parse(row.ValueJson);
        // Older persisted settings lack the DNS properties. Fill only new keys from deployment defaults.
        var stored = document.RootElement.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone(), StringComparer.Ordinal);
        // Discard the redundant setting introduced by PR #34, including persisted copies.
        stored.Remove("DnsAccountNameservers");
        using var defaults = JsonDocument.Parse(BackendSettingsSchema.Serialize(configuredSettings));
        foreach (var key in new[] { nameof(KubernetesBackendSettings.DnsAutoCreateZones), nameof(KubernetesBackendSettings.DnsWebIp), nameof(KubernetesBackendSettings.DnsNs1), nameof(KubernetesBackendSettings.DnsNs2), nameof(KubernetesBackendSettings.IpAddresses), nameof(KubernetesBackendSettings.AcmeProviders), nameof(KubernetesBackendSettings.AcmeDefaultProvider), nameof(KubernetesBackendSettings.AcmeUserProviders), nameof(KubernetesBackendSettings.AcmePackageProviders), nameof(KubernetesBackendSettings.TlsListenerSetName) })
            stored.TryAdd(key, defaults.RootElement.GetProperty(key).Clone());
        if (!BackendSettingsSchema.TryRead<KubernetesBackendSettings>(JsonSerializer.SerializeToElement(stored),
            out var settings, out _))
            throw new InvalidOperationException("Stored Kubernetes backend settings are invalid.");
        return settings!;
    }

    public async Task<IResult> ReadAsync(AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return Results.Unauthorized();
        if (principal.Kind != "admin") return Results.StatusCode(403);
        return Results.Ok(BackendSettingsSchema.Describe("Kubernetes", await GetEffectiveSettingsAsync(ct)));
    }

    public async Task<IResult> WriteAsync(AuthenticationHeaderValue auth, BackendSettingsUpdate update,
        CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return Results.Unauthorized();
        if (principal.Kind != "admin") return Results.StatusCode(403);
        if (!BackendSettingsSchema.TryRead<KubernetesBackendSettings>(update.Values, out var proposed, out var errors))
            return Results.ValidationProblem(errors);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var current = await GetEffectiveSettingsAsync(ct);
        if (update.Revision != BackendSettingsSchema.Revision(current))
            return Results.Conflict(new { type = "SETTINGS_CHANGED" });
        var accounts = await db.Accounts.AsNoTracking().Where(x => x.Kind == "reseller" || x.Kind == "admin").Select(x => x.Username).ToListAsync(ct);
        if (proposed!.IpAddresses.Any(x => (x.Resellers ?? []).Any(r => !accounts.Contains(r))))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["IpAddresses"] = ["Unknown reseller."] });
        var users = await db.Accounts.AsNoTracking().Where(x => x.Kind == "user" || x.Kind == "reseller" || x.Kind == "admin").Select(x => new { x.Id, x.Username }).ToListAsync(ct);
        if (proposed.IpAddresses.Any(x => (x.AssignedUsers ?? []).Any(u => !users.Any(a => a.Username == u))))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["IpAddresses"] = ["Unknown assigned user."] });
        var existingUses = await (from r in db.DnsRecords.AsNoTracking()
            join z in db.DnsZones.AsNoTracking() on r.ZoneId equals z.Id
            where (r.Type == "A" || r.Type == "AAAA") && (r.Name == "@" || r.Name == z.Name || r.Name == z.Name + ".")
            select new { r.Value, z.OwnerId }).ToListAsync(ct);
        foreach (var dedicated in proposed.IpAddresses.Where(x => x.Status == "owned"))
        {
            var ownerId = users.Single(x => x.Username == dedicated.AssignedUsers![0]).Id;
            if (existingUses.Any(x => System.Net.IPAddress.TryParse(x.Value, out var address) && address.ToString() == dedicated.Address && x.OwnerId != ownerId))
                return Results.Conflict(new { type = "IP_IN_USE_BY_OTHER_USER", address = dedicated.Address });
        }
        var used = await UsedIpsAsync(ct);
        foreach (var old in current.IpAddresses.Where(x => used.ContainsKey(x.Address)))
            if (!proposed.IpAddresses.Any(x => x.Address == old.Address && x.Status != "available"))
                return Results.Conflict(new { type = "IP_IN_USE", address = old.Address });
        {
            var row = await db.Configuration.SingleOrDefaultAsync(x => x.Key == SettingsKey, ct);
            if (row is null) { row = new PanelConfiguration { Key = SettingsKey }; db.Configuration.Add(row); }
            row.ValueJson = BackendSettingsSchema.Serialize(proposed!);

            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return Results.Ok(BackendSettingsSchema.Describe("Kubernetes", await GetEffectiveSettingsAsync(ct)));
    }

    private async Task<Dictionary<string,int>> UsedIpsAsync(CancellationToken ct)
    {
        var rows = await (from r in db.DnsRecords.AsNoTracking()
            join z in db.DnsZones.AsNoTracking() on r.ZoneId equals z.Id
            where (r.Type == "A" || r.Type == "AAAA") && (r.Name == "@" || r.Name == z.Name || r.Name == z.Name + ".")
            select new { r.Value, z.OwnerId }).Distinct().ToListAsync(ct);
        return rows.Where(x => System.Net.IPAddress.TryParse(x.Value, out _))
            .GroupBy(x => System.Net.IPAddress.Parse(x.Value).ToString()).ToDictionary(g => g.Key, g => g.Select(x => x.OwnerId).Distinct().Count());
    }

    public async Task<IResult> NetworkInventoryAsync(AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var principal = await identity.AuthenticateAsync(auth, ct);
        if (principal is null) return Results.Unauthorized();
        if (principal.Kind != "admin") return Results.StatusCode(403);
        var used = await UsedIpsAsync(ct);
        var resellers = await db.Accounts.AsNoTracking().Where(x => x.Kind == "reseller" || x.Kind == "admin")
            .OrderBy(x => x.Username).Select(x => x.Username).ToListAsync(ct);
        var devices = new List<NetworkDeviceAddress>();
        string? warning = null;
        if (configuration?.GetValue<bool>("KubernetesHosting:Enabled") == true)
        {
            try
            {
                var ns = configuration["KubernetesHosting:GatewayNamespace"] ?? "envoy-gateway-system";
                var gateway = configuration["KubernetesHosting:GatewayName"] ?? throw new InvalidOperationException("Gateway not configured");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var start = new System.Diagnostics.ProcessStartInfo(configuration["KubernetesHosting:KubectlPath"] ?? "kubectl")
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var arg in new[] { "-n", ns, "get", "gateway", gateway, "-o", "json" }) start.ArgumentList.Add(arg);
                using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("Cannot start kubectl");
                try
                {
                    var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                    var error = process.StandardError.ReadToEndAsync(timeout.Token);
                    await process.WaitForExitAsync(timeout.Token);
                    await error;
                    if (process.ExitCode != 0) throw new InvalidOperationException("Gateway read failed");
                    using var json = JsonDocument.Parse(await output);
                    if (json.RootElement.TryGetProperty("status", out var status) && status.TryGetProperty("addresses", out var addresses))
                        foreach (var address in addresses.EnumerateArray())
                            if (System.Net.IPAddress.TryParse(address.GetProperty("value").GetString(), out var ip))
                                devices.Add(new(ip.ToString(), "Gateway/" + ns + "/" + gateway, ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
                }
                finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            }
            catch (Exception) when (!ct.IsCancellationRequested) { warning = "Gateway address discovery unavailable."; }
        }
        var accountNames = await db.Accounts.AsNoTracking().OrderBy(x => x.Username).Select(x => x.Username).ToListAsync(ct);
        return Results.Ok(new { users = used, resellers, devices, warning, accountNames });
    }

}
