using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Adapters;

namespace HostingPanel.Api.Services;

public sealed record AccountOverviewResult(int Status,
    IReadOnlyDictionary<string,string>? Config, string? Package,
    IReadOnlyDictionary<string,string>? PackageSettings, string? Error,
    IReadOnlyDictionary<string,string>? Usage = null, string? UsageError = null);
