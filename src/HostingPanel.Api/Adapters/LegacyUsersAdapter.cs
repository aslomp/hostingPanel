using System.Text.Json;

namespace HostingPanel.Api.Adapters;

/// <summary>
/// Converts the tested legacy CMD_API_SHOW_ALL_USERS JSON array into
/// the documented modern GET /api/search/users response contract.
/// </summary>
public static class LegacyUsersAdapter
{
    public static IReadOnlyList<string> ParseUsernames(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (!root.TryGetProperty("error", out var error) ||
                !(error.ValueKind == JsonValueKind.Number && error.TryGetInt32(out var number) && number == 1) &&
                !(error.ValueKind == JsonValueKind.String && error.GetString() == "1") &&
                !(error.ValueKind == JsonValueKind.True))
                throw new FormatException("Unexpected legacy user-list object.");
            var message = root.TryGetProperty("text", out var text) &&
                text.ValueKind == JsonValueKind.String ? text.GetString() : null;
            throw new LegacyApiException(string.IsNullOrWhiteSpace(message)
                ? "Legacy DirectAdmin API returned an error." : message);
        }

        if (root.ValueKind != JsonValueKind.Array)
            throw new FormatException("Unexpected legacy user-list response.");
        var list = new List<string>();
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw new FormatException("Legacy user-list contains an invalid entry.");
            list.Add(item.GetString()!);
        }
        return list;
    }

    public static IReadOnlyList<string> SearchUsers(IEnumerable<string> usernames, string? query, int limit)
    {
        if (limit < 1 || limit > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        return usernames
            .Where(name => string.IsNullOrEmpty(query) || name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToArray();
    }
}

public sealed class LegacyApiException(string message) : Exception(message);
