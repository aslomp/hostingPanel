using HostingPanel.Api;

internal static class ApacheConfigurationRevisionTests
{
    public static void Run()
    {
        var a = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var b = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var input = new Dictionary<Guid, string> { [a] = "site a", [b] = "site b" };
        var first = new ApacheConfigurationRevision(input);
        var reordered = new ApacheConfigurationRevision(input.Reverse());
        Require(first.Id == reordered.Id);
        Require(first.Id.Length == 64);
        input[a] = "changed";
        Require(first.VirtualHosts[a] == "site a");
        Require(first.Id != new ApacheConfigurationRevision(input).Id);
        input.Remove(b);
        Require(first.Id != new ApacheConfigurationRevision(input).Id);
        Require(first.Id != new ApacheConfigurationRevision([]).Id);
        try
        {
            _ = new ApacheConfigurationRevision(new[] { KeyValuePair.Create(a, "one"), KeyValuePair.Create(a, "two") });
            throw new Exception("Duplicate domain accepted.");
        }
        catch (ArgumentException) { }
        VerifyStorageAsync(first).GetAwaiter().GetResult();
        VerifyUnavailableStorageAsync(first).GetAwaiter().GetResult();
    }

    private static async Task VerifyStorageAsync(ApacheConfigurationRevision revision)
    {
        var root = Directory.CreateTempSubdirectory("apache-candidate-").FullName;
        try
        {
            var store = new ApacheCandidateStore(root);
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try
            {
                await store.RecordAsync(revision, cancelled.Token);
                throw new Exception("Cancelled candidate was recorded.");
            }
            catch (OperationCanceledException) { }
            Require(Directory.GetFileSystemEntries(root).Length == 0);

            var path = await store.RecordAsync(revision, CancellationToken.None);
            var concurrent = await Task.WhenAll(
                Enumerable.Range(0, 8).Select(_ => store.RecordAsync(revision, CancellationToken.None)));
            Require(concurrent.All(p => p == path));
            Require(File.Exists(path));
            Require(path == await store.RecordAsync(revision, CancellationToken.None));
            Require(Directory.GetFiles(root).Length == 1);
            using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Require(document.RootElement.GetProperty("Id").GetString() == revision.Id);
            Require(document.RootElement.GetProperty("Domains").GetArrayLength() == revision.VirtualHosts.Count);
            Require(!File.Exists(Path.Combine(root, "current")));
            var empty = new ApacheConfigurationRevision([]);
            var emptyPath = await store.RecordAsync(empty, CancellationToken.None);
            Require(emptyPath != path);
            Require(Directory.GetFiles(root).Length == 2);
            File.Delete(emptyPath);
            await File.WriteAllTextAsync(path, "corrupt");
            try
            {
                await store.RecordAsync(revision, CancellationToken.None);
                throw new Exception("Conflicting candidate accepted.");
            }
            catch (IOException) { }
            Require(await File.ReadAllTextAsync(path) == "corrupt");
            Require(Directory.GetFiles(root).Length == 1);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task VerifyUnavailableStorageAsync(ApacheConfigurationRevision revision)
    {
        var root = Directory.CreateTempSubdirectory("apache-unavailable-").FullName;
        try
        {
            var missing = Path.Combine(root, "missing");
            try
            {
                await new ApacheCandidateStore(missing).RecordAsync(revision, CancellationToken.None);
                throw new Exception("Missing storage was accepted.");
            }
            catch (DirectoryNotFoundException) { }
            Require(!Directory.Exists(missing));

            if (OperatingSystem.IsLinux())
            {
                var real = Directory.CreateDirectory(Path.Combine(root, "real")).FullName;
                var alias = Path.Combine(root, "alias");
                Directory.CreateSymbolicLink(alias, real);
                try
                {
                    await new ApacheCandidateStore(alias).RecordAsync(revision, CancellationToken.None);
                    throw new Exception("Symbolic storage root was accepted.");
                }
                catch (IOException) { }
                Require(Directory.GetFileSystemEntries(real).Length == 0);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new Exception("Apache revision invariant failed.");
    }
}
