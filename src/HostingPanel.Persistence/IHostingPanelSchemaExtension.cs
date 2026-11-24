namespace HostingPanel.Api.Persistence;

/// <summary>Additional provider-owned SQLite schema migrations in the common panel database.</summary>
public interface IHostingPanelSchemaExtension
{
    string Provider { get; }
    IReadOnlyList<HostingPanelSchemaMigration> Migrations { get; }
}

/// <summary>Monotonically increasing schema version and idempotent SQL for a provider.</summary>
public sealed record HostingPanelSchemaMigration(int Version, string Sql);
