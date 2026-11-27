using System.Text.Json;
using HostingPanel.Api.Persistence;

namespace HostingPanel.Api;

internal static class PhpRuntimePolicy
{
    internal const string Key = "phpRuntime";
    internal static Dictionary<string,string> Fields(string? json) =>
        JsonSerializer.Deserialize<Dictionary<string,string>>(json ?? "{}") ?? new();
    internal static string[] Images(KubernetesBackendSettings settings) => settings.PhpImages;
    internal static PhpRuntimeSettings Default(KubernetesBackendSettings settings) => new(settings.PhpImages[0]);
    internal static PhpRuntimeSettings Resolve(KubernetesBackendSettings settings, string? package, string? account)
    {
        var images = Images(settings);
        var value = Default(settings);
        foreach (var json in new[] { package, account })
            if (Fields(json).TryGetValue(Key, out var raw))
            {
                var proposed = JsonSerializer.Deserialize<PhpRuntimeSettings>(raw);
                if (proposed is null || !proposed.Valid(images))
                    throw new InvalidOperationException("Invalid stored account PHP runtime settings.");
                value = proposed;
            }
        return value;
    }
}
