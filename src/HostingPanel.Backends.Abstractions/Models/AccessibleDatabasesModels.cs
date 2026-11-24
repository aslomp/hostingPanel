using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace HostingPanel.Api.Services;

public sealed record OwnedDatabase(string Owner, JsonElement Database);

public sealed record DatabaseInventoryWarning(string Account, int Status, string Reason);

public sealed record AccessibleDatabaseInventory(int Status, IReadOnlyList<OwnedDatabase>? Items,
    IReadOnlyList<DatabaseInventoryWarning>? Warnings, string? Error);
