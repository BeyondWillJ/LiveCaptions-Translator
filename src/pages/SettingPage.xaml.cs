using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Appearance;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;
using Wpf.Ui.Controls;

namespace LiveCaptionsTranslator
{
    public partial class SettingPage : Page
    {
        private const int PAGE_HEIGHT = 350;
        private static SettingWindow? SettingWindow;
        private List<FontChoice> fontChoices = [];
        private bool updatingFontChoices;

        public SettingPage()
        {
            InitializeComponent();
            ApplicationThemeManager.ApplySystemTheme();
            DataContext = Translator.Setting;

            Loaded += (s, e) =>
            {
                (App.Current.MainWindow as MainWindow)?.AutoHeightAdjust(
                    minHeight: PAGE_HEIGHT, maxHeight: PAGE_HEIGHT);
                CheckForFirstUse();
            };

            TranslateAPIBox.ItemsSource = Translator.Setting?.Configs.Keys;
            TranslateAPIBox.SelectedIndex = 0;

            LoadFontChoices();
            RefreshFontChoices();
            SelectConfiguredFont();

            LoadAPISetting();
        }

        private void LiveCaptionsButton_click(object sender, RoutedEventArgs e)
        {
            if (Translator.Window == null)
                return;

            var button = sender as Wpf.Ui.Controls.Button;
            var text = ButtonText.Text;

            bool isHide = Translator.Window.Current.BoundingRectangle == Rect.Empty;
            if (isHide)
            {
                LiveCaptionsHandler.RestoreLiveCaptions(Translator.Window);
                ButtonText.Text = LocalizationService.Get("Hide");
            }
            else
            {
                LiveCaptionsHandler.HideLiveCaptions(Translator.Window);
                ButtonText.Text = LocalizationService.Get("Show");
            }
        }

        private void TranslateAPIBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            LoadAPISetting();
        }

