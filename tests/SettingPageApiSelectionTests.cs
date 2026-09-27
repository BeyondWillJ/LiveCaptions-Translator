using System.Windows.Controls;

namespace LiveCaptionsTranslator.Tests;

public class SettingPageApiSelectionTests
{
    [Fact]
    public void SavedOpenAiSelection_RemainsSelectedAndDoesNotRewritePreferenceDuringInitialization()
    {
        WpfTestDispatcher.Invoke(() =>
        {
            string persistedApi = "OpenAI";
            bool initializing = true;
            var combo = new ComboBox();
            combo.SelectionChanged += (_, _) =>
            {
                if (!initializing && combo.SelectedItem is string selected)
                    persistedApi = selected;
            };
            combo.ItemsSource = LiveCaptionsTranslator.apis.TranslateAPI.TRANSLATE_FUNCTIONS.Keys.ToArray();
            combo.SelectedItem = SettingPage.ResolveInitialApiSelection(
                persistedApi, LiveCaptionsTranslator.apis.TranslateAPI.TRANSLATE_FUNCTIONS.Keys);
            initializing = false;

            Assert.Equal("OpenAI", combo.SelectedItem);
            Assert.Equal("OpenAI", persistedApi);
        });
    }

    [Fact]
    public void SettingPage_PreservesSavedOpenAiSelectionAndSetting()
    {
        WpfTestDispatcher.Invoke(() =>
        {
            string? originalApi = null;
            try
            {
                var setting = LiveCaptionsTranslator.Translator.Setting;
                originalApi = setting.ApiName;
                setting.ApiName = "OpenAI";
                setting.FlushPendingSave();
                Assert.Equal("OpenAI", LiveCaptionsTranslator.models.Setting.Load(
                    LiveCaptionsTranslator.utils.AppPaths.SettingFile).ApiName);

                var page = new LiveCaptionsTranslator.SettingPage();
                var combo = Assert.IsType<ComboBox>(page.FindName("TranslateAPIBox"));

                Assert.Equal("OpenAI", combo.SelectedItem);
                Assert.Equal("OpenAI", setting.ApiName);
                Assert.Equal(setting, page.DataContext);
            }
            finally
            {
                if (originalApi != null)
                {
                    LiveCaptionsTranslator.Translator.Setting.ApiName = originalApi;
                    LiveCaptionsTranslator.Translator.Setting.FlushPendingSave();
                }
            }
        });
    }
}
