using System.Windows;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.Tests;

public class WindowHandlerTests
{
    [Fact]
    public void VirtualDesktopVisibility_AcceptsValidNegativeMonitorCoordinates()
    {
        var desktop = new Rect(-1920, 0, 3840, 1080);

        Assert.True(WindowHandler.IsVisibleOnVirtualDesktop(
            new Rect(-1600, 120, 800, 240), desktop));
        Assert.False(WindowHandler.IsVisibleOnVirtualDesktop(
            new Rect(3000, 120, 800, 240), desktop));
        Assert.False(WindowHandler.IsVisibleOnVirtualDesktop(
            new Rect(-1980, 120, 100, 60), desktop));
    }
}
