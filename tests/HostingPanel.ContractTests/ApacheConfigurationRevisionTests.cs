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
    }

    private static async Task VerifyStorageAsync(ApacheConfigurationRevision revision)
    {
        var root = Directory.CreateTempSubdirectory("apache-candidate-").FullName;
        try
        {
            var store = new ApacheCandidateStore(root);
            var path = await store.RecordAsync(revision, CancellationToken.None);
            Require(File.Exists(path));
            Require(path == await store.RecordAsync(revision, CancellationToken.None));
            Require(Directory.GetFiles(root).Length == 1);
            using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Require(document.RootElement.GetProperty("Id").GetString() == revision.Id);
            Require(document.RootElement.GetProperty("Domains").GetArrayLength() == revision.VirtualHosts.Count);
            Require(!File.Exists(Path.Combine(root, "current")));
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

    private static void Require(bool condition)
    {
        if (!condition) throw new Exception("Apache revision invariant failed.");
    }
}
