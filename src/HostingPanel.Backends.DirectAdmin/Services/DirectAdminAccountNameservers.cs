using System.Net.Http.Headers;
using System.Text.Json;
namespace HostingPanel.Api;

public sealed class DirectAdminAccountNameservers(IHttpClientFactory clients) : IAccountNameservers
{
    public async Task<IResult> ReadAsync(AuthenticationHeaderValue auth, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/session/user-config");
        request.Headers.Authorization = auth;
        try
        {
            using var response = await clients.CreateClient("legacy-directadmin").SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return Results.StatusCode((int)response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = json.RootElement;
            string? Get(string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            var a = Get("ns1"); var b = Get("ns2");
            return Results.Ok(new AccountNameserversView(a, b, a, b, false, false));
        }
        catch (HttpRequestException) { return Results.StatusCode(502); }
        catch (JsonException) { return Results.StatusCode(502); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Results.StatusCode(504); }
    }

    public async Task<IResult> WriteAsync(AuthenticationHeaderValue auth, AccountNameserversUpdate update, CancellationToken ct)
    {
        // DirectAdmin documents changes by the account's administrator/reseller, not self-service.
        var result = await ReadAsync(auth, ct);
        return result is IStatusCodeHttpResult { StatusCode: 200 } ? Results.StatusCode(403) : result;
    }
}
