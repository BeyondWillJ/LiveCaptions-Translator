using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Controls;

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.utils;
using LiveCaptionsTranslator.Utils;
using Button = Wpf.Ui.Controls.Button;
using Color = System.Windows.Media.Color;

namespace LiveCaptionsTranslator
{
    public partial class OverlayWindow : Window
    {
        private CaptionVisible onlyMode = CaptionVisible.Both;
        private readonly DispatcherTimer silenceClearTimer = new();
        private bool renderQueued;
        private bool overflowCheckQueued;
        private string lastOriginalCaption = string.Empty;
        private string lastCurrentTranslation = string.Empty;
        private string lastPreviousTranslation = string.Empty;
        private string lastNoticePrefix = string.Empty;
        private string clearedOriginalPrefix = string.Empty;
        private string clearedTranslationPrefix = string.Empty;
        private bool overlayWasCleared;

        public CaptionVisible OnlyMode
        {
            get => onlyMode;
            set
            {
                onlyMode = value;
                ResizeForOnlyMode();
            }
        }
        public CaptionLocation SwitchMode { get; set; } = CaptionLocation.TranslationTop;

        public OverlayWindow()
        {
            InitializeComponent();

            silenceClearTimer.Tick += SilenceClearTimer_Tick;
            Loaded += OverlayWindow_Loaded;
            Unloaded += OverlayWindow_Unloaded;
            SizeChanged += (_, _) => QueueOverflowCheck();

            OriginalCaption.FontWeight = Translator.Setting.OverlayWindow.FontBold == Utils.FontBold.Both ?
                FontWeights.Bold : FontWeights.Regular;
            TranslatedCaption.FontWeight = Translator.Setting.OverlayWindow.FontBold >= Utils.FontBold.TranslationOnly ?
                FontWeights.Bold : FontWeights.Regular;

            OriginalCaptionDecorator.StrokeThickness = Translator.Setting.OverlayWindow.FontStroke;
            TranslatedCaptionDecorator.StrokeThickness = Translator.Setting.OverlayWindow.FontStroke;

            ApplyFontSize();
            ApplyFontFamily();
            ApplyFontColor();
            ApplyBackgroundColor();
            ApplyBackgroundOpacity();
        }

        private void OverlayWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Translator.Caption.PropertyChanged += TranslatedChanged;
            Translator.Setting.OverlayWindow.PropertyChanged += OverlaySettingChanged;
            CaptureOverlayText();
            RenderOverlay();
            RestartSilenceClearTimer();
            QueueOverflowCheck();
        }

        private void OverlayWindow_Unloaded(object sender, RoutedEventArgs e)
        {
            silenceClearTimer.Stop();
            Translator.Caption.PropertyChanged -= TranslatedChanged;
            Translator.Setting.OverlayWindow.PropertyChanged -= OverlaySettingChanged;
        }

