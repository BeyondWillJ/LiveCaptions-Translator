using System.Diagnostics;
using System.ComponentModel;
using System.Text.Json;
using System.Windows.Automation;
using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator;

public static class Translator
{
    private static readonly TimeSpan ActiveCaptionPollInterval = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan IdleCaptionPollInterval = TimeSpan.FromMilliseconds(100);
    private static AutomationElement? window;
    private static readonly Caption caption;
    private static readonly Setting setting;
    private static readonly CaptionSegmenter segmenter = new(TimeProvider.System);
    private static readonly CaptionChangeThrottle draftThrottle = new();
    private static LiveCaptionsSession? liveCaptionsSession;
    private static readonly SemaphoreSlim liveCaptionsConnectLock = new(1, 1);
    private static readonly object modeStateLock = new();
    private static readonly TranslationWorkQueue translationQueue = new(
        ProcessTranslationAsync,
        ex => SetServiceFailure(ex),
        (request, status, reason) => _ = MarkDroppedAsync(request, status, reason));
    private static CancellationTokenSource? lifetimeCancellation;
    private static Task[] workers = [];
    private static volatile bool logOnly;
    private static long modeGeneration;
    private static string currentStatus = string.Empty;
    private static string currentDiagnostic = string.Empty;
    private static bool translationServiceFailure;
    private static bool historyWriteFailure;
    private static long lastCaptureToDisplayMs;

    public static AutomationElement? Window
    {
        get => window;
        set => window = value;
    }

    public static Caption Caption => caption;
    public static Setting Setting => setting;
    public static Guid SessionId => segmenter.SessionId;
    public static bool LogOnlyFlag => logOnly;
    public static long ModeGeneration => Interlocked.Read(ref modeGeneration);
    public static bool IsPaused => logOnly;
    public static string CurrentStatus => currentStatus;
    public static string CurrentDiagnostic => currentDiagnostic;
    public static int PendingTranslationCount => translationQueue.PendingFinalCount;
    public static int PendingDraftCount => translationQueue.PendingDraftCount;
    public static long SkippedTranslationCount => translationQueue.SkippedCount;
    public static long LastRequestElapsedMs => translationQueue.LastRequestElapsedMs;
    public static long LastCaptureToDisplayMs => Interlocked.Read(ref lastCaptureToDisplayMs);
    public static bool FirstUseFlag { get; }
    public static event Action? TranslationLogged;
    public static event Action? StatusChanged;

    static Translator()
    {
        FirstUseFlag = !models.Setting.IsConfigExist();
        caption = models.Caption.GetInstance();
        setting = models.Setting.Load();
        setting.PropertyChanged += SettingPropertyChanged;
        caption.StatusRoute = $"{setting.ApiName} → {setting.TargetLanguage}";
        SQLiteHistoryLogger.MarkPendingInterruptedAsync().GetAwaiter().GetResult();
    }

    public static void Start()
    {
        if (lifetimeCancellation != null)
            return;
        lifetimeCancellation = new CancellationTokenSource();
        CancellationToken token = lifetimeCancellation.Token;
        Task[] translationWorkers = translationQueue.Start(token);
        workers = [Task.Run(() => SyncLoopAsync(token), token), .. translationWorkers];
        SetStatus("Connecting to system captions");
    }

