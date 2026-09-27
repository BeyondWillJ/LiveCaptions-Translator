using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.Tests;

public class CaptionSegmenterTests
{
    [Fact]
    public void SnapshotWithMultipleSentences_FinalizesEverySentenceInOrder()
    {
        var time = new ManualTimeProvider();
        var segmenter = new CaptionSegmenter(time);

        segmenter.ProcessSnapshot("First sentence. Second sentence.");
        time.Advance(TimeSpan.FromMilliseconds(200));
        IReadOnlyList<CaptionSegmentEvent> events = segmenter.ProcessSnapshot("First sentence. Second sentence.");

        CaptionSegmentEvent[] finalized = events.Where(e => e.Kind == CaptionSegmentEventKind.Finalized).ToArray();
        Assert.Equal(["First sentence.", "Second sentence."], finalized.Select(e => e.Text));
        Assert.Equal(2, finalized.Select(e => e.SegmentId).Distinct().Count());
        Assert.Equal(finalized.OrderBy(e => e.SegmentId).Select(e => e.SegmentId), finalized.Select(e => e.SegmentId));
    }

    [Fact]
    public void UnpunctuatedText_FinalizesAfterMonotonicIdleTime()
    {
        var time = new ManualTimeProvider();
        var segmenter = new CaptionSegmenter(time);
        CaptionSegmentEvent draft = Assert.Single(segmenter.ProcessSnapshot("A thought without punctuation"),
            e => e.Kind == CaptionSegmentEventKind.DraftRevision);

        time.Advance(TimeSpan.FromMilliseconds(1499));
        Assert.DoesNotContain(segmenter.ProcessSnapshot("A thought without punctuation"),
            e => e.Kind == CaptionSegmentEventKind.Finalized);
        time.Advance(TimeSpan.FromMilliseconds(1));
        CaptionSegmentEvent final = Assert.Single(segmenter.ProcessSnapshot("A thought without punctuation"),
            e => e.Kind == CaptionSegmentEventKind.Finalized);

        Assert.Equal(draft.SegmentId, final.SegmentId);
        Assert.Equal("A thought without punctuation", final.Text);
    }

    [Fact]
    public void AbbreviationsAndDecimals_DoNotSplitSentences()
    {
        var time = new ManualTimeProvider();
        var segmenter = new CaptionSegmenter(time);
        const string snapshot = "Dr. Smith paid 3.14 dollars. The U. S. stayed there. For example, e.g. this still belongs here!";

        segmenter.ProcessSnapshot(snapshot);
        time.Advance(TimeSpan.FromMilliseconds(200));
        CaptionSegmentEvent[] finalized = segmenter.ProcessSnapshot(snapshot)
            .Where(e => e.Kind == CaptionSegmentEventKind.Finalized).ToArray();

        Assert.Equal(3, finalized.Length);
        Assert.Equal("Dr. Smith paid 3.14 dollars.", finalized[0].Text);
        Assert.Equal("The U.S. stayed there.", finalized[1].Text);
        Assert.Equal("For example, e.g. this still belongs here!", finalized[2].Text);
    }

    [Fact]
    public void RollingSnapshot_UsesReliableOverlapWithoutDuplicatingTail()
    {
        var time = new ManualTimeProvider();
        var segmenter = new CaptionSegmenter(time);
        segmenter.ProcessSnapshot("A complete caption sentence. draft tail");

        segmenter.ProcessSnapshot("draft tail continues.");
        time.Advance(TimeSpan.FromMilliseconds(200));
        CaptionSegmentEvent[] finalized = segmenter.ProcessSnapshot("draft tail continues.")
            .Where(e => e.Kind == CaptionSegmentEventKind.Finalized).ToArray();

        Assert.Equal(new[] { "A complete caption sentence.", "draft tail continues." },
            finalized.Select(e => e.Text));
        Assert.Equal(2, finalized.Select(e => e.SegmentId).Distinct().Count());
    }

