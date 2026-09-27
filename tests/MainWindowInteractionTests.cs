using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.utils;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace LiveCaptionsTranslator.Tests;

public class MainWindowInteractionTests
{
    [Fact]
    public void OverlayWindow_RendersMatchingSourceAndLateTranslationIntoCaptionRuns()
    {
        WpfTestDispatcher.Invoke(() =>
        {
            var caption = Translator.Caption;
            var settings = Translator.Setting.OverlayWindow;
            var previous = new OverlayCaptionState(
                caption.OverlaySourceSegmentId, caption.OverlaySourceRevision,
                caption.OverlayOriginalCaption, caption.OverlaySessionId, caption.OverlayEpoch,
                caption.OverlayTranslationSegmentId, caption.OverlayTranslationRevision,
                caption.OverlayCurrentTranslation, caption.OverlayTranslationSessionId,
                caption.OverlayTranslationEpoch, caption.OverlayTranslatedSourceText,
                caption.OverlayNoticePrefix, settings.SilenceClearDelay);
            OverlayWindow? window = null;

            try
            {
                settings.SilenceClearDelay = 0;
                var session = Guid.NewGuid();
                caption.OverlayOriginalCaption = string.Empty;
                caption.OverlayCurrentTranslation = string.Empty;
                caption.OverlayNoticePrefix = string.Empty;
                caption.OverlaySourceSegmentId = 0;
                caption.OverlaySourceRevision = 0;
                caption.OverlaySessionId = null;
                caption.OverlayEpoch = 0;
                caption.OverlayTranslationSegmentId = 0;
                caption.OverlayTranslationRevision = 0;
                caption.OverlayTranslationSessionId = null;
                caption.OverlayTranslationEpoch = 0;
                caption.OverlayTranslatedSourceText = string.Empty;

                window = new OverlayWindow { Opacity = 0 };
                window.Show();
                PumpDispatcher();

                const string source = "日本語の字幕";
                const string translation = "对应的中文译文";
                caption.OverlayOriginalCaption = source;
                caption.OverlaySourceSegmentId = 42;
                caption.OverlaySourceRevision = 1;
                caption.OverlaySessionId = session;
                caption.OverlayEpoch = 3;
                PumpDispatcher();

                caption.OverlayCurrentTranslation = translation;
                caption.OverlayTranslationSegmentId = 42;
                caption.OverlayTranslationRevision = 1;
                caption.OverlayTranslationSessionId = session;
                caption.OverlayTranslationEpoch = 3;
                caption.OverlayTranslatedSourceText = source;
                PumpDispatcher();

                var original = Assert.IsType<System.Windows.Controls.TextBlock>(
                    window.FindName("OriginalCaption"));
                var translatedRun = Assert.IsType<System.Windows.Documents.Run>(
                    window.FindName("CurrentTranslationRun"));
                Assert.Equal(source, original.Text);
                Assert.Equal(translation, translatedRun.Text);
            }
            finally
            {
                try { window?.Close(); }
                catch { }
                caption.OverlaySourceSegmentId = previous.SourceSegmentId;
                caption.OverlaySourceRevision = previous.SourceRevision;
                caption.OverlayOriginalCaption = previous.Original;
                caption.OverlaySessionId = previous.SessionId;
                caption.OverlayEpoch = previous.Epoch;
                caption.OverlayTranslationSegmentId = previous.TranslationSegmentId;
                caption.OverlayTranslationRevision = previous.TranslationRevision;
                caption.OverlayCurrentTranslation = previous.Translation;
                caption.OverlayTranslationSessionId = previous.TranslationSessionId;
                caption.OverlayTranslationEpoch = previous.TranslationEpoch;
                caption.OverlayTranslatedSourceText = previous.TranslatedSource;
                caption.OverlayNoticePrefix = previous.NoticePrefix;
                settings.SilenceClearDelay = previous.SilenceClearDelay;
            }
        });
    }

