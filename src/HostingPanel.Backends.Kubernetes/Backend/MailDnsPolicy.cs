using System.Text.Json;

namespace HostingPanel.Api;

internal sealed record MailDnsPolicy(bool Publish, bool Rotate)
{
    internal const string PublicationKey = "mailAutoPublish";
    internal const string RotationKey = "mailAutoRotate";
    internal static MailDnsPolicy Read(string json)
    {
        using var doc = JsonDocument.Parse(json);
        bool Enabled(string key) => !doc.RootElement.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.False;
        return new(Enabled(PublicationKey), Enabled(RotationKey));
    }
}
