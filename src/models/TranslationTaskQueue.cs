namespace LiveCaptionsTranslator.models;

internal readonly record struct TranslationEnqueueResult(
    bool Accepted,
    long? EvictedHistoryId = null,
    TranslationRequest? EvictedRequest = null)
{
    public static implicit operator bool(TranslationEnqueueResult result) => result.Accepted;
}

internal sealed class TranslationWorkQueue
{
    public const int FinalQueueCapacity = 16;
    public static readonly TimeSpan StaleWait = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan MinimumDraftInterval = TimeSpan.FromMilliseconds(250);

    private readonly object sync = new();
    private readonly Queue<QueuedRequest> finalUpdates = new();
    private readonly SemaphoreSlim finalAvailable = new(0);
    private readonly SemaphoreSlim draftAvailable = new(0);
    private readonly Func<TranslationRequest, CancellationToken, Task> processor;
    private readonly Action<Exception>? onError;
    private readonly Action<TranslationRequest, TranslationStatus, string>? onDropped;
    private readonly TimeProvider timeProvider;
    private TranslationRequest? latestDraft;
    private CancellationTokenSource? activeFinalCancellation;
    private CancellationTokenSource? activeDraftCancellation;
    private TranslationRequest? activeFinalRequest;
    private bool completed;
    private long lastFinalizedSegmentId;
    private long lastFinalizedRevision;
    private long lastDraftStartedAt;
    private long skippedCount;
    private long requestCount;
    private long lastRequestElapsedMs;

