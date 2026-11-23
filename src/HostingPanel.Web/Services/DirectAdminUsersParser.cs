using System.Text.Json;

namespace HostingPanel.Web.Services;

public static class DirectAdminUsersParser
{
    // Per the DirectAdmin examples, CMD_API_SHOW_ALL_USERS?json=yes
    // returns a JSON array of usernames. A legacy error object is not success.
    public static IReadOnlyList<string> Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            throw new FormatException("DirectAdmin gaf geen gebruikersgegevens terug.");
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            var root = document.RootElement;
            var description = root.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String
                ? text.GetString() : "De bewerking is niet toegestaan of is mislukt.";
            throw new InvalidOperationException($"DirectAdmin: {description}");
        }
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new FormatException("Onverwacht antwoord van CMD_API_SHOW_ALL_USERS.");

        var result = new List<string>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(entry.GetString()))
                throw new FormatException("DirectAdmin retourneerde een ongeldige gebruikersnaam.");
            result.Add(entry.GetString()!);
        }
        return result;
    }
}
