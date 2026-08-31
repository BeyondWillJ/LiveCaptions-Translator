using System.ComponentModel;
using System.Runtime.CompilerServices;
using LiveCaptionsTranslator.Utils;

namespace LiveCaptionsTranslator.models
{
    public class MainWindowState : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private bool topmost = true;
        private bool captionLogEnabled = false;
        private bool latencyShow = false;
        private int originalFontSize = 15;
        private int translatedFontSize = 18;

        public bool Topmost
        {
            get => topmost;
            set
            {
                topmost = value;
                OnPropertyChanged("Topmost");
            }
        }
        public bool CaptionLogEnabled
        {
            get => captionLogEnabled;
            set
            {
                captionLogEnabled = value;
                OnPropertyChanged("CaptionLogEnabled");
            }
        }
        public bool LatencyShow
        {
            get => latencyShow;
            set
            {
                latencyShow = value;
                OnPropertyChanged("LatencyShow");
            }
        }
        public int OriginalFontSize
        {
            get => originalFontSize;
            set
            {
                originalFontSize = value;
                OnPropertyChanged("OriginalFontSize");
            }
        }
        public int TranslatedFontSize
        {
            get => translatedFontSize;
            set
            {
                translatedFontSize = value;
                OnPropertyChanged("TranslatedFontSize");
            }
        }

        public void OnPropertyChanged([CallerMemberName] string propName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
            Translator.Setting?.Save();
        }
    }

    public class OverlayWindowState : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private int fontSize = 15;
        private string fontFamily = "Segoe UI";
        private int fontWeight = 400;
        private int fontStretch = 5;
        private string fontStyle = "Normal";
        private List<string> recentFontFaces = [];
        private Color fontColor = Color.White;
        private string? fontColorHex;
        private FontBold fontBold = FontBold.None;
        private double fontStroke = 0.0;

        private Color backgroundColor = Color.Black;
        private string? backgroundColorHex;
        private int opacity = 150;
        private double silenceClearDelay = 1.5;

        public int FontSize
        {
            get => fontSize;
            set
            {
                fontSize = value;
                OnPropertyChanged("FontSize");
            }
        }
        public string FontFamily
        {
            get => fontFamily;
            set
            {
                fontFamily = value;
                OnPropertyChanged("FontFamily");
            }
        }
        public int FontWeight
        {
            get => fontWeight;
            set
            {
                fontWeight = value;
                OnPropertyChanged("FontWeight");
            }
        }
        public int FontStretch
        {
            get => fontStretch;
            set
            {
                fontStretch = value;
                OnPropertyChanged("FontStretch");
            }
        }
        public string FontStyle
        {
            get => fontStyle;
            set
            {
                fontStyle = value;
                OnPropertyChanged("FontStyle");
            }
        }
        public List<string> RecentFontFaces
        {
            get => recentFontFaces;
            set
            {
                recentFontFaces = value;
                OnPropertyChanged("RecentFontFaces");
            }
        }
        public Color FontColor
        {
            get => fontColor;
            set
            {
                fontColor = value;
                OnPropertyChanged("FontColor");
            }
        }
        public string FontColorHex
        {
            get => fontColorHex ?? LegacyColorToHex(fontColor);
            set
            {
                fontColorHex = value;
                OnPropertyChanged("FontColorHex");
            }
        }
        public FontBold FontBold
        {
            get => fontBold;
            set
            {
                fontBold = value;
                OnPropertyChanged("FontBold");
            }
        }
        public double FontStroke
        {
            get => fontStroke;
            set
            {
                fontStroke = value;
                OnPropertyChanged("FontStroke");
            }
        }
        public Color BackgroundColor
        {
            get => backgroundColor;
            set
            {
                backgroundColor = value;
                OnPropertyChanged("BackgroundColor");
            }
        }
        public string BackgroundColorHex
        {
            get => backgroundColorHex ?? LegacyColorToHex(backgroundColor);
            set
            {
                backgroundColorHex = value;
                OnPropertyChanged("BackgroundColorHex");
            }
        }
        public int Opacity
        {
            get => opacity;
            set
            {
                opacity = value;
                OnPropertyChanged("Opacity");
            }
        }
        public double SilenceClearDelay
        {
            get => silenceClearDelay;
            set
            {
                silenceClearDelay = value;
                OnPropertyChanged("SilenceClearDelay");
            }
        }

        public void OnPropertyChanged([CallerMemberName] string propName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
            Translator.Setting?.Save();
        }

        private static string LegacyColorToHex(Color color) => color switch
        {
            Color.White => "#FFFFFF",
            Color.Yellow => "#FFFF00",
            Color.LimeGreen => "#32CD32",
            Color.Aqua => "#00FFFF",
            Color.Blue => "#0000FF",
            Color.DeepPink => "#FF1493",
            Color.Red => "#FF0000",
            Color.Black => "#000000",
            _ => "#FFFFFF"
        };
    }
}
