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

public sealed class KubernetesOperationQueue(HostingPanelDbContext db)
{
    public void Schedule(string type, Guid id, string operation, object? payload = null)
    {
        db.ReconciliationOperations.Add(new ReconciliationOperation {
            ResourceType = type, ResourceId = id, Action = operation,
            PayloadJson = JsonSerializer.Serialize(payload ?? new { })
        });
    }
}
