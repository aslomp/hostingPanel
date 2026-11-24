using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;

namespace HostingPanel.Api;

/// <summary>A deterministic candidate snapshot, not evidence of Apache acceptance.</summary>
public sealed class ApacheConfigurationRevision
{
    public string Id { get; }
    public IReadOnlyDictionary<Guid, string> VirtualHosts { get; }

    public ApacheConfigurationRevision(IEnumerable<KeyValuePair<Guid, string>> virtualHosts)
    {
        ArgumentNullException.ThrowIfNull(virtualHosts);
        var ordered = new SortedDictionary<Guid, string>();
        foreach (var (id, configuration) in virtualHosts)
        {
            if (id == Guid.Empty || string.IsNullOrWhiteSpace(configuration))
                throw new ArgumentException("Invalid virtualhost snapshot entry.", nameof(virtualHosts));
            ordered.Add(id, configuration);
        }

        VirtualHosts = new ReadOnlyDictionary<Guid, string>(ordered);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(ordered);
        Id = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
