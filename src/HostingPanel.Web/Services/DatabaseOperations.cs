using System.Text.Json;
using HostingPanel.Api;
using HostingPanel.Api.Services;

namespace HostingPanel.Web.Services;

public sealed record DatabaseOverviewRow(string Owner, string Name, long TableCount, long UserCount, long SizeBytes, long DefinerIssues);
public sealed record DatabaseOverviewResult(int Status, IReadOnlyList<DatabaseOverviewRow>? Items, IReadOnlyList<DatabaseInventoryWarning>? Warnings, string? Error);

public sealed class DatabaseOperations(BackendOperationScope scope)
{
    public Task<DatabaseInventory> ListAsync(CancellationToken ct = default) =>
        scope.RunAsync<IDatabases, DatabaseInventory>((p, auth, token) => p.ListAsync(auth, token), ct);

    public Task<DatabaseMembers> UsersAsync(string database, CancellationToken ct = default) =>
        scope.RunAsync<IDatabases, DatabaseMembers>((p, auth, token) => p.UsersAsync(database, auth, token), ct);

    public Task<DatabaseMutation> CreateAsync(DatabaseCreateRequest input, CancellationToken ct = default) =>
        scope.RunAsync<IDatabases, DatabaseMutation>((p, auth, token) => p.CreateAsync(input, auth, token), ct);

    public Task<DatabaseMutation> DeleteAsync(string database, string confirmation, CancellationToken ct = default) =>
        scope.RunAsync<IDatabases, DatabaseMutation>((p, auth, token) => p.DeleteAsync(database, confirmation, auth, token), ct);

    public async Task<DatabaseOverviewResult> ListAccessibleAsync(CancellationToken ct = default)
    {
        var result = await scope.RunAsync<IAccessibleDatabases, AccessibleDatabaseInventory>((p, auth, token) => p.ListAsync(auth, token), ct);
        return new(result.Status, result.Items?.Select(Map).ToArray(), result.Warnings, result.Error);
    }

    private static DatabaseOverviewRow Map(OwnedDatabase item)
    {
        var db = item.Database;
        return new(item.Owner, Text(db, "database") ?? Text(db, "name") ?? "",
            Number(db, "tableCount"), Number(db, "userCount"), Number(db, "sizeBytes"), Number(db, "definerIssues"));
    }

    private static string? Text(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long Number(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
}
