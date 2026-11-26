using System.Text.Json;
using System.Text.Json.Nodes;
using HostingPanel.Api;

internal static class ListenerSetOptOutTests
{
    public static void Run()
    {
        const string a = "hp-tls-00000000000000000000000000000001";
        const string b = "hp-tls-00000000000000000000000000000002";
        const string key = "cert-manager.io/ignore-tls-listeners";
        var ignored = ListenerSetReconciler.IgnoredListeners([b, a]);
        Require(ignored == a + "," + b);
        var original = new JsonArray(new JsonObject { ["name"] = a });
        var next = new JsonArray(new JsonObject { ["name"] = a }, new JsonObject { ["name"] = b });
        var existing = JsonNode.Parse("""{"metadata":{"resourceVersion":"42"}}""")!.AsObject();
        var patch = JsonSerializer.SerializeToNode(ListenerSetReconciler.BuildUpdatePatch(existing, original, original, a))!.AsArray();
        Require(patch.Count == 2 && patch[0]!["op"]!.GetValue<string>() == "test");
        Require(patch[1]!["path"]!.GetValue<string>() == "/metadata/annotations");
        Require(patch[1]!["value"]![key]!.GetValue<string>() == a);
        existing["metadata"]!["annotations"] = new JsonObject { ["other"] = "preserved", [key] = a };
        Require(ListenerSetReconciler.BuildUpdatePatch(existing, original, original, a).Length == 0);
        patch = JsonSerializer.SerializeToNode(ListenerSetReconciler.BuildUpdatePatch(existing, original, next, ignored))!.AsArray();
        Require(patch.Count == 3);
        Require(patch[1]!["path"]!.GetValue<string>() == "/spec/listeners");
        Require(patch[2]!["path"]!.GetValue<string>() == "/metadata/annotations/cert-manager.io~1ignore-tls-listeners");
        Require(patch[2]!["value"]!.GetValue<string>() == ignored);
        existing["metadata"]!["annotations"]![key] = ignored;
        patch = JsonSerializer.SerializeToNode(ListenerSetReconciler.BuildUpdatePatch(existing, next, original, a))!.AsArray();
        Require(patch.Count == 3 && patch[2]!["value"]!.GetValue<string>() == a);
        Require(existing["metadata"]!["annotations"]!["other"]!.GetValue<string>() == "preserved");
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new Exception("ListenerSet shim exclusion contract failed.");
    }
}
