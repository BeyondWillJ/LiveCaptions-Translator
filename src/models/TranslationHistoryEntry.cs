using CsvHelper.Configuration.Attributes;

namespace LiveCaptionsTranslator.models
{
    public class TranslationHistoryEntry
    {
        [Ignore]
        public long HistoryId { get; set; }
        [Ignore]
        public long SegmentId { get; set; }
        [Ignore]
        public Guid? SessionId { get; set; }
        [Ignore]
        public long? Epoch { get; set; }
        [Ignore]
        public long Revision { get; set; }
        public required string Timestamp { get; set; }
        [Ignore]
        public required string TimestampFull { get; set; }
        public required string SourceText { get; set; }
        public required string TranslatedText { get; set; }
        public required string TargetLanguage { get; set; }
        public required string ApiUsed { get; set; }
        public string Status { get; set; } = nameof(TranslationStatus.Succeeded);
        public string ErrorCode { get; set; } = string.Empty;
        public string ErrorMessage { get; set; } = string.Empty;
        [Ignore]
        public string LocalizedStatus => LiveCaptionsTranslator.utils.LocalizationService.Get(Status);
        [Ignore]
        public string LocalizedErrorReason
        {
            get
            {
                string source = ErrorCode switch
                {
                    "Timeout" => "Request timed out.",
                    "HttpStatus" => "The translation service returned an unsuccessful response.",
                    "InvalidResponse" => "The translation service returned an unsupported response.",
                    "ResponseTooLarge" => "The translation response exceeded the allowed size.",
                    "NetworkError" => "The translation service could not be reached.",
                    "QueueCapacity" => "Skipped because the translation queue was full.",
                    "QueueWaitExpired" => "Skipped because the request waited too long.",
                    "SupersededAfterTimeout" => "Skipped because a newer caption was waiting.",
                    "Paused" => "Translation was paused.",
                    "LegacyFailure" => "The previous translation request failed.",
                    _ => ErrorMessage
                };
                return string.IsNullOrWhiteSpace(source)
                    ? string.Empty : LiveCaptionsTranslator.utils.LocalizationService.Get(source);
            }
        }
    }
}
