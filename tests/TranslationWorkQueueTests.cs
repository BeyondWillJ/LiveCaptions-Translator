using System.Collections.Concurrent;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.Tests;

public class TranslationWorkQueueTests
{
    [Fact]
    public async Task DraftQueue_KeepsOnlyNewestPendingRevision()
    {
        var processed = new ConcurrentQueue<long>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new TranslationWorkQueue((request, _) =>
        {
            processed.Enqueue(request.Update.Revision);
            completed.TrySetResult();
            return Task.CompletedTask;
        });

        queue.Enqueue(Request(segment: 1, revision: 1, isFinal: false));
        queue.Enqueue(Request(segment: 1, revision: 2, isFinal: false));
        queue.Enqueue(Request(segment: 1, revision: 3, isFinal: false));
        using var lifetime = new CancellationTokenSource();
        Task[] workers = queue.Start(lifetime.Token);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await StopAsync(queue, lifetime, workers);

        Assert.Equal(new long[] { 3 }, processed);
    }

    [Fact]
    public async Task FinalQueue_IsOrderedAndRejectsDuplicateSegment()
    {
        var processed = new ConcurrentQueue<long>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new TranslationWorkQueue((request, _) =>
        {
            processed.Enqueue(request.Update.SegmentId);
            if (processed.Count == 2)
                completed.TrySetResult();
            return Task.CompletedTask;
        });

        Assert.True(queue.Enqueue(Request(segment: 1, revision: 2, isFinal: true)));
        Assert.True(queue.Enqueue(Request(segment: 2, revision: 4, isFinal: true)));
        Assert.False(queue.Enqueue(Request(segment: 1, revision: 2, isFinal: true)));
        using var lifetime = new CancellationTokenSource();
        Task[] workers = queue.Start(lifetime.Token);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await StopAsync(queue, lifetime, workers);

        Assert.Equal(new long[] { 1, 2 }, processed);
    }

    [Fact]
    public async Task NewDraft_LeavesActiveRequestAloneAndKeepsNewestPendingRevision()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseActive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processed = new ConcurrentQueue<long>();
        var replacementCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new TranslationWorkQueue(async (request, token) =>
        {
            processed.Enqueue(request.Update.Revision);
            if (request.Update.Revision == 1)
            {
                started.TrySetResult();
                using CancellationTokenRegistration registration = token.Register(
                    () => cancelled.TrySetResult());
                await releaseActive.Task.WaitAsync(token);
            }
            else
            {
                replacementCompleted.TrySetResult();
            }
        });

        using var lifetime = new CancellationTokenSource();
        Task[] workers = queue.Start(lifetime.Token);
        queue.Enqueue(Request(segment: 1, revision: 1, isFinal: false));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        queue.Enqueue(Request(segment: 1, revision: 2, isFinal: false));
        queue.Enqueue(Request(segment: 1, revision: 3, isFinal: false));
        await Task.Delay(50);
        Assert.False(cancelled.Task.IsCompleted);
        releaseActive.TrySetResult();
        await Task.WhenAll(
            replacementCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        await StopAsync(queue, lifetime, workers);

        Assert.Equal(new long[] { 1, 3 }, processed);
        Assert.False(cancelled.Task.IsCompleted);
    }

    [Fact]
    public async Task WorkerException_IsReportedWithoutStoppingFollowingFinals()
    {
        var errorReported = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new TranslationWorkQueue((request, _) =>
        {
            if (request.Update.SegmentId == 1)
                throw new InvalidOperationException("simulated failure");
            secondCompleted.TrySetResult();
            return Task.CompletedTask;
        }, ex => errorReported.TrySetResult(ex));

        queue.Enqueue(Request(segment: 1, revision: 1, isFinal: true));
        queue.Enqueue(Request(segment: 2, revision: 1, isFinal: true));
        using var lifetime = new CancellationTokenSource();
        Task[] workers = queue.Start(lifetime.Token);
        Exception error = await errorReported.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await secondCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await StopAsync(queue, lifetime, workers);

        Assert.Equal("simulated failure", error.Message);
        Assert.True(queue.IsDraftObsolete(Request(1, 99, false).Update));
    }

