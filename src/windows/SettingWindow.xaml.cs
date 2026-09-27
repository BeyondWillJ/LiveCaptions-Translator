using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = Wpf.Ui.Controls.TextBlock;
using ComboBox = System.Windows.Controls.ComboBox;

namespace LiveCaptionsTranslator
{
    public partial class SettingWindow : FluentWindow
    {
        private System.Windows.Controls.Button? currentSelected;
        private Dictionary<string, FrameworkElement> sectionReferences = new();

        public SettingWindow()
        {
            InitializeComponent();
            ApplicationThemeManager.ApplySystemTheme();
            DataContext = Translator.Setting;
            SecretProtector.StatusChanged += RefreshSecretStorageNotice;
            LocalizationService.LanguageChanged += OnLanguageChanged;
            Closed += (_, _) => StopWatchingSecretStorageStatus();
            RefreshSecretStorageNotice();

            Loaded += (sender, args) =>
            {
                SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);
                Initialize();
                SelectButton(PromptButton);
            };
        }

        private void Initialize()
        {
            RefreshSecretStorageNotice();

            sectionReferences = new Dictionary<string, FrameworkElement>
            {
                { "General", ContentPanel },
                { "Prompt", PromptSection }
            };

            foreach (var apiName in TranslateAPI.TRANSLATE_FUNCTIONS.Keys.Where(apiName =>
                         !TranslateAPI.NO_CONFIG_APIS.Contains(apiName)))
            {
                if (FindName($"{apiName}Section") is StackPanel section)
                    sectionReferences[apiName] = section;
                SwitchConfig(apiName, Translator.Setting.ConfigIndices[apiName]);
            }
        }

        private void RefreshSecretStorageNotice()
        {
            if (!Dispatcher.CheckAccess())
            {
                if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                    Dispatcher.BeginInvoke(new Action(RefreshSecretStorageNotice), DispatcherPriority.DataBind);
                return;
            }

            var warnings = new List<string>
            {
                LocalizationService.Get("API keys are protected for the current Windows user; re-enter them on another device.")
            };
            if (SecretProtector.DecryptionFailed)
                warnings.Add(LocalizationService.Get("Saved API keys could not be decrypted. Enter them again."));
            if (!string.IsNullOrWhiteSpace(SecretProtector.CleanupWarning))
                warnings.Add($"{LocalizationService.Get("Remove the old plaintext file:")} {SecretProtector.CleanupWarning}");
            if (!string.IsNullOrWhiteSpace(SecretProtector.PersistenceWarning))
                warnings.Add($"{LocalizationService.Get("Settings could not be saved securely:")} {SecretProtector.PersistenceWarning}");
            SecretStorageNotice.Text = string.Join(" ", warnings);
        }

        private void OnLanguageChanged(string _) => RefreshSecretStorageNotice();

        internal void StopWatchingSecretStorageStatus()
        {
            SecretProtector.StatusChanged -= RefreshSecretStorageNotice;
            LocalizationService.LanguageChanged -= OnLanguageChanged;
        }

