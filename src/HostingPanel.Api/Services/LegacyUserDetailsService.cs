using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using HostingPanel.Api.Adapters;

namespace HostingPanel.Api.Services;

public sealed class LegacyUserDetailsService(IHttpClientFactory clients, IWebHostEnvironment environment)
: IUserDetails
{
    public async Task<UserDetailResult> GetAsync(string username, string kind,
        AuthenticationHeaderValue authentication, CancellationToken cancellationToken)
{
        var legacyCall = kind == "config" ? "CMD_API_SHOW_USER_CONFIG" : "CMD_API_SHOW_USER_USAGE";
        var client = clients.CreateClient("legacy-directadmin");

        async Task<UserDetailResult> Fetch(string command)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                command + "?user=" + Uri.EscapeDataString(username));
            request.Headers.Authorization = authentication;
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new((int)response.StatusCode, null, "ACCESS_DENIED");
            if (response.StatusCode is HttpStatusCode.NotFound)
                return new(404, null, "NOT_FOUND");
            if (!response.IsSuccessStatusCode)
                return new(502, null, "UPSTREAM_ERROR");
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return new(200, LegacyUserDetailsAdapter.Parse(body), null);
        }

        try
        {
            var config = await Fetch("CMD_API_SHOW_USER_CONFIG");
            if (config.Status != 200) return config;
            var result = kind == "config"
                ? LegacyUserDetailsAdapter.ConvertConfig(config.Fields!)
                : new JsonObject();
            if (kind == "usage")
            {
                var usage = await Fetch(legacyCall);
                if (usage.Status != 200) return usage;
                result = LegacyUserDetailsAdapter.ConvertUsage(usage.Fields!, config.Fields!);
            }

            // Swagger 2.0 defines required fields for these objects. We never fabricate
            // unknown features or missing disk/email statistics to make a response look valid.
            var path = Path.Combine(environment.ContentRootPath, "spec", "directadmin.swagger.json");
            using var stream = File.OpenRead(path);
            using var spec = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var schema = kind == "config" ? "web.userConfig" : "web.userUsage";
            var required = spec.RootElement.GetProperty("definitions").GetProperty(schema)
                .GetProperty("required").EnumerateArray().Select(x => x.GetString()!).ToArray();
            var missing = LegacyUserDetailsAdapter.MissingRequired(result, required);
            if (missing.Length > 0)
                return new(501, null, "LEGACY_FIELDS_UNAVAILABLE");
            return new(200, null, null, result);
        }
        catch (LegacyApiException) { return new(403, null, "ACCESS_DENIED"); }
        catch (FormatException) { return new(502, null, "UPSTREAM_ERROR"); }
        catch (JsonException) { return new(502, null, "UPSTREAM_ERROR"); }
        catch (HttpRequestException) { return new(502, null, "UPSTREAM_UNAVAILABLE"); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(504, null, "UPSTREAM_TIMEOUT"); }
    }
}

public sealed record UserDetailResult(int Status,
    IReadOnlyDictionary<string,string>? Fields,
    string? Error,
    JsonObject? Result = null);
