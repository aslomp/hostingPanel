using System.Text.Json;
using System.Text.RegularExpressions;

internal static class SwaggerRouteContractTests
{
    public static void Run()
    {
        var root = FindRepositoryRoot();
        using var swagger = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "spec", "swagger.json")));
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in swagger.RootElement.GetProperty("paths").EnumerateObject())
            foreach (var operation in path.Value.EnumerateObject())
                if (new[] { "get", "post", "put", "patch", "delete", "head", "options" }
                    .Contains(operation.Name, StringComparer.OrdinalIgnoreCase))
                    expected.Add(operation.Name.ToUpperInvariant() + " " + path.Name);

        var actual = new Dictionary<string, string>(StringComparer.Ordinal);
        var directory = Path.Combine(root, "src", "HostingPanel.Api", "Controllers");
        var pattern = new Regex(@"\[Http(Get|Post|Put|Patch|Delete|Head|Options)\(""(/api/[^""]+)""\)\]",
            RegexOptions.CultureInvariant);
        foreach (var file in Directory.GetFiles(directory, "*Controller.cs"))
            foreach (Match match in pattern.Matches(File.ReadAllText(file)))
            {
                var key = match.Groups[1].Value.ToUpperInvariant() + " " + match.Groups[2].Value;
                if (!actual.TryAdd(key, Path.GetFileName(file)))
                    throw new InvalidOperationException(
                        $"Duplicate DirectAdmin route {key} in {actual[key]} and {Path.GetFileName(file)}");
            }

        var missing = expected.Except(actual.Keys).ToArray();
        var extra = actual.Keys.Except(expected).ToArray();
        if (missing.Length != 0 || extra.Length != 0)
            throw new InvalidOperationException($"Swagger route mismatch. Missing: {string.Join(", ", missing)}; Extra: {string.Join(", ", extra)}");
        if (expected.Count != 326)
            throw new InvalidOperationException($"Pinned Swagger baseline changed: {expected.Count} operations");
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "spec", "swagger.json"))
                && Directory.Exists(Path.Combine(current.FullName, "src", "HostingPanel.Api", "Controllers")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Unable to locate HostingPanel repository root.");
    }
}
