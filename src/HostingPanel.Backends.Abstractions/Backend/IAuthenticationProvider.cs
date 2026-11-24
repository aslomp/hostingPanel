using System.Net.Http.Headers;
namespace HostingPanel.Api;
public interface IAuthenticationProvider
{
    Task<int> AuthenticateSessionAsync(AuthenticationHeaderValue auth, CancellationToken ct) => Task.FromResult(501);
}
