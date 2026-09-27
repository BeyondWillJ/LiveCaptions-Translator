using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.Tests;

public class OverlayPresentationStateTests
{
    [Fact]
    public void UnpunctuatedSource_RestartsSilenceTimerAndZeroDisablesIt()
    {
        var state = new OverlayPresentationState();
        state.Reset(Snapshot(0, 0, string.Empty));

        OverlayUpdate update = state.Update(Snapshot(1, 1, "still speaking without punctuation"));

        Assert.True(update.RestartSilenceTimer);
        Assert.True(state.ShouldRunSilenceTimer(1.5));
        Assert.False(state.ShouldRunSilenceTimer(0));
    }

    [Fact]
    public void LateMatchingTranslation_AppearsAloneThenClearsOnNextTimeout()
    {
        var state = new OverlayPresentationState();
        state.Reset(Snapshot(1, 1, "source"));

        OverlayRenderState firstClear = state.OnSilenceElapsed();
        Assert.Empty(firstClear.Original);
        Assert.Empty(firstClear.Translation);

        OverlayUpdate late = state.Update(Snapshot(
            1, 1, "source", translationSegment: 1, translationRevision: 1,
            translation: "late translation"));
        Assert.True(late.RestartSilenceTimer);
        Assert.Empty(late.Render.Original);
        Assert.Equal("late translation", late.Render.Translation);

        OverlayRenderState secondClear = state.OnSilenceElapsed();
        Assert.Empty(secondClear.Original);
        Assert.Empty(secondClear.Translation);
    }

    [Fact]
    public void NewSource_NeverDisplaysTranslationFromPreviousSegment()
    {
        var state = new OverlayPresentationState();
        state.Reset(Snapshot(
            1, 2, "old source", translationSegment: 1, translationRevision: 2,
            translation: "old translation"));

        OverlayUpdate next = state.Update(Snapshot(
            2, 1, "new source", translationSegment: 1, translationRevision: 2,
            translation: "old translation"));
        Assert.Equal("new source", next.Render.Original);
        Assert.Empty(next.Render.Translation);

        OverlayUpdate staleArrival = state.Update(Snapshot(
            2, 1, "new source", translationSegment: 1, translationRevision: 2,
            translation: "even later old translation"));
        Assert.Empty(staleArrival.Render.Translation);
    }

    [Fact]
    public void NewRevisionKeepsTranslationOfMatchingSourcePrefix()
    {
        var state = new OverlayPresentationState();
        Guid session = Guid.NewGuid();
        state.Reset(Snapshot(4, 1, "The detective found", translationSegment: 4,
            translationRevision: 1, translation: "侦探发现了", sessionId: session,
            translationSessionId: session, translationSource: "The detective found"));

        OverlayUpdate draft = state.Update(Snapshot(4, 2, "The detective found the key", translationSegment: 4,
            translationRevision: 1, translation: "侦探发现了", sessionId: session,
            translationSessionId: session, translationSource: "The detective found"));

        Assert.Equal("The detective found the key", draft.Render.Original);
        Assert.Equal("侦探发现了", draft.Render.Translation);
    }

    [Fact]
    public void CorrectedSourceDoesNotKeepTranslationOfUnrelatedDraft()
    {
        var state = new OverlayPresentationState();
        Guid session = Guid.NewGuid();
        state.Reset(Snapshot(4, 1, "The detective found", translationSegment: 4,
            translationRevision: 1, translation: "侦探发现了", sessionId: session,
            translationSessionId: session, translationSource: "The detective found"));

        OverlayUpdate correction = state.Update(Snapshot(4, 2, "The detective lost the key", translationSegment: 4,
            translationRevision: 1, translation: "侦探发现了", sessionId: session,
            translationSessionId: session, translationSource: "The detective found"));

        Assert.Equal("The detective lost the key", correction.Render.Original);
        Assert.Empty(correction.Render.Translation);
    }

    [Fact]
    public void NewSessionAndEpoch_NeverMatchAnOldTranslationWithReusedSegmentId()
    {
        Guid oldSession = Guid.NewGuid();
        Guid newSession = Guid.NewGuid();
        var state = new OverlayPresentationState();
        state.Reset(Snapshot(8, 1, "old source", translationSegment: 8, translationRevision: 1,
            translation: "old translation", sessionId: oldSession, translationSessionId: oldSession, epoch: 3, translationEpoch: 3));

        OverlayUpdate next = state.Update(Snapshot(8, 1, "new source", translationSegment: 8,
            translationRevision: 1, translation: "old translation", sessionId: newSession,
            translationSessionId: oldSession, epoch: 0, translationEpoch: 3));

        Assert.Equal("new source", next.Render.Original);
        Assert.Empty(next.Render.Translation);
    }

    [Fact]
    public void Overflow_DropsPreviousFirstAndTrimsLeadingText()
    {
        var state = new OverlayPresentationState();
        state.Reset(Snapshot(
            3, 1, "current", translationSegment: 3, translationRevision: 1,
            translation: "current translation", previous: "old completed sentence"));

        OverlayRenderState withoutPrevious = state.SuppressPreviousForOverflow();
        Assert.Empty(withoutPrevious.PreviousTranslation);
        Assert.Equal("current translation", withoutPrevious.Translation);

        const string longText = "first clause, second clause, important ending";
        string trimmed = OverlayPresentationState.TrimLeadingToFit(
            longText, candidate => candidate.Length > 24);
        Assert.StartsWith("…", trimmed);
        Assert.EndsWith("important ending", trimmed);
        Assert.True(trimmed.Length < longText.Length);
    }

    private static OverlaySnapshot Snapshot(
        long sourceSegment,
        long sourceRevision,
        string original,
        long translationSegment = 0,
        long translationRevision = 0,
        string translation = "",
        string previous = "",
        Guid? sessionId = null,
        Guid? translationSessionId = null,
        long epoch = 0,
        long translationEpoch = 0,
        string translationSource = "") => new(
            sourceSegment,
            sourceRevision,
            original,
            translationSegment,
            translationRevision,
            translation,
            previous,
            string.Empty,
            sessionId,
            epoch,
            translationSessionId,
            translationEpoch,
            string.IsNullOrEmpty(translationSource) &&
                sourceSegment == translationSegment && sourceRevision >= translationRevision &&
                translationSessionId == sessionId && translationEpoch == epoch
                ? original
                : translationSource);
}