    public static async Task StopAsync()
    {
        lifetimeCancellation?.Cancel();
        translationQueue.Complete();
        if (workers.Length > 0)
            await Task.WhenAny(Task.WhenAll(workers), Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        try
        {
            Setting.FlushPendingSave();
        }
        catch
        {
            SetDiagnostic("Settings could not be fully saved before exit.");
        }
        ReleaseLiveCaptions();
    }

    public static void SetLogOnly(bool enabled)
    {
        lock (modeStateLock)
        {
            if (logOnly == enabled)
                return;
            logOnly = enabled;
            Interlocked.Increment(ref modeGeneration);
        }

        if (enabled)
        {
            translationQueue.CancelPending(TranslationStatus.SourceOnly, "Paused");
            _ = MarkSessionSourceOnlyAsync();
            caption.ClearContexts();
            SetStatus("Paused");
            caption.DisplayTranslatedCaption = LocalizationService.Get("[Paused]");
            caption.OverlayNoticePrefix = LocalizationService.Get("[Paused]");
            caption.OverlayCurrentTranslation = string.Empty;
        }
        else
        {
            translationServiceFailure = false;
            caption.ClearContexts();
            caption.TranslatedCaption = string.Empty;
            caption.DisplayTranslatedCaption = string.Empty;
            caption.OverlayCurrentTranslation = string.Empty;
            caption.OverlayTranslatedSourceText = string.Empty;
            caption.OverlayNoticePrefix = string.Empty;
            caption.OverlayTranslationSegmentId = 0;
            caption.OverlayTranslationRevision = 0;
            caption.OverlayTranslationSessionId = null;
            caption.OverlayTranslationEpoch = 0;
            SetStatus("Waiting for captions");
        }
    }

    public static async Task EnsureLiveCaptionsAsync(CancellationToken token = default)
    {
        if (Window != null)
            return;

        await liveCaptionsConnectLock.WaitAsync(token);
        try
        {
            if (Window != null)
                return;
            LiveCaptionsSession session = await LiveCaptionsHandler.ConnectAsync(token);
            liveCaptionsSession = session;
            Window = session.Window;
            LiveCaptionsHandler.InitializeSessionOrRelease(
                session,
                captionWindow =>
                {
                    token.ThrowIfCancellationRequested();
                    LiveCaptionsHandler.FixLiveCaptions(captionWindow);
                    if (!FirstUseFlag)
                        LiveCaptionsHandler.HideLiveCaptions(captionWindow);
                },
                _ => ReleaseLiveCaptions());
        }
        finally
        {
            liveCaptionsConnectLock.Release();
        }
    }

    public static void ReleaseLiveCaptions()
    {
        var session = liveCaptionsSession;
        liveCaptionsSession = null;
        Window = null;
        if (session == null)
            return;
        try
        {
            bool released = LiveCaptionsHandler.ReleaseSession(session,
                LiveCaptionsHandler.KillLiveCaptions,
                LiveCaptionsHandler.RestoreLiveCaptions);
            if (!released)
                Debug.WriteLine("Windows Live Captions did not exit within the shutdown timeout.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not release the Live Captions session: {ex.GetType().Name}.");
        }
    }

    private static async Task SyncLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await EnsureLiveCaptionsAsync(token);
                string fullText = LiveCaptionsHandler.GetCaptions(Window!);
                IReadOnlyList<CaptionSegmentEvent> updates = segmenter.ProcessSnapshot(fullText);
                foreach (CaptionSegmentEvent update in updates)
                    await ProcessSegmentEventAsync(update, token);
                if (updates.Count == 0 && string.IsNullOrWhiteSpace(fullText) &&
                    !translationServiceFailure && !historyWriteFailure)
                    SetStatus(logOnly ? "Paused" : "Waiting for captions");
                await Task.Delay(string.IsNullOrWhiteSpace(fullText)
                    ? IdleCaptionPollInterval : ActiveCaptionPollInterval, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (ElementNotAvailableException)
            {
                ReleaseLiveCaptions();
                SetStatus("Connecting to system captions");
                await Task.Delay(250, token);
            }
            catch (Exception ex)
            {
                SetStatus("Connecting to system captions");
                SetDiagnostic($"System captions could not be read: {ex.GetType().Name}.");
                await Task.Delay(1000, token);
            }
        }
    }

