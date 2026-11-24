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

/// <summary>DirectAdmin handles user identity, account scope and database ownership.</summary>
public sealed class DatabaseManagementService(IHttpClientFactory clients)
: IDatabases
{
    private async Task<(int Status,string Body)> SendAsync(
        HttpMethod method, string path, AuthenticationHeaderValue auth,
        HttpContent? content, CancellationToken ct)
    : IDatabases
{
        using var request=new HttpRequestMessage(method,path){Content=content};
        request.Headers.Authorization=auth;
        using var response=await clients.CreateClient("legacy-directadmin").SendAsync(request,ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return ((int)response.StatusCode,"ACCESS_DENIED");
        if (response.StatusCode==HttpStatusCode.NotFound) return (404,"NOT_FOUND");
        if (!response.IsSuccessStatusCode) return (502,"UPSTREAM_ERROR");
        return (200,await response.Content.ReadAsStringAsync(ct));
    }

    public async Task<DatabaseInventory> ListAsync(AuthenticationHeaderValue auth,CancellationToken ct)
    {
        try
        {
            var result=await SendAsync(HttpMethod.Get,"CMD_API_DATABASES?json=yes",auth,null,ct);
            if(result.Status!=200) return new(result.Status,null,result.Body);
            return new(200,LegacyDatabaseAdapter.ParseNames(result.Body),null);
        }
        catch(LegacyApiException){return new(403,null,"ACCESS_DENIED");}
        catch(FormatException){return new(502,null,"INVALID_LEGACY_RESPONSE");}
        catch(JsonException){return new(502,null,"INVALID_LEGACY_RESPONSE");}
        catch(HttpRequestException){return new(502,null,"UPSTREAM_UNAVAILABLE");}
        catch(TaskCanceledException) when(!ct.IsCancellationRequested){return new(504,null,"UPSTREAM_TIMEOUT");}
    }

    public async Task<DatabaseMembers> UsersAsync(string database,
        AuthenticationHeaderValue auth,CancellationToken ct)
    {
        if(!LegacyDatabaseAdapter.ValidName(database))
            return new(400,null,"INVALID_DATABASE");
        try
        {
            // DirectAdmin documents CMD_API_DB_USER?db=... as the per-database user list.
            var result=await SendAsync(HttpMethod.Get,"CMD_API_DB_USER?db="+
                Uri.EscapeDataString(database)+"&json=yes",auth,null,ct);
            if(result.Status!=200) return new(result.Status,null,result.Body);
            return new(200,LegacyDatabaseAdapter.ParseNames(result.Body),null);
        }
        catch(LegacyApiException){return new(403,null,"ACCESS_DENIED");}
        catch(FormatException){return new(502,null,"INVALID_LEGACY_RESPONSE");}
        catch(JsonException){return new(502,null,"INVALID_LEGACY_RESPONSE");}
        catch(HttpRequestException){return new(502,null,"UPSTREAM_UNAVAILABLE");}
        catch(TaskCanceledException) when(!ct.IsCancellationRequested){return new(504,null,"UPSTREAM_TIMEOUT");}
    }

    public async Task<DatabaseMutation> CreateAsync(DatabaseCreateRequest input,
        AuthenticationHeaderValue auth,CancellationToken ct)
    {
        if (!LegacyDatabaseAdapter.ValidName(input.Database) ||
            !LegacyDatabaseAdapter.ValidName(input.DbUser) ||
            string.IsNullOrWhiteSpace(input.Password) || input.Password.Length > 128)
            return new(400,"INVALID_DATABASE_REQUEST");

        var before=await ListAsync(auth,ct);
        if(before.Databases is null) return new(before.Status,before.Error);
        // The legacy DirectAdmin create form applies the account prefix itself.
        // The user-supplied suffix need not already exist in the preflight list.
        try
        {
            using var content=new FormUrlEncodedContent(
                LegacyDatabaseAdapter.CreateForm(input.Database,input.DbUser,input.Password));
            var result=await SendAsync(HttpMethod.Post,"CMD_API_DATABASES",auth,content,ct);
            if(result.Status!=200) return new(result.Status,result.Body);
            LegacyDatabaseAdapter.RequireSuccess(result.Body);
            var after=await ListAsync(auth,ct);
            if(after.Databases is null) return new(502,"CHANGE_UNVERIFIED");
            // Account-prefix handling differs by DirectAdmin settings. We refuse
            // to claim that an exact, unknown database name was created.
            var newlyCreated=after.Databases.Except(before.Databases,StringComparer.OrdinalIgnoreCase).ToArray();
            return newlyCreated.Length == 1
                ? new(200,null) : new(409,"CHANGE_NOT_OBSERVED");
        }
        catch(LegacyApiException){return new(422,"DIRECTADMIN_REJECTED_OPERATION");}
        catch(ArgumentException){return new(400,"INVALID_DATABASE_REQUEST");}
        catch(FormatException){return new(502,"INVALID_LEGACY_RESPONSE");}
        catch(JsonException){return new(502,"INVALID_LEGACY_RESPONSE");}
        catch(HttpRequestException){return new(502,"UPSTREAM_UNAVAILABLE");}
        catch(TaskCanceledException) when(!ct.IsCancellationRequested){return new(504,"UPSTREAM_TIMEOUT");}
    }

    public async Task<DatabaseMutation> DeleteAsync(string database,string confirmation,
        AuthenticationHeaderValue auth,CancellationToken ct)
    {
        if (!LegacyDatabaseAdapter.ValidName(database) ||
            !string.Equals(database,confirmation,StringComparison.Ordinal))
            return new(400,"CONFIRMATION_REQUIRED");
        var before=await ListAsync(auth,ct);
        if(before.Databases is null) return new(before.Status,before.Error);
        if(!before.Databases.Contains(database,StringComparer.OrdinalIgnoreCase))
            return new(404,"DATABASE_NOT_FOUND");
        try
        {
            using var content=new FormUrlEncodedContent(LegacyDatabaseAdapter.DeleteForm(database));
            var result=await SendAsync(HttpMethod.Post,"CMD_API_DATABASES",auth,content,ct);
            if(result.Status!=200) return new(result.Status,result.Body);
            LegacyDatabaseAdapter.RequireSuccess(result.Body);
            var after=await ListAsync(auth,ct);
            if(after.Databases is null) return new(502,"CHANGE_UNVERIFIED");
            return LegacyDatabaseAdapter.Contains(before.Databases,after.Databases,database,false)
                ? new(200,null) : new(409,"CHANGE_NOT_OBSERVED");
        }
        catch(LegacyApiException){return new(422,"DIRECTADMIN_REJECTED_OPERATION");}
        catch(FormatException){return new(502,"INVALID_LEGACY_RESPONSE");}
        catch(JsonException){return new(502,"INVALID_LEGACY_RESPONSE");}
        catch(HttpRequestException){return new(502,"UPSTREAM_UNAVAILABLE");}
        catch(TaskCanceledException) when(!ct.IsCancellationRequested){return new(504,"UPSTREAM_TIMEOUT");}
    }
}
