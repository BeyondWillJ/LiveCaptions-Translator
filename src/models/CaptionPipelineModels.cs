namespace LiveCaptionsTranslator.models
{
    public sealed record CaptionUpdate(
        Guid SessionId,
        long Epoch,
        long SegmentId,
        long Revision,
        string Text,
        bool IsFinal,
        long CapturedTimestamp,
        DateTimeOffset CapturedAt,
        long HistoryId = 0,
        long ModeGeneration = 0);

    public enum TranslationStatus
    {
        Pending,
        Succeeded,
        Failed,
        Skipped,
        SourceOnly,
        Interrupted
    }

    public sealed record TranslationOutcome(
        TranslationStatus Status,
        string Text,
        string ErrorCode,
        string Diagnostic,
        long ElapsedMs);

    public sealed record TranslationResult(
        Guid SessionId,
        long Epoch,
        long SegmentId,
        long Revision,
        string SourceText,
        string TranslatedText,
        bool IsFinal,
        bool IsChoke);

    public sealed record TranslationSettingsSnapshot(
        string ApiName,
        string TargetLanguage,
        string Prompt,
        bool ContextAware,
        bool LatencyShow,
        TranslateAPIConfig Config,
        IReadOnlyList<TranslationHistoryEntry> Contexts);

    public sealed record TranslationRequest(
        CaptionUpdate Update,
        TranslationSettingsSnapshot Settings);

    internal static class CaptionDraftTranslationMatcher
    {
        public static bool MatchesCurrentCaption(
            Guid? sourceSessionId,
            long sourceEpoch,
            long sourceSegmentId,
            long sourceRevision,
            string sourceText,
            Guid? translationSessionId,
            long translationEpoch,
            long translationSegmentId,
            long translationRevision,
            string translatedSourceText)
        {
            if (string.IsNullOrWhiteSpace(translatedSourceText) ||
                translationRevision <= 0 || translationRevision > sourceRevision ||
                translationSessionId != sourceSessionId || translationEpoch != sourceEpoch ||
                translationSegmentId != sourceSegmentId)
                return false;

            string translatedSource = NormalizeWhitespace(translatedSourceText);
            string currentSource = NormalizeWhitespace(sourceText);
            return translatedSource.Length > 0 &&
                   currentSource.StartsWith(translatedSource, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeWhitespace(string value) =>
            string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