    private static async Task ProcessSegmentEventAsync(CaptionSegmentEvent update, CancellationToken token)
    {
        if (update.Kind == CaptionSegmentEventKind.Diagnostic)
        {
            SetDiagnostic(update.Diagnostic ?? "Caption text could not be aligned.");
            return;
        }

        if (update.Kind == CaptionSegmentEventKind.DraftRevision)
        {
            ShowSource(update);
            if (logOnly)
            {
                SetStatus("Paused");
                return;
            }
            if (!draftThrottle.ShouldAttempt(update.SegmentId, Setting.MaxSyncInterval))
            {
                SetStatus("Waiting for next caption change");
                return;
            }
            long generation = ModeGeneration;
            TranslationSettingsSnapshot draftSettings = await CaptureSettingsAsync();
            if (logOnly || generation != ModeGeneration)
                return;
            var draft = new CaptionUpdate(update.SessionId, update.Epoch, update.SegmentId,
                update.Revision, update.Text, false, update.CapturedTimestamp, update.CapturedAt,
                ModeGeneration: generation);
            translationQueue.Enqueue(new TranslationRequest(draft, draftSettings));
            return;
        }

        ShowSource(update);
        long eventModeGeneration = ModeGeneration;
        bool isPaused = logOnly;
        TranslationStatus initialStatus = isPaused
            ? TranslationStatus.SourceOnly : TranslationStatus.Pending;
        string apiUsed = isPaused ? "Paused" : Setting.ApiName;
        long historyId;
        try
        {
            historyId = await SQLiteHistoryLogger.SaveSourceAsync(
                update.SessionId, update.Epoch, update.SegmentId, update.Revision, update.Text,
                Setting.TargetLanguage, apiUsed, initialStatus, token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            historyWriteFailure = true;
            SetStatus("History write failure");
            SetDiagnostic($"A finalized caption could not be stored: {ex.GetType().Name}.");
            return;
        }
        historyWriteFailure = false;
        TranslationLogged?.Invoke();

        if (isPaused || eventModeGeneration != ModeGeneration)
        {
            if (!isPaused)
                await MarkSourceOnlyAsync(historyId, update, "PauseChangedDuringSave");
            SetStatus("Paused");
            return;
        }

        var final = new CaptionUpdate(update.SessionId, update.Epoch, update.SegmentId,
            update.Revision, update.Text, true, update.CapturedTimestamp, update.CapturedAt,
            historyId, eventModeGeneration);
        TranslationSettingsSnapshot finalSettings = await CaptureSettingsAsync();
        if (logOnly || eventModeGeneration != ModeGeneration)
        {
            await MarkSourceOnlyAsync(historyId, update, "PauseChangedDuringSnapshot");
            SetStatus("Paused");
            return;
        }
        TranslationEnqueueResult queued = translationQueue.Enqueue(
            new TranslationRequest(final, finalSettings));
        if (!queued.Accepted && queued.EvictedRequest is null &&
            update.SegmentId == caption.OverlaySourceSegmentId)
            SetStatus("Translating");
    }

    private static void ShowSource(CaptionSegmentEvent update)
    {
        bool preserveDraftTranslation = CaptionDraftTranslationMatcher.MatchesCurrentCaption(
            update.SessionId, update.Epoch, update.SegmentId, update.Revision, update.Text,
            caption.OverlayTranslationSessionId, caption.OverlayTranslationEpoch,
            caption.OverlayTranslationSegmentId, caption.OverlayTranslationRevision,
            caption.OverlayTranslatedSourceText);
        caption.OriginalCaption = update.Text;
        caption.OverlaySessionId = update.SessionId;
        caption.OverlayEpoch = update.Epoch;
        caption.OverlaySourceSegmentId = update.SegmentId;
        caption.OverlaySourceRevision = update.Revision;
        caption.DisplayOriginalCaption = TextUtil.ShortenDisplaySentence(
            update.Text, TextUtil.VERYLONG_THRESHOLD);
        caption.OverlayOriginalCaption = update.Text;
        if (!preserveDraftTranslation)
        {
            caption.TranslatedCaption = string.Empty;
            caption.DisplayTranslatedCaption = string.Empty;
            caption.OverlayCurrentTranslation = string.Empty;
            caption.OverlayTranslatedSourceText = string.Empty;
            caption.OverlayNoticePrefix = string.Empty;
            caption.OverlayTranslationSegmentId = 0;
            caption.OverlayTranslationRevision = 0;
            caption.OverlayTranslationSessionId = null;
            caption.OverlayTranslationEpoch = 0;
        }
        SetStatus(logOnly ? "Paused" : "Translating");
    }

    private static Task<TranslationSettingsSnapshot> CaptureSettingsAsync()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            return dispatcher.InvokeAsync(CaptureSettingsOnCurrentThread).Task;
        return Task.FromResult(CaptureSettingsOnCurrentThread());
    }

    private static TranslationSettingsSnapshot CaptureSettingsOnCurrentThread()
    {
        return CreateSettingsSnapshot(Setting.ApiName, Setting.TargetLanguage, Setting.Prompt,
            Setting.ContextAware, Setting.MainWindow.LatencyShow, Setting[Setting.ApiName], Caption.AwareContexts);
    }

