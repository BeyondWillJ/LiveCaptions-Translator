using System.Text.Json;
using LiveCaptionsTranslator;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.Utils;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.Tests;

public class SettingTests
{
    [Fact]
    public void Load_RepairsOutOfRangeAndEmptyConfigurations()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "setting.json");
        try
        {
            var setting = new Setting();
            setting.ConfigIndices["OpenAI"] = 999;
            setting.Configs["DeepL"].Clear();
            setting.Save(path);

            Setting loaded = Setting.Load(path);

            Assert.Equal(0, loaded.ConfigIndices["OpenAI"]);
            Assert.Single(loaded.Configs["DeepL"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LoadOrDefault_PreservesCorruptFileWithoutCreatingPlaintextBackup()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "setting.json");
        try
        {
            File.WriteAllText(path, "{ definitely-not-json");

            Setting loaded = Setting.LoadOrDefault(path);

            Assert.Equal("Google", loaded.ApiName);
            Assert.Empty(Directory.GetFiles(directory, "setting.json.corrupt-*"));
            Assert.Equal("{ definitely-not-json", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void NumericSettings_AreClampedIndependently()
    {
        var setting = new Setting
        {
            MaxSyncInterval = 0,
            NumContexts = -1,
            DisplaySentences = 50
        };

        Assert.Equal(1, setting.MaxSyncInterval);
        Assert.Equal(0, setting.NumContexts);
        Assert.Equal(10, setting.DisplaySentences);
    }

    [Fact]
    public void OverlayFont_DefaultsToNotoSerifJpMedium()
    {
        var overlay = new OverlayWindowState();

        Assert.Equal("Noto Serif JP", overlay.FontFamily);
        Assert.Equal(500, overlay.FontWeight);
        Assert.Equal(5, overlay.FontStretch);
        Assert.Equal("Normal", overlay.FontStyle);
    }

    [Fact]
    public void ContextAndDisplayCounts_RemainIndependentAfterSaveAndLoad()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "setting.json");
        try
        {
            var setting = new Setting
            {
                NumContexts = 7,
                DisplaySentences = 2
            };
            setting.Save(path);

            Setting loaded = Setting.Load(path);

            Assert.Equal(7, loaded.NumContexts);
            Assert.Equal(2, loaded.DisplaySentences);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void OverlayDisplayPreferences_PersistWithoutPersistingClickThrough()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "setting.json");
        try
        {
            var setting = new Setting();
            setting.OverlayWindow.DisplayMode = CaptionVisible.SubtitleOnly;
            setting.OverlayWindow.CaptionLocation = CaptionLocation.SubtitleTop;
            setting.Save(path);

            Setting loaded = Setting.Load(path);
            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement overlay = json.RootElement.GetProperty("OverlayWindow");

            Assert.Equal(CaptionVisible.SubtitleOnly, loaded.OverlayWindow.DisplayMode);
            Assert.Equal(CaptionLocation.SubtitleTop, loaded.OverlayWindow.CaptionLocation);
            Assert.False(overlay.TryGetProperty("IsClickThrough", out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FlushPendingSave_WritesTheLatestCapturedSettingsSnapshot()
    {
        Setting setting = Translator.Setting;
        string path = AppPaths.SettingFile;
        byte[]? original = File.Exists(path) ? File.ReadAllBytes(path) : null;
        string originalPrompt = setting.Prompt;
        try
        {
            setting.Prompt = "first captured value";
            setting.Prompt = "latest captured value";
            setting.FlushPendingSave();

            Assert.Equal("latest captured value", Setting.Load(path).Prompt);
        }
        finally
        {
            setting.Prompt = originalPrompt;
            setting.FlushPendingSave();
            if (original is null)
                File.Delete(path);
            else
                File.WriteAllBytes(path, original);
        }
    }

    [Fact]
    public void CaptionChangeThrottle_AttemptsEveryConfiguredRevisionAndResetsPerSentence()
    {
        var throttle = new CaptionChangeThrottle();

        Assert.False(throttle.ShouldAttempt(10, 3));
        Assert.False(throttle.ShouldAttempt(10, 3));
        Assert.True(throttle.ShouldAttempt(10, 3));
        Assert.False(throttle.ShouldAttempt(10, 3));
        Assert.False(throttle.ShouldAttempt(11, 3));
        Assert.True(throttle.ShouldAttempt(11, 2));
    }
}
