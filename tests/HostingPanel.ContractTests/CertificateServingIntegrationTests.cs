using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using HostingPanel.Api;
using HostingPanel.Api.Persistence;
using HostingPanel.Providers.CertManager;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class CertificateServingIntegrationTests
{
    private const string Ns = "certificate-serving-test";
    private const string Group = "gateway.networking.k8s.io";
    private const string Set = "hosting-sites";
    private const string HostA = "alpha.example.test";
    private const string HostB = "beta.example.test";

    public static async Task RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var ct = deadline.Token;
        var dbPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".db");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CertManager:Enabled"] = "true",
            ["CertManager:ListenerSetName"] = Set,
            ["KubernetesHosting:Enabled"] = "true",
            ["KubernetesHosting:Namespace"] = Ns,
            ["KubernetesHosting:GatewayName"] = "external",
            ["KubernetesHosting:GatewayNamespace"] = Ns
        }).Build();
        var settings = new KubernetesBackendSettings
        {
            AcmeProviders = [new("test-ca", "Test CA", "test-ca", "Issuer")],
            AcmeDefaultProvider = "test-ca",
            TlsListenerSetName = Set
        };
        var services = new ServiceCollection();
        services.AddLogging(x => x.AddConsole());
        services.AddDbContext<HostingPanelDbContext>(x => x.UseSqlite("Data Source=" + dbPath));
        services.AddSingleton<IKubernetesSettingsReader>(new SettingsReader(settings));
        await using var sp = services.BuildServiceProvider();
        using var resources = new KubernetesResourceClient();
        var work = new CertificateWork();
        var notifications = new HostingOperationNotifications();
        using var reconciler = new CertificateReconciler(sp.GetRequiredService<IServiceScopeFactory>(),
            configuration, sp.GetRequiredService<ILogger<CertificateReconciler>>(), resources, notifications, work);
        var owner = new HostingAccount { Username = "test-owner", Kind = "user" };
        var a = new HostedDomain { OwnerId = owner.Id, Name = HostA };
        var b = new HostedDomain { OwnerId = owner.Id, Name = HostB };
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HostingPanelDbContext>();
            await db.Database.EnsureCreatedAsync(ct);
            db.Accounts.Add(owner);
            db.Domains.AddRange(a, b);
            foreach (var domain in new[] { a, b })
                db.Configuration.Add(new PanelConfiguration {
                    Key = KubernetesCertificateProvider.AcmeKey(domain.Id),
                    ValueJson = JsonSerializer.Serialize(DomainAcmeConfiguration.Default with { Enabled = true })
                });
            await db.SaveChangesAsync(ct);
        }

        using var root = X509Certificate2.CreateFromPem(await File.ReadAllTextAsync(
            Environment.GetEnvironmentVariable("TEST_CA_CERT") ?? throw new InvalidOperationException("TEST_CA_CERT missing"), ct));
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, 18443, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            },
            SslOptions = new SslClientAuthenticationOptions
            {
                CertificateChainPolicy = new X509ChainPolicy
                {
                    TrustMode = X509ChainTrustMode.CustomRootTrust,
                    CustomTrustStore = { root },
                    RevocationMode = X509RevocationMode.NoCheck
                }
            }
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };

        async Task Eventually(string description, Func<Task<bool>> condition)
        {
            Exception? last = null;
            while (!ct.IsCancellationRequested)
            {
                try { if (await condition()) { Console.WriteLine(description + ": OK"); return; } }
                catch (Exception ex) when (!ct.IsCancellationRequested) { last = ex; }
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
                catch (OperationCanceledException) { break; }
            }
            throw new InvalidOperationException(description + " did not converge", last);
        }

        async Task<bool> Https(string host)
        {
            using var response = await http.GetAsync("https://" + host + "/", ct);
            return response.IsSuccessStatusCode &&
                (await response.Content.ReadAsStringAsync(ct)).Contains("hostingpanel-tls-fixture", StringComparison.Ordinal);
        }
        async Task<bool> Ready(Guid id)
        {
            var json = await resources.TryGetAsync("cert-manager.io", "v1", Ns, "certificates", CertManagerClient.Name(id), ct);
            if (json is null) return false;
            using var doc = JsonDocument.Parse(json);
            return CertificateReconciler.IsReady(doc.RootElement);
        }
        async Task Remove(HostedDomain domain)
        {
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HostingPanelDbContext>();
            db.Domains.Remove(await db.Domains.SingleAsync(x => x.Id == domain.Id, ct));
            db.Configuration.Remove(await db.Configuration.SingleAsync(x => x.Key == KubernetesCertificateProvider.AcmeKey(domain.Id), ct));
            await db.SaveChangesAsync(ct);
            work.DomainChanged(domain.Id);
        }
        async Task<bool> Gone(Guid id) =>
            await resources.TryGetAsync("cert-manager.io", "v1", Ns, "certificates", CertManagerClient.Name(id), ct) is null &&
            await resources.TryGetAsync(Group, "v1", Ns, "httproutes", "hp-tls-" + id.ToString("N"), ct) is null;

        await reconciler.StartAsync(ct);
        try
        {
            await Eventually("Both certificates issued by cert-manager", async () => await Ready(a.Id) && await Ready(b.Id));
            await Eventually("Both HTTPS hosts validate against private CA", async () => await Https(HostA) && await Https(HostB));
            await Remove(a);
            await Eventually("First certificate and route removed", () => Gone(a.Id));
            await Eventually("Other domain remains serving", async () =>
            {
                var raw = await resources.GetListenerSetAsync(Ns, Set, ct);
                if (raw is null) return false;
                using var doc = JsonDocument.Parse(raw);
                var listeners = doc.RootElement.GetProperty("spec").GetProperty("listeners").EnumerateArray().ToArray();
                return listeners.Length == 1 && listeners[0].GetProperty("hostname").GetString() == HostB &&
                    await Ready(b.Id) && await Https(HostB);
            });
            await Remove(b);
            await Eventually("Last certificate, route and ListenerSet removed", async () =>
                await Gone(b.Id) && await resources.GetListenerSetAsync(Ns, Set, ct) is null);
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await reconciler.StopAsync(stop.Token);
        }
    }

    private sealed class SettingsReader(KubernetesBackendSettings settings) : IKubernetesSettingsReader
    {
        public Task<KubernetesBackendSettings> GetEffectiveSettingsAsync(CancellationToken ct) => Task.FromResult(settings);
    }
}
