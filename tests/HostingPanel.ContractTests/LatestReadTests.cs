using HostingPanel.Web.Services;

public static class LatestReadTests
{
    public static void Run() => VerifyAsync().GetAwaiter().GetResult();

    private static async Task VerifyAsync()
    {
        using var read = new LatestRead();
        var first = new TaskCompletionSource<int>();
        var second = new TaskCompletionSource<int>();
        CancellationToken firstToken = default;
        var seen = new List<int>();
        void Fail(Exception ex) => throw new Exception("Unexpected read error", ex);
        var old = read.RunAsync(ct => { firstToken = ct; return first.Task; }, () => true, seen.Add, Fail);
        var current = read.RunAsync(_ => second.Task, () => true, seen.Add, Fail);
        if (!firstToken.IsCancellationRequested || !read.Loading) throw new Exception("Superseded read was not cancelled");
        first.SetResult(1);
        await old;
        if (!read.Loading || seen.Count != 0) throw new Exception("Stale read changed current state");
        second.SetResult(2);
        await current;
        if (read.Loading || !seen.SequenceEqual(new[] { 2 })) throw new Exception("Current read not applied");
        await read.RunAsync(_ => Task.FromResult(3), () => false, seen.Add, Fail);
        if (seen.Count != 1) throw new Exception("Obsolete context accepted");
        var pairFirst = new TaskCompletionSource<int>();
        var contextCurrent = true;
        var secondCalled = false;
        var pair = read.RunPairAsync(_ => pairFirst.Task, _ =>
        {
            secondCalled = true;
            return Task.FromResult(5);
        }, () => contextCurrent, _ => throw new Exception("Obsolete pair accepted"), Fail);
        contextCurrent = false;
        pairFirst.SetResult(4);
        await pair;
        if (secondCalled || read.Loading) throw new Exception("Obsolete pair dispatched its second read");
        await read.RunPairAsync(_ => Task.FromResult(6), _ => Task.FromResult(7), () => true,
            result =>
            {
                if (result.First != 6 || result.Second != 7) throw new Exception("Pair result changed");
            }, Fail);
        var errors = new List<Exception>();
        var jsonError = new System.Text.Json.JsonException("invalid response");
        await read.RunAsync<int>(_ => Task.FromException<int>(jsonError), () => true,
            _ => throw new Exception("Failed read accepted"), errors.Add);
        if (errors.Count != 1 || !ReferenceEquals(errors[0], jsonError) || read.Loading)
            throw new Exception("Current JSON failure was not reported");
        var staleJson = new TaskCompletionSource<int>();
        var staleRead = read.RunAsync(_ => staleJson.Task, () => true,
            _ => throw new Exception("Stale read accepted"), errors.Add);
        await read.RunAsync(_ => Task.FromResult(8), () => true, _ => { }, Fail);
        staleJson.SetException(new System.Text.Json.JsonException("obsolete response"));
        await staleRead;
        if (errors.Count != 1 || read.Loading)
            throw new Exception("Superseded JSON failure leaked into current state");
        await read.RunAsync<int>(_ => Task.FromException<int>(jsonError), () => false,
            _ => throw new Exception("Obsolete context accepted"), errors.Add);
        if (errors.Count != 1 || read.Loading)
            throw new Exception("Obsolete context reported a JSON failure");
        var last = new TaskCompletionSource<int>();
        CancellationToken lastToken = default;
        var pending = read.RunAsync(ct => { lastToken = ct; return last.Task; }, () => true, seen.Add, Fail);
        read.Dispose();
        if (!lastToken.IsCancellationRequested || read.Loading) throw new Exception("Disposal did not cancel");
        last.SetResult(4);
        await pending;
        if (seen.Count != 1) throw new Exception("Disposed component accepted a result");
    }
}