        private void TargetLangBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TargetLangBox.SelectedItem != null)
                Translator.Setting.TargetLanguage = TargetLangBox.SelectedItem.ToString();
        }

        private void TargetLangBox_LostFocus(object sender, RoutedEventArgs e)
        {
            Translator.Setting.TargetLanguage = TargetLangBox.Text;
        }

        private void OverlayFontFamilyBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (updatingFontChoices || OverlayFontFamilyBox.SelectedItem is not FontChoice choice)
                return;

            Translator.Setting.OverlayWindow.FontFamily = choice.Family.Source;
            Translator.Setting.OverlayWindow.FontWeight = choice.Typeface.Weight.ToOpenTypeWeight();
            Translator.Setting.OverlayWindow.FontStretch = choice.Typeface.Stretch.ToOpenTypeStretch();
            Translator.Setting.OverlayWindow.FontStyle = choice.Typeface.Style.ToString();

            var recent = Translator.Setting.OverlayWindow.RecentFontFaces;
            recent.Remove(choice.Key);
            recent.Insert(0, choice.Key);
            if (recent.Count > 5)
                recent.RemoveRange(5, recent.Count - 5);
            Translator.Setting.OverlayWindow.OnPropertyChanged("RecentFontFaces");
        }

        private void OverlayFontFamilyBox_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key is Key.Up or Key.Down or Key.Enter or Key.Escape or Key.Tab)
                return;
            RefreshFontChoices(OverlayFontFamilyBox.Text);
            OverlayFontFamilyBox.IsDropDownOpen = true;
        }

        private void OverlayFontFamilyBox_DropDownOpened(object sender, EventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(OverlayFontFamilyBox.Text) &&
                OverlayFontFamilyBox.SelectedItem == null)
                return;
            var selected = OverlayFontFamilyBox.SelectedItem;
            RefreshFontChoices();
            updatingFontChoices = true;
            OverlayFontFamilyBox.SelectedItem = selected;
            updatingFontChoices = false;
        }

        private void RefreshFontChoices(string search = "")
        {
            var recent = Translator.Setting.OverlayWindow.RecentFontFaces;
            IEnumerable<FontChoice> filtered = fontChoices;
            if (!string.IsNullOrWhiteSpace(search))
                filtered = filtered.Where(choice => choice.DisplayName.Contains(
                    search.Trim(), StringComparison.CurrentCultureIgnoreCase));

            updatingFontChoices = true;
            OverlayFontFamilyBox.ItemsSource = filtered
                .OrderBy(choice =>
                {
                    int index = recent.IndexOf(choice.Key);
                    return index < 0 ? int.MaxValue : index;
                })
                .ThenBy(choice => choice.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            updatingFontChoices = false;
        }

        private void LoadFontChoices()
        {
            fontChoices = Fonts.SystemFontFamilies
                .SelectMany(family => family.GetTypefaces().Select(typeface => new FontChoice(family, typeface)))
                .ToList();
        }

        private void SelectConfiguredFont()
        {
            updatingFontChoices = true;
            OverlayFontFamilyBox.SelectedItem = fontChoices.FirstOrDefault(choice => choice.Matches(
                Translator.Setting.OverlayWindow.FontFamily,
                Translator.Setting.OverlayWindow.FontWeight,
                Translator.Setting.OverlayWindow.FontStretch,
                Translator.Setting.OverlayWindow.FontStyle)) ?? fontChoices.FirstOrDefault();
            updatingFontChoices = false;
        }

        private void UiLanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (UiLanguageBox.SelectedValue is string language)
            {
                LocalizationService.SetLanguage(language);
                LoadFontChoices();
                RefreshFontChoices();
                SelectConfiguredFont();
                try
                {
                    bool isHidden = Translator.Window?.Current.BoundingRectangle == Rect.Empty;
                    ButtonText.Text = LocalizationService.Get(isHidden ? "Show" : "Hide");
                }
                catch (System.Windows.Automation.ElementNotAvailableException)
                {
                    ButtonText.Text = LocalizationService.Get("Show");
                }
            }
        }

        private void APISettingButton_click(object sender, RoutedEventArgs e)
        {
            if (SettingWindow != null && SettingWindow.IsLoaded)
                SettingWindow.Activate();
            else
            {
                SettingWindow = new SettingWindow();
                SettingWindow.Closed += (sender, args) => SettingWindow = null;
                SettingWindow.Show();
            }
        }

        private void Contexts_ValueChanged(object sender, NumberBoxValueChangedEventArgs args)
        {
            if (Translator.Setting.DisplaySentences > Translator.Setting.NumContexts)
                Translator.Setting.DisplaySentences = Translator.Setting.NumContexts;
        }

        private void DisplaySentences_ValueChanged(object sender, NumberBoxValueChangedEventArgs args)
        {
            if (Translator.Setting.DisplaySentences > Translator.Setting.NumContexts)
                Translator.Setting.NumContexts = Translator.Setting.DisplaySentences;
            Translator.Caption.OnPropertyChanged("DisplayLogCards");
            Translator.Caption.OnPropertyChanged("OverlayPreviousTranslation");
        }

        private void LiveCaptionsInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            LiveCaptionsInfoFlyout.Show();
        }

        private void LiveCaptionsInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            LiveCaptionsInfoFlyout.Hide();
        }

        private void FrequencyInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            FrequencyInfoFlyout.Show();
        }

        private void FrequencyInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            FrequencyInfoFlyout.Hide();
        }

        private void TranslateAPIInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            TranslateAPIInfoFlyout.Show();
        }

        private void TranslateAPIInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            TranslateAPIInfoFlyout.Hide();
        }

        private void TargetLangInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            TargetLangInfoFlyout.Show();
        }

        private void TargetLangInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            TargetLangInfoFlyout.Hide();
        }

        private void CaptionLogMaxInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            CaptionLogMaxInfoFlyout.Show();
        }

        private void CaptionLogMaxInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            CaptionLogMaxInfoFlyout.Hide();
        }

        private void ContextAwareInfo_MouseEnter(object sender, MouseEventArgs e)
        {
            ContextAwareInfoFlyout.Show();
        }

        private void ContextAwareInfo_MouseLeave(object sender, MouseEventArgs e)
        {
            ContextAwareInfoFlyout.Hide();
        }

        private void CheckForFirstUse()
        {
            if (Translator.FirstUseFlag)
                ButtonText.Text = LocalizationService.Get("Hide");
        }

        public void LoadAPISetting()
        {
            var configType = Translator.Setting[Translator.Setting.ApiName].GetType();
            var languagesProp = configType.GetProperty(
                "SupportedLanguages", BindingFlags.Public | BindingFlags.Static);

            // Traverse base classes to find `SupportedLanguages`
            while (configType != null && languagesProp == null)
            {
                configType = configType.BaseType;
                languagesProp = configType.GetProperty(
                    "SupportedLanguages", BindingFlags.Public | BindingFlags.Static);
            }
            if (languagesProp == null)
                languagesProp = typeof(TranslateAPIConfig).GetProperty(
                    "SupportedLanguages", BindingFlags.Public | BindingFlags.Static);

            var supportedLanguages = (Dictionary<string, string>)languagesProp.GetValue(null);
            TargetLangBox.ItemsSource = supportedLanguages.Keys;

            string targetLang = Translator.Setting.TargetLanguage;
            if (!supportedLanguages.ContainsKey(targetLang))
                supportedLanguages[targetLang] = targetLang;    // add custom language to supported languages
            TargetLangBox.SelectedItem = targetLang;
        }

        private sealed class FontChoice
        {
            public FontFamily Family { get; }
            public Typeface Typeface { get; }
            public string DisplayName { get; }
            public string Key => $"{Family.Source}|{Typeface.Weight.ToOpenTypeWeight()}|" +
                                 $"{Typeface.Stretch.ToOpenTypeStretch()}|{Typeface.Style}";

            public FontChoice(FontFamily family, Typeface typeface)
            {
                Family = family;
                Typeface = typeface;
                var language = System.Windows.Markup.XmlLanguage.GetLanguage(LocalizationService.CurrentLanguage);
                string faceName = typeface.FaceNames.TryGetValue(language, out string? localized) ?
                    localized : typeface.FaceNames.Values.FirstOrDefault() ?? string.Empty;
                DisplayName = string.IsNullOrWhiteSpace(faceName) || faceName.Equals("Regular", StringComparison.OrdinalIgnoreCase) ?
                    family.Source : $"{family.Source} — {faceName}";
            }

            public bool Matches(string family, int weight, int stretch, string style) =>
                Family.Source == family &&
                Typeface.Weight.ToOpenTypeWeight() == weight &&
                Typeface.Stretch.ToOpenTypeStretch() == stretch &&
                Typeface.Style.ToString() == style;

            public override string ToString() => DisplayName;
        }
    }
}
