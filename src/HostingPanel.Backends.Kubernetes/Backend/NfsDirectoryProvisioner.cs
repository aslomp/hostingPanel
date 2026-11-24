using System.Diagnostics;

namespace HostingPanel.Api;

/// <summary>
/// Runs against a trusted storage-side directory (or a mount whose provisioner
/// identity is explicitly authorized to set ownership). Never changes existing
/// website ownership or recursively modifies tenant data.
/// </summary>
public sealed class NfsDirectoryProvisioner(IConfiguration configuration)
{
    public async Task EnsureAsync(Guid domainId, PhpAccountIdentity identity, CancellationToken ct)
    {
        var root = configuration["KubernetesHosting:NfsProvisioningPath"];
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("Configure KubernetesHosting:NfsProvisioningPath.");
        if (!OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(root))
            throw new InvalidOperationException("NFS provisioning requires an absolute Linux filesystem path.");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (root == "/" || !Directory.Exists(root))
            throw new InvalidOperationException("The dedicated provisioning root must already exist.");
        // The root and its ancestors are administrator-controlled. Tenants must
        // have traversal only, never permission to create or rename entries here.
        for (DirectoryInfo? dir = new(root); dir is not null; dir = dir.Parent)
        {
            if (dir.LinkTarget is not null)
                throw new InvalidOperationException("Provisioning paths must not contain symbolic links.");
        }
        var rootMode = File.GetUnixFileMode(root);
        if ((rootMode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            throw new InvalidOperationException("Provisioning root must not be group/world writable.");
        var target = Path.Combine(root, domainId.ToString("N"));
        var expected = $"{identity.Uid}:{identity.Gid}:700:directory";
        if (Directory.Exists(target) || File.Exists(target) || new DirectoryInfo(target).LinkTarget is not null)
        {
            await ValidateAsync(target, expected, ct);
            return;
        }

        // Stage privately, set ownership before publishing, and never overwrite
        // a directory created concurrently. The parent is not tenant-writable.
        var staging = Path.Combine(root, ".provision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            await RunAsync("/usr/bin/chmod", ct, "0700", "--", staging);
            await RunAsync("/usr/bin/chown", ct, "--no-dereference", $"{identity.Uid}:{identity.Gid}", "--", staging);
            await ValidateAsync(staging, expected, ct);
            try { Directory.Move(staging, target); }
            catch (IOException) when (Directory.Exists(target))
            {
                await ValidateAsync(target, expected, ct);
            }
            await ValidateAsync(target, expected, ct);
        }
        finally
        {
            // Only remove our empty staging directory; never recurse.
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: false);
        }
    }

    private static async Task ValidateAsync(string path, string expected, CancellationToken ct)
    {
        if (new DirectoryInfo(path).LinkTarget is not null)
            throw new InvalidOperationException("Website directory is a symbolic link.");
        var actual = await RunAsync("/usr/bin/stat", ct, "-c", "%u:%g:%a:%F", "--", path);
        if (!string.Equals(actual.Trim(), expected, StringComparison.Ordinal))
            throw new InvalidOperationException("Website directory has unexpected ownership, permissions or type.");
    }

    private static async Task<string> RunAsync(string executable, CancellationToken ct, params string[] arguments)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var start = new ProcessStartInfo(executable) {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment["LC_ALL"] = "C";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start storage utility.");
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(token);
            var stderr = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Storage provisioning failed: " + error);
            return output;
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}
