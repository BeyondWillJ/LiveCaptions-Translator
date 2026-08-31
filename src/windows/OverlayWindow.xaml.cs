using System.ComponentModel;
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
using ColorEnum = LiveCaptionsTranslator.Utils.Color;

namespace LiveCaptionsTranslator
{
    public partial class OverlayWindow : Window
    {
        private readonly Dictionary<ColorEnum, SolidColorBrush> colorMap = new()
        {
            {ColorEnum.White, Brushes.White},
            {ColorEnum.Yellow, Brushes.Yellow},
            {ColorEnum.LimeGreen, Brushes.LimeGreen},
            {ColorEnum.Aqua, Brushes.Aqua},
            {ColorEnum.Blue, Brushes.Blue},
            {ColorEnum.DeepPink, Brushes.DeepPink},
            {ColorEnum.Red, Brushes.Red},
            {ColorEnum.Black, Brushes.Black},
        };
        private CaptionVisible onlyMode = CaptionVisible.Both;
        private readonly DispatcherTimer silenceClearTimer = new();
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
            DataContext = Translator.Caption;

            silenceClearTimer.Tick += SilenceClearTimer_Tick;
            Loaded += OverlayWindow_Loaded;
            Unloaded += OverlayWindow_Unloaded;

            OriginalCaption.FontWeight = Translator.Setting.OverlayWindow.FontBold == Utils.FontBold.Both ?
                FontWeights.Bold : FontWeights.Regular;
            TranslatedCaption.FontWeight = Translator.Setting.OverlayWindow.FontBold >= Utils.FontBold.TranslationOnly ?
                FontWeights.Bold : FontWeights.Regular;

            OriginalCaptionDecorator.StrokeThickness = Translator.Setting.OverlayWindow.FontStroke;
            TranslatedCaptionDecorator.StrokeThickness = Translator.Setting.OverlayWindow.FontStroke;

            OriginalCaption.Foreground = colorMap[Translator.Setting.OverlayWindow.FontColor];
            UpdateTranslationColor(colorMap[Translator.Setting.OverlayWindow.FontColor]);

            BorderBackground.Background = colorMap[Translator.Setting.OverlayWindow.BackgroundColor];
            BorderBackground.Opacity = Translator.Setting.OverlayWindow.Opacity;

            ApplyFontSize();
            ApplyFontFamily();
            ApplyBackgroundOpacity();
        }

        private void OverlayWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Translator.Caption.PropertyChanged += TranslatedChanged;
            Translator.Setting.OverlayWindow.PropertyChanged += OverlaySettingChanged;
            CaptureOverlayText();
            RestartSilenceClearTimer();
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

