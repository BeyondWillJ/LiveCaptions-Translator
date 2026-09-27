using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.Tests;

public class LocalizationStateTests
{
    [Fact]
    public void LanguageChange_RelocalizesStatusAndPreservesCurrentServiceRoute()
    {
        Setting setting = Translator.Setting;
        string originalLanguage = LocalizationService.CurrentLanguage;
        string originalApi = setting.ApiName;
        string originalTarget = setting.TargetLanguage;
        try
        {
            LocalizationService.SetLanguage("zh-CN", save: false);
            setting.ApiName = "OpenAI";
            setting.TargetLanguage = "ja-JP";
            Translator.SetStatus("Service failure");

            LocalizationService.SetLanguage("ja-JP");

            Assert.Equal("OpenAI → ja-JP", Translator.Caption.StatusRoute);
            Assert.Equal("翻訳サービスに問題があります", Translator.Caption.StatusMessage);
            Assert.Equal("OpenAI", setting.ApiName);
            Assert.Equal("ja-JP", setting.TargetLanguage);
        }
        finally
        {
            setting.ApiName = originalApi;
            setting.TargetLanguage = originalTarget;
            LocalizationService.SetLanguage(originalLanguage);
            setting.FlushPendingSave();
        }
    }
}