    internal static TranslationSettingsSnapshot CreateSettingsSnapshot(
        string apiName,
        string targetLanguage,
        string prompt,
        bool contextAware,
        bool latencyShow,
        TranslateAPIConfig currentConfig,
        IEnumerable<TranslationHistoryEntry> contextEntries)
    {
        Type configType = currentConfig.GetType();
        string serializedConfig = JsonSerializer.Serialize(currentConfig, configType);
        var configCopy = (TranslateAPIConfig?)JsonSerializer.Deserialize(serializedConfig, configType)
                         ?? new TranslateAPIConfig();
        var contexts = contextEntries.Select(entry => new TranslationHistoryEntry
        {
            SegmentId = entry.SegmentId,
            Timestamp = entry.Timestamp,
            TimestampFull = entry.TimestampFull,
            SourceText = entry.SourceText,
            TranslatedText = entry.TranslatedText,
            TargetLanguage = entry.TargetLanguage,
            ApiUsed = entry.ApiUsed,
            Status = entry.Status,
            ErrorCode = entry.ErrorCode,
            ErrorMessage = entry.ErrorMessage
        }).ToArray();
        return new TranslationSettingsSnapshot(apiName, targetLanguage, prompt,
            contextAware, latencyShow, configCopy, Array.AsReadOnly(contexts));
    }

    private static async Task ProcessTranslationAsync(TranslationRequest request, CancellationToken token)
    {
        CaptionUpdate update = request.Update;
        bool mayStart;
        lock (modeStateLock)
        {
            mayStart = IsModeCurrentLocked(update);
            if (mayStart)
                SetStatus("Translating");
        }
        if (!mayStart)
        {
            if (update.IsFinal)
                await MarkPendingSourceOnlyAsync(update, "Paused");
            return;
        }

        TranslationOutcome outcome = await TranslateAsync(request.Settings, update.Text, token);
        if (token.IsCancellationRequested)
            return;
        if (!IsModeCurrent(update))
        {
            if (update.IsFinal)
                await MarkPendingSourceOnlyAsync(update, "Paused");
            return;
        }

        if (update.IsFinal)
        {
            bool saved = await SQLiteHistoryLogger.CompleteTranslationAsync(
                update.HistoryId, update.SessionId, update.SegmentId, update.Revision, outcome, token);
            if (!saved)
                return;

            bool modeChanged;
            lock (modeStateLock)
            {
                modeChanged = !IsModeCurrentLocked(update);
                if (!modeChanged && outcome.Status == TranslationStatus.Succeeded)
                {
                    caption.AddContext(new TranslationHistoryEntry
                    {
                        HistoryId = update.HistoryId,
                        SessionId = update.SessionId,
                        Epoch = update.Epoch,
                        SegmentId = update.SegmentId,
                        Revision = update.Revision,
                        Timestamp = update.CapturedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
                        TimestampFull = update.CapturedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                        SourceText = update.Text,
                        TranslatedText = outcome.Text,
                        TargetLanguage = request.Settings.TargetLanguage,
                        ApiUsed = request.Settings.ApiName,
                        Status = outcome.Status.ToString()
                    });
                }
            }
            if (modeChanged)
            {
                await MarkPendingSourceOnlyAsync(update, "Paused");
                return;
            }

            TranslationLogged?.Invoke();
        }

        bool shouldDisplay;
        lock (modeStateLock)
        {
            shouldDisplay = IsModeCurrentLocked(update) && IsCurrentOrTranslatableDraft(update);
            if (shouldDisplay)
                DisplayOutcome(update, outcome);
        }
        if (!shouldDisplay && update.IsFinal && !IsModeCurrent(update))
            await MarkPendingSourceOnlyAsync(update, "Paused");
    }

    private static bool IsCurrent(CaptionUpdate update) =>
        caption.OverlaySessionId == update.SessionId &&
        caption.OverlayEpoch == update.Epoch &&
        caption.OverlaySourceSegmentId == update.SegmentId &&
        caption.OverlaySourceRevision == update.Revision;

    private static bool IsCurrentOrTranslatableDraft(CaptionUpdate update) =>
        IsCurrent(update) || (!update.IsFinal && CaptionDraftTranslationMatcher.MatchesCurrentCaption(
            caption.OverlaySessionId, caption.OverlayEpoch, caption.OverlaySourceSegmentId,
            caption.OverlaySourceRevision, caption.OverlayOriginalCaption,
            update.SessionId, update.Epoch, update.SegmentId, update.Revision, update.Text));

