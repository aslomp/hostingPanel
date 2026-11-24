using Microsoft.AspNetCore.Hosting;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using HostingPanel.Api.Adapters;

namespace HostingPanel.Api.Services;

public sealed record UserDetailResult(int Status,
    IReadOnlyDictionary<string,string>? Fields,
    string? Error,
    JsonObject? Result = null);
