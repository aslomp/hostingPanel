using HostingPanel.Providers.PowerDNS;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HostingPanel.Api.Adapters;
using HostingPanel.Api.Persistence;
using HostingPanel.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace HostingPanel.Api;

internal static class KubernetesInputValidation
{
    internal static bool ValidUser(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 64
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    internal static bool ValidDomain(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 253
        && Uri.CheckHostName(name) == UriHostNameType.Dns;
    internal static bool ValidDb(string? name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 128
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
}
