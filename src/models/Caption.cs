using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models
{
    public class Caption : INotifyPropertyChanged
    {
        public const int MAX_CONTEXTS = 10;

        private static Caption? instance = null;
        public event PropertyChangedEventHandler? PropertyChanged;

        private string displayOriginalCaption = string.Empty;
        private string displayTranslatedCaption = string.Empty;
        private string overlayOriginalCaption = " ";
        private string overlayCurrentTranslation = " ";
        private string overlayTranslatedSourceText = string.Empty;
        private string overlayNoticePrefix = " ";
        private string statusMessage = string.Empty;
        private string statusDiagnostic = string.Empty;
        private string statusRoute = string.Empty;
        private readonly object contextsLock = new();

        public string OriginalCaption { get; set; } = string.Empty;
        public string TranslatedCaption { get; set; } = string.Empty;
        public long OverlaySourceSegmentId { get; set; }
        public long OverlaySourceRevision { get; set; }
        public Guid? OverlaySessionId { get; set; }
        public long OverlayEpoch { get; set; }
        public Guid? OverlayTranslationSessionId { get; set; }
        public long OverlayTranslationEpoch { get; set; }
        public long OverlayTranslationSegmentId { get; set; }
        public long OverlayTranslationRevision { get; set; }

        public Queue<TranslationHistoryEntry> Contexts { get; } = new(MAX_CONTEXTS);

        public IEnumerable<TranslationHistoryEntry> AwareContexts => GetPreviousContexts(Translator.Setting.NumContexts);
        public string AwareContextsCaption => GetPreviousText(Translator.Setting.NumContexts, TextType.Caption);

        public IEnumerable<TranslationHistoryEntry> DisplayLogCards =>
            GetPreviousContexts(Translator.Setting.DisplaySentences).Reverse();

        public string DisplayOriginalCaption
        {
            get => displayOriginalCaption;
            set
            {
                displayOriginalCaption = value;
                OnPropertyChanged("DisplayOriginalCaption");
            }
        }
        public string DisplayTranslatedCaption
        {
            get => displayTranslatedCaption;
            set
            {
                displayTranslatedCaption = value;
                OnPropertyChanged("DisplayTranslatedCaption");
            }
        }

        public string OverlayOriginalCaption
        {
            get => overlayOriginalCaption;
            set
            {
                overlayOriginalCaption = value;
                OnPropertyChanged("OverlayOriginalCaption");
            }
        }
        public string OverlayNoticePrefix
        {
            get => overlayNoticePrefix;
            set
            {
                overlayNoticePrefix = value;
                OnPropertyChanged("OverlayNoticePrefix");
            }
        }
        public string OverlayCurrentTranslation
        {
            get => overlayCurrentTranslation;
            set
            {
                overlayCurrentTranslation = value;
                OnPropertyChanged("OverlayCurrentTranslation");
            }
        }

        public string OverlayPreviousTranslation =>
            GetPreviousText(Translator.Setting.DisplaySentences, TextType.Translation,
                OverlaySourceSegmentId);

        public string StatusMessage
        {
            get => statusMessage;
            set
            {
                if (statusMessage == value)
                    return;
                statusMessage = value;
                OnPropertyChanged();
            }
        }

        public string OverlayTranslatedSourceText
        {
            get => overlayTranslatedSourceText;
            set
            {
                overlayTranslatedSourceText = value;
                OnPropertyChanged("OverlayTranslatedSourceText");
            }
        }

        public string StatusDiagnostic
        {
            get => statusDiagnostic;
            set
            {
                if (statusDiagnostic == value)
                    return;
                statusDiagnostic = value;
                OnPropertyChanged();
            }
        }

        public string StatusRoute
        {
            get => statusRoute;
            set
            {
                if (statusRoute == value)
                    return;
                statusRoute = value;
                OnPropertyChanged();
            }
        }

        private Caption()
        {
        }

        public static Caption GetInstance()
        {
            if (instance != null)
                return instance;
            instance = new Caption();
            return instance;
        }

        public string GetPreviousText(int count, TextType textType, long excludedSegmentId = -1)
        {
            lock (contextsLock)
            {
                if (count <= 0 || Contexts.Count == 0)
                    return string.Empty;

                var values = Contexts
                    .Where(entry => entry.SegmentId != excludedSegmentId)
                    .Where(entry => string.Equals(entry.Status, nameof(TranslationStatus.Succeeded), StringComparison.Ordinal))
                    .Reverse().Take(count).Reverse()
                    .Select(entry => textType == TextType.Caption ? entry.SourceText : entry.TranslatedText)
                    .ToList();
                if (values.Count == 0)
                    return string.Empty;

                var prev = values.Aggregate((accu, cur) =>
                {
                    if (!string.IsNullOrEmpty(accu))
                    {
                        if (Array.IndexOf(TextUtil.PUNC_EOS, accu[^1]) == -1)
                            accu += TextUtil.isCJChar(accu[^1]) ? "。" : ". ";
                        else
                            accu += TextUtil.isCJChar(accu[^1]) ? "" : " ";
                    }
                    cur = RegexPatterns.NoticePrefix().Replace(cur, "");
                    return accu + cur;
                });

                if (textType == TextType.Translation)
                    prev = RegexPatterns.NoticePrefix().Replace(prev, "");
                if (!string.IsNullOrEmpty(prev) && Array.IndexOf(TextUtil.PUNC_EOS, prev[^1]) == -1)
                    prev += TextUtil.isCJChar(prev[^1]) ? "。" : ".";
                if (!string.IsNullOrEmpty(prev) && Encoding.UTF8.GetByteCount(prev[^1].ToString()) < 2)
                    prev += " ";
                return prev;
            }
        }

        public IEnumerable<TranslationHistoryEntry> GetPreviousContexts(int count)
        {
            lock (contextsLock)
            {
                if (count <= 0 || Contexts.Count == 0)
                    return [];

                return Contexts
                    .Reverse().Take(count).Reverse()
                    .Where(entry => entry != null &&
                                    string.Equals(entry.Status, nameof(TranslationStatus.Succeeded), StringComparison.Ordinal))
                    .ToList();
            }
        }

        public void AddContext(TranslationHistoryEntry entry)
        {
            lock (contextsLock)
            {
                if (Contexts.Count >= MAX_CONTEXTS)
                    Contexts.Dequeue();
                Contexts.Enqueue(entry);
            }
            OnPropertyChanged("DisplayLogCards");
            OnPropertyChanged("OverlayPreviousTranslation");
        }

        public void ClearContexts()
        {
            lock (contextsLock)
                Contexts.Clear();
            OnPropertyChanged("DisplayLogCards");
            OnPropertyChanged("OverlayPreviousTranslation");
        }

        public void OnPropertyChanged([CallerMemberName] string propName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
        }
    }

    public enum TextType
    {
        Caption,
        Translation
    }
}
