using Microsoft.EntityFrameworkCore;

namespace HostingPanel.Api.Persistence;

/// <summary>Backend-owned EF model additions. Implementations must use deterministic entity mappings
/// and must never modify or remove HostingPanel's core tables.</summary>
public interface IHostingPanelModelExtension
{
    void Configure(ModelBuilder modelBuilder);
}