    [Fact]
    public async Task FinalQueue_IsBoundedAndReturnsEvictedHistoryRows()
    {
        var processed = new ConcurrentQueue<long>();
        var dropped = new ConcurrentQueue<(long HistoryId, TranslationStatus Status)>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new TranslationWorkQueue((request, _) =>
        {
            processed.Enqueue(request.Update.HistoryId);
            if (processed.Count == TranslationWorkQueue.FinalQueueCapacity)
                completed.TrySetResult();
            return Task.CompletedTask;
        }, onDropped: (request, status, _) => dropped.Enqueue((request.Update.HistoryId, status)));

        TranslationEnqueueResult lastResult = default;
        for (int index = 1; index <= 20; index++)
            lastResult = queue.Enqueue(Request(index, 1, true));

        Assert.Equal(TranslationWorkQueue.FinalQueueCapacity, queue.PendingFinalCount);
        Assert.Equal(new long[] { 101, 201, 301, 401 }, dropped.Select(item => item.HistoryId));
        Assert.All(dropped, item => Assert.Equal(TranslationStatus.Skipped, item.Status));
        Assert.Equal(401L, lastResult.EvictedHistoryId);

        using var lifetime = new CancellationTokenSource();
        Task[] workers = queue.Start(lifetime.Token);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await StopAsync(queue, lifetime, workers);
        Assert.Equal(16, processed.Count);
    }

    [Fact]
    public async Task FinalQueueSignals_StayBoundedAcrossOverflowAndPause()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new TranslationWorkQueue(async (request, token) =>
        {
            if (request.Update.SegmentId != 1)
                return;
            firstStarted.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally
            {
                if (token.IsCancellationRequested)
                    firstCancelled.TrySetResult();
            }
        });
        using var lifetime = new CancellationTokenSource();
        Task[] workers = queue.Start(lifetime.Token);
        queue.Enqueue(Request(1, 1, true));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        for (int segmentId = 2; segmentId <= 100; segmentId++)
            queue.Enqueue(Request(segmentId, 1, true));

        Assert.Equal(TranslationWorkQueue.FinalQueueCapacity, queue.PendingFinalCount);
        Assert.Equal(queue.PendingFinalCount, queue.PendingFinalSignalCount);

