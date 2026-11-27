using System.Net.Http.Headers;
using HostingPanel.Api.Services;

namespace HostingPanel.Api;

public sealed record PhpRuntimeSettings(string Image, int CpuRequest = 25, int CpuLimit = 1000,
    int MemoryRequest = 32, int MemoryLimit = 256, int MaxWorkers = 8, int IdleSeconds = 10)
{
    public bool Valid(IReadOnlyCollection<string> images) => images.Contains(Image, StringComparer.Ordinal)
        && CpuRequest >= 1 && CpuRequest <= CpuLimit && CpuLimit <= 64000
        && MemoryRequest >= 16 && MemoryRequest <= MemoryLimit && MemoryLimit <= 65536
        && MaxWorkers is >= 1 and <= 256 && IdleSeconds is >= 1 and <= 3600;
}
public sealed record PhpRuntimeView(int Status, PhpRuntimeSettings? Settings, string[] Images, string? Error = null);
public interface IAccountPhpSettings
{
    Task<PhpRuntimeView> ReadAsync(string username, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<AccountOperationResult> WriteAsync(string username, PhpRuntimeSettings? settings, AuthenticationHeaderValue auth, CancellationToken ct);
    Task<PhpRuntimeView> DefaultsAsync(AuthenticationHeaderValue auth, CancellationToken ct);
}
