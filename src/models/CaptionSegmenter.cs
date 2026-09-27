using System.Text.RegularExpressions;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models;

public enum CaptionSegmentEventKind
{
    DraftRevision,
    Finalized,
    Corrected,
    Diagnostic
}

public sealed record CaptionSegmentEvent(
    CaptionSegmentEventKind Kind,
    Guid SessionId,
    long Epoch,
    long SegmentId,
    long Revision,
    string Text,
    long CapturedTimestamp,
    DateTimeOffset CapturedAt,
    string? Diagnostic = null);

/// <summary>
/// Turns successive Windows Live Captions snapshots into stable sentence events.
/// It stores only the current unresolved tail and the most recent finalized sentence.
/// </summary>
public sealed class CaptionSegmenter
{
    private static readonly TimeSpan PunctuationStability = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan UnpunctuatedIdle = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan EmptyEpochDelay = TimeSpan.FromMilliseconds(300);
    private const int MinimumReliableOverlap = 8;

    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "vs", "etc", "e.g", "i.e",
        "fig", "no", "inc", "ltd", "dept", "approx", "jan", "feb", "mar", "apr", "jun",
        "jul", "aug", "sep", "sept", "oct", "nov", "dec"
    };

    private readonly TimeProvider timeProvider;
    private readonly List<PendingSentence> pending = [];
    private string lastSnapshot = string.Empty;
    private string activeText = string.Empty;
    private long nextSegmentId;
    private long epoch = 1;
    private long? emptyStartedAt;
    private bool hadTextAtEmptyStart;
    private bool endedBySilence;
    private long? lastFinalizedAt;
    private long lastFinalizedId;
    private long lastFinalizedRevision;
    private string lastFinalizedText = string.Empty;
    private int lastFinalizedStart = -1;

    public CaptionSegmenter(TimeProvider? timeProvider = null, Guid? sessionId = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        SessionId = sessionId ?? Guid.NewGuid();
    }

    public Guid SessionId { get; }
    public long Epoch => epoch;

    public IReadOnlyList<CaptionSegmentEvent> ProcessSnapshot(string? snapshot)
    {
        long timestamp = timeProvider.GetTimestamp();
        DateTimeOffset capturedAt = timeProvider.GetUtcNow();
        string normalized = Normalize(snapshot ?? string.Empty);
        var events = new List<CaptionSegmentEvent>();

        if (normalized.Length == 0)
        {
            if (!emptyStartedAt.HasValue)
            {
                emptyStartedAt = timestamp;
                hadTextAtEmptyStart = lastSnapshot.Length > 0 || activeText.Length > 0;
            }
            if (!endedBySilence && hadTextAtEmptyStart &&
                timeProvider.GetElapsedTime(emptyStartedAt.Value, timestamp) >= EmptyEpochDelay)
            {
                EndCurrentEpoch(events, capturedAt, timestamp);
            }
            return events;
        }

        if (!endedBySilence && emptyStartedAt.HasValue && hadTextAtEmptyStart &&
            timeProvider.GetElapsedTime(emptyStartedAt.Value, timestamp) >= EmptyEpochDelay)
        {
            EndCurrentEpoch(events, capturedAt, timestamp);
        }
        emptyStartedAt = null;
        hadTextAtEmptyStart = false;
        if (endedBySilence)
        {
            StartNewEpoch();
            endedBySilence = false;
        }

        if (lastSnapshot.Length == 0 && activeText.Length == 0)
        {
            activeText = normalized;
        }
        else if (normalized.StartsWith(lastSnapshot, StringComparison.Ordinal))
        {
            activeText += normalized[lastSnapshot.Length..];
        }
        else
        {
            int committedBoundary = Math.Max(0, lastSnapshot.Length - activeText.Length);
            int commonPrefix = CommonPrefixLength(lastSnapshot, normalized);
            if (commonPrefix >= committedBoundary && (committedBoundary > 0 || commonPrefix > 0))
            {
                activeText = normalized[committedBoundary..];
            }
            else if (TryApplyLastFinalCorrection(normalized, commonPrefix, events, capturedAt))
            {
                // The latest finalized sentence retained its identity and was revised in place.
            }
            else
            {
                int overlap = FindReliableOverlap(lastSnapshot, normalized);
                if (overlap >= MinimumReliableOverlap)
                {
                    int overlapStart = lastSnapshot.Length - overlap;
                    int activeStart = Math.Max(0, lastSnapshot.Length - activeText.Length);
                    lastFinalizedStart = lastFinalizedStart >= overlapStart
                        ? lastFinalizedStart - overlapStart : -1;
                    if (overlapStart > activeStart)
                    {
                        int preservedLength = Math.Min(activeText.Length, overlapStart - activeStart);
                        activeText = activeText[..preservedLength] + normalized;
                    }
                    else
                    {
                        activeText += normalized[overlap..];
                    }
                }
                else
                {
                    FinalizeAll(events, capturedAt, timestamp);
                    StartNewEpoch();
                    activeText = normalized;
                    events.Add(new CaptionSegmentEvent(CaptionSegmentEventKind.Diagnostic,
                    SessionId, epoch, 0, 0, string.Empty, timestamp, capturedAt,
                        "Caption snapshot could not be reliably aligned; started a new epoch."));
                }
            }
        }

        lastSnapshot = normalized;
        ReconcileAndEmit(events, timestamp, capturedAt);
        return events;
    }

    private void EndCurrentEpoch(
        List<CaptionSegmentEvent> events, DateTimeOffset capturedAt, long timestamp)
    {
        if (activeText.Length > 0)
            FinalizeAll(events, capturedAt, timestamp);
        pending.Clear();
        activeText = string.Empty;
        lastSnapshot = string.Empty;
        endedBySilence = true;
    }

    private void ReconcileAndEmit(List<CaptionSegmentEvent> events, long timestamp, DateTimeOffset capturedAt)
    {
        var parts = Split(activeText);
        var candidates = parts.Sentences
            .Select(sentence => new Candidate(sentence.Text, sentence.EndIndex, true))
            .ToList();
        if (!string.IsNullOrWhiteSpace(parts.TrailingText))
            candidates.Add(new Candidate(parts.TrailingText, activeText.Length, false));

        bool anyTextChanged = false;
        for (int i = 0; i < candidates.Count; i++)
        {
            Candidate candidate = candidates[i];
            if (i < pending.Count)
            {
                PendingSentence previous = pending[i];
                bool punctuationChanged = previous.HasTerminator != candidate.HasTerminator;
                if (!string.Equals(previous.Text, candidate.Text, StringComparison.Ordinal) || punctuationChanged)
                {
                    previous.Text = candidate.Text;
                    previous.EndIndex = candidate.EndIndex;
                    previous.HasTerminator = candidate.HasTerminator;
                    previous.Revision++;
                    previous.ChangedAt = timestamp;
                    anyTextChanged = true;
                }
                else
                {
                    previous.EndIndex = candidate.EndIndex;
                }
            }
            else
            {
                pending.Add(new PendingSentence(candidate.Text, candidate.EndIndex,
                    candidate.HasTerminator, ++nextSegmentId, 1, timestamp));
                anyTextChanged = true;
            }
        }

        if (pending.Count > candidates.Count)
            pending.RemoveRange(candidates.Count, pending.Count - candidates.Count);

        if (anyTextChanged && pending.Count > 0)
        {
            PendingSentence latest = pending[^1];
            events.Add(CreateEvent(CaptionSegmentEventKind.DraftRevision, latest.Id,
                latest.Revision, latest.Text, capturedAt));
        }

        while (pending.Count > 0)
        {
            PendingSentence first = pending[0];
            TimeSpan stableFor = timeProvider.GetElapsedTime(first.ChangedAt, timestamp);
            bool shouldFinalize = first.HasTerminator
                ? stableFor >= PunctuationStability
                : pending.Count == 1 && stableFor >= UnpunctuatedIdle;
            if (!shouldFinalize)
                break;

            int boundary = Math.Clamp(first.EndIndex, 0, activeText.Length);
            int startInSnapshot = Math.Max(0, lastSnapshot.Length - activeText.Length);
            lastFinalizedStart = startInSnapshot;
            lastFinalizedId = first.Id;
            lastFinalizedRevision = first.Revision;
            lastFinalizedText = first.Text;
            lastFinalizedAt = timestamp;
            events.Add(CreateEvent(CaptionSegmentEventKind.Finalized, first.Id,
                first.Revision, first.Text, capturedAt));

            activeText = activeText[boundary..].TrimStart();
            pending.RemoveAt(0);
            RebasePending(activeText);
        }
    }

    private bool TryApplyLastFinalCorrection(
        string snapshot,
        int commonPrefix,
        List<CaptionSegmentEvent> events,
        DateTimeOffset capturedAt)
    {
        if (lastFinalizedStart < 0 || commonPrefix < lastFinalizedStart || lastFinalizedText.Length == 0)
            return false;

        int start = lastFinalizedStart;
        if (start >= snapshot.Length)
            return false;
        string correctedTail = snapshot[start..];
        SplitResult split = Split(correctedTail);
        string revised = split.Sentences.Count > 0
            ? split.Sentences[0].Text
            : split.TrailingText.Trim();
        if (revised.Length == 0 || string.Equals(revised, lastFinalizedText, StringComparison.Ordinal))
            return false;

        int revisedBoundary = split.Sentences.Count > 0
            ? start + split.Sentences[0].EndIndex
            : snapshot.Length;
        lastFinalizedText = revised;
        lastFinalizedRevision++;
        events.Add(CreateEvent(CaptionSegmentEventKind.Corrected, lastFinalizedId,
            lastFinalizedRevision, revised, capturedAt));
        activeText = snapshot[Math.Clamp(revisedBoundary, 0, snapshot.Length)..].TrimStart();
        pending.Clear();
        return true;
    }

    private void FinalizeAll(List<CaptionSegmentEvent> events, DateTimeOffset capturedAt, long timestamp)
    {
        while (pending.Count > 0)
        {
            PendingSentence first = pending[0];
            int startInSnapshot = Math.Max(0, lastSnapshot.Length - activeText.Length);
            lastFinalizedStart = startInSnapshot;
            lastFinalizedId = first.Id;
            lastFinalizedRevision = first.Revision;
            lastFinalizedText = first.Text;
            lastFinalizedAt = timestamp;
            events.Add(CreateEvent(CaptionSegmentEventKind.Finalized, first.Id,
                first.Revision, first.Text, capturedAt));
            int boundary = Math.Clamp(first.EndIndex, 0, activeText.Length);
            activeText = activeText[boundary..].TrimStart();
            pending.RemoveAt(0);
            RebasePending(activeText);
        }

        if (!string.IsNullOrWhiteSpace(activeText))
        {
            long id = ++nextSegmentId;
            lastFinalizedStart = Math.Max(0, lastSnapshot.Length - activeText.Length);
            lastFinalizedId = id;
            lastFinalizedRevision = 1;
            lastFinalizedText = activeText.Trim();
            lastFinalizedAt = timestamp;
            events.Add(CreateEvent(CaptionSegmentEventKind.Finalized, id, 1,
                lastFinalizedText, capturedAt));
        }
    }

    private void RebasePending(string text)
    {
        if (pending.Count == 0)
            return;
        SplitResult split = Split(text);
        var candidates = split.Sentences
            .Select(sentence => new Candidate(sentence.Text, sentence.EndIndex, true))
            .ToList();
        if (!string.IsNullOrWhiteSpace(split.TrailingText))
            candidates.Add(new Candidate(split.TrailingText, text.Length, false));
        for (int i = 0; i < Math.Min(pending.Count, candidates.Count); i++)
            pending[i].EndIndex = candidates[i].EndIndex;
    }

    private void StartNewEpoch()
    {
        epoch++;
        lastSnapshot = string.Empty;
        activeText = string.Empty;
        pending.Clear();
        lastFinalizedAt = null;
        lastFinalizedText = string.Empty;
        lastFinalizedStart = -1;
    }

    private CaptionSegmentEvent CreateEvent(
        CaptionSegmentEventKind kind, long id, long revision, string text, DateTimeOffset capturedAt) =>
        new(kind, SessionId, epoch, id, revision, text, timeProvider.GetTimestamp(), capturedAt);

    private static SplitResult Split(string text)
    {
        var sentences = new List<SentenceBoundary>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (!IsTerminator(text, i))
                continue;

            int end = i + 1;
            while (end < text.Length && IsClosingMark(text[end]))
                end++;
            string value = text[start..end].Trim();
            if (value.Length > 0)
            {
                sentences.Add(new SentenceBoundary(value, end));
                start = end;
            }
            i = end - 1;
            while (start < text.Length && char.IsWhiteSpace(text[start]))
                start++;
        }

        string trailing = start < text.Length ? text[start..].Trim() : string.Empty;
        return new SplitResult(sentences, trailing);
    }

    private static bool IsTerminator(string text, int index)
    {
        char value = text[index];
        if (value is '!' or '?' or '。' or '！' or '？')
            return true;
        if (value != '.')
            return false;
        if (index > 0 && index + 1 < text.Length && char.IsDigit(text[index - 1]) && char.IsDigit(text[index + 1]))
            return false;
        if (index + 1 < text.Length && text[index + 1] == '.')
            return false;

        int tokenStart = index - 1;
        while (tokenStart >= 0 && (char.IsLetter(text[tokenStart]) || text[tokenStart] == '.'))
            tokenStart--;
        string token = text[(tokenStart + 1)..index].TrimEnd('.');
        if (Abbreviations.Contains(token))
            return false;
        if (token.Length == 1 && char.IsLetter(token[0]))
            return false;
        if (token.Contains('.') && token.Split('.').All(part => part.Length == 1))
            return false;
        return true;
    }

    private static bool IsClosingMark(char value) => value is '"' or '\'' or ')' or ']' or '}' or '”' or '’' or '」' or '』';

    private static string Normalize(string value)
    {
        value = Regex.Replace(value, @"(?<!\w)(?:[A-Z]\.\s*)+[A-Z]\.(?![A-Za-z])",
            match => Regex.Replace(match.Value, @"\s+", string.Empty));
        value = TextUtil.ReplaceNewlines(value, TextUtil.MEDIUM_THRESHOLD);
        value = value.Replace("\r", " ").Replace('\t', ' ');
        return Regex.Replace(value, @"\s+", " ").Trim();
    }

    private static int CommonPrefixLength(string first, string second)
    {
        int length = Math.Min(first.Length, second.Length);
        int index = 0;
        while (index < length && first[index] == second[index])
            index++;
        return index;
    }

    private static int FindReliableOverlap(string previous, string current)
    {
        int limit = Math.Min(previous.Length, current.Length);
        for (int length = limit; length >= MinimumReliableOverlap; length--)
        {
            if (previous.AsSpan(previous.Length - length, length).SequenceEqual(current.AsSpan(0, length)))
                return length;
        }
        return 0;
    }

    private sealed record Candidate(string Text, int EndIndex, bool HasTerminator);
    private sealed record SentenceBoundary(string Text, int EndIndex);
    private sealed record SplitResult(List<SentenceBoundary> Sentences, string TrailingText);

    private sealed class PendingSentence(
        string text, int endIndex, bool hasTerminator, long id, long revision, long changedAt)
    {
        public string Text { get; set; } = text;
        public int EndIndex { get; set; } = endIndex;
        public bool HasTerminator { get; set; } = hasTerminator;
        public long Id { get; } = id;
        public long Revision { get; set; } = revision;
        public long ChangedAt { get; set; } = changedAt;
    }
}
