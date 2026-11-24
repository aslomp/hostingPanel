using System.Net.Http.Headers;

namespace HostingPanel.Api.Services;

public sealed record HostingPackageDefinition(string Name, IReadOnlyDictionary<string,string> Settings);
public sealed record PackageDetailsResult(int Status, HostingPackageDefinition? Package, string? Error);

public interface IPackageManagement : IAccountPackages
{
    Task<PackageDetailsResult> GetAsync(string kind, string name, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<AccountOperationResult> SaveAsync(string kind, HostingPackageDefinition package, bool create, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<AccountOperationResult> DeleteAsync(string kind, string name, AuthenticationHeaderValue auth, CancellationToken ct);
}

public static class PackageFields
{
    public static readonly string[] Limits = ["quota","bandwidth","vdomains","nsubdomains","domainptr","mysql","nemails","nemailf","nemailml","nemailr","ftp"];
    public static readonly string[] Flags = ["php","ssl","ssh","cgi","aftp","dnscontrol","suspend_at_limit"];
    public static readonly string[] ResellerFlags = ["userssh","serverip"];
    public static bool ValidKind(string kind) => kind is "user" or "reseller";
    public static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 128 &&
        name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
    public static bool ValidSettings(string kind, IReadOnlyDictionary<string,string> settings) =>
        settings.All(x => Limits.Contains(x.Key) ? x.Value == "unlimited" || (long.TryParse(x.Value, out var n) && n >= 0)
            : Flags.Contains(x.Key) || (kind == "reseller" && ResellerFlags.Contains(x.Key)) ? x.Value is "ON" or "OFF"
            : kind == "reseller" && x.Key == "ips" ? int.TryParse(x.Value, out var ips) && ips >= 0
            : kind == "reseller" && x.Key == "dns" && x.Value is "OFF" or "TWO" or "THREE");
}