    private static bool IsModeCurrent(CaptionUpdate update)
    {
        lock (modeStateLock)
            return IsModeCurrentLocked(update);
    }

    private static bool IsModeCurrentLocked(CaptionUpdate update) =>
        !logOnly && update.ModeGeneration == Interlocked.Read(ref modeGeneration);

    private static void DisplayOutcome(CaptionUpdate update, TranslationOutcome outcome)
    {
        Interlocked.Exchange(ref lastCaptureToDisplayMs,
            Math.Max(0, (long)TimeProvider.System.GetElapsedTime(
                update.CapturedTimestamp, TimeProvider.System.GetTimestamp()).TotalMilliseconds));
        if (outcome.Status == TranslationStatus.Succeeded)
        {
            translationServiceFailure = false;
            caption.TranslatedCaption = outcome.Text;
            caption.DisplayTranslatedCaption = TextUtil.ShortenDisplaySentence(
                outcome.Text, TextUtil.VERYLONG_THRESHOLD);
            caption.OverlayTranslationSegmentId = update.SegmentId;
            caption.OverlayTranslationRevision = update.Revision;
            caption.OverlayTranslationSessionId = update.SessionId;
            caption.OverlayTranslationEpoch = update.Epoch;
            caption.OverlayTranslatedSourceText = update.Text;
            var match = RegexPatterns.NoticePrefixAndTranslation().Match(outcome.Text);
            caption.OverlayNoticePrefix = match.Groups[1].Value.Trim();
            caption.OverlayCurrentTranslation = match.Groups[2].Value.Trim();
            SetStatus("Ready");
            SetDiagnostic(string.Empty);
            return;
        }

        translationServiceFailure = true;
        caption.TranslatedCaption = string.Empty;
        caption.DisplayTranslatedCaption = LocalizationService.Get("Translation failed");
        caption.OverlayTranslationSegmentId = update.SegmentId;
        caption.OverlayTranslationRevision = update.Revision;
        caption.OverlayTranslationSessionId = update.SessionId;
        caption.OverlayTranslationEpoch = update.Epoch;
        caption.OverlayTranslatedSourceText = string.Empty;
        caption.OverlayCurrentTranslation = string.Empty;
        caption.OverlayNoticePrefix = LocalizationService.Get("[Translation failed]");
        SetStatus("Service failure");
        SetDiagnostic(outcome.Diagnostic);
    }

