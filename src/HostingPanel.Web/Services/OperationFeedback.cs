namespace HostingPanel.Web.Services;

// Presentation formatting only: preserve raw upstream responses and HTTP status.
// A successful HTTP response is not a guarantee that a backend mutation completed.
public static class OperationFeedback
{
    public static string FormatRead((int Status, string Body) response) =>
        $"HTTP {response.Status}\n{response.Body}";

    public static string FormatMutation((int Status, string Body) response) =>
        $"Backend HTTP {response.Status}: {response.Body}";
}
