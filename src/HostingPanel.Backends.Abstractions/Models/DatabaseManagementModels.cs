using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Adapters;

namespace HostingPanel.Api.Services;

public sealed record DatabaseInventory(int Status, IReadOnlyList<string>? Databases, string? Error);

public sealed record DatabaseMembers(int Status, IReadOnlyList<string>? Users, string? Error);

public sealed record DatabaseMutation(int Status, string? Error);

public sealed record DatabaseCreateRequest(string Database, string DbUser, string Password);

public sealed record DatabaseDeleteRequest(string Confirmation);
