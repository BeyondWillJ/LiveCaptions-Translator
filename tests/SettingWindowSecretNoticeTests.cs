using LiveCaptionsTranslator.utils;
using Wpf.Ui.Controls;

namespace LiveCaptionsTranslator.Tests;

public class SettingWindowSecretNoticeTests
{
    [Fact]
    public void SecretStorageNoticeTracksFailuresPathsAndLanguageChanges()
    {
        string originalLanguage = LocalizationService.CurrentLanguage;

        WpfTestDispatcher.Invoke(() =>
        {
            SecretProtector.ResetLoadStatus();
            SettingWindow? window = null;
            try
            {
                window = new SettingWindow();
                var notice = Assert.IsType<TextBlock>(window.FindName("SecretStorageNotice"));
                Assert.Contains(LocalizationService.Get(
                    "API keys are protected for the current Windows user; re-enter them on another device."),
                    notice.Text);

                Assert.Empty(SecretProtector.Unprotect("dpapi:v1:not-valid-ciphertext"));
                Assert.Contains(LocalizationService.Get(
                    "Saved API keys could not be decrypted. Enter them again."), notice.Text);

                const string cleanupPath = "C:\\legacy\\setting.json.bak";
                const string persistencePath = "C:\\current\\setting.json";
                SecretProtector.SetCleanupWarning(cleanupPath);
                SecretProtector.SetPersistenceWarning(persistencePath);
                Assert.Contains(cleanupPath, notice.Text);
                Assert.Contains(persistencePath, notice.Text);

                LocalizationService.SetLanguage("ja-JP", save: false);
                Assert.Contains(LocalizationService.Get(
                    "Saved API keys could not be decrypted. Enter them again."), notice.Text);
                Assert.Contains(LocalizationService.Get("Settings could not be saved securely:"), notice.Text);
            }
            finally
            {
                window?.StopWatchingSecretStorageStatus();
                try { window?.Close(); }
                catch { }
                SecretProtector.ResetLoadStatus();
                LocalizationService.SetLanguage(originalLanguage, save: false);
            }
        });
    }
}
