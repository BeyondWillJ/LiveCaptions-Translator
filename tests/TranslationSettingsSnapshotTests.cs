using LiveCaptionsTranslator;
using LiveCaptionsTranslator.models;

namespace LiveCaptionsTranslator.Tests;

public class TranslationSettingsSnapshotTests
{
    [Fact]
    public void Capture_ClonesProviderConfigAndCompletedContexts()
    {
        var config = new OpenAIConfig { ApiKey = "snapshot-key", ModelName = "model-before-edit" };
        var context = new TranslationHistoryEntry
        {
            Timestamp = "2026-01-01 00:00:00",
            TimestampFull = "2026-01-01 00:00:00",
            SourceText = "source before edit",
            TranslatedText = "translation before edit",
            TargetLanguage = "ja-JP",
            ApiUsed = "OpenAI"
        };
        var sourceContexts = new List<TranslationHistoryEntry> { context };

        TranslationSettingsSnapshot snapshot = Translator.CreateSettingsSnapshot(
            "OpenAI", "ja-JP", "prompt before edit", true, true, config, sourceContexts);

        config.ApiKey = "edited-key";
        config.ModelName = "model-after-edit";
        context.SourceText = "source after edit";
        sourceContexts.Clear();

        var copiedConfig = Assert.IsType<OpenAIConfig>(snapshot.Config);
        Assert.Equal("snapshot-key", copiedConfig.ApiKey);
        Assert.Equal("model-before-edit", copiedConfig.ModelName);
        Assert.Single(snapshot.Contexts);
        Assert.Equal("source before edit", snapshot.Contexts[0].SourceText);
        Assert.Equal("translation before edit", snapshot.Contexts[0].TranslatedText);
        Assert.Equal("prompt before edit", snapshot.Prompt);
    }
}
