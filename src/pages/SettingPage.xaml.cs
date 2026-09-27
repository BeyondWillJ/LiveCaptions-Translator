using System.Reflection;
using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Appearance;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.utils;
using Wpf.Ui.Controls;

namespace LiveCaptionsTranslator
{
    public partial class SettingPage : Page
    {
        private const int PAGE_HEIGHT = 350;
        private static SettingWindow? SettingWindow;
        private List<FontFamilyChoice> fontFamilies = [];
        private ListCollectionView? fontFamiliesView;
        private readonly DispatcherTimer fontSearchTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
        private string pendingFontSearch = string.Empty;
        private bool fontPickerInitialized;
        private bool updatingFontChoices;
        private bool suppressFontSearch;
        private bool suppressLanguageChange = true;
        private bool initializingApiSelection = true;
        private int fontLoadGeneration;

        public SettingPage()
        {
            InitializeComponent();
            ApplicationThemeManager.ApplySystemTheme();
            DataContext = Translator.Setting;

            Loaded += SettingPage_Loaded;
            fontSearchTimer.Tick += FontSearchTimer_Tick;

            TranslateAPIBox.ItemsSource = TranslateAPI.TRANSLATE_FUNCTIONS.Keys.ToArray();
            string savedApi = Translator.Setting.ApiName;
            TranslateAPIBox.SelectedItem = ResolveInitialApiSelection(savedApi,
                TranslateAPI.TRANSLATE_FUNCTIONS.Keys);
            initializingApiSelection = false;

            LoadAPISetting();
        }

        private async void SettingPage_Loaded(object sender, RoutedEventArgs e)
        {
            (App.Current.MainWindow as MainWindow)?.AutoHeightAdjust(
                minHeight: PAGE_HEIGHT, maxHeight: PAGE_HEIGHT);
            CheckForFirstUse();

            await Dispatcher.InvokeAsync(() =>
            {
                suppressLanguageChange = true;
                UiLanguageBox.SelectedValue = Translator.Setting.UiLanguage;
                suppressLanguageChange = false;
                LocalizationService.Refresh(this);
            }, DispatcherPriority.ContextIdle);
            await InitializeFontPickerAsync();
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
            if (initializingApiSelection || TranslateAPIBox.SelectedItem is not string apiName)
                return;
            Translator.Setting.ApiName = apiName;
            LoadAPISetting();
        }

