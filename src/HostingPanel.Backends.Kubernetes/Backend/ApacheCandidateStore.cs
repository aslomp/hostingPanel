using System.Text;
using System.Text.Json;

namespace HostingPanel.Api;

/// <summary>Stores candidate metadata and immutable configuration snapshots; activation is separate.</summary>
public sealed class ApacheCandidateStore
{
    private readonly string root;

    public ApacheCandidateStore(string root)
    {
        if (!Path.IsPathFullyQualified(root))
            throw new ArgumentException("An absolute dedicated storage path is required.", nameof(root));
        this.root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (Path.GetPathRoot(this.root) == this.root)
            throw new ArgumentException("A dedicated storage directory is required.", nameof(root));
    }

    public async Task<string> RecordAsync(ApacheConfigurationRevision revision, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("Dedicated Apache storage is unavailable.");
        for (DirectoryInfo? directory = new(root); directory is not null; directory = directory.Parent)
            if (directory.LinkTarget is not null)
                throw new IOException("Configuration storage must not use symbolic links.");

        var payload = JsonSerializer.Serialize(new
        {
            revision.Id,
            Domains = revision.VirtualHosts.Keys.Order().ToArray()
        });
        var target = Path.Combine(root, revision.Id + ".candidate.json");
        var temporary = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");
        var ownsTemporary = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                ownsTemporary = true;
                await stream.WriteAsync(Encoding.UTF8.GetBytes(payload), ct);
                await stream.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            try { File.Move(temporary, target, overwrite: false); }
            catch (IOException) when (File.Exists(target))
            {
                if (new FileInfo(target).LinkTarget is not null ||
                    await File.ReadAllTextAsync(target, ct) != payload)
                    throw new IOException("Existing candidate metadata does not match.");
            }
            return target;
        }
        finally
        {
            if (ownsTemporary) File.Delete(temporary);
        }
    }

    /// <summary>Stages executable files in an immutable revision directory without activating it.</summary>
    public async Task<string> StageAsync(ApacheConfigurationRevision revision, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ct.ThrowIfCancellationRequested();
        EnsureRoot();
        var target = Path.Combine(root, revision.Id);
        if (Directory.Exists(target))
        {
            await VerifyAsync(target, revision, ct);
            return target;
        }

        var staging = Path.Combine(root, ".stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var (id, content) in revision.VirtualHosts)
            {
                ct.ThrowIfCancellationRequested();
                var path = Path.Combine(staging, "site-" + id.ToString("N") + ".conf");
                await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(content), ct);
                await stream.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            try { Directory.Move(staging, target); }
            catch (IOException) when (Directory.Exists(target))
            {
                await VerifyAsync(target, revision, ct);
            }
            return target;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private void EnsureRoot()
    {
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("Dedicated Apache storage is unavailable.");
        for (DirectoryInfo? directory = new(root); directory is not null; directory = directory.Parent)
            if (directory.LinkTarget is not null)
                throw new IOException("Configuration storage must not use symbolic links.");
    }

    private static async Task VerifyAsync(string path, ApacheConfigurationRevision revision, CancellationToken ct)
    {
        if (new DirectoryInfo(path).LinkTarget is not null ||
            Directory.GetFileSystemEntries(path).Length != revision.VirtualHosts.Count)
            throw new IOException("Existing configuration revision does not match.");
        foreach (var (id, content) in revision.VirtualHosts)
        {
            var file = new FileInfo(Path.Combine(path, "site-" + id.ToString("N") + ".conf"));
            if (!file.Exists || file.LinkTarget is not null ||
                await File.ReadAllTextAsync(file.FullName, ct) != content)
                throw new IOException("Existing configuration revision does not match.");
        }
    }
}