    [Fact]
    public void OverlayWindow_AppliesNotoSerifJpMediumToBothCaptions()
    {
        WpfTestDispatcher.Invoke(() =>
        {
            var overlaySettings = Translator.Setting.OverlayWindow;
            string previousFontFamily = overlaySettings.FontFamily;
            int previousFontWeight = overlaySettings.FontWeight;
            int previousFontStretch = overlaySettings.FontStretch;
            string previousFontStyle = overlaySettings.FontStyle;
            var previousFontBold = overlaySettings.FontBold;
            OverlayWindow? window = null;

            try
            {
                overlaySettings.FontFamily = "Noto Serif JP";
                overlaySettings.FontWeight = 500;
                overlaySettings.FontStretch = 5;
                overlaySettings.FontStyle = "Normal";
                overlaySettings.FontBold = LiveCaptionsTranslator.Utils.FontBold.None;

                window = new OverlayWindow();
                var original = Assert.IsType<TextBlock>(window.FindName("OriginalCaption"));
                var translated = Assert.IsType<TextBlock>(window.FindName("TranslatedCaption"));

                Assert.Equal("Noto Serif JP", original.FontFamily.Source);
                Assert.Equal("Noto Serif JP", translated.FontFamily.Source);
                Assert.Equal(500, original.FontWeight.ToOpenTypeWeight());
                Assert.Equal(500, translated.FontWeight.ToOpenTypeWeight());
                Assert.Equal(System.Windows.FontWeights.Medium, original.FontWeight);
                Assert.Equal(System.Windows.FontWeights.Medium, translated.FontWeight);
                Assert.Contains(original.FontFamily.GetTypefaces(), typeface =>
                    typeface.Weight == System.Windows.FontWeights.Medium);
            }
            finally
            {
                try { window?.Close(); }
                catch { }
                overlaySettings.FontFamily = previousFontFamily;
                overlaySettings.FontWeight = previousFontWeight;
                overlaySettings.FontStretch = previousFontStretch;
                overlaySettings.FontStyle = previousFontStyle;
                overlaySettings.FontBold = previousFontBold;
                Translator.Setting.FlushPendingSave();
            }
        });
    }

    [Fact]
    public void PauseButton_RoutedClicksTogglePauseStateAndStatusImmediately()
    {
        WpfTestDispatcher.Invoke(() =>
        {
            MainWindow? window = null;
            var setting = Translator.Setting;
            bool originalPaused = Translator.IsPaused;
            bool originalTopmost = setting.MainWindow.Topmost;
            Dictionary<string, string> originalBounds = new(setting.WindowBounds);
            try
            {
                if (Translator.IsPaused)
                    Translator.SetLogOnly(false);

                window = new MainWindow();
                var pauseButton = Assert.IsAssignableFrom<Button>(window.FindName("LogOnlyButton"));
                var icon = Assert.IsType<SymbolIcon>(pauseButton.Icon);

                pauseButton.RaiseEvent(new System.Windows.RoutedEventArgs(
                    ButtonBase.ClickEvent, pauseButton));

                Assert.True(Translator.IsPaused);
                Assert.Equal("Paused", Translator.CurrentStatus);
                Assert.Equal(LocalizationService.Get("[Paused]"), Translator.Caption.DisplayTranslatedCaption);
                Assert.True(icon.Filled);

                pauseButton.RaiseEvent(new System.Windows.RoutedEventArgs(
                    ButtonBase.ClickEvent, pauseButton));

                Assert.False(Translator.IsPaused);
                Assert.Equal("Waiting for captions", Translator.CurrentStatus);
                Assert.Empty(Translator.Caption.DisplayTranslatedCaption);
                Assert.False(icon.Filled);
            }
            finally
            {
                try { window?.Close(); }
                catch { }
                if (Translator.IsPaused != originalPaused)
                    Translator.SetLogOnly(originalPaused);
                setting.MainWindow.Topmost = originalTopmost;
                setting.WindowBounds = originalBounds;
                setting.FlushPendingSave();
            }
        });
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private sealed record OverlayCaptionState(
        long SourceSegmentId,
        long SourceRevision,
        string Original,
        Guid? SessionId,
        long Epoch,
        long TranslationSegmentId,
        long TranslationRevision,
        string Translation,
        Guid? TranslationSessionId,
        long TranslationEpoch,
        string TranslatedSource,
        string NoticePrefix,
        double SilenceClearDelay);
}