        private void NewButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string apiName &&
                Translator.Setting.Configs.TryGetValue(apiName, out var configs) &&
                Translator.Setting.ConfigIndices.TryGetValue(apiName, out int configIndex))
            {
                Type? type = Type.GetType($"LiveCaptionsTranslator.models.{apiName}Config");
                if (type is null || Activator.CreateInstance(type) is not TranslateAPIConfig config)
                    return;
                configs.Insert(configIndex + 1, config);
                SwitchConfig(apiName, configIndex + 1);

                Translator.Setting.OnPropertyChanged("Configs");
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string apiName &&
                Translator.Setting.Configs.TryGetValue(apiName, out var configs) &&
                Translator.Setting.ConfigIndices.TryGetValue(apiName, out int configIndex))
            {
                if (configs.Count <= 1)
                {
                    (FindName($"{apiName}DeleteFlyout") as Flyout)?.Show();
                    return;
                }
                configs.RemoveAt(configIndex);
                SwitchConfig(apiName, Math.Max(0, Math.Min(configs.Count - 1, configIndex)));

                Translator.Setting.OnPropertyChanged("Configs");
            }
        }

        private void PriorButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string apiName &&
                Translator.Setting.ConfigIndices.TryGetValue(apiName, out int configIndex))
            {
                SwitchConfig(apiName, configIndex - 1);
            }
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string apiName &&
                Translator.Setting.ConfigIndices.TryGetValue(apiName, out int configIndex))
            {
                SwitchConfig(apiName, configIndex + 1);
            }
        }

        private void NavigationButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button)
            {
                SelectButton(button);
                if (button.Tag is string targetSection &&
                    sectionReferences.TryGetValue(targetSection, out FrameworkElement? element))
                    element?.BringIntoView();
            }
        }

        private void OpenAIAPIUrlInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            OpenAIAPIUrlInfoFlyout.Show();
        }

        private void OpenAIAPIUrlInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            OpenAIAPIUrlInfoFlyout.Hide();
        }

        private void OllamaAPIUrlInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            OllamaAPIUrlInfoFlyout.Show();
        }

        private void OllamaAPIUrlInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            OllamaAPIUrlInfoFlyout.Hide();
        }

        private void LMStudioAPIUrlInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            LMStudioAPIUrlInfoFlyout.Show();
        }

        private void LMStudioAPIUrlInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            LMStudioAPIUrlInfoFlyout.Hide();
        }

        private async void LoadModelsButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string apiName && apiName == "LMStudio")
            {
                string baseUrl = (Translator.Setting["LMStudio"] as LMStudioConfig)?.ApiUrl ?? "";

                if (string.IsNullOrWhiteSpace(baseUrl))
                {
                    System.Windows.MessageBox.Show(LocalizationService.Get("Please set the API URL first."),
                        LocalizationService.Get("Load Models"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }

                button.IsEnabled = false;
                try
                {
                    var models = await ModelsApiService.FetchModelsAsync(apiName, baseUrl);
                    var comboBox = FindName($"{apiName}ModelComboBox") as ComboBox;
                    if (comboBox != null)
                    {
                        comboBox.ItemsSource = models;
                        if (models.Count > 0)
                            System.Windows.MessageBox.Show(LocalizationService.Format("Loaded {0} model(s).", models.Count),
                                LocalizationService.Get("Load Models"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                        else
                            System.Windows.MessageBox.Show(LocalizationService.Get("No models found or unable to connect. Check that the server is running."),
                                LocalizationService.Get("Load Models"), System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    }
                }
                finally
                {
                    button.IsEnabled = true;
                }
            }
        }

        private void LMStudioModelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is ModelsApiService.ModelInfo mi)
            {
                var config = Translator.Setting["LMStudio"] as LMStudioConfig;
                if (config != null)
                {
                    config.ModelName = mi.Id;
                    if (sender is ComboBox cb)
                        cb.Text = mi.Id;
                }
            }
        }

        private void SwitchConfig(string apiName, int index)
        {
            if (!Translator.Setting.Configs.TryGetValue(apiName, out var configs) ||
                !Translator.Setting.ConfigIndices.ContainsKey(apiName) ||
                index < 0 || index >= configs.Count)
                return;

            if (Translator.Setting.ConfigIndices[apiName] != index)
                Translator.Setting.ConfigIndices[apiName] = index;

            if (FindName($"{apiName}Index") is TextBlock indexTextBlock)
            {
                int total = configs.Count;
                indexTextBlock.Text = $"{index + 1}/{total}";
            }
            Translator.Setting.OnPropertyChanged(null);
        }

        private void SelectButton(System.Windows.Controls.Button button)
        {
            if (currentSelected != null)
                currentSelected.Background = new SolidColorBrush(Colors.Transparent);
            button.Background = (Brush)FindResource("ControlFillColorSecondaryBrush");
            currentSelected = button;
        }
    }
}