        private void TargetLangBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TargetLangBox.SelectedItem is string targetLanguage)
                Translator.Setting.TargetLanguage = targetLanguage;
        }

        private void TargetLangBox_LostFocus(object sender, RoutedEventArgs e)
        {
            Translator.Setting.TargetLanguage = TargetLangBox.Text;
        }

        public static string ResolveInitialApiSelection(string savedApi, IEnumerable<string> availableApis) =>
            availableApis.Contains(savedApi, StringComparer.Ordinal) ? savedApi : "Google";

        private void OverlayFontFamilyBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (updatingFontChoices || OverlayFontFamilyBox.SelectedItem is not FontFamilyChoice choice)
                return;

            suppressFontSearch = true;
            LoadFontFaces(choice, preserveConfiguredFace: false, applySelection: true);
            Dispatcher.BeginInvoke(new Action(() => suppressFontSearch = false), DispatcherPriority.ContextIdle);
        }

        private void OverlayFontFaceBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (updatingFontChoices || OverlayFontFaceBox.SelectedItem is not FontFaceChoice choice)
                return;

            ApplyFontFace(choice);
        }

        private void ApplyFontFace(FontFaceChoice choice)
        {
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
            ApplyFontSort();
        }

        private void FontSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (suppressFontSearch)
                return;
            pendingFontSearch = FontSearchBox.Text.Trim();
            fontSearchTimer.Stop();
            fontSearchTimer.Start();
            if (OverlayFontFamilyBox.IsEnabled)
                OverlayFontFamilyBox.IsDropDownOpen = true;
        }

        private void FontSearchTimer_Tick(object? sender, EventArgs e)
        {
            fontSearchTimer.Stop();
            if (fontFamiliesView == null)
                return;
            string query = pendingFontSearch;
            fontFamiliesView.Filter = string.IsNullOrWhiteSpace(query) ? null :
                item => item is FontFamilyChoice choice && choice.SearchName.Contains(
                    query, StringComparison.CurrentCultureIgnoreCase);
            fontFamiliesView.Refresh();
        }

        private async Task InitializeFontPickerAsync(bool forceReload = false)
        {
            if (fontPickerInitialized && !forceReload)
                return;
            int generation = ++fontLoadGeneration;
            OverlayFontFamilyBox.IsEnabled = false;
            FontLoadStatus.Visibility = Visibility.Visible;
            FontLoadStatus.Text = LocalizationService.Get("Loading fonts...");
            string language = LocalizationService.CurrentLanguage;
            var loadedFamilies = await Task.Run(() => Fonts.SystemFontFamilies
                .Select(family => new FontFamilyChoice(family, language))
                .ToList());
            if (generation != fontLoadGeneration || !IsLoaded)
                return;

            fontPickerInitialized = true;
            fontFamilies = loadedFamilies;
            fontFamiliesView = new ListCollectionView(fontFamilies);
            ApplyFontSort();
            OverlayFontFamilyBox.ItemsSource = fontFamiliesView;
            SelectConfiguredFont();
            OverlayFontFamilyBox.IsEnabled = true;
            FontLoadStatus.Visibility = Visibility.Collapsed;
        }

        private void ApplyFontSort()
        {
            if (fontFamiliesView == null)
                return;
            fontFamiliesView.CustomSort = new FontFamilyChoiceComparer(
                Translator.Setting.OverlayWindow.RecentFontFaces);
            fontFamiliesView.Refresh();
        }

        private void SelectConfiguredFont()
        {
            updatingFontChoices = true;
            FontFamilyChoice? family = fontFamilies.FirstOrDefault(choice =>
                choice.Family.Source == Translator.Setting.OverlayWindow.FontFamily) ??
                fontFamilies.FirstOrDefault();
            OverlayFontFamilyBox.SelectedItem = family;
            updatingFontChoices = false;
            if (family != null)
                LoadFontFaces(family, preserveConfiguredFace: true, applySelection: false);
        }

        private void LoadFontFaces(FontFamilyChoice family, bool preserveConfiguredFace, bool applySelection)
        {
            string language = LocalizationService.CurrentLanguage;
            List<FontFaceChoice> faces = family.Family.GetTypefaces()
                .Select(typeface => new FontFaceChoice(family.Family, typeface, language))
                .OrderBy(choice => choice.Typeface.Weight.ToOpenTypeWeight())
                .ThenBy(choice => choice.Typeface.Stretch.ToOpenTypeStretch())
                .ThenBy(choice => choice.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            FontFaceChoice? selected = null;
            if (preserveConfiguredFace && family.Family.Source == Translator.Setting.OverlayWindow.FontFamily)
            {
                selected = faces.FirstOrDefault(choice => choice.Matches(
                    Translator.Setting.OverlayWindow.FontWeight,
                    Translator.Setting.OverlayWindow.FontStretch,
                    Translator.Setting.OverlayWindow.FontStyle));
            }
            selected ??= faces.FirstOrDefault(choice => choice.Matches(400, 5, "Normal")) ?? faces.FirstOrDefault();

            updatingFontChoices = true;
            OverlayFontFaceBox.ItemsSource = faces;
            OverlayFontFaceBox.SelectedItem = selected;
            OverlayFontFaceBox.IsEnabled = selected != null;
            updatingFontChoices = false;

            if (applySelection && selected != null)
                ApplyFontFace(selected);
        }

        private async void UiLanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!suppressLanguageChange && UiLanguageBox.SelectedValue is string language)
            {
                LocalizationService.SetLanguage(language);
                try
                {
                    bool isHidden = Translator.Window?.Current.BoundingRectangle == Rect.Empty;
                    ButtonText.Text = LocalizationService.Get(isHidden ? "Show" : "Hide");
                }
                catch (System.Windows.Automation.ElementNotAvailableException)
                {
                    ButtonText.Text = LocalizationService.Get("Show");
                }
                suppressFontSearch = true;
                FontSearchBox.Clear();
                pendingFontSearch = string.Empty;
                suppressFontSearch = false;
                fontPickerInitialized = false;
                await InitializeFontPickerAsync(forceReload: true);
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
            Translator.Caption.OnPropertyChanged("DisplayLogCards");
            Translator.Caption.OnPropertyChanged("OverlayPreviousTranslation");
        }

        private void DisplaySentences_ValueChanged(object sender, NumberBoxValueChangedEventArgs args)
        {
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
            Type? configType = Translator.Setting[Translator.Setting.ApiName].GetType();
            System.Reflection.PropertyInfo? languagesProp = null;
            while (configType != null && languagesProp == null)
            {
                languagesProp = configType.GetProperty(
                    "SupportedLanguages", BindingFlags.Public | BindingFlags.Static);
                configType = configType.BaseType;
            }
            if (languagesProp?.GetValue(null) is not Dictionary<string, string> supportedLanguages)
                throw new InvalidOperationException("The selected translation service has no language list.");
            TargetLangBox.ItemsSource = supportedLanguages.Keys;

            string targetLang = Translator.Setting.TargetLanguage;
            if (!supportedLanguages.ContainsKey(targetLang))
                supportedLanguages[targetLang] = targetLang;    // add custom language to supported languages
            TargetLangBox.SelectedItem = targetLang;
        }

        private sealed class FontFamilyChoice
        {
            public FontFamily Family { get; }
            public string DisplayName { get; }
            public string SearchName { get; }

            public FontFamilyChoice(FontFamily family, string languageName)
            {
                Family = family;
                var language = System.Windows.Markup.XmlLanguage.GetLanguage(languageName);
                DisplayName = family.FamilyNames.TryGetValue(language, out string? localized) ?
                    localized : family.FamilyNames.Values.FirstOrDefault() ?? family.Source;
                SearchName = string.Join(' ', family.FamilyNames.Values.Append(family.Source).Distinct());
            }

            public override string ToString() => DisplayName;
        }

        private sealed class FontFaceChoice
        {
            public FontFamily Family { get; }
            public Typeface Typeface { get; }
            public string DisplayName { get; }
            public string Key => $"{Family.Source}|{Typeface.Weight.ToOpenTypeWeight()}|" +
                                 $"{Typeface.Stretch.ToOpenTypeStretch()}|{Typeface.Style}";

            public FontFaceChoice(FontFamily family, Typeface typeface, string languageName)
            {
                Family = family;
                Typeface = typeface;
                var language = System.Windows.Markup.XmlLanguage.GetLanguage(languageName);
                DisplayName = typeface.FaceNames.TryGetValue(language, out string? localized) ?
                    localized : typeface.FaceNames.Values.FirstOrDefault() ?? "Regular";
            }

            public bool Matches(int weight, int stretch, string style) =>
                Typeface.Weight.ToOpenTypeWeight() == weight &&
                Typeface.Stretch.ToOpenTypeStretch() == stretch &&
                Typeface.Style.ToString() == style;

            public override string ToString() => DisplayName;
        }

        private sealed class FontFamilyChoiceComparer : IComparer
        {
            private readonly List<string> recent;

            public FontFamilyChoiceComparer(List<string> recent)
            {
                this.recent = recent;
            }

            public int Compare(object? x, object? y)
            {
                if (x is not FontFamilyChoice left || y is not FontFamilyChoice right)
                    return 0;
                int leftIndex = FindRecentIndex(left.Family.Source);
                int rightIndex = FindRecentIndex(right.Family.Source);
                if (leftIndex < 0)
                    leftIndex = int.MaxValue;
                if (rightIndex < 0)
                    rightIndex = int.MaxValue;
                int recentComparison = leftIndex.CompareTo(rightIndex);
                return recentComparison != 0 ? recentComparison :
                    StringComparer.CurrentCultureIgnoreCase.Compare(left.DisplayName, right.DisplayName);
            }

            private int FindRecentIndex(string familyName) => recent.FindIndex(key =>
                key.StartsWith($"{familyName}|", StringComparison.Ordinal));
        }
    }
}