    [Fact]
    public void UnalignedSnapshot_FinalizesPreviousCaptionBeforeStartingNewEpoch()
    {
        var time = new ManualTimeProvider();
        var segmenter = new CaptionSegmenter(time);
        CaptionSegmentEvent previousDraft = Assert.Single(
            segmenter.ProcessSnapshot("The earlier caption remains visible"),
            e => e.Kind == CaptionSegmentEventKind.DraftRevision);

        time.Advance(TimeSpan.FromMilliseconds(100));
        IReadOnlyList<CaptionSegmentEvent> events = segmenter.ProcessSnapshot("A different subtitle appeared");

        CaptionSegmentEvent finalized = Assert.Single(events,
            e => e.Kind == CaptionSegmentEventKind.Finalized);
        CaptionSegmentEvent diagnostic = Assert.Single(events,
            e => e.Kind == CaptionSegmentEventKind.Diagnostic);
        CaptionSegmentEvent nextDraft = Assert.Single(events,
            e => e.Kind == CaptionSegmentEventKind.DraftRevision);
        Assert.Equal(previousDraft.Text, finalized.Text);
        Assert.Equal(previousDraft.SessionId, finalized.SessionId);
        Assert.Equal(previousDraft.Epoch, finalized.Epoch);
        Assert.Equal(previousDraft.SegmentId, finalized.SegmentId);
        Assert.True(diagnostic.Epoch > finalized.Epoch);
        Assert.Equal(diagnostic.Epoch, nextDraft.Epoch);
        Assert.NotEqual(finalized.SegmentId, nextDraft.SegmentId);
    }

    [Fact]
    public void RecognitionCorrection_UpdatesExistingFinalizedIdentity()
    {
        var time = new ManualTimeProvider();
        var segmenter = new CaptionSegmenter(time);
        segmenter.ProcessSnapshot("Hello world.");
        time.Advance(TimeSpan.FromMilliseconds(200));
        CaptionSegmentEvent original = Assert.Single(segmenter.ProcessSnapshot("Hello world."),
            e => e.Kind == CaptionSegmentEventKind.Finalized);

        CaptionSegmentEvent corrected = Assert.Single(segmenter.ProcessSnapshot("Hello there!"),
            e => e.Kind == CaptionSegmentEventKind.Corrected);

        Assert.Equal(original.SegmentId, corrected.SegmentId);
        Assert.Equal(original.Revision + 1, corrected.Revision);
        Assert.Equal("Hello there!", corrected.Text);
    }

    [Fact]
    public void EmptyCaptionGapStartsNewEpochForRepeatedText()
    {
        var time = new ManualTimeProvider();
        var segmenter = new CaptionSegmenter(time);
        segmenter.ProcessSnapshot("Repeated sentence.");
        time.Advance(TimeSpan.FromMilliseconds(200));
        CaptionSegmentEvent first = Assert.Single(segmenter.ProcessSnapshot("Repeated sentence."),
            e => e.Kind == CaptionSegmentEventKind.Finalized);

        segmenter.ProcessSnapshot(string.Empty);
        time.Advance(TimeSpan.FromMilliseconds(300));
        segmenter.ProcessSnapshot(string.Empty);
        CaptionSegmentEvent second = Assert.Single(segmenter.ProcessSnapshot("Repeated sentence."),
            e => e.Kind == CaptionSegmentEventKind.DraftRevision);

        Assert.NotEqual(first.Epoch, second.Epoch);
        Assert.NotEqual(first.SegmentId, second.SegmentId);
    }

    [Fact]
    public void BriefEmptySnapshotDoesNotEndCurrentCaption()
    {
        var time = new ManualTimeProvider();
        var segmenter = new CaptionSegmenter(time);
        CaptionSegmentEvent draft = Assert.Single(segmenter.ProcessSnapshot("A caption that briefly disappears"),
            e => e.Kind == CaptionSegmentEventKind.DraftRevision);

        segmenter.ProcessSnapshot(string.Empty);
        time.Advance(TimeSpan.FromMilliseconds(299));
        IReadOnlyList<CaptionSegmentEvent> resumed = segmenter.ProcessSnapshot("A caption that briefly disappears");
        Assert.DoesNotContain(resumed, e => e.Kind is CaptionSegmentEventKind.Diagnostic or CaptionSegmentEventKind.Finalized);

        time.Advance(TimeSpan.FromMilliseconds(1201));
        CaptionSegmentEvent finalized = Assert.Single(
            segmenter.ProcessSnapshot("A caption that briefly disappears"),
            e => e.Kind == CaptionSegmentEventKind.Finalized);
        Assert.Equal(draft.Epoch, finalized.Epoch);
        Assert.Equal(draft.SegmentId, finalized.SegmentId);
    }

