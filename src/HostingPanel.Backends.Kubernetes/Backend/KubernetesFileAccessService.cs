using HostingPanel.Providers.PowerDNS;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HostingPanel.Api.Adapters;
using HostingPanel.Api.Persistence;
using HostingPanel.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace HostingPanel.Api;

public sealed class KubernetesFileAccessService : IFileAccess
{
    public Task<FileAccessResult> ListFtpAsync(string domain, AuthenticationHeaderValue auth, CancellationToken ct)
        => Task.FromResult(new FileAccessResult(501, "KUBERNETES_FTP_NOT_IMPLEMENTED"));
    public Task<FileAccessResult> GetFtpAsync(string domain, string user, AuthenticationHeaderValue auth, CancellationToken ct)
        => Task.FromResult(new FileAccessResult(501, "KUBERNETES_FTP_NOT_IMPLEMENTED"));
    public Task<FileAccessResult> CreateFtpAsync(FtpAccountRequest input, AuthenticationHeaderValue auth, CancellationToken ct)
        => Task.FromResult(new FileAccessResult(501, "KUBERNETES_FTP_NOT_IMPLEMENTED"));
    public Task<FileAccessResult> UpdateFtpAsync(string user, FtpAccountRequest input, AuthenticationHeaderValue auth, CancellationToken ct)
        => Task.FromResult(new FileAccessResult(501, "KUBERNETES_FTP_NOT_IMPLEMENTED"));
    public Task<FileAccessResult> DeleteFtpAsync(string domain, string user, AuthenticationHeaderValue auth, CancellationToken ct)
        => Task.FromResult(new FileAccessResult(501, "KUBERNETES_FTP_NOT_IMPLEMENTED"));
}
