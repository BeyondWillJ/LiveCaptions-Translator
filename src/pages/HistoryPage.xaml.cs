using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;
using TextBlock = System.Windows.Controls.TextBlock;

namespace LiveCaptionsTranslator
{
    public partial class HistoryPage : Page
    {
        public const int MIN_HEIGHT = 300;

        private int currentPage = 1;
        private int searchPage = 1;
        private int maxPage = 1;
        private int maxRowPerPage = 30;
        private readonly RequestGeneration historyLoad = new();

        public string SearchText { get; set; } = string.Empty;

        public HistoryPage()
        {
            InitializeComponent();
            ApplicationThemeManager.ApplySystemTheme();

            Loaded += async (s, e) =>
            {
                await LoadHistory();
                (App.Current.MainWindow as MainWindow)?.AutoHeightAdjust(minHeight: MIN_HEIGHT, maxHeight: MIN_HEIGHT);
                Translator.TranslationLogged += OnTranslationLogged;
                HistoryStatusFilter.SelectionChanged += HistoryStatusFilter_SelectionChanged;
            };
            Unloaded += (s, e) =>
            {
                HistoryDataGrid.ItemsSource = null;
                Translator.TranslationLogged -= OnTranslationLogged;
                HistoryStatusFilter.SelectionChanged -= HistoryStatusFilter_SelectionChanged;
                historyLoad.CancelCurrent();
            };

            HistoryMaxRow.SelectionChanged += maxRow_SelectionChanged;
        }

        private async void OnTranslationLogged()
        {
            await LoadHistory();
        }

        private async void PageDown_click(object sender, RoutedEventArgs e)
        {
            if (currentPage - 1 >= 1)
                currentPage--;
            await LoadHistory();
        }

        private async void PageUp_click(object sender, RoutedEventArgs e)
        {
            if (currentPage < maxPage)
                currentPage++;
            await LoadHistory();
        }

        private async void Delete_click(object sender, RoutedEventArgs e)
        {
            var dialogHostContainer = (Application.Current.MainWindow as MainWindow)?.DialogHostContainer;
            if (dialogHostContainer is null)
                return;

            var dialog = new ContentDialog
            {
                Title = new TextBlock
                {
                    Text = LocalizationService.Get("Do you want to delete all history?"),
                    FontSize = 18,
                    FontWeight = FontWeights.Regular
                },
                Content = LocalizationService.Get("This operation cannot be undone!"),
                PrimaryButtonText = LocalizationService.Get("Yes"),
                CloseButtonText = LocalizationService.Get("No"),
                DefaultButton = ContentDialogButton.Close,
                DialogHost = dialogHostContainer,
                Padding = new Thickness(8, 4, 8, 8),
            };

            dialogHostContainer.Visibility = Visibility.Visible;
            var result = await dialog.ShowAsync();
            dialogHostContainer.Visibility = Visibility.Collapsed;

            if (result == ContentDialogResult.Primary)
            {
                currentPage = 1;
                await SQLiteHistoryLogger.ClearHistory();
                await LoadHistory();
            }
        }

        private async void maxRow_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 0 || e.AddedItems[0] is not ComboBoxItem item || item.Tag is not string tag)
                return;
            maxRowPerPage = Convert.ToInt32(tag);
            currentPage = 1;
            await LoadHistory();
        }

        private async void HistoryStatusFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            currentPage = 1;
            await LoadHistory();
        }

        private async void Refresh_click(object sender, RoutedEventArgs e)
        {
            await LoadHistory();
        }

        private async void Export_click(object sender, RoutedEventArgs e)
        {
            SaveFileDialog saveFileDialog = new SaveFileDialog
            {
                Filter = "CSV (*.csv)|*.csv|All file (*.*)|*.*",
                DefaultExt = ".csv",
                FileName = $"exported_{DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss")}.csv",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                try
                {
                    await SQLiteHistoryLogger.ExportToCSV(saveFileDialog.FileName);
                    SnackbarHost.Show(LocalizationService.Get("Saved Success."),
                        $"{LocalizationService.Get("File saved to:")} {saveFileDialog.FileName}", SnackbarType.Success);
                }
                catch (Exception ex)
                {
                    SnackbarHost.Show(LocalizationService.Get("Save Failed."),
                        $"{LocalizationService.Get("Failed to save file:")} {ex.Message}", SnackbarType.Error);
                }
            }
        }

        private async void HistorySearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
        {
            string searchText = (sender as AutoSuggestBox)?.Text ?? "";

            // Clear search by Ctrl+A and Delete and Enter
            if (string.IsNullOrEmpty(searchText))
            {
                SearchText = string.Empty;
                currentPage = searchPage;
            }
            else // Submit search
            {
                if (string.IsNullOrEmpty(SearchText))
                {
                    searchPage = currentPage;
                }
                SearchText = searchText;
                currentPage = 1;
            }
            await LoadHistory();
        }

        private async void HistorySearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            // Press X to clear search box
            if (args.Reason == AutoSuggestionBoxTextChangeReason.ProgrammaticChange)
            {
                if (!string.IsNullOrEmpty(SearchText))
                {
                    SearchText = string.Empty;
                    currentPage = searchPage;
                    await LoadHistory();
                }
            }
        }

        public async Task LoadHistory()
        {
            RequestLease request = historyLoad.Begin();

            HistoryStateText.Text = LocalizationService.Get("Loading history...");
            HistoryStateText.Visibility = Visibility.Visible;

            try
            {
                int requestedPage = currentPage;
                var data = await SQLiteHistoryLogger.LoadHistoryPageAsync(
                    requestedPage, maxRowPerPage, SearchText, SelectedStatusFilter(), request.Token);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!historyLoad.IsCurrent(request))
                        return;
                    maxPage = data.MaxPage;
                    currentPage = data.ActualPage;
                    HistoryDataGrid.ItemsSource = data.Rows;
                    PageNumber.Text = $"{currentPage}/{maxPage}";
                    HistoryStateText.Text = data.Rows.Count == 0
                        ? LocalizationService.Get("No history found.") : string.Empty;
                    HistoryStateText.Visibility = data.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Unable to load translation history: {ex.GetType().Name}");
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!historyLoad.IsCurrent(request))
                        return;
                    HistoryDataGrid.ItemsSource = null;
                    HistoryStateText.Text = LocalizationService.Get("Unable to load history.");
                    HistoryStateText.Visibility = Visibility.Visible;
                });
            }
        }

        private string SelectedStatusFilter()
        {
            string? status = (HistoryStatusFilter.SelectedItem as ComboBoxItem)?.Tag as string;
            return string.IsNullOrEmpty(status) || status == "All" ? string.Empty : status;
        }
    }
}
