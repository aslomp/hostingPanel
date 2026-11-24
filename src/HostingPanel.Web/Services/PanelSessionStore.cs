using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
namespace HostingPanel.Web.Services;
public sealed class PanelSessionStore : IDisposable
{
    public const string SessionClaim = "hostingpanel_session";
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 10000 });
    public sealed record Session(string Username, string Password);
    public string Create(string username, string password, DateTimeOffset expires)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _cache.Set(id, new Session(username, password), new MemoryCacheEntryOptions { AbsoluteExpiration = expires, Size = 1 });
        return id;
    }
    public Session? Get(string? id) => id is null ? null : _cache.Get<Session>(id);
    public void Remove(string? id) { if (id is not null) _cache.Remove(id); }
    public void Dispose() => _cache.Dispose();
}
