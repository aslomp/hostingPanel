using HostingPanel.Web.Services;
using HostingPanel.Web.Realtime;

public static class DialogMutationTests
{
    public static void Run() => VerifyAsync().GetAwaiter().GetResult();

    private static async Task VerifyAsync()
    {
        using var session = new PanelSession(new TicketChannelRegistry());
        session.SetCredentials("testuser", "test-password");
        using var mutation = new DialogMutation();
        var channel = session.TicketChannel;
        var pending = new TaskCompletionSource();
        var accepted = false;
        var first = mutation.RunAsync(session, channel, async current =>
        {
            await pending.Task;
            accepted = current();
        });
        if (!mutation.Busy) throw new Exception("Mutation not marked busy");
        await mutation.RunAsync(session, channel, _ => throw new Exception("Duplicate submission executed"));
        session.SetCredentials("otheruser", "test-password");
        pending.SetResult();
        await first;
        if (accepted || mutation.Busy) throw new Exception("Obsolete mutation feedback accepted");
        await mutation.RunAsync(session, channel, _ => throw new Exception("Obsolete dialog submitted"));
        await mutation.RunAsync(session, session.TicketChannel, _ => throw new InvalidOperationException("expected"));
        if (mutation.Error != "expected" || mutation.Busy) throw new Exception("Error state not restored");
        await mutation.RunAsync(session, session.TicketChannel, _ =>
            throw new System.Text.Json.JsonException("invalid JSON"));
        if (mutation.Error != "invalid JSON" || mutation.Busy)
            throw new Exception("JSON errors must restore mutation state");

        var staleFailure = new TaskCompletionSource();
        var failing = mutation.RunAsync(session, session.TicketChannel, async _ =>
        {
            await staleFailure.Task;
            throw new System.Text.Json.JsonException("obsolete JSON error");
        });
        session.SetCredentials("thirduser", "test-password");
        staleFailure.SetResult();
        await failing;
        if (mutation.Error is not null || mutation.Busy)
            throw new Exception("Obsolete JSON error leaked across sessions");

        var completion = new TaskCompletionSource();
        var afterDispose = false;
        var last = mutation.RunAsync(session, session.TicketChannel, async current =>
        {
            await completion.Task;
            afterDispose = current();
        });
        mutation.Dispose();
        completion.SetResult();
        await last;
        if (afterDispose || mutation.Busy) throw new Exception("Disposed dialog accepted feedback");
    }
}
