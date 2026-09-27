using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Data;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

using LiveCaptionsTranslator.utils;
using LiveCaptionsTranslator.Utils;
using Button = Wpf.Ui.Controls.Button;

namespace LiveCaptionsTranslator
{
    public partial class MainWindow : FluentWindow
    {
        public OverlayWindow? OverlayWindow { get; set; } = null;
        public bool IsAutoHeight { get; set; } = true;

        public MainWindow()
        {
            InitializeComponent();
            ApplicationThemeManager.ApplySystemTheme();
            StatusText.DataContext = Translator.Caption;
            StatusText.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new Binding(nameof(models.Caption.StatusMessage)));
            StatusRouteText.DataContext = Translator.Caption;
            StatusRouteText.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new Binding(nameof(models.Caption.StatusRoute)));
            StatusDiagnosticText.DataContext = Translator.Caption;
            StatusDiagnosticText.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new Binding(nameof(models.Caption.StatusDiagnostic)));

            Loaded += async (s, e) =>
            {
                SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);
                RootNavigation.Navigate(typeof(CaptionPage));
                IsAutoHeight = true;
                await CheckForFirstUse();
                _ = CheckForUpdates();
            };

            double screenWidth = SystemParameters.PrimaryScreenWidth;
            double screenHeight = SystemParameters.PrimaryScreenHeight;

            var windowState = WindowHandler.LoadState(this, Translator.Setting);
            if (!WindowHandler.IsVisibleOnVirtualDesktop(windowState))
            {
                WindowHandler.RestoreState(this, new Rect(
                    (screenWidth - 775) / 2, screenHeight * 3 / 4 - 167, 775, 167));
            }
            else
                WindowHandler.RestoreState(this, windowState);

            ToggleTopmost(Translator.Setting.MainWindow.Topmost);
            ShowLogCard(Translator.Setting.MainWindow.CaptionLogEnabled);
        }

        private void TopmostButton_Click(object sender, RoutedEventArgs e)
        {
            ToggleTopmost(!this.Topmost);
        }

        private void OverlayModeButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var symbolIcon = button?.Icon as SymbolIcon;

            if (OverlayWindow == null)
            {
                if (symbolIcon != null)
                {
                    symbolIcon.Symbol = SymbolRegular.ClosedCaption24;
                    symbolIcon.Filled = true;
                }

                OverlayWindow = new OverlayWindow();
                OverlayWindow.ClickThroughStateChanged += UpdateOverlayInteractionButton;
                OverlayWindow.SizeChanged +=
                    (s, e) => WindowHandler.SaveState(OverlayWindow, Translator.Setting);
                OverlayWindow.LocationChanged +=
                    (s, e) => WindowHandler.SaveState(OverlayWindow, Translator.Setting);

                double screenWidth = SystemParameters.PrimaryScreenWidth;
                double screenHeight = SystemParameters.PrimaryScreenHeight;

                var windowState = WindowHandler.LoadState(OverlayWindow, Translator.Setting);
                if (!WindowHandler.IsVisibleOnVirtualDesktop(windowState))
                {
                    WindowHandler.RestoreState(OverlayWindow, new Rect(
                        (screenWidth - 650) / 2, screenHeight * 5 / 6 - 135, 650, 135));
                }
                else
                    WindowHandler.RestoreState(OverlayWindow, windowState);

                OverlayWindow.Show();
                UpdateOverlayInteractionButton();
            }
            else
            {
                if (symbolIcon != null)
                {
                    symbolIcon.Symbol = SymbolRegular.ClosedCaptionOff24;
                    symbolIcon.Filled = false;
                }

                OverlayWindow.ClickThroughStateChanged -= UpdateOverlayInteractionButton;
                OverlayWindow.Close();
                OverlayWindow = null;
                UpdateOverlayInteractionButton();
            }
        }

        private void LogOnlyButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var symbolIcon = button?.Icon as SymbolIcon;

            if (Translator.LogOnlyFlag)
            {
                Translator.SetLogOnly(false);
                if (symbolIcon != null)
                    symbolIcon.Filled = false;
            }
            else
            {
                Translator.SetLogOnly(true);
                if (symbolIcon != null)
                    symbolIcon.Filled = true;
            }

            Translator.ClearContexts();
        }

        private void CaptionLogButton_Click(object sender, RoutedEventArgs e)
        {
            Translator.Setting.MainWindow.CaptionLogEnabled = !Translator.Setting.MainWindow.CaptionLogEnabled;
            ShowLogCard(Translator.Setting.MainWindow.CaptionLogEnabled);
            CaptionPage.Instance?.AutoHeight();
        }

        private void MainWindow_LocationChanged(object sender, EventArgs e)
        {
            var window = sender as Window;
            WindowHandler.SaveState(window, Translator.Setting);
        }

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            MainWindow_LocationChanged(sender, e);
            IsAutoHeight = false;
        }

        public void ToggleTopmost(bool enabled)
        {
            var button = TopmostButton as Button;
            var symbolIcon = button?.Icon as SymbolIcon;
            if (symbolIcon != null)
                symbolIcon.Filled = enabled;
            this.Topmost = enabled;
            Translator.Setting.MainWindow.Topmost = enabled;
        }

        private async Task CheckForFirstUse()
        {
            if (!Translator.FirstUseFlag)
                return;

            RootNavigation.Navigate(typeof(SettingPage));
            try
            {
                await Translator.EnsureLiveCaptionsAsync();
                if (Translator.Window != null)
                    LiveCaptionsHandler.RestoreLiveCaptions(Translator.Window);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Live Captions first-use setup failed: {ex.Message}");
            }

        }

        private void RestoreOverlayInteraction_Click(object sender, RoutedEventArgs e)
        {
            OverlayWindow?.RestoreInteraction();
        }

        private void UpdateOverlayInteractionButton()
        {
            RestoreOverlayButton.Visibility = OverlayWindow?.IsClickThrough == true
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private async Task CheckForUpdates()
        {
            if (Translator.FirstUseFlag)
                return;

            string latestVersion = string.Empty;
            try
            {
                latestVersion = await UpdateUtil.GetLatestVersion();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Update check failed: {ex.Message}");
                return;
            }

            var currentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString();
            var ignoredVersion = Translator.Setting.IgnoredUpdateVersion;
            if (!string.IsNullOrEmpty(ignoredVersion) && ignoredVersion == latestVersion)
                return;
            if (Version.TryParse(latestVersion, out var remoteVersion) &&
                Version.TryParse(currentVersion, out var localVersion) &&
                remoteVersion > localVersion)
            {
                var dialog = new Wpf.Ui.Controls.MessageBox
                {
                    Title = LocalizationService.Get("New Version Available"),
                    Content = $"{LocalizationService.Get("A new version has been detected:")} {latestVersion}\n" +
                              $"{LocalizationService.Get("Current version:")} {currentVersion}\n" +
                              LocalizationService.Get("Please visit GitHub to download the latest release."),
                    PrimaryButtonText = LocalizationService.Get("Update"),
                    CloseButtonText = LocalizationService.Get("Ignore this version")
                };
                var result = await dialog.ShowDialogAsync();

                if (result == Wpf.Ui.Controls.MessageBoxResult.Primary)
                {
                    var url = UpdateUtil.GitHubReleasesUrl;
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = url,
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex)
                    {
                        SnackbarHost.Show(LocalizationService.Get("[ERROR] Open Browser Failed."), ex.Message, SnackbarType.Error,
                            timeout: 2, closeButton: true);
                    }
                }
                else
                    Translator.Setting.IgnoredUpdateVersion = latestVersion;
            }
        }

        public void ShowLogCard(bool enabled)
        {
            if (CaptionLogButton.Icon is SymbolIcon icon)
            {
                if (enabled)
                    icon.Symbol = SymbolRegular.History24;
                else
                    icon.Symbol = SymbolRegular.HistoryDismiss24;
                CaptionPage.Instance?.CollapseTranslatedCaption(enabled);
            }
        }

        public void AutoHeightAdjust(int minHeight = -1, int maxHeight = -1)
        {
            if (minHeight > 0 && Height < minHeight)
            {
                Height = minHeight;
                IsAutoHeight = true;
            }

            if (IsAutoHeight && maxHeight > 0 && Height > maxHeight)
                Height = maxHeight;
        }
    }
}
