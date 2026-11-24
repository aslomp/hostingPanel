using System.Diagnostics;
using System.Globalization;

namespace HostingPanel.Api;

/// <summary>
/// Uses the existing DirectAdmin /home/{user}/domains/{domain} layout.
/// Existing directories are never chowned, chmodded or moved.
/// Provisioning new directories requires an authorized storage-side mount.
/// </summary>
public sealed class NfsDirectoryProvisioner(IConfiguration configuration)
{
    public async Task<(int Uid, int Gid)> EnsureAsync(
        string username, string domain, PhpAccountIdentity identity, CancellationToken ct)
    {
        if (!ValidPart(username, allowDots: false) || !ValidPart(domain, allowDots: true))
            throw new InvalidOperationException("Invalid DirectAdmin account or domain path.");

        var root = configuration["KubernetesHosting:NfsProvisioningPath"];
        if (string.IsNullOrWhiteSpace(root) || !OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(root))
            throw new InvalidOperationException("Configure an absolute Linux KubernetesHosting:NfsProvisioningPath.");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (root == "/" || !Directory.Exists(root))
            throw new InvalidOperationException("The NFS /home provisioning root must exist.");
        for (DirectoryInfo? ancestor = new(root); ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor.LinkTarget is not null)
                throw new InvalidOperationException("NFS provisioning ancestors must not be symlinks.");
        var mode = File.GetUnixFileMode(root);
        if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            throw new InvalidOperationException("The NFS /home root must not be group/world writable.");

        var home = Path.Combine(root, username);
        await CreateIfMissingAsync(home, identity.Uid, identity.Gid, "0711", ct);
        var (uid, gid) = await OwnershipAsync(home, ct);
        if (uid == 0 || gid == 0)
            throw new InvalidOperationException("Hosted account home must not be owned by root.");

        var domains = Path.Combine(home, "domains");
        var site = Path.Combine(domains, domain);
        var publicHtml = Path.Combine(site, "public_html");
        await CreateIfMissingAsync(domains, uid, gid, "0711", ct);
        await CreateIfMissingAsync(site, uid, gid, "0711", ct);
        await CreateIfMissingAsync(publicHtml, uid, gid, "0755", ct);

        // DirectAdmin may use private_html as a symlink to public_html.
        // It is deliberately not modified or recreated.
        return (uid, gid);
    }

    private static bool ValidPart(string input, bool allowDots) =>
        input.Length is > 0 and <= 253 && input != "." && input != ".."
        && input.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'
            || (allowDots && c == '.'))
        && !input.Contains("..", StringComparison.Ordinal);

    private static async Task CreateIfMissingAsync(
        string path, int uid, int gid, string mode, CancellationToken ct)
    {
        var entry = new DirectoryInfo(path);
        if (entry.LinkTarget is not null || (File.Exists(path) && !Directory.Exists(path)))
            throw new InvalidOperationException("DirectAdmin storage path is not a real directory: " + path);
        if (Directory.Exists(path)) return;

        // Create only the requested component; never recurse or modify old data.
        var parent = Directory.GetParent(path)?.FullName
            ?? throw new InvalidOperationException("Invalid storage path.");
        if (!Directory.Exists(parent) || new DirectoryInfo(parent).LinkTarget is not null)
            throw new InvalidOperationException("Storage parent is missing or a symlink.");
        await RunAsync("/usr/bin/mkdir", ct, "-m", "0700", "--", path);
        await RunAsync("/usr/bin/chown", ct, "--no-dereference",
            uid.ToString(CultureInfo.InvariantCulture) + ":" + gid.ToString(CultureInfo.InvariantCulture),
            "--", path);
        await RunAsync("/usr/bin/chmod", ct, mode, "--", path);
    }

    private static async Task<(int Uid, int Gid)> OwnershipAsync(string path, CancellationToken ct)
    {
        if (new DirectoryInfo(path).LinkTarget is not null || !Directory.Exists(path))
            throw new InvalidOperationException("DirectAdmin account home is not a real directory.");
        var output = await RunAsync("/usr/bin/stat", ct, "-c", "%u:%g", "--", path);
        var parts = output.Trim().Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var uid)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var gid))
            throw new InvalidOperationException("Cannot determine DirectAdmin account ownership.");
        return (uid, gid);
    }

    private static async Task<string> RunAsync(string executable, CancellationToken ct, params string[] arguments)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment["LC_ALL"] = "C";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Cannot start storage utility.");
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Storage provisioning failed: " + error);
            return output;
        }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}
