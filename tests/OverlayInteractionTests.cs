using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using LiveCaptionsTranslator.apis;

namespace LiveCaptionsTranslator.Tests;

public class OverlayInteractionTests
{
    [Fact]
    public void RestoreOverlayButton_RoutedClickRestoresNativeWindowInteraction()
    {
        WpfTestDispatcher.Invoke(() =>
        {
            MainWindow? mainWindow = null;
            OverlayWindow? overlayWindow = null;
            bool originalTopmost = false;
            Dictionary<string, string>? originalBounds = null;
            try
            {
                var setting = Translator.Setting;
                originalTopmost = setting.MainWindow.Topmost;
                originalBounds = new Dictionary<string, string>(setting.WindowBounds);

                mainWindow = new MainWindow();
                var overlayModeButton = Assert.IsAssignableFrom<Button>(
                    mainWindow.FindName("OverlayModeButton"));
                var restoreButton = Assert.IsAssignableFrom<Button>(
                    mainWindow.FindName("RestoreOverlayButton"));

                overlayModeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, overlayModeButton));
                overlayWindow = Assert.IsType<OverlayWindow>(mainWindow.OverlayWindow);
                overlayWindow.Hide();

                var clickThroughButton = Assert.IsAssignableFrom<Button>(
                    overlayWindow.FindName("ClickThrough"));
                var controlPanel = Assert.IsAssignableFrom<Panel>(
                    overlayWindow.FindName("ControlPanel"));
                nint handle = new WindowInteropHelper(overlayWindow).Handle;
                Assert.NotEqual(nint.Zero, handle);

                Assert.Equal(Visibility.Collapsed, restoreButton.Visibility);

                clickThroughButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, clickThroughButton));
                Assert.True(overlayWindow.IsClickThrough);
                Assert.Equal(Visibility.Collapsed, controlPanel.Visibility);
                Assert.Equal(Visibility.Visible, restoreButton.Visibility);

                int clickThroughStyle = WindowsAPI.GetWindowLong(handle, WindowsAPI.GWL_EXSTYLE);
                Assert.NotEqual(0, clickThroughStyle & WindowsAPI.WS_EX_TRANSPARENT);

                restoreButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, restoreButton));
                Assert.False(overlayWindow.IsClickThrough);
                Assert.Equal(Visibility.Visible, controlPanel.Visibility);
                Assert.Equal(Visibility.Collapsed, restoreButton.Visibility);

                int restoredStyle = WindowsAPI.GetWindowLong(handle, WindowsAPI.GWL_EXSTYLE);
                Assert.Equal(0, restoredStyle & WindowsAPI.WS_EX_TRANSPARENT);
            }
            finally
            {
                try { overlayWindow?.Close(); }
                catch { }
                try { mainWindow?.Close(); }
                catch { }

                if (originalBounds != null)
                {
                    Translator.Setting.MainWindow.Topmost = originalTopmost;
                    Translator.Setting.WindowBounds = originalBounds;
                    Translator.Setting.FlushPendingSave();
                }
            }
        });
    }
}
