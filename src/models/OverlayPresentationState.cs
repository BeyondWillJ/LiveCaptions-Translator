using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models
{
    internal sealed record OverlaySnapshot(
        long SourceSegmentId,
        long SourceRevision,
        string Original,
        long TranslationSegmentId,
        long TranslationRevision,
        string Translation,
        string PreviousTranslation,
        string NoticePrefix,
        Guid? SourceSessionId = null,
        long SourceEpoch = 0,
        Guid? TranslationSessionId = null,
        long TranslationEpoch = 0,
        string TranslationSourceText = "");

    internal sealed record OverlayRenderState(
        string Original,
        string Translation,
        string PreviousTranslation,
        string NoticePrefix);

    internal sealed record OverlayUpdate(
        bool Changed,
        bool RestartSilenceTimer,
        OverlayRenderState Render);

    internal sealed class OverlayPresentationState
    {
        private OverlaySnapshot current = new(0, 0, string.Empty, 0, 0,
            string.Empty, string.Empty, string.Empty);
        private long clearedSourceSegmentId = -1;
        private long clearedSourceRevision = -1;
        private Guid? clearedSourceSessionId;
        private long clearedSourceEpoch;
        private bool sourceCleared;
        private bool allowLateTranslation;
        private bool showLateTranslation;
        private bool suppressPreviousForOverflow;

        public OverlayRenderState CurrentRender => Render();

        public void Reset(OverlaySnapshot snapshot)
        {
            current = snapshot;
            clearedSourceSegmentId = -1;
            clearedSourceRevision = -1;
            clearedSourceSessionId = null;
            clearedSourceEpoch = 0;
            sourceCleared = false;
            allowLateTranslation = false;
            showLateTranslation = false;
            suppressPreviousForOverflow = false;
        }

        public OverlayUpdate Update(OverlaySnapshot snapshot)
        {
            bool sourceChanged = current.SourceSessionId != snapshot.SourceSessionId ||
                                 current.SourceEpoch != snapshot.SourceEpoch ||
                                 current.SourceSegmentId != snapshot.SourceSegmentId ||
                                 current.SourceRevision != snapshot.SourceRevision;
            bool translationChanged = current.TranslationSessionId != snapshot.TranslationSessionId ||
                                      current.TranslationEpoch != snapshot.TranslationEpoch ||
                                      current.TranslationSegmentId != snapshot.TranslationSegmentId ||
                                      current.TranslationRevision != snapshot.TranslationRevision;
            bool originalChanged = !string.Equals(current.Original, snapshot.Original, StringComparison.Ordinal);
            bool textChanged = sourceChanged || translationChanged || originalChanged ||
                               !string.Equals(current.Translation, snapshot.Translation, StringComparison.Ordinal) ||
                               !string.Equals(current.TranslationSourceText, snapshot.TranslationSourceText,
                                   StringComparison.Ordinal) ||
                               !string.Equals(current.PreviousTranslation, snapshot.PreviousTranslation,
                                   StringComparison.Ordinal) ||
                               !string.Equals(current.NoticePrefix, snapshot.NoticePrefix, StringComparison.Ordinal);
            if (!textChanged)
                return new OverlayUpdate(false, false, Render());

            current = snapshot;
            bool restartTimer = false;
            if (sourceChanged || originalChanged)
            {
                sourceCleared = false;
                allowLateTranslation = false;
                showLateTranslation = false;
                suppressPreviousForOverflow = false;
                restartTimer = true;
            }
            else if (translationChanged && sourceCleared && allowLateTranslation &&
                     snapshot.TranslationSegmentId == clearedSourceSegmentId &&
                     snapshot.TranslationRevision == clearedSourceRevision &&
                     snapshot.TranslationSessionId == clearedSourceSessionId &&
                     snapshot.TranslationEpoch == clearedSourceEpoch &&
                     snapshot.SourceSessionId == clearedSourceSessionId &&
                     snapshot.SourceEpoch == clearedSourceEpoch &&
                     snapshot.SourceSegmentId == clearedSourceSegmentId &&
                     snapshot.SourceRevision == clearedSourceRevision)
            {
                showLateTranslation = true;
                restartTimer = true;
            }

            return new OverlayUpdate(true, restartTimer, Render());
        }

        public OverlayRenderState OnSilenceElapsed()
        {
            if (sourceCleared && showLateTranslation)
            {
                showLateTranslation = false;
                return Render();
            }

            clearedSourceSegmentId = current.SourceSegmentId;
            clearedSourceRevision = current.SourceRevision;
            clearedSourceSessionId = current.SourceSessionId;
            clearedSourceEpoch = current.SourceEpoch;
            sourceCleared = true;
            bool matchingTranslation = current.TranslationSegmentId == clearedSourceSegmentId &&
                                       current.TranslationRevision == clearedSourceRevision &&
                                       current.TranslationSessionId == clearedSourceSessionId &&
                                       current.TranslationEpoch == clearedSourceEpoch &&
                                       !string.IsNullOrWhiteSpace(current.Translation);
            allowLateTranslation = !matchingTranslation;
            showLateTranslation = false;
            return Render();
        }

        public bool ShouldRunSilenceTimer(double delaySeconds) =>
            delaySeconds > 0 && (!string.IsNullOrWhiteSpace(current.Original) || showLateTranslation);

        public OverlayRenderState SuppressPreviousForOverflow()
        {
            suppressPreviousForOverflow = true;
            return Render();
        }

        public static string TrimLeadingToFit(string text, Func<string, bool> isNearOverflow)
        {
            string candidate = text;
            while (candidate.Length > 12)
            {
                string rendered = "…" + candidate.TrimStart();
                if (!isNearOverflow(rendered))
                    return rendered;
                int punctuation = candidate.IndexOfAny(TextUtil.PUNC_EOS.Concat(TextUtil.PUNC_COMMA).ToArray());
                int removeCount = punctuation >= 0 ? punctuation + 1 : Math.Max(1, candidate.Length / 8);
                candidate = candidate[Math.Min(removeCount, candidate.Length)..];
            }
            return "…" + candidate.TrimStart();
        }

        private OverlayRenderState Render()
        {
            bool clearedCurrentSource = sourceCleared &&
                                        current.SourceSessionId == clearedSourceSessionId &&
                                        current.SourceEpoch == clearedSourceEpoch &&
                                        current.SourceSegmentId == clearedSourceSegmentId &&
                                        current.SourceRevision == clearedSourceRevision;
            string original = clearedCurrentSource ? string.Empty : current.Original;
            bool translationMatchesSource = CaptionDraftTranslationMatcher.MatchesCurrentCaption(
                current.SourceSessionId, current.SourceEpoch, current.SourceSegmentId,
                current.SourceRevision, current.Original,
                current.TranslationSessionId, current.TranslationEpoch, current.TranslationSegmentId,
                current.TranslationRevision, current.TranslationSourceText);
            string translation = translationMatchesSource && (!clearedCurrentSource || showLateTranslation)
                ? current.Translation
                : string.Empty;
            string previous = suppressPreviousForOverflow ? string.Empty : current.PreviousTranslation;
            string notice = string.IsNullOrEmpty(original) && string.IsNullOrEmpty(translation)
                ? string.Empty
                : current.NoticePrefix;
            return new OverlayRenderState(original, translation, previous, notice);
        }
    }
}