        private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                this.DragMove();
        }

        private void TopThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            double newHeight = this.Height - e.VerticalChange;

            if (newHeight >= this.MinHeight)
            {
                this.Top += e.VerticalChange;
                this.Height = newHeight;
            }
        }

        private void BottomThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            double newHeight = this.Height + e.VerticalChange;

            if (newHeight >= this.MinHeight)
            {
                this.Height = newHeight;
            }
        }

        private void LeftThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            double newWidth = this.Width - e.HorizontalChange;

            if (newWidth >= this.MinWidth)
            {
                this.Left += e.HorizontalChange;
                this.Width = newWidth;
            }
        }

        private void RightThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            double newWidth = this.Width + e.HorizontalChange;

            if (newWidth >= this.MinWidth)
            {
                this.Width = newWidth;
            }
        }

        private void TopLeftThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            TopThumb_OnDragDelta(sender, e);
            LeftThumb_OnDragDelta(sender, e);
        }

        private void TopRightThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            TopThumb_OnDragDelta(sender, e);
            RightThumb_OnDragDelta(sender, e);
        }

        private void BottomLeftThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            BottomThumb_OnDragDelta(sender, e);
            LeftThumb_OnDragDelta(sender, e);
        }

        private void BottomRightThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            BottomThumb_OnDragDelta(sender, e);
            RightThumb_OnDragDelta(sender, e);
        }

        private void TranslatedChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is "OverlayOriginalCaption" or "OverlayCurrentTranslation" or
                "OverlayPreviousTranslation" or "OverlayNoticePrefix")
                QueueOverlayUpdate();
        }

        private void QueueOverlayUpdate()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(QueueOverlayUpdate), DispatcherPriority.Render);
                return;
            }
            if (renderQueued)
                return;
            renderQueued = true;
            Dispatcher.BeginInvoke(new Action(ProcessOverlayUpdate), DispatcherPriority.Render);
        }

        private void ProcessOverlayUpdate()
        {
            renderQueued = false;
            if (Translator.Caption == null)
                return;

            bool textChanged = false;
            textChanged |= UpdateIfChanged(ref lastOriginalCaption, Translator.Caption.OverlayOriginalCaption);
            textChanged |= UpdateIfChanged(ref lastCurrentTranslation, Translator.Caption.OverlayCurrentTranslation);
            textChanged |= UpdateIfChanged(ref lastPreviousTranslation, Translator.Caption.OverlayPreviousTranslation);
            textChanged |= UpdateIfChanged(ref lastNoticePrefix, Translator.Caption.OverlayNoticePrefix);
            if (!textChanged)
                return;

            RenderOverlay();
            RestartSilenceClearTimer();
            QueueOverflowCheck();
        }

        private void OverlaySettingChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OverlaySettingChanged(sender, e)));
                return;
            }

            switch (e.PropertyName)
            {
                case "FontFamily":
                case "FontWeight":
                case "FontStretch":
                case "FontStyle":
                    ApplyFontFamily();
                    QueueOverflowCheck();
                    break;
                case "FontSize":
                    ApplyFontSize();
                    QueueOverflowCheck();
                    break;
                case "FontBold":
                    ApplyFontWeight();
                    QueueOverflowCheck();
                    break;
                case "FontStroke":
                    ApplyFontStroke();
                    break;
                case "FontColorHex":
                    ApplyFontColor();
                    break;
                case "BackgroundColorHex":
                    ApplyBackgroundColor();
                    break;
                case "Opacity":
                    ApplyBackgroundOpacity();
                    break;
                case "SilenceClearDelay":
                    RestartSilenceClearTimer();
                    break;
            }
        }

        private static bool UpdateIfChanged(ref string previous, string current)
        {
            if (string.Equals(previous, current, StringComparison.Ordinal))
                return false;
            previous = current;
            return true;
        }

        private void CaptureOverlayText()
        {
            lastOriginalCaption = Translator.Caption.OverlayOriginalCaption;
            lastCurrentTranslation = Translator.Caption.OverlayCurrentTranslation;
            lastPreviousTranslation = Translator.Caption.OverlayPreviousTranslation;
            lastNoticePrefix = Translator.Caption.OverlayNoticePrefix;
        }

        private void RestartSilenceClearTimer()
        {
            silenceClearTimer.Stop();
            double delay = Translator.Setting.OverlayWindow.SilenceClearDelay;
            if (delay <= 0 || !IsLastSentenceComplete())
                return;
            silenceClearTimer.Interval = TimeSpan.FromSeconds(delay);
            silenceClearTimer.Start();
        }

        private bool IsLastSentenceComplete()
        {
            string caption = lastOriginalCaption.TrimEnd();
            return caption.Length > 0 && Array.IndexOf(TextUtil.PUNC_EOS, caption[^1]) >= 0;
        }

        private void SilenceClearTimer_Tick(object? sender, EventArgs e)
        {
            silenceClearTimer.Stop();
            ClearOverlayVisuals();
        }

        private void ClearOverlayVisuals()
        {
            clearedOriginalPrefix = lastOriginalCaption;
            clearedTranslationPrefix = lastCurrentTranslation;
            overlayWasCleared = true;
            RenderOverlay();
        }

        private void RenderOverlay()
        {
            string original = lastOriginalCaption;
            if (overlayWasCleared && !string.IsNullOrEmpty(clearedOriginalPrefix) &&
                original.StartsWith(clearedOriginalPrefix, StringComparison.Ordinal))
                original = original[clearedOriginalPrefix.Length..].TrimStart();

            string translation = lastCurrentTranslation;
            if (overlayWasCleared)
            {
                if (string.IsNullOrEmpty(original))
                    translation = string.Empty;
                else if (!string.IsNullOrEmpty(clearedTranslationPrefix) &&
                         translation.StartsWith(clearedTranslationPrefix, StringComparison.Ordinal))
                    translation = translation[clearedTranslationPrefix.Length..].TrimStart();
            }

            SetTextIfChanged(OriginalCaption, original);
            SetTextIfChanged(CurrentTranslationRun, translation);
            SetTextIfChanged(PreviousTranslationRun, overlayWasCleared ? string.Empty : lastPreviousTranslation);
            SetTextIfChanged(NoticePrefixRun,
                string.IsNullOrEmpty(original) && string.IsNullOrEmpty(translation) ? string.Empty : lastNoticePrefix);
        }

        private static void SetTextIfChanged(System.Windows.Controls.TextBlock target, string text)
        {
            if (!string.Equals(target.Text, text, StringComparison.Ordinal))
                target.Text = text;
        }

        private static void SetTextIfChanged(System.Windows.Documents.Run target, string text)
        {
            if (!string.Equals(target.Text, text, StringComparison.Ordinal))
                target.Text = text;
        }

        private void QueueOverflowCheck()
        {
            if (overflowCheckQueued || !IsLoaded)
                return;
            overflowCheckQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                overflowCheckQueued = false;
                ClearCompletedSentenceIfNearOverflow();
            }), DispatcherPriority.Loaded);
        }

        private void ClearCompletedSentenceIfNearOverflow()
        {
            if (!IsLastSentenceComplete())
                return;

            bool originalOverflow = OriginalCaptionCard.Visibility == Visibility.Visible &&
                                    IsNearOverflow(OriginalCaption, OriginalCaptionCard);
            bool translationOverflow = TranslatedCaptionCard.Visibility == Visibility.Visible &&
                                       IsNearOverflow(TranslatedCaption, TranslatedCaptionCard);
            if (originalOverflow || translationOverflow)
            {
                silenceClearTimer.Stop();
                ClearOverlayVisuals();
            }
        }

        private bool IsNearOverflow(System.Windows.Controls.TextBlock textBlock, FrameworkElement container)
        {
            if (string.IsNullOrWhiteSpace(textBlock.Text) || container.ActualWidth <= 20 || container.ActualHeight <= 20)
                return false;

            double maxTextWidth = Math.Max(1, container.ActualWidth - 26);
            var typeface = new Typeface(textBlock.FontFamily, textBlock.FontStyle,
                textBlock.FontWeight, textBlock.FontStretch);
            var formattedText = new FormattedText(textBlock.Text, CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight, typeface, textBlock.FontSize, textBlock.Foreground,
                VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                MaxTextWidth = maxTextWidth
            };
            double availableTextHeight = Math.Max(1, container.ActualHeight - 22);
            return formattedText.Height >= availableTextHeight * 0.9;
        }

        private void Window_MouseEnter(object sender, MouseEventArgs e)
        {
            ControlPanel.Visibility = Visibility.Visible;
        }

        private void Window_MouseLeave(object sender, MouseEventArgs e)
        {
            ControlPanel.Visibility = Visibility.Hidden;
        }

        private void FontIncrease_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting.OverlayWindow.FontSize + StyleConsts.DELTA_FONT_SIZE < StyleConsts.MAX_FONT_SIZE)
            {
                Translator.Setting.OverlayWindow.FontSize += StyleConsts.DELTA_FONT_SIZE;
            }
        }

        private void FontDecrease_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting.OverlayWindow.FontSize - StyleConsts.DELTA_FONT_SIZE > StyleConsts.MIN_FONT_SIZE)
            {
                Translator.Setting.OverlayWindow.FontSize -= StyleConsts.DELTA_FONT_SIZE;
            }
        }

        private void FontBold_Click(object sender, RoutedEventArgs e)
        {
            Translator.Setting.OverlayWindow.FontBold++;
            if (Translator.Setting.OverlayWindow.FontBold > Utils.FontBold.Both)
                Translator.Setting.OverlayWindow.FontBold = Utils.FontBold.None;
        }

        private void FontStrokeIncrease_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting.OverlayWindow.FontStroke + StyleConsts.DELTA_STROKE > StyleConsts.MAX_STROKE)
                return;
            Translator.Setting.OverlayWindow.FontStroke += StyleConsts.DELTA_STROKE;
        }

        private void FontStrokeDecrease_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting.OverlayWindow.FontStroke - StyleConsts.DELTA_STROKE < StyleConsts.MIN_STROKE)
                return;
            Translator.Setting.OverlayWindow.FontStroke -= StyleConsts.DELTA_STROKE;
        }

        private void FontColorPicker_Click(object sender, RoutedEventArgs e)
        {
            string? color = ShowColorPalette(Translator.Setting.OverlayWindow.FontColorHex);
            if (color != null)
                Translator.Setting.OverlayWindow.FontColorHex = color;
        }

        private void BackgroundOpacityIncrease_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting.OverlayWindow.Opacity + StyleConsts.DELTA_OPACITY < StyleConsts.MAX_OPACITY)
                Translator.Setting.OverlayWindow.Opacity += StyleConsts.DELTA_OPACITY;
            else
                Translator.Setting.OverlayWindow.Opacity = StyleConsts.MAX_OPACITY;
        }

        private void BackgroundOpacityDecrease_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting.OverlayWindow.Opacity - StyleConsts.DELTA_OPACITY > StyleConsts.MIN_OPACITY)
                Translator.Setting.OverlayWindow.Opacity -= StyleConsts.DELTA_OPACITY;
            else
                Translator.Setting.OverlayWindow.Opacity = StyleConsts.MIN_OPACITY;
        }

        private void BackgroundColorPicker_Click(object sender, RoutedEventArgs e)
        {
            string? color = ShowColorPalette(Translator.Setting.OverlayWindow.BackgroundColorHex);
            if (color != null)
                Translator.Setting.OverlayWindow.BackgroundColorHex = color;
        }

        private void OnlyModeButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var symbolIcon = button?.Icon as SymbolIcon;

            if (onlyMode == CaptionVisible.SubtitleOnly)
            {
                // (0) Subtitle + Translation
                symbolIcon.Symbol = SymbolRegular.PanelBottom20;
                OnlyMode = CaptionVisible.Both;
            }
            else if (onlyMode == CaptionVisible.Both)
            {
                // (1) Translation Only
                symbolIcon.Symbol = SymbolRegular.PanelTopExpand20;
                OnlyMode = CaptionVisible.TranslationOnly;
            }
            else
            {
                // (2) Subtitle Only
                symbolIcon.Symbol = SymbolRegular.PanelTopContract20;
                OnlyMode = CaptionVisible.SubtitleOnly;
            }
        }

        private void SwitchModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (SwitchMode == CaptionLocation.TranslationTop)
            {
                Grid.SetRow(TranslatedCaptionCard, 1);
                Grid.SetRow(OriginalCaptionCard, 0);
                SwitchMode = CaptionLocation.SubtitleTop;
            }
            else
            {
                Grid.SetRow(TranslatedCaptionCard, 0);
                Grid.SetRow(OriginalCaptionCard, 1);
                SwitchMode = CaptionLocation.TranslationTop;
            }
        }

        private void ClickThrough_Click(object sender, RoutedEventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var extendedStyle = WindowsAPI.GetWindowLong(hwnd, WindowsAPI.GWL_EXSTYLE);
            WindowsAPI.SetWindowLong(hwnd, WindowsAPI.GWL_EXSTYLE, extendedStyle | WindowsAPI.WS_EX_TRANSPARENT);
            ControlPanel.Visibility = Visibility.Collapsed;
        }

        public void ResizeForOnlyMode()
        {
            bool showOriginal = onlyMode != CaptionVisible.TranslationOnly;
            bool showTranslation = onlyMode != CaptionVisible.SubtitleOnly;
            OriginalCaptionCard.Visibility = showOriginal ? Visibility.Visible : Visibility.Collapsed;
            TranslatedCaptionCard.Visibility = showTranslation ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetRowSpan(OriginalCaptionCard, showOriginal && !showTranslation ? 2 : 1);
            Grid.SetRowSpan(TranslatedCaptionCard, showTranslation && !showOriginal ? 2 : 1);
            QueueOverflowCheck();
        }

        public void ApplyFontSize()
        {
            OriginalCaption.FontSize = Translator.Setting.OverlayWindow.FontSize;
            TranslatedCaption.FontSize = (int)(OriginalCaption.FontSize * 1.25);
        }

        public void ApplyFontFamily()
        {
            var fontFamily = new System.Windows.Media.FontFamily(Translator.Setting.OverlayWindow.FontFamily);
            var fontStretch = System.Windows.FontStretch.FromOpenTypeStretch(
                Math.Clamp(Translator.Setting.OverlayWindow.FontStretch, 1, 9));
            var fontStyle = Translator.Setting.OverlayWindow.FontStyle switch
            {
                "Italic" => FontStyles.Italic,
                "Oblique" => FontStyles.Oblique,
                _ => FontStyles.Normal
            };
            OriginalCaption.FontFamily = fontFamily;
            OriginalCaption.FontStretch = fontStretch;
            OriginalCaption.FontStyle = fontStyle;
            TranslatedCaption.FontFamily = fontFamily;
            TranslatedCaption.FontStretch = fontStretch;
            TranslatedCaption.FontStyle = fontStyle;
            ApplyFontWeight();
        }

        private void ApplyFontWeight()
        {
            var selectedWeight = System.Windows.FontWeight.FromOpenTypeWeight(
                Math.Clamp(Translator.Setting.OverlayWindow.FontWeight, 1, 999));
            var boldMode = Translator.Setting.OverlayWindow.FontBold;
            OriginalCaption.FontWeight = boldMode is Utils.FontBold.SubtitleOnly or Utils.FontBold.Both ?
                FontWeights.Bold : selectedWeight;
            TranslatedCaption.FontWeight = boldMode is Utils.FontBold.TranslationOnly or Utils.FontBold.Both ?
                FontWeights.Bold : selectedWeight;
        }

        public void ApplyFontStroke()
        {
            OriginalCaptionDecorator.StrokeThickness = Translator.Setting.OverlayWindow.FontStroke;
            TranslatedCaptionDecorator.StrokeThickness = Translator.Setting.OverlayWindow.FontStroke;
        }

        public void ApplyFontColor()
        {
            var brush = new SolidColorBrush(ParseColor(Translator.Setting.OverlayWindow.FontColorHex, Colors.White));
            OriginalCaption.Foreground = brush;
            TranslatedCaption.Foreground = brush;
            UpdateTranslationColor(brush);
            FontColorPicker.Background = brush;
        }

        public void ApplyBackgroundColor()
        {
            BorderBackground.Background = new SolidColorBrush(
                ParseColor(Translator.Setting.OverlayWindow.BackgroundColorHex, Colors.Black));
            BackgroundColorPicker.Background = BorderBackground.Background;
            ApplyBackgroundOpacity();
        }

        public void ApplyBackgroundOpacity()
        {
            Color color = ((SolidColorBrush)BorderBackground.Background).Color;
            BorderBackground.Background = new SolidColorBrush(Color.FromArgb(
                (byte)Translator.Setting.OverlayWindow.Opacity, color.R, color.G, color.B));
        }

        private static Color ParseColor(string? value, Color fallback)
        {
            try
            {
                return (Color)ColorConverter.ConvertFromString(value ?? string.Empty);
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException)
            {
                return fallback;
            }
        }

        private static string? ShowColorPalette(string currentColor)
        {
            Color color = ParseColor(currentColor, Colors.White);
            using var dialog = new System.Windows.Forms.ColorDialog
            {
                AllowFullOpen = true,
                AnyColor = true,
                FullOpen = true,
                SolidColorOnly = false,
                Color = System.Drawing.Color.FromArgb(color.R, color.G, color.B)
            };
            if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
                return null;
            return $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        }

        private void UpdateTranslationColor(SolidColorBrush brush)
        {
            var color = brush.Color;

            double target = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B > 127 ? 0 : 255;
            byte r = (byte)Math.Clamp(color.R + (target - color.R) * 0.3, 0, 255);
            byte g = (byte)Math.Clamp(color.G + (target - color.G) * 0.4, 0, 255);
            byte b = (byte)Math.Clamp(color.B + (target - color.B) * 0.3, 0, 255);

            NoticePrefixRun.Foreground = brush;
            PreviousTranslationRun.Foreground = brush;
            CurrentTranslationRun.Foreground = new SolidColorBrush(Color.FromRgb(r, g, b));
        }
    }
}
