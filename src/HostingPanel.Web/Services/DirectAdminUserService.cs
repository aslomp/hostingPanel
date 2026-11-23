namespace HostingPanel.Web.Services;

public sealed record DomainInfo(string Name, string BandwidthUsed, string BandwidthLimit,
    string DiskUsage, string LogUsage, string Subdomains, string Suspended,
    string Quota, string Ssl, string Cgi, string Php);

public sealed record UserOverview(
    string Username,
    IReadOnlyDictionary<string, string> Configuration,
    IReadOnlyDictionary<string, string> Usage,
    IReadOnlyList<DomainInfo> Domains);

/// <summary>Read-only DirectAdmin user management. This is deliberately not a mutation API.</summary>
public sealed class DirectAdminUserService(DirectAdminSession session)
{
    public async Task<UserOverview> GetOverviewAsync(string username, CancellationToken cancellationToken = default)
    {
        if (!IsValidUsername(username))
            throw new ArgumentException("Ongeldige gebruikersnaam.", nameof(username));
        var encoded = Uri.EscapeDataString(username);
        var config = await GetFieldsAsync($"CMD_API_SHOW_USER_CONFIG?user={encoded}", cancellationToken);
        var usage = await GetFieldsAsync($"CMD_API_SHOW_USER_USAGE?user={encoded}", cancellationToken);
        var domainFields = await GetFieldsAsync($"CMD_API_SHOW_USER_DOMAINS?user={encoded}", cancellationToken);

        var domains = domainFields
            .Where(kv => kv.Key.Contains('.', StringComparison.Ordinal))
            .Select(kv =>
            {
                var parts = kv.Value.Split(':');
                string At(int index) => index < parts.Length ? parts[index] : "—";
                return new DomainInfo(kv.Key, At(0), At(1), At(2), At(3), At(4),
                    At(5), At(6), At(7), At(8), At(9));
            })
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new UserOverview(username, config, usage, domains);
    }

    public async Task<IReadOnlyList<string>> GetPackagesAsync(CancellationToken cancellationToken = default)
    {
        var (status, body) = await session.GetRawAsync("CMD_API_PACKAGES_USER", cancellationToken);
        CheckHttp(status);
        return LegacyApiResponse.ParseList(body);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetPackageAsync(string package,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(package) || package.Contains('/') || package.Contains('\\')
            || package.Any(char.IsControl))
            throw new ArgumentException("Ongeldige pakketnaam.", nameof(package));
        return await GetFieldsAsync($"CMD_API_PACKAGES_USER?package={Uri.EscapeDataString(package)}",
            cancellationToken);
    }

    private async Task<IReadOnlyDictionary<string, string>> GetFieldsAsync(string endpoint, CancellationToken ct)
    {
        var (status, body) = await session.GetRawAsync(endpoint, ct);
        CheckHttp(status);
        return LegacyApiResponse.ParseFields(body);
    }

    public static bool IsValidUsername(string username) =>
        !string.IsNullOrWhiteSpace(username)
        && username.Length <= 64
        && username.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static void CheckHttp(int status)
    {
        if (status is 401 or 403)
            throw new UnauthorizedAccessException("Je hebt geen toegang tot deze DirectAdmin-gegevens.");
        if (status is < 200 or >= 300)
            throw new HttpRequestException($"DirectAdmin reageerde met HTTP {status}.");
    }
}