        private void TranslatedChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => TranslatedChanged(sender, e)));
                return;
            }

            ApplyFontSize();

            bool textChanged = e.PropertyName switch
            {
                "OverlayOriginalCaption" => UpdateIfChanged(ref lastOriginalCaption, Translator.Caption.OverlayOriginalCaption),
                "OverlayCurrentTranslation" => UpdateIfChanged(ref lastCurrentTranslation, Translator.Caption.OverlayCurrentTranslation),
                "OverlayPreviousTranslation" => UpdateIfChanged(ref lastPreviousTranslation, Translator.Caption.OverlayPreviousTranslation),
                "OverlayNoticePrefix" => UpdateIfChanged(ref lastNoticePrefix, Translator.Caption.OverlayNoticePrefix),
                _ => false
            };

            if (!textChanged)
                return;

            Dispatcher.BeginInvoke(new Action(ApplyPostClearDisplay), DispatcherPriority.DataBind);
            RestartSilenceClearTimer();
        }

        private void OverlaySettingChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OverlaySettingChanged(sender, e)));
                return;
            }

            if (e.PropertyName is "FontFamily" or "FontWeight" or "FontStretch" or "FontStyle")
                ApplyFontFamily();
            else if (e.PropertyName == "SilenceClearDelay")
                RestartSilenceClearTimer();
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
            clearedOriginalPrefix = lastOriginalCaption;
            clearedTranslationPrefix = lastCurrentTranslation;
            overlayWasCleared = true;

            OriginalCaption.SetCurrentValue(System.Windows.Controls.TextBlock.TextProperty, string.Empty);
            NoticePrefixRun.SetCurrentValue(System.Windows.Documents.Run.TextProperty, string.Empty);
            PreviousTranslationRun.SetCurrentValue(System.Windows.Documents.Run.TextProperty, string.Empty);
            CurrentTranslationRun.SetCurrentValue(System.Windows.Documents.Run.TextProperty, string.Empty);
        }

        private void ApplyPostClearDisplay()
        {
            if (!overlayWasCleared)
                return;

            string original = lastOriginalCaption;
            if (!string.IsNullOrEmpty(clearedOriginalPrefix) && original.StartsWith(clearedOriginalPrefix, StringComparison.Ordinal))
                original = original[clearedOriginalPrefix.Length..].TrimStart();
            OriginalCaption.SetCurrentValue(System.Windows.Controls.TextBlock.TextProperty, original);

            string translation = lastCurrentTranslation;
            if (!string.IsNullOrEmpty(clearedTranslationPrefix) && translation.StartsWith(clearedTranslationPrefix, StringComparison.Ordinal))
                translation = translation[clearedTranslationPrefix.Length..].TrimStart();
            CurrentTranslationRun.SetCurrentValue(System.Windows.Documents.Run.TextProperty, translation);
            PreviousTranslationRun.SetCurrentValue(System.Windows.Documents.Run.TextProperty, string.Empty);
            NoticePrefixRun.SetCurrentValue(System.Windows.Documents.Run.TextProperty,
                string.IsNullOrEmpty(original) && string.IsNullOrEmpty(translation) ? string.Empty : lastNoticePrefix);
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
                ApplyFontSize();
            }
        }

        private void FontDecrease_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting.OverlayWindow.FontSize - StyleConsts.DELTA_FONT_SIZE > StyleConsts.MIN_FONT_SIZE)
            {
                Translator.Setting.OverlayWindow.FontSize -= StyleConsts.DELTA_FONT_SIZE;
                ApplyFontSize();
            }
        }

        private void FontBold_Click(object sender, RoutedEventArgs e)
        {
            Translator.Setting.OverlayWindow.FontBold++;
            if (Translator.Setting.OverlayWindow.FontBold > Utils.FontBold.Both)
                Translator.Setting.OverlayWindow.FontBold = Utils.FontBold.None;
            ApplyFontWeight();
        }

        private void FontStrokeIncrease_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting.OverlayWindow.FontStroke + StyleConsts.DELTA_STROKE > StyleConsts.MAX_STROKE)
                return;
            Translator.Setting.OverlayWindow.FontStroke += StyleConsts.DELTA_STROKE;
            ApplyFontStroke();
        }

        private void FontStrokeDecrease_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting.OverlayWindow.FontStroke - StyleConsts.DELTA_STROKE < StyleConsts.MIN_STROKE)
                return;
            Translator.Setting.OverlayWindow.FontStroke -= StyleConsts.DELTA_STROKE;
            ApplyFontStroke();
        }

        private void FontColorCycle_Click(object sender, RoutedEventArgs e)
        {
            Translator.Setting.OverlayWindow.FontColor++;
            if (Translator.Setting.OverlayWindow.FontColor > ColorEnum.Black)
                Translator.Setting.OverlayWindow.FontColor = ColorEnum.White;
            OriginalCaption.Foreground = colorMap[Translator.Setting.OverlayWindow.FontColor];
            TranslatedCaption.Foreground = colorMap[Translator.Setting.OverlayWindow.FontColor];
            UpdateTranslationColor(colorMap[Translator.Setting.OverlayWindow.FontColor]);
        }

        private void BackgroundOpacityIncrease_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting.OverlayWindow.Opacity + StyleConsts.DELTA_OPACITY < StyleConsts.MAX_OPACITY)
                Translator.Setting.OverlayWindow.Opacity += StyleConsts.DELTA_OPACITY;
            else
                Translator.Setting.OverlayWindow.Opacity = StyleConsts.MAX_OPACITY;
            ApplyBackgroundOpacity();
        }

        private void BackgroundOpacityDecrease_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting.OverlayWindow.Opacity - StyleConsts.DELTA_OPACITY > StyleConsts.MIN_OPACITY)
                Translator.Setting.OverlayWindow.Opacity -= StyleConsts.DELTA_OPACITY;
            else
                Translator.Setting.OverlayWindow.Opacity = StyleConsts.MIN_OPACITY;
            ApplyBackgroundOpacity();
        }

        private void BackgroundColorCycle_Click(object sender, RoutedEventArgs e)
        {
            Translator.Setting.OverlayWindow.BackgroundColor++;
            if (Translator.Setting.OverlayWindow.BackgroundColor > ColorEnum.Black)
                Translator.Setting.OverlayWindow.BackgroundColor = ColorEnum.White;
            BorderBackground.Background = colorMap[Translator.Setting.OverlayWindow.BackgroundColor];

            BorderBackground.Opacity = Translator.Setting.OverlayWindow.Opacity;
            ApplyBackgroundOpacity();
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
            if (onlyMode == CaptionVisible.TranslationOnly)
            {
                // (1) Translation Only
                OriginalCaptionCard.Visibility = Visibility.Collapsed;
                this.MinHeight -= StyleConsts.DELTA_OVERLAY_HEIGHT;
                this.Height -= StyleConsts.DELTA_OVERLAY_HEIGHT;
                this.Top += StyleConsts.DELTA_OVERLAY_HEIGHT;
            }
            if (onlyMode == CaptionVisible.SubtitleOnly)
            {
                // restore
                OriginalCaptionCard.Visibility = Visibility.Visible;
                this.Top -= StyleConsts.DELTA_OVERLAY_HEIGHT;
                this.Height += StyleConsts.DELTA_OVERLAY_HEIGHT;
                this.MinHeight += StyleConsts.DELTA_OVERLAY_HEIGHT;

                // (2) Subtitle Only
                TranslatedCaptionCard.Visibility = Visibility.Collapsed;
                this.MinHeight -= StyleConsts.DELTA_OVERLAY_HEIGHT;
                this.Height -= StyleConsts.DELTA_OVERLAY_HEIGHT;
            }
            else if (onlyMode == CaptionVisible.Both)
            {
                // restore
                TranslatedCaptionCard.Visibility = Visibility.Visible;
                this.Height += StyleConsts.DELTA_OVERLAY_HEIGHT;
                this.MinHeight += StyleConsts.DELTA_OVERLAY_HEIGHT;
            }
        }

        public void ApplyFontSize()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                OriginalCaption.FontSize = Translator.Setting.OverlayWindow.FontSize;
                TranslatedCaption.FontSize = (int)(OriginalCaption.FontSize * 1.25);
            }), DispatcherPriority.Background);
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

        public void ApplyBackgroundOpacity()
        {
            Color color = ((SolidColorBrush)BorderBackground.Background).Color;
            BorderBackground.Background = new SolidColorBrush(Color.FromArgb(
                (byte)Translator.Setting.OverlayWindow.Opacity, color.R, color.G, color.B));
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
