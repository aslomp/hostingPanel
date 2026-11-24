using System.Net.Http.Headers;

namespace HostingPanel.Api;

/// <summary>Manages the desired web-serving configuration for a domain.
/// The provider chooses the web server, routing and runtime infrastructure.
/// Credentials identify the caller; authorization belongs to the provider.</summary>
public interface IWebHosting
{
    Task<WebsiteListResult> ListWebsitesAsync(string? owner, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<WebsiteResult> GetWebsiteAsync(string domain, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<WebsiteMutationResult> CreateWebsiteAsync(WebsiteConfiguration website, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<WebsiteMutationResult> UpdateWebsiteAsync(WebsiteConfiguration website, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<WebsiteMutationResult> DeleteWebsiteAsync(string domain, string confirmation, AuthenticationHeaderValue auth, CancellationToken ct);
}

public sealed record WebsiteConfiguration(string Domain, bool Php = true, bool Tls = true, bool Cgi = false);

/// <summary>Observed runtime availability is not inferred from persisted configuration.</summary>
public enum WebsiteProvisioningState { Unknown, Pending, Ready, Failed }

public sealed record WebsiteInfo(string Domain, IReadOnlyDictionary<string, string> Settings,
    WebsiteProvisioningState State);
public sealed record WebsiteListResult(int Status, IReadOnlyList<string>? Websites, string? Error);
public sealed record WebsiteResult(int Status, WebsiteInfo? Website, string? Error);

/// <summary>200 means the provider confirmed its configuration operation; 202 means desired state accepted.
/// Neither is proof of end-to-end HTTP availability.</summary>
public sealed record WebsiteMutationResult(int Status, string? Error)
{
    public bool Executed => Status == 200 && Error is null;
    public bool ProvisioningPending => Status == 202 && Error is null;
}
