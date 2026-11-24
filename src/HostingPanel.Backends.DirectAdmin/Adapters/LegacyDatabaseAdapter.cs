using System.Net;
using System.Text.Json;
using HostingPanel.Api.Adapters;

namespace HostingPanel.Api.Adapters;

public static class LegacyDatabaseAdapter
{
    public static bool ValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 64 &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    public static IReadOnlyList<string> ParseNames(string response) =>
        LegacyAccountListParser.Parse(response).Where(ValidName)
            .ToArray();

    public static void RequireSuccess(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) throw new FormatException("Empty database response.");
        string? error = null;
        if (response.TrimStart().StartsWith('{'))
        {
            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException("Invalid database response.");
            if (doc.RootElement.TryGetProperty("error", out var e)) error = e.ToString();
        }
        else
        {
            foreach(var item in response.Split('&',StringSplitOptions.RemoveEmptyEntries))
            {
                var i = item.IndexOf('=');
                if(i<1) throw new FormatException("Invalid legacy status.");
                if(WebUtility.UrlDecode(item[..i]).Equals("error",StringComparison.OrdinalIgnoreCase))
                    error=WebUtility.UrlDecode(item[(i+1)..]);
            }
        }
        if(error is "1" or "true" or "True") throw new LegacyApiException("Database operation rejected.");
        if(error is not ("0" or "false" or "False")) throw new FormatException("Missing database success status.");
    }

    public static Dictionary<string,string> CreateForm(string database, string username, string password)
    {
        if(!ValidName(database) || !ValidName(username) ||
            string.IsNullOrWhiteSpace(password) || password.Length > 128)
            throw new ArgumentException("Invalid database creation fields.");
        return new() {
            ["action"]="create",
            ["name"]=database,
            ["user"]=username,
            ["passwd"]=password,
            ["passwd2"]=password
        };
    }
    public static Dictionary<string,string> DeleteForm(string database)
    {
        if(!ValidName(database)) throw new ArgumentException("Invalid database.");
        return new(){["action"]="delete",["select0"]=database};
    }

    public static bool Contains(IReadOnlyList<string> before, IReadOnlyList<string> after, string target,
        bool created) => created
        ? !before.Contains(target,StringComparer.OrdinalIgnoreCase) &&
            after.Contains(target,StringComparer.OrdinalIgnoreCase)
        : before.Contains(target,StringComparer.OrdinalIgnoreCase) &&
            !after.Contains(target,StringComparer.OrdinalIgnoreCase);
}
