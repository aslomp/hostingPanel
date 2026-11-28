using System.Net.Http.Headers;
using HostingPanel.Api.Adapters;
using System.Net;

namespace HostingPanel.Api.Services;

public sealed record FtpAccountRequest(
    string Domain, string User, string Type, string Password, string? CustomPath, IReadOnlyList<string>? PublicKeys = null);

public sealed record FileAccessResult(int Status, string Body);
