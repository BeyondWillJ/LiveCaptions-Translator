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
using LiveCaptionsTranslator.models;
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
        private readonly OverlayPresentationState presentationState = new();
        private bool renderQueued;
        private bool overflowCheckQueued;
        private CaptionLocation switchMode = CaptionLocation.TranslationTop;
        private bool isClickThrough;
        public event Action? ClickThroughStateChanged;

        public bool IsClickThrough => isClickThrough;

        public CaptionVisible OnlyMode
        {
            get => onlyMode;
            set
            {
                onlyMode = value;
                ResizeForOnlyMode();
                UpdateOnlyModeIcon();
                Translator.Setting.OverlayWindow.DisplayMode = value;
            }
        }
        public CaptionLocation SwitchMode
        {
            get => switchMode;
            set
            {
                switchMode = value;
                ApplyCaptionLocation();
                Translator.Setting.OverlayWindow.CaptionLocation = value;
            }
        }

        public OverlayWindow()
        {
            InitializeComponent();

            onlyMode = Translator.Setting.OverlayWindow.DisplayMode;
            switchMode = Translator.Setting.OverlayWindow.CaptionLocation;
            ResizeForOnlyMode();
            UpdateOnlyModeIcon();
            ApplyCaptionLocation();

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
            presentationState.Reset(CaptureOverlaySnapshot());
            ApplyRender(presentationState.CurrentRender);
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
                "OverlayPreviousTranslation" or "OverlayNoticePrefix" or "OverlaySessionId" or
                "OverlayEpoch" or "OverlayTranslationSessionId" or "OverlayTranslationEpoch" or
                "OverlayTranslationSegmentId" or "OverlayTranslationRevision" or
                "OverlayTranslatedSourceText")
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

            OverlayUpdate update = presentationState.Update(CaptureOverlaySnapshot());
            if (!update.Changed)
                return;
            if (update.RestartSilenceTimer)
                RestartSilenceClearTimer();
            ApplyRender(update.Render);
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

        private static OverlaySnapshot CaptureOverlaySnapshot() => new(
            Translator.Caption.OverlaySourceSegmentId,
            Translator.Caption.OverlaySourceRevision,
            Translator.Caption.OverlayOriginalCaption,
            Translator.Caption.OverlayTranslationSegmentId,
            Translator.Caption.OverlayTranslationRevision,
            Translator.Caption.OverlayCurrentTranslation,
            Translator.Caption.OverlayPreviousTranslation,
            Translator.Caption.OverlayNoticePrefix,
            Translator.Caption.OverlaySessionId,
            Translator.Caption.OverlayEpoch,
            Translator.Caption.OverlayTranslationSessionId,
            Translator.Caption.OverlayTranslationEpoch,
            Translator.Caption.OverlayTranslatedSourceText);

        private void RestartSilenceClearTimer()
        {
            silenceClearTimer.Stop();
            double delay = Translator.Setting.OverlayWindow.SilenceClearDelay;
            if (!presentationState.ShouldRunSilenceTimer(delay))
                return;
            silenceClearTimer.Interval = TimeSpan.FromSeconds(delay);
            silenceClearTimer.Start();
        }

        private void SilenceClearTimer_Tick(object? sender, EventArgs e)
        {
            silenceClearTimer.Stop();
            ApplyRender(presentationState.OnSilenceElapsed());
        }

        private void ApplyRender(OverlayRenderState render)
        {
            SetTextIfChanged(OriginalCaption, render.Original);
            SetTextIfChanged(CurrentTranslationRun, render.Translation);
            SetTextIfChanged(PreviousTranslationRun, render.PreviousTranslation);
            SetTextIfChanged(NoticePrefixRun, render.NoticePrefix);
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
            bool originalOverflow = OriginalCaptionCard.Visibility == Visibility.Visible &&
                                    IsNearOverflow(OriginalCaption, OriginalCaptionCard);
            bool translationOverflow = TranslatedCaptionCard.Visibility == Visibility.Visible &&
                                       IsNearOverflow(TranslatedCaption, TranslatedCaptionCard);
            if (originalOverflow || translationOverflow)
            {
                if (!string.IsNullOrWhiteSpace(PreviousTranslationRun.Text))
                {
                    ApplyRender(presentationState.SuppressPreviousForOverflow());
                    QueueOverflowCheck();
                    return;
                }
                if (originalOverflow)
                    OriginalCaption.Text = TrimToFit(OriginalCaption.Text, OriginalCaption, OriginalCaptionCard);
                if (translationOverflow)
                    CurrentTranslationRun.Text = TrimToFit(
                        CurrentTranslationRun.Text, TranslatedCaption, TranslatedCaptionCard);
            }
        }

        private string TrimToFit(string text, System.Windows.Controls.TextBlock textBlock,
            FrameworkElement container)
        {
            return OverlayPresentationState.TrimLeadingToFit(text,
                candidate => IsNearOverflow(candidate, textBlock, container));
        }

        private bool IsNearOverflow(System.Windows.Controls.TextBlock textBlock, FrameworkElement container)
        {
            return IsNearOverflow(textBlock.Text, textBlock, container);
        }

        private bool IsNearOverflow(string text, System.Windows.Controls.TextBlock textBlock,
            FrameworkElement container)
        {
            if (string.IsNullOrWhiteSpace(text) || container.ActualWidth <= 20 || container.ActualHeight <= 20)
                return false;

            double maxTextWidth = Math.Max(1, container.ActualWidth - 26);
            var typeface = new Typeface(textBlock.FontFamily, textBlock.FontStyle,
                textBlock.FontWeight, textBlock.FontStretch);
            var formattedText = new FormattedText(text, CultureInfo.CurrentUICulture,
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
            if (onlyMode == CaptionVisible.SubtitleOnly)
                OnlyMode = CaptionVisible.Both;
            else if (onlyMode == CaptionVisible.Both)
                OnlyMode = CaptionVisible.TranslationOnly;
            else
                OnlyMode = CaptionVisible.SubtitleOnly;
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

        private void ApplyCaptionLocation()
        {
            bool translationTop = switchMode == CaptionLocation.TranslationTop;
            Grid.SetRow(TranslatedCaptionCard, translationTop ? 0 : 1);
            Grid.SetRow(OriginalCaptionCard, translationTop ? 1 : 0);
        }

        private void UpdateOnlyModeIcon()
        {
            if (OnlyModeButton.Icon is not SymbolIcon icon)
                return;
            icon.Symbol = onlyMode switch
            {
                CaptionVisible.TranslationOnly => SymbolRegular.PanelTopExpand20,
                CaptionVisible.SubtitleOnly => SymbolRegular.PanelTopContract20,
                _ => SymbolRegular.PanelBottom20
            };
        }

        private void ClickThrough_Click(object sender, RoutedEventArgs e)
        {
            SetClickThrough(true);
        }

        public void RestoreInteraction() => SetClickThrough(false);

        private void SetClickThrough(bool enabled)
        {
            if (isClickThrough == enabled)
                return;
            var hwnd = new WindowInteropHelper(this).Handle;
            var extendedStyle = WindowsAPI.GetWindowLong(hwnd, WindowsAPI.GWL_EXSTYLE);
            int updated = enabled
                ? extendedStyle | WindowsAPI.WS_EX_TRANSPARENT
                : extendedStyle & ~WindowsAPI.WS_EX_TRANSPARENT;
            WindowsAPI.SetWindowLong(hwnd, WindowsAPI.GWL_EXSTYLE, updated);
            isClickThrough = enabled;
            ControlPanel.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
            ClickThroughStateChanged?.Invoke();
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
            var boldWeight = System.Windows.FontWeight.FromOpenTypeWeight(
                Math.Max(selectedWeight.ToOpenTypeWeight(), FontWeights.Bold.ToOpenTypeWeight()));
            var boldMode = Translator.Setting.OverlayWindow.FontBold;
            OriginalCaption.FontWeight = boldMode is Utils.FontBold.SubtitleOnly or Utils.FontBold.Both ?
                boldWeight : selectedWeight;
            TranslatedCaption.FontWeight = boldMode is Utils.FontBold.TranslationOnly or Utils.FontBold.Both ?
                boldWeight : selectedWeight;
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
