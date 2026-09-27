namespace LiveCaptionsTranslator.utils;

internal sealed class RequestGeneration
{
    private readonly object sync = new();
    private CancellationTokenSource? currentCancellation;
    private long generation;

    public RequestLease Begin()
    {
        CancellationTokenSource? previous;
        RequestLease request;
        lock (sync)
        {
            previous = currentCancellation;
            currentCancellation = new CancellationTokenSource();
            request = new RequestLease(++generation, currentCancellation.Token);
        }
        previous?.Cancel();
        previous?.Dispose();
        return request;
    }

    public bool IsCurrent(RequestLease request)
    {
        lock (sync)
            return request.Generation == generation && !request.Token.IsCancellationRequested;
    }

    public void CancelCurrent()
    {
        CancellationTokenSource? previous;
        lock (sync)
        {
            generation++;
            previous = currentCancellation;
            currentCancellation = null;
        }
        previous?.Cancel();
        previous?.Dispose();
    }
}

internal readonly record struct RequestLease(long Generation, CancellationToken Token);