    [Fact]
    public void ReappearingTextAfterLongEmptyIntervalStartsNewEpochEvenWithoutAnotherEmptyPoll()
    {
        var time = new ManualTimeProvider();
        var segmenter = new CaptionSegmenter(time);
        CaptionSegmentEvent first = Assert.Single(segmenter.ProcessSnapshot("The first caption remains"),
            e => e.Kind == CaptionSegmentEventKind.DraftRevision);

        segmenter.ProcessSnapshot(string.Empty);
        time.Advance(TimeSpan.FromMilliseconds(300));
        IReadOnlyList<CaptionSegmentEvent> resumed = segmenter.ProcessSnapshot("The first caption remains");

        CaptionSegmentEvent finalized = Assert.Single(resumed,
            e => e.Kind == CaptionSegmentEventKind.Finalized);
        CaptionSegmentEvent nextDraft = Assert.Single(resumed,
            e => e.Kind == CaptionSegmentEventKind.DraftRevision);
        Assert.Equal(first.Epoch, finalized.Epoch);
        Assert.Equal(first.SegmentId, finalized.SegmentId);
        Assert.True(nextDraft.Epoch > finalized.Epoch);
        Assert.NotEqual(finalized.SegmentId, nextDraft.SegmentId);
        Assert.DoesNotContain(resumed, e => e.Kind == CaptionSegmentEventKind.Diagnostic);
    }

    [Fact]
    public void ConsecutiveBlankLines_UseExistingShortLineContinuation()
    {
        var segmenter = new CaptionSegmenter(new ManualTimeProvider());

        IReadOnlyList<CaptionSegmentEvent> events = segmenter.ProcessSnapshot("\n\nFirst line.\n\nSecond line.");

        Assert.Contains(events, e => e.Kind == CaptionSegmentEventKind.DraftRevision &&
                                     e.Text == "—Second line.");
    }

    [Fact]
    public void LongLineBreak_AddsSentenceBoundaryBeforeTheNextLine()
    {
        var time = new ManualTimeProvider();
        var segmenter = new CaptionSegmenter(time);
        string firstLine = new('A', 45);
        string snapshot = $"{firstLine}\nnext line.";

        segmenter.ProcessSnapshot(snapshot);
        time.Advance(TimeSpan.FromMilliseconds(200));
        CaptionSegmentEvent[] finalized = segmenter.ProcessSnapshot(snapshot)
            .Where(e => e.Kind == CaptionSegmentEventKind.Finalized).ToArray();

        Assert.Equal($"{firstLine}.", finalized[0].Text);
        Assert.Equal("next line.", finalized[1].Text);
    }

    [Fact]
    public void ShortLineBreak_ContinuesWithAnEmDashInsteadOfSplitting()
    {
        var time = new ManualTimeProvider();
        var segmenter = new CaptionSegmenter(time);
        const string snapshot = "short caption\ncontinued caption";

        segmenter.ProcessSnapshot(snapshot);
        time.Advance(TimeSpan.FromMilliseconds(1500));
        CaptionSegmentEvent final = Assert.Single(segmenter.ProcessSnapshot(snapshot),
            e => e.Kind == CaptionSegmentEventKind.Finalized);

        Assert.Equal("short caption—continued caption", final.Text);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long ticks;
        private DateTimeOffset utcNow = DateTimeOffset.UnixEpoch;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration)
        {
            ticks += duration.Ticks;
            utcNow += duration;
        }
    }
}
