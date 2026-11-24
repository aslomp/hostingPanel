using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Adapters;

namespace HostingPanel.Api.Services;

public sealed record DomainListResult(int Status, IReadOnlyList<string>? Domains, string? Error)
{
    public IReadOnlyDictionary<string, string>? Provisioning { get; init; }
}

public sealed record DomainCreatePreview(string Domain, bool Php = true, bool Ssl = true, bool Cgi = false);

public sealed record DomainMutationRequest(string Domain, string Action, string Confirmation,
    bool Php = true, bool Ssl = true, bool Cgi = false);

public sealed record DomainMutationResult(int Status, string? Error);

public sealed record DomainDetailResult(int Status,
    IReadOnlyDictionary<string,string>? Settings, string? Error);

public sealed record DomainSettingRequest(string Setting, string Value);