    private static async Task<TranslationOutcome> TranslateAsync(
        TranslationSettingsSnapshot settingsSnapshot, string text, CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            string translatedText;
            if (settingsSnapshot.ContextAware && !TranslateAPI.IsLLMBasedApi(settingsSnapshot.ApiName))
            {
                string contextText = string.Join(" ", settingsSnapshot.Contexts.Select(entry => entry.SourceText));
                TranslationOutcome contextual = await TranslateAPI.TranslateOutcomeAsync(
                    settingsSnapshot, $"{contextText} 🔤 {text} 🔤", token);
                if (contextual.Status != TranslationStatus.Succeeded)
                    return contextual with { ElapsedMs = (long)stopwatch.Elapsed.TotalMilliseconds };
                translatedText = RegexPatterns.TargetSentence().Match(contextual.Text).Groups[1].Value;
            }
            else
            {
                TranslationOutcome result = await TranslateAPI.TranslateOutcomeAsync(settingsSnapshot, text, token);
                if (result.Status != TranslationStatus.Succeeded)
                    return result with { ElapsedMs = (long)stopwatch.Elapsed.TotalMilliseconds };
                translatedText = result.Text.Replace("🔤", string.Empty);
            }

            if (settingsSnapshot.LatencyShow)
                translatedText = $"[{(long)stopwatch.Elapsed.TotalMilliseconds,4} ms] " + translatedText;
            return new TranslationOutcome(TranslationStatus.Succeeded, translatedText,
                string.Empty, string.Empty, (long)stopwatch.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    private static async Task MarkDroppedAsync(
        TranslationRequest request, TranslationStatus status, string reason)
    {
        CaptionUpdate update = request.Update;
        if (!update.IsFinal || update.HistoryId <= 0)
            return;
        try
        {
            string message = reason switch
            {
                "QueueCapacity" => "Skipped because the translation queue was full.",
                "QueueWaitExpired" => "Skipped because the request waited too long.",
                "SupersededAfterTimeout" => "Skipped because a newer caption was waiting.",
                _ => string.Empty
            };
            if (status == TranslationStatus.SourceOnly)
                await SQLiteHistoryLogger.MarkSourceOnlyAsync(update.HistoryId, update.SessionId,
                    update.SegmentId, update.Revision, reason);
            else
                await SQLiteHistoryLogger.MarkPendingAsync(update.HistoryId, update.SessionId,
                    update.SegmentId, update.Revision, status,
                    status == TranslationStatus.Skipped ? reason : string.Empty, message);
            TranslationLogged?.Invoke();
        }
        catch (Exception ex)
        {
            SetDiagnostic($"A caption status could not be saved: {ex.GetType().Name}.");
        }
    }

    private static async Task MarkPendingSourceOnlyAsync(CaptionUpdate update, string reason)
    {
        try
        {
            await SQLiteHistoryLogger.MarkSourceOnlyAsync(update.HistoryId, update.SessionId,
                update.SegmentId, update.Revision, reason);
            TranslationLogged?.Invoke();
        }
        catch (Exception ex)
        {
            SetDiagnostic($"A caption status could not be saved: {ex.GetType().Name}.");
        }
    }

    private static async Task MarkSourceOnlyAsync(long historyId, CaptionSegmentEvent update, string reason)
    {
        try
        {
            await SQLiteHistoryLogger.MarkPendingAsync(historyId, update.SessionId,
                update.SegmentId, update.Revision, TranslationStatus.SourceOnly, reason, string.Empty);
            TranslationLogged?.Invoke();
        }
        catch (Exception ex)
        {
            SetDiagnostic($"A caption status could not be saved: {ex.GetType().Name}.");
        }
    }

    private static async Task MarkSessionSourceOnlyAsync()
    {
        try
        {
            await SQLiteHistoryLogger.MarkSessionPendingSourceOnlyAsync(SessionId);
            TranslationLogged?.Invoke();
        }
        catch (Exception ex)
        {
            SetDiagnostic($"Pending captions could not be marked as source only: {ex.GetType().Name}.");
        }
    }

    private static void SetServiceFailure(Exception exception)
    {
        translationServiceFailure = true;
        SetStatus("Service failure");
        SetDiagnostic($"Translation processing failed: {exception.GetType().Name}.");
    }

    internal static void SetStatus(string value)
    {
        currentStatus = value;
        caption.StatusMessage = LocalizationService.Get(value);
        caption.StatusRoute = $"{Setting.ApiName} → {Setting.TargetLanguage}";
        StatusChanged?.Invoke();
    }

    private static void SetDiagnostic(string value)
    {
        currentDiagnostic = value;
        caption.StatusDiagnostic = value;
        StatusChanged?.Invoke();
    }

    private static void SettingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Setting.ApiName) or nameof(Setting.TargetLanguage))
            caption.StatusRoute = $"{Setting.ApiName} → {Setting.TargetLanguage}";
        else if (e.PropertyName == nameof(Setting.UiLanguage))
            caption.StatusMessage = LocalizationService.Get(currentStatus);
    }

    public static async Task Log(string originalText, string translatedText,
        bool isOverwrite = false, CancellationToken token = default)
    {
        await SQLiteHistoryLogger.LogTranslation(originalText, translatedText,
            Setting.TargetLanguage, Setting.ApiName, isOverwrite, token);
        TranslationLogged?.Invoke();
    }

    public static async Task AddContexts(CancellationToken token = default)
    {
        TranslationHistoryEntry? lastLog = await SQLiteHistoryLogger.LoadLastTranslation(token);
        if (lastLog?.Status == nameof(TranslationStatus.Succeeded))
            Caption.AddContext(lastLog);
    }

    public static void ClearContexts() => Caption.ClearContexts();

    public static async Task<bool> IsOverwrite(string originalText, CancellationToken token = default)
    {
        string lastOriginalText = await SQLiteHistoryLogger.LoadLastSourceText(token);
        if (string.IsNullOrEmpty(lastOriginalText))
            return false;
        int minLen = Math.Min(originalText.Length, lastOriginalText.Length);
        double similarity = TextUtil.Similarity(originalText[..minLen], lastOriginalText[..minLen]);
        return similarity > TextUtil.SIM_THRESHOLD;
    }
}