        queue.CancelPending(TranslationStatus.SourceOnly, "Paused");
        Assert.Equal(0, queue.PendingFinalCount);
        Assert.Equal(0, queue.PendingFinalSignalCount);
        await firstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await StopAsync(queue, lifetime, workers);
    }

    [Fact]
    public async Task QueueOverflow_PreservesEverySourceAndMarksEvictedRowsSkipped()
    {
        await SQLiteHistoryLogger.ClearHistory();
        Guid sessionId = Guid.NewGuid();
        var requests = new List<TranslationRequest>();
        var settings = new TranslationSettingsSnapshot(
            "OpenAI", "zh-CN", "Translate to {0}", false, false,
            new TranslateAPIConfig(), []);

        for (int segmentId = 1; segmentId <= 20; segmentId++)
        {
            string source = $"source-{segmentId}";
            long historyId = await SQLiteHistoryLogger.SaveSourceAsync(
                sessionId, 1, segmentId, 0, source, "zh-CN", "OpenAI");
            requests.Add(new TranslationRequest(
                new CaptionUpdate(sessionId, 1, segmentId, 0, source, true,
                    TimeProvider.System.GetTimestamp(), DateTimeOffset.UtcNow, historyId),
                settings));
        }

        var allCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processorErrors = new ConcurrentQueue<Exception>();
        int completedCount = 0;
        var queue = new TranslationWorkQueue(async (request, token) =>
        {
            CaptionUpdate update = request.Update;
            try
            {
                bool completed = await SQLiteHistoryLogger.CompleteTranslationAsync(
                    update.HistoryId, update.SessionId, update.SegmentId, update.Revision,
                    new TranslationOutcome(TranslationStatus.Succeeded,
                        $"translation-{update.SegmentId}", "", "", 1), token);
                if (!completed)
                    throw new InvalidOperationException($"History row {update.HistoryId} was not pending.");
            }
            finally
            {
                if (Interlocked.Increment(ref completedCount) == 16)
                    allCompleted.TrySetResult();
            }
        }, onError: processorErrors.Enqueue, onDropped: (request, status, reason) =>
        {
            CaptionUpdate update = request.Update;
            bool updated = SQLiteHistoryLogger.MarkPendingAsync(
                update.HistoryId, update.SessionId, update.SegmentId, update.Revision,
                status, reason, "The pending queue is full.").GetAwaiter().GetResult();
            Assert.True(updated, $"History row {update.HistoryId} should remain pending when evicted.");
        });

        foreach (TranslationRequest request in requests)
            Assert.True(queue.Enqueue(request));

        Assert.Equal(16, queue.PendingFinalCount);
        using var lifetime = new CancellationTokenSource();
        Task[] workers = queue.Start(lifetime.Token);
        await allCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await StopAsync(queue, lifetime, workers);

        Assert.Empty(processorErrors);
        Assert.Equal(16, completedCount);
        var page = await SQLiteHistoryLogger.LoadHistoryPageAsync(
            page: 1, maxRow: 100, searchText: string.Empty, statusFilter: string.Empty);
        Assert.Equal(20, page.TotalCount);

        foreach (int segmentId in Enumerable.Range(1, 20))
        {
            var row = Assert.Single(page.Rows, item => item.SourceText == $"source-{segmentId}");
            if (segmentId <= 4)
            {
                Assert.Equal("Skipped", row.Status);
                Assert.Equal("QueueCapacity", row.ErrorCode);
                Assert.Empty(row.TranslatedText);
            }
            else
            {
                Assert.Equal("Succeeded", row.Status);
                Assert.Equal($"translation-{segmentId}", row.TranslatedText);
            }
        }
    }

    [Fact]
    public async Task RequestMetrics_RecordCountAndMonotonicElapsedTime()
    {
        var time = new ManualTimeProvider();
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new TranslationWorkQueue((_, _) =>
        {
            time.Advance(TimeSpan.FromMilliseconds(47));
            processed.TrySetResult();
            return Task.CompletedTask;
        }, timeProvider: time);

        using var lifetime = new CancellationTokenSource();
        Task[] workers = queue.Start(lifetime.Token);
        queue.Enqueue(Request(1, 1, true));
        await processed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(SpinWait.SpinUntil(() => queue.RequestCount == 1, TimeSpan.FromSeconds(1)));
        await StopAsync(queue, lifetime, workers);

        Assert.Equal(1, queue.RequestCount);
        Assert.Equal(47, queue.LastRequestElapsedMs);
        Assert.Equal(0, queue.PendingFinalCount);
    }

    [Fact]
    public async Task EightSecondService_IsCancelledAfterFourSecondsWhenANewerFinalIsWaiting()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSkipped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dropped = new ConcurrentQueue<(long HistoryId, TranslationStatus Status)>();
        var queue = new TranslationWorkQueue(async (request, token) =>
        {
            if (request.Update.SegmentId == 1)
            {
                firstStarted.TrySetResult();
                try { await Task.Delay(TimeSpan.FromSeconds(8), token); }
                finally
                {
                    if (token.IsCancellationRequested)
                        firstCancelled.TrySetResult();
                }
            }
        }, onDropped: (request, status, _) =>
        {
            dropped.Enqueue((request.Update.HistoryId, status));
            if (request.Update.SegmentId == 1 && status == TranslationStatus.Skipped)
                firstSkipped.TrySetResult();
        });

        using var lifetime = new CancellationTokenSource();
        Task[] workers = queue.Start(lifetime.Token);
        queue.Enqueue(Request(1, 1, true));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        queue.Enqueue(Request(2, 1, true));
        await firstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(6));
        await firstSkipped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await StopAsync(queue, lifetime, workers);

        Assert.Contains((101L, TranslationStatus.Skipped), dropped);
    }

    [Fact]
    public async Task FinalThatWaitsOverFourSecondsBeforeStart_IsSkipped()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDropped = new TaskCompletionSource<(long HistoryId, TranslationStatus Status, string Reason)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new TranslationWorkQueue(async (request, token) =>
        {
            if (request.Update.SegmentId == 1)
            {
                firstStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
        }, onDropped: (request, status, reason) =>
        {
            if (request.Update.SegmentId == 2)
                secondDropped.TrySetResult((request.Update.HistoryId, status, reason));
        });

        using var lifetime = new CancellationTokenSource();
        Task[] workers = queue.Start(lifetime.Token);
        queue.Enqueue(Request(1, 1, true));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        queue.Enqueue(Request(2, 1, true));

        var dropped = await secondDropped.Task.WaitAsync(TimeSpan.FromSeconds(7));
        await StopAsync(queue, lifetime, workers);

        Assert.Equal(201L, dropped.HistoryId);
        Assert.Equal(TranslationStatus.Skipped, dropped.Status);
        Assert.Equal("QueueWaitExpired", dropped.Reason);
    }

    [Fact]
    public async Task Pause_CancelsActiveWorkDropsPendingAndAllowsOnlyNewWorkAfterResume()
    {
        var activeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activeCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumedCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processed = new ConcurrentQueue<long>();
        var dropped = new ConcurrentQueue<(long HistoryId, TranslationStatus Status)>();
        var queue = new TranslationWorkQueue(async (request, token) =>
        {
            processed.Enqueue(request.Update.SegmentId);
            if (request.Update.SegmentId == 1)
            {
                activeStarted.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally
                {
                    if (token.IsCancellationRequested)
                        activeCancelled.TrySetResult();
                }
            }
            else if (request.Update.SegmentId == 3)
                resumedCompleted.TrySetResult();
        }, onDropped: (request, status, _) => dropped.Enqueue((request.Update.HistoryId, status)));

        using var lifetime = new CancellationTokenSource();
        Task[] workers = queue.Start(lifetime.Token);
        queue.Enqueue(Request(1, 1, true));
        await activeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        queue.Enqueue(Request(2, 1, true));

        queue.CancelPending(TranslationStatus.SourceOnly, "Paused");
        await activeCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains((201L, TranslationStatus.SourceOnly), dropped);

        queue.Enqueue(Request(3, 1, true));
        await resumedCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await StopAsync(queue, lifetime, workers);

        Assert.Equal(new long[] { 1, 3 }, processed);
        Assert.Contains((101L, TranslationStatus.SourceOnly), dropped);
    }

    private static TranslationRequest Request(long segment, long revision, bool isFinal) => new(
        new CaptionUpdate(Guid.NewGuid(), 1, segment, revision, $"text-{segment}-{revision}", isFinal,
            TimeProvider.System.GetTimestamp(), DateTimeOffset.UtcNow, segment * 100 + revision),
        new TranslationSettingsSnapshot(
            "Google", "zh-CN", "Translate to {0}", false, false, new TranslateAPIConfig(), []));

    private static async Task StopAsync(
        TranslationWorkQueue queue, CancellationTokenSource lifetime, Task[] workers)
    {
        lifetime.Cancel();
        queue.Complete();
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;

        public void Advance(TimeSpan duration) => ticks += duration.Ticks;
    }
}