    public TranslationWorkQueue(
        Func<TranslationRequest, CancellationToken, Task> processor,
        Action<Exception>? onError = null,
        Action<TranslationRequest, TranslationStatus, string>? onDropped = null,
        TimeProvider? timeProvider = null)
    {
        this.processor = processor;
        this.onError = onError;
        this.onDropped = onDropped;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public int PendingFinalCount
    {
        get { lock (sync) return finalUpdates.Count; }
    }

    internal int PendingFinalSignalCount => finalAvailable.CurrentCount;

    public int PendingDraftCount
    {
        get { lock (sync) return latestDraft is null ? 0 : 1; }
    }

    public long SkippedCount => Interlocked.Read(ref skippedCount);
    public long RequestCount => Interlocked.Read(ref requestCount);
    public long LastRequestElapsedMs => Interlocked.Read(ref lastRequestElapsedMs);

    public Task[] Start(CancellationToken token) =>
    [
        Task.Run(() => FinalLoopAsync(token), token),
        Task.Run(() => DraftLoopAsync(token), token)
    ];

    public TranslationEnqueueResult Enqueue(TranslationRequest request)
    {
        TranslationRequest? evicted = null;
        lock (sync)
        {
            if (completed)
                return new TranslationEnqueueResult(false);

            if (request.Update.IsFinal)
            {
                long segmentId = request.Update.SegmentId;
                long revision = request.Update.Revision;
                if (segmentId < lastFinalizedSegmentId ||
                    (segmentId == lastFinalizedSegmentId && revision <= lastFinalizedRevision))
                    return new TranslationEnqueueResult(false);

                if (segmentId > lastFinalizedSegmentId)
                {
                    lastFinalizedSegmentId = segmentId;
                    lastFinalizedRevision = revision;
                }
                else
                {
                    lastFinalizedRevision = revision;
                }

                if (finalUpdates.Count >= FinalQueueCapacity)
                {
                    evicted = finalUpdates.Dequeue().Request;
                    if (!finalAvailable.Wait(0))
                        throw new InvalidOperationException("Final queue wake-up signals are out of sync with queued requests.");
                }
                finalUpdates.Enqueue(new QueuedRequest(request, timeProvider.GetTimestamp()));
                activeDraftCancellation?.Cancel();
                finalAvailable.Release();
            }
            else
            {
                if (IsDraftObsoleteLocked(request.Update))
                    return new TranslationEnqueueResult(false);
                latestDraft = request;
                if (draftAvailable.CurrentCount == 0)
                    draftAvailable.Release();
            }
        }

        if (evicted != null)
        {
            Interlocked.Increment(ref skippedCount);
            onDropped?.Invoke(evicted, TranslationStatus.Skipped, "QueueCapacity");
        }
        return new TranslationEnqueueResult(true, evicted?.Update.HistoryId, evicted);
    }

    public bool IsDraftObsolete(CaptionUpdate update)
    {
        lock (sync)
            return IsDraftObsoleteLocked(update);
    }

    private bool IsDraftObsoleteLocked(CaptionUpdate update) =>
        !update.IsFinal && update.SegmentId <= lastFinalizedSegmentId;

    public void CancelPending(TranslationStatus status, string reason)
    {
        List<TranslationRequest> dropped;
        TranslationRequest? activeFinal;
        lock (sync)
        {
            dropped = finalUpdates.Select(item => item.Request).ToList();
            activeFinal = activeFinalRequest;
            finalUpdates.Clear();
            while (finalAvailable.Wait(0)) { }
            latestDraft = null;
            activeFinalCancellation?.Cancel();
            activeDraftCancellation?.Cancel();
        }

        foreach (TranslationRequest request in dropped)
        {
            if (status == TranslationStatus.Skipped)
                Interlocked.Increment(ref skippedCount);
            onDropped?.Invoke(request, status, reason);
        }

        if (activeFinal != null)
            onDropped?.Invoke(activeFinal, status, reason);
    }

    public void Complete()
    {
        lock (sync)
        {
            if (completed)
                return;
            completed = true;
            latestDraft = null;
            activeFinalCancellation?.Cancel();
            activeDraftCancellation?.Cancel();
        }
        finalAvailable.Release();
        draftAvailable.Release();
    }

    private async Task FinalLoopAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                await finalAvailable.WaitAsync(token);
                QueuedRequest? item;
                lock (sync)
                {
                    if (finalUpdates.Count == 0)
                    {
                        if (completed)
                            return;
                        continue;
                    }
                    item = finalUpdates.Dequeue();
                }

                if (timeProvider.GetElapsedTime(item.EnqueuedAt, timeProvider.GetTimestamp()) > StaleWait)
                {
                    Interlocked.Increment(ref skippedCount);
                    onDropped?.Invoke(item.Request, TranslationStatus.Skipped, "QueueWaitExpired");
                    continue;
                }

                await ProcessFinalAsync(item.Request, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessFinalAsync(TranslationRequest request, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (sync)
        {
            activeFinalCancellation = cancellation;
            activeFinalRequest = request;
        }

        long startedAt = timeProvider.GetTimestamp();
        Task processing = ProcessSafelyAsync(request, cancellation.Token);
        try
        {
            while (!processing.IsCompleted)
            {
                if (timeProvider.GetElapsedTime(startedAt, timeProvider.GetTimestamp()) >= StaleWait &&
                    PendingFinalCount > 0)
                {
                    cancellation.Cancel();
                    try { await processing; }
                    catch (OperationCanceledException) { }
                    Interlocked.Increment(ref skippedCount);
                    onDropped?.Invoke(request, TranslationStatus.Skipped, "SupersededAfterTimeout");
                    return;
                }
                await Task.WhenAny(processing, Task.Delay(TimeSpan.FromMilliseconds(100), timeProvider, token));
            }
            await processing;
        }
        finally
        {
            lock (sync)
            {
                if (ReferenceEquals(activeFinalCancellation, cancellation))
                {
                    activeFinalCancellation = null;
                    activeFinalRequest = null;
                }
            }
        }
    }

    private async Task DraftLoopAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                await draftAvailable.WaitAsync(token);
                TranslationRequest? request;
                lock (sync)
                {
                    request = latestDraft;
                    latestDraft = null;
                    if (request is null && completed)
                        return;
                }
                if (request is null || IsDraftObsolete(request.Update))
                    continue;

                long now = timeProvider.GetTimestamp();
                if (lastDraftStartedAt != 0)
                {
                    TimeSpan elapsed = timeProvider.GetElapsedTime(lastDraftStartedAt, now);
                    TimeSpan remaining = MinimumDraftInterval - elapsed;
                    if (remaining > TimeSpan.Zero)
                        await Task.Delay(remaining, timeProvider, token);
                }

                lock (sync)
                {
                    if (latestDraft is not null)
                    {
                        request = latestDraft;
                        latestDraft = null;
                    }
                }
                if (IsDraftObsolete(request.Update))
                    continue;

                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                lock (sync)
                    activeDraftCancellation = cancellation;
                lastDraftStartedAt = timeProvider.GetTimestamp();
                try
                {
                    await ProcessSafelyAsync(request, cancellation.Token);
                }
                finally
                {
                    lock (sync)
                    {
                        if (ReferenceEquals(activeDraftCancellation, cancellation))
                            activeDraftCancellation = null;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessSafelyAsync(TranslationRequest request, CancellationToken token)
    {
        long started = timeProvider.GetTimestamp();
        try
        {
            await processor(request, token);
            Interlocked.Increment(ref requestCount);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
        }
        finally
        {
            Interlocked.Exchange(ref lastRequestElapsedMs,
                (long)timeProvider.GetElapsedTime(started, timeProvider.GetTimestamp()).TotalMilliseconds);
        }
    }

    private sealed record QueuedRequest(TranslationRequest Request, long EnqueuedAt);
}
