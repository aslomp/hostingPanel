using HostingPanel.Providers.CertManager;
namespace HostingPanel.Api;
public sealed partial class KubernetesCertificateProvider
{
    private async Task<CertificateSnapshot> ReadCertificateSnapshotAsync(CancellationToken ct)
    {
        if (configuration?.GetValue<bool>("CertManager:Enabled") != true) return CertificateSnapshot.Empty;
        return await CertificateSnapshot.LoadAsync(CertificateClient(configuration), ct);
    }
}
