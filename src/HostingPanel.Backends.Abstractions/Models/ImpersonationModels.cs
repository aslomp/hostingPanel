using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HostingPanel.Api.Adapters;

namespace HostingPanel.Api.Services;

public sealed record ImpersonationCheck(int Status, string? Error);
