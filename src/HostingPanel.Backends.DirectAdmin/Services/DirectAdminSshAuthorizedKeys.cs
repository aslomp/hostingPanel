using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HostingPanel.Api.Adapters;

namespace HostingPanel.Api.Services;

public sealed class DirectAdminSshAuthorizedKeys(IHttpClientFactory clients) : ISshAuthorizedKeys
{
    private const string Endpoint = "CMD_API_SSH_KEYS";
    private static int Map(HttpStatusCode code) => code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound ? (int)code : 502;

    public async Task<SshKeyResult> ListAsync(AuthenticationHeaderValue auth, CancellationToken ct)
    {
        try
        {
            using var client = clients.CreateClient("legacy-directadmin");
            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint + "?json=yes&enabled_users=no");
            request.Headers.Authorization = auth;
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return new(Map(response.StatusCode), "UPSTREAM_ERROR");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("authorized_keys", out var keys) || keys.ValueKind != JsonValueKind.Object)
                return new(502, "INVALID_LEGACY_RESPONSE");
            var result = new List<SshAuthorizedKey>();
            foreach (var item in keys.EnumerateObject())
            {
                var value = item.Value;
                var type = value.GetProperty("type").GetString();
                var data = value.GetProperty("data").GetString();
                var fingerprint = value.GetProperty("fingerprint").GetString();
                if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(data) || fingerprint != item.Name)
                    return new(502, "INVALID_LEGACY_RESPONSE");
                var comment = value.TryGetProperty("comment", out var c) ? c.GetString() : null;
                result.Add(new(item.Name, type + " " + data + (string.IsNullOrEmpty(comment) ? "" : " " + comment)));
            }
            return new(200, Keys: result);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { return new(502, "INVALID_LEGACY_RESPONSE"); }
        catch (HttpRequestException) { return new(502, "UPSTREAM_UNAVAILABLE"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(504, "UPSTREAM_TIMEOUT"); }
    }

    private static string? Material(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl)) return null;
        var parts = key.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] is not ("ssh-ed25519" or "ssh-rsa" or "ecdsa-sha2-nistp256" or "ecdsa-sha2-nistp384" or "ecdsa-sha2-nistp521")) return null;
        try
        {
            var bytes = Convert.FromBase64String(parts[1]);
            var type = System.Text.Encoding.ASCII.GetBytes(parts[0]);
            if (bytes.Length <= 4 + type.Length || System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes) != type.Length || !bytes.AsSpan(4, type.Length).SequenceEqual(type)) return null;
            return parts[0] + " " + Convert.ToBase64String(bytes);
        }
        catch (FormatException) { return null; }
    }

    public async Task<SshKeyResult> AuthorizeAsync(string key, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        var material = Material(key);
        if (material is null) return new(400, "INVALID_SSH_KEY");
        var before = await ListAsync(auth, ct);
        if (before.Status != 200) return before;
        if (before.Keys!.Any(x => Material(x.PublicKey) == material)) return new(200);
        return await MutateAsync(new() { ["action"] = "authorize", ["type"] = "paste", ["text"] = key.Trim() },
            keys => keys.Any(x => Material(x.PublicKey) == material), auth, ct);
    }

    public async Task<SshKeyResult> RevokeAsync(string fingerprint, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fingerprint) || fingerprint.Any(char.IsControl)) return new(400, "INVALID_FINGERPRINT");
        var before = await ListAsync(auth, ct);
        if (before.Status != 200) return before;
        if (!before.Keys!.Any(x => x.Fingerprint == fingerprint)) return new(404, "SSH_KEY_NOT_FOUND");
        return await MutateAsync(new() { ["action"] = "delete", ["type"] = "authorized_keys", ["select0"] = fingerprint },
            keys => keys.All(x => x.Fingerprint != fingerprint), auth, ct);
    }

    private async Task<SshKeyResult> MutateAsync(Dictionary<string,string> fields, Func<IReadOnlyList<SshAuthorizedKey>,bool> verify, AuthenticationHeaderValue auth, CancellationToken ct)
    {
        try
        {
            using var client = clients.CreateClient("legacy-directadmin");
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = auth;
            request.Content = new FormUrlEncodedContent(fields);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return new(Map(response.StatusCode), "UPSTREAM_ERROR");
            LegacyDomainsAdapter.AssertMutationSucceeded(await response.Content.ReadAsStringAsync(ct));
            var observed = await ListAsync(auth, ct);
            return observed.Status == 200 && verify(observed.Keys!) ? new(200) : new(502, "SSH_KEY_MUTATION_UNVERIFIED");
        }
        catch (LegacyApiException) { return new(422, "DIRECTADMIN_REJECTED_OPERATION"); }
        catch (Exception ex) when (ex is FormatException or JsonException) { return new(502, "INVALID_LEGACY_RESPONSE"); }
        catch (HttpRequestException) { return new(502, "UPSTREAM_UNAVAILABLE"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(504, "UPSTREAM_TIMEOUT"); }
    }
}
