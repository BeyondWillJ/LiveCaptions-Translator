using System.Windows;

using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public partial class App : Application
    {
        App()
        {
            LocalizationService.Initialize(Translator.Setting?.UiLanguage ?? "zh-CN");
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            Translator.Setting?.ScheduleSave();
            Translator.Start();
        }

        private static void OnProcessExit(object? sender, EventArgs e)
        {
            Translator.StopAsync().GetAwaiter().GetResult();
        }
    }
}
